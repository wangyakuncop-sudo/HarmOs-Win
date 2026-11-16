using System.Net;
using HarmOsWin.Transport;

namespace HarmOsWin.Core;

/// <summary>
/// 单台设备的会话。INV-08：角色协商决定 SOURCE/HOST/BOTH，本类同时实现源端与宿主端语义。
/// 握手顺序不可颠倒：先 CONTROL(NEED_ACK) → 设备 ACK → 设备才开始推流（Windows 防火墙入站规则）。
/// </summary>
public sealed class DeviceSession : IDisposable
{
    private readonly Func<byte[], Task> _send;
    private readonly ReliableControlChannel _control;
    private readonly VideoReceiveChannel _videoStats = new();
    private readonly VideoAssembler _assembler = new();
    private ClipboardBridge? _clipboard;

    public string Id { get; }
    public string Host { get; }
    public int Port { get; }
    /// <summary>
    /// 本机收流端口（采集桥把 HWP 流发到的那个端口，双路并行时按设备区分 9010/9011）。
    /// **不能**拿它当路由键：采集桥的出流 socket 是临时源端口，帧包的 remote.Port
    /// 跟它无关。路由必须靠「包是从哪个本机端点收上来的」。
    /// </summary>
    public int LocalPort { get; }
    public byte Role { get; private set; }
    public bool Locked { get; private set; }
    public ushort Width { get; private set; }
    public ushort Height { get; private set; }
    public byte Folded { get; private set; }
    private IVideoDecoder _decoder = new PassthroughDecoder();
    /// <summary>换解码器时自动把 FrameDecoded 接到当前 Renderer（GATE-02 切换到 MF 硬解）。</summary>
    public IVideoDecoder Decoder
    {
        get => _decoder;
        set
        {
            _decoder = value;
            _decoder.FrameDecoded += f => Renderer.Present(f);
        }
    }
    public IRenderSurface Renderer { get; set; } = new NullRenderSurface();
    public ClipboardBridge? Clipboard => _clipboard;

    public long Heartbeats { get; private set; }
    public long ControlIn { get; private set; }

    // 解码线程：收包线程只做重组，绝不在里面解码。
    // 在收包线程同步 Feed 时，一帧解码+转换约 4ms，期间到达的包只能挤在 socket 缓冲里，
    // 60fps 下必然溢出（实测 0.59% 视频丢包 + p99 帧间隔 56ms）。
    private readonly object _feedGate = new();
    // H.264 的 P 帧以前一帧为参考，丢掉任意一个中间 P 帧都会让后续帧的运动矢量/
    // 残差基于错误参考 → 大面积色块与文字重影。实测：参考桥 80fps 时单槽「保新弃新」
    // 队列丢 P 帧导致花屏；同一份裸流用 60fps 离线回放（不丢帧）画面完全清晰。
    // 因此队列必须保序，溢出时只丢**队尾新帧**，绝不从中间抽帧。
    // 2026-11-09 真机实测：滑动时设备出 90fps，而 MF 低延迟单帧 Feed 17.8ms
    // （≈56fps 上限），输入持续超过消费 → 队列长期贴满。旧策略「清空 + 等下一个
    // IDR」会冻结最多 1 秒（-iFrameInterval 1000），就是用户说的「滑动卡」。
    // 现改为丢新帧：队列内参考链始终连续，画面持续流畅；代价是稳态延迟约
    // 8 × 17.8ms ≈ 0.14s，停止滑动后设备降到 ~1fps，队列迅速排空、延迟归零。
    private const int MaxDecodeQueue = 8;
    private readonly Queue<(uint Id, byte[] Data)> _decodeQueue = new();
    private bool _awaitKeyframe;  // 队列溢出后丢弃整段，等下一个 IDR 重新起链
    private long _awaitKeyframeSinceMs;
    private long _lastKeyframeRequestMs;
    private int _rescueCount;

    // ── 「等 IDR」不能无期限等（历史技术债）──
    // 溢出后置 _awaitKeyframe，只放行 IDR；如果设备端几秒内一个 IDR 都不给，
    // 画面就永久定格 —— 这是用户报过「页面卡住」的一类真故障，不是静态画面。
    // 处置分两级：
    //   ① 1.5s 仍未等到 → 再催一次 IDR（400ms 节流），最多每轮催 8 次；
    //   ② 累计约 4.7s 仍等到 → 抛 KeyframeStarvation，由上层重建 gRPC 流/解码链。
    // ⚠ 绝不能拿「帧间隔」当断流依据（参考项目 v1.0.3 教训：每 ~25s 误杀健康流，
    //   静态画面本就不推帧）。这里只认「我们自己正在丢帧」这个内部状态。
    private const int KeyframeRescueMs = 1_500;
    private const int KeyframeRequestGapMs = 400;
    private const int KeyframeStarvationHits = 8;

    /// <summary>等 IDR 已超过阈值仍无进展（记录次数，供诊断）。</summary>
    public long KeyframeStalls { get; private set; }
    private readonly AutoResetEvent _feedSignal = new(false);
    private Thread? _feedThread;
    private volatile bool _feedRunning;
    public long DecodeQueueDropped { get; private set; }

    // 端到端延迟打点：设备发包时写入 HWP 头时间戳（epoch 毫秒低 32 位）。
    // 两端系统时钟实测可能相差数分钟，必须先用最小单向延迟法标定 offset，否则算出 4.29e9 ms 的假值。
    private long _lastStreamTsAbs;
    private long _clockOffsetMs = long.MinValue;
    private long _latencySumUs;
    private long _latencyCount;
    public double LastFrameLatencyMs { get; private set; }
    public double AvgFrameLatencyMs =>
        Interlocked.Read(ref _latencyCount) == 0
            ? 0
            : Interlocked.Read(ref _latencySumUs) / 1000.0 / Interlocked.Read(ref _latencyCount);

    /// <summary>把 uint32 低位的毫秒时间戳还原成与参考时间同窗的绝对毫秒。</summary>
    private static long UnwrapMs(uint low32, long referenceMs)
    {
        long cand = (referenceMs & ~0xFFFFFFFFL) | low32;
        if (cand - referenceMs > 0x80000000L)
        {
            cand -= 0x100000000L;
        }
        else if (referenceMs - cand > 0x80000000L)
        {
            cand += 0x100000000L;
        }
        return cand;
    }

    /// <summary>设备时钟标定：取多次采样里最小的 (PC - 设备)，它最接近真实零延迟。</summary>
    private void ObserveDeviceClock(uint deviceTs)
    {
        long pc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long diff = pc - UnwrapMs(deviceTs, pc);
        if (_clockOffsetMs == long.MinValue || diff < _clockOffsetMs)
        {
            _clockOffsetMs = diff;
        }
    }

    public event Action<string>? Log;
    public event Action<bool>? LockStateChanged;
    public event Action<ushort, ushort, byte>? ResolutionChanged;
    public event Action<uint, byte[]>? StreamFrameReady;
    /// <summary>解码队列溢出、参考链已断，需要立刻向设备请求一个 IDR。
    /// 订阅方（参考采集模式）用 onRequestIDRFrame 上行，把冻结窗口从
    /// 「等设备自然 IDR（最多 iFrameInterval=1000ms）」缩短到一次往返。</summary>
    public event Action? KeyframeNeeded;
    /// <summary>等 IDR 超时且反复催帧无效 —— 参考链已死，上层应重建流（不是断流，别误判成设备离线）。</summary>
    public event Action? KeyframeStarvation;
    /// <summary>GATE-05：设备端本地注入探针结果回传。</summary>
    public event Action<InjectReport>? InjectResultReceived;

    public DeviceSession(string id, string host, int port, byte role, byte deviceId,
        Func<byte[], Task> send, int localPort = 0)
    {
        Id = id;
        Host = host;
        Port = port;
        LocalPort = localPort;
        Role = role;
        _send = send;
        // seqBase 按设备号分区，避免三台设备 ACK 串台
        _control = new ReliableControlChannel(send, 1_000_000 + deviceId * 100_000);
        _assembler.FrameReady += OnFrameReady;
        Decoder.FrameDecoded += f => Renderer.Present(f);
        _feedRunning = true;
        _feedThread = new Thread(FeedLoop)
        {
            IsBackground = true,
            Name = $"decode-{id}",
            Priority = ThreadPriority.AboveNormal
        };
        _feedThread.Start();
    }

    /// <summary>解码线程：保序消费全部排队帧，绝不跳过中间 P 帧。</summary>
    private void FeedLoop()
    {
        while (_feedRunning)
        {
            _feedSignal.WaitOne(200);
            // 一次唤醒把队列排空：AutoResetEvent 只置位一次，若只取一帧，
            // 其余帧要等 200ms 超时才能继续，会人为制造卡顿。
            while (true)
            {
                uint id = 0;
                byte[]? data = null;
                lock (_feedGate)
                {
                    if (_decodeQueue.Count > 0)
                    {
                        (id, data) = _decodeQueue.Dequeue();
                    }
                }
                if (data is null)
                {
                    break;
                }
                try
                {
                    Decoder.Feed(id, data);
                }
                catch
                {
                    // 单帧解码失败不影响后续帧
                }
            }
            // 队列已排空：检查是否卡在「等 IDR」，卡住就催帧/判定重建（历史技术债兜底）。
            // 循环本身 200ms 必醒一次（WaitOne 超时），所以这里天然是 200ms 巡检。
            RescueKeyframeIfStalled();
        }
    }

    /// <summary>握手：第一个 CONTROL 包必须由 PC 先发出，否则设备端的入站流会被防火墙丢弃。</summary>
    // 握手是否已完成（成功或失败都算完成）。双路并行下上层要据此决定
    // 何时下发 CMD_STREAM —— 采集桥是串行起的，第二台比第一台晚好几秒，
    // 用固定延时会让它还在握手就被要求推流（实测 ok=False）。
    private int _handshakeDone;
    public bool HandshakeDone => Volatile.Read(ref _handshakeDone) != 0;

    /// <summary>
    /// 进行中的握手任务。**必须与后续 CMD_STREAM 串行**：
    /// ReliableControlChannel 只有一个 _pending 等待槽，两条 SendAsync 并发时
    /// 后发者覆盖前者 → ACK 串台 → 必有一条超时失败（双路实测 device-2 ok=False 的根因）。
    /// </summary>
    public Task<bool>? HandshakeTask { get; set; }

    public async Task<bool> ConnectAsync()
    {
        Log?.Invoke($"[{Id}] 握手：先发 CONTROL(ROLE) → 等待 ACK");
        bool ok = await _control.SendAsync(Wire.EncodeRole(Role, 1));
        Log?.Invoke($"[{Id}] 握手 {(ok ? "成功" : "失败")}，RTO={_control.RtoMs}ms");
        Interlocked.Exchange(ref _handshakeDone, 1);
        return ok;
    }

    public Task<bool> StartStreamAsync() => _control.SendAsync(Wire.EncodeStreamCtl(true));
    public Task<bool> StopStreamAsync() => _control.SendAsync(Wire.EncodeStreamCtl(false));
    public Task<bool> SendInputAsync(InputEvent ev) =>
        _control.SendAsync(Wire.Control(Cmd.Input, Wire.EncodeInput(ev)));
    public Task<bool> SendNavAsync(byte key) => _control.SendAsync(Wire.EncodeNav(key));
    public Task<bool> SendClipTextAsync(string text) =>
        _control.SendAsync(Wire.Control(Cmd.ClipText, Wire.EncodeClipText(text)));
    public Task<bool> SetLockStateAsync(bool locked) =>
        _control.SendAsync(Wire.EncodeLockState(locked));

    public async Task<bool> SendClipImageAsync(byte[] png)
    {
        bool all = true;
        foreach (byte[] part in Wire.EncodeClipImage(png))
        {
            all &= await _control.SendAsync(part);
        }
        return all;
    }

    /// <summary>启用 PC→设备 剪贴板监听（双向的另一半由 HandlePacket 处理）。</summary>
    public void EnableClipboard()
    {
        _clipboard ??= new ClipboardBridge(p => _control.SendAsync(p));
        _clipboard.Start();
    }

    public void HandlePacket(HwpHeader header, byte[] data, IPEndPoint remote)
    {
        ReadOnlySpan<byte> payload = HwpPacket.Payload(data, header);
        switch (header.Channel)
        {
            case Hwp.ChAck:
                double rtt = Hwp.Elapsed(Hwp.Now(), header.TimestampMs);
                _control.ObserveRtt(rtt);
                _control.OnAck(header.Seq);
                break;
            case Hwp.ChHeartbeat:
                Heartbeats++;
                ObserveDeviceClock(header.TimestampMs);
                // 回 ACK 让设备端也能测 RTT，同时维持双向保活
                _ = _send(HwpPacket.Build(Hwp.ChAck, Hwp.FlagIsAck, header.Seq,
                    header.TimestampMs, ReadOnlySpan<byte>.Empty));
                break;
            case Hwp.ChVideo:
                _videoStats.OnPacket(header.Seq);
                break;
            case Hwp.ChStream:
                _videoStats.OnPacket(header.Seq);
                // 设备不发心跳，只能用视频包标定时钟：取历史最小 (PC收包时刻 - 设备发送时刻)，
                // 它≈最小时延，用它做 offset 后算出的端到端延迟才有意义。
                ObserveDeviceClock(header.TimestampMs);
                _lastStreamTsAbs = UnwrapMs(header.TimestampMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _assembler.OnPacket(header, payload);
                break;
            case Hwp.ChControl:
                HandleControl(header, payload);
                break;
        }
    }

    private void HandleControl(HwpHeader header, ReadOnlySpan<byte> payload)
    {
        if ((header.Flags & Hwp.FlagNeedAck) != 0)
        {
            _ = _send(HwpPacket.Build(Hwp.ChAck, Hwp.FlagIsAck, header.Seq,
                header.TimestampMs, ReadOnlySpan<byte>.Empty));
        }
        if (payload.Length == 0)
        {
            return;
        }
        ControlIn++;
        byte cmd = payload[0];
        ReadOnlySpan<byte> body = payload[1..];
        switch (cmd)
        {
            case Cmd.ClipText:
                var (hash, text) = Wire.DecodeClipText(body);
                _clipboard?.ApplyRemoteText(text, hash);
                Log?.Invoke($"[{Id}] 剪贴板文本 ← 设备 ({text.Length} 字符)");
                break;
            case Cmd.Resolution:
                if (body.Length >= 5)
                {
                    ushort w = (ushort)(body[0] | (body[1] << 8));
                    ushort h = (ushort)(body[2] | (body[3] << 8));
                    byte folded = body[4];
                    // 设备每 2s 重报一次分辨率（为让中途接入的 PC 也能拿到尺寸）。
                    // 必须先去重：无条件触发会让订阅方每次重建解码器，画面永远起不来。
                    // 2026-11-08 实测：35s 内重报 ~17 次 → decode.frames=0 / errors=7（distinctFrames=473 证明包是好的）。
                    if (w == Width && h == Height && folded == Folded)
                    {
                        break;
                    }
                    Width = w;
                    Height = h;
                    Folded = folded;
                    ResolutionChanged?.Invoke(Width, Height, Folded);
                    Log?.Invoke($"[{Id}] 分辨率 {Width}x{Height} folded={Folded}（编码器需重建）");
                }
                break;
            case Cmd.Role:
                if (body.Length > 0)
                {
                    Role = body[0];
                }
                break;
            case Cmd.LockState:
                Locked = body.Length > 0 && body[0] != 0;
                LockStateChanged?.Invoke(Locked);
                break;
            case Cmd.InjectResult:
                {
                    InjectReport ir = Wire.DecodeInjectResult(body);
                    InjectResultReceived?.Invoke(ir);
                    Log?.Invoke($"[{Id}] 注入 ← 设备 ok={ir.Ok} code={ir.ErrCode} " +
                                $"stage={ir.Stage} {ir.ElapsedUs / 1000.0:F2}ms syscap={ir.Syscap}");
                    break;
                }
            case Cmd.Nav:
            case Cmd.Input:
            case Cmd.Stream:
            case Cmd.ClipImage:
                break;
        }
    }

    private void OnFrameReady(uint frameId, byte[] annexB)
    {
        if (_lastStreamTsAbs > 0 && _clockOffsetMs != long.MinValue)
        {
            double ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastStreamTsAbs - _clockOffsetMs;
            if (ms >= 0 && ms < 60_000)
            {
                LastFrameLatencyMs = ms;
                Interlocked.Add(ref _latencySumUs, (long)(ms * 1000));
                Interlocked.Increment(ref _latencyCount);
            }
        }
        StreamFrameReady?.Invoke(frameId, annexB);
        bool isKey = H264Sps.IsKeyFrame(annexB);
        bool overshoot = false;
        lock (_feedGate)
        {
            // ⚠ 过载时**绝不能只丢队尾新帧**。2026-11-09 真机实测：那样做会让
            // 后续 P 帧的参考帧（被丢掉的那一帧）从未进入解码器，参考链错位，
            // 画面出现大面积红绿紫块 + 马赛克（用户现场截图即此）。
            // 我曾用「丢新帧」替换「清空队列」，把冻结换成了花屏，更糟，已回退。
            //
            // 正确做法：H.264 P 帧依赖前一帧，丢弃任意中间帧都会断链；唯一安全的
            // 丢弃点是 IDR。所以溢出时丢整段、等到下一个 IDR 重建参考链。
            // 为了不让用户等满一个 iFrameInterval（设备端 1000ms），溢出瞬间
            // 立即通过上行通道请求一次 IDR（见 RequestKeyframe 事件）。
            if (_awaitKeyframe)
            {
                if (!isKey)
                {
                    DecodeQueueDropped++;
                    return;
                }
                _awaitKeyframe = false;
                _rescueCount = 0;
            }
            if (_decodeQueue.Count >= MaxDecodeQueue)
            {
                DecodeQueueDropped += _decodeQueue.Count + 1;
                _decodeQueue.Clear();
                _awaitKeyframe = true;
                _awaitKeyframeSinceMs = Environment.TickCount64;
                _lastKeyframeRequestMs = _awaitKeyframeSinceMs;
                _rescueCount = 0;
                overshoot = true;
            }
            else
            {
                _decodeQueue.Enqueue((frameId, annexB));
            }
        }
        if (overshoot)
        {
            // 立刻催 IDR，把「冻结到设备自然 IDR」的最坏 1 秒缩短为一次往返。
            KeyframeNeeded?.Invoke();
        }
        _feedSignal.Set();
    }

    /// <summary>
    /// 等 IDR 的兜底巡检（由解码线程在队列排空时每 ~200ms 调一次）。
    /// 只在「我们自己正在丢帧」时生效，不做任何断流判定。
    /// </summary>
    private void RescueKeyframeIfStalled()
    {
        if (!_awaitKeyframe)
        {
            return;
        }
        long now = Environment.TickCount64;
        long waited = now - _awaitKeyframeSinceMs;
        if (waited < KeyframeRescueMs || now - _lastKeyframeRequestMs < KeyframeRequestGapMs)
        {
            return;
        }
        _lastKeyframeRequestMs = now;
        _rescueCount++;
        KeyframeStalls++;
        KeyframeNeeded?.Invoke();
        if (_rescueCount >= KeyframeStarvationHits)
        {
            // 约 4.7s 都没等到 IDR：判定参考链已死，交给上层重建流；
            // 计时基线重置，避免每 400ms 反复抛事件。
            _rescueCount = 0;
            _awaitKeyframeSinceMs = now;
            Log?.Invoke($"[{Id}] 等 IDR {waited}ms 无进展（累计 {KeyframeStalls} 次催帧），请求重建解码链");
            KeyframeStarvation?.Invoke();
        }
        else if (_rescueCount <= 2 || _rescueCount % 4 == 0)
        {
            // 400ms 一次全记会刷屏，只记前两次与每 4 次
            Log?.Invoke($"[{Id}] 等 IDR {waited}ms，再次催帧（第 {_rescueCount} 次）");
        }
    }

    public (long Frames, long Dropped, long Bytes, long Packets, long VideoLost, int RtoMs) Stats() =>
        (_assembler.FramesCompleted, _assembler.FramesDropped, _assembler.BytesReceived,
         _assembler.PacketsReceived, _videoStats.Lost, _control.RtoMs);

    /// <summary>重组诊断：收到过多少个不同 frameId、最近一包的分片数。</summary>
    public (long DistinctFrames, int LastFragCount) AssemblerDiag() =>
        (_assembler.DistinctFrames, _assembler.LastFragCount);

    public void Dispose()
    {
        _clipboard?.Dispose();
        _assembler.Reset();
        _feedRunning = false;
        _feedSignal.Set();
        _feedThread?.Join(500);
        _feedSignal.Dispose();
    }
}

/// <summary>
/// ≤3 台设备管理。所有会话共用一个 UDP 端点，按来源 IP 派发；
/// 切换标签只改 ActiveId，不拆 UDP、不停止推流（NEXT-05 验收点）。
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly HwpEndpoint _endpoint;
    private readonly Dictionary<string, DeviceSession> _sessions = new();
    private readonly object _gate = new();

    public string? ActiveId { get; private set; }
    public const int MaxDevices = 3;

    public event Action<string>? Log;

    public SessionManager(int localPort = Hwp.LocalPort)
    {
        _endpoint = new HwpEndpoint(localPort);
        // 闭包带上「这个包是从哪个本机端口收上来的」—— 双路并行下这是唯一可靠的路由键
        // （两个采集桥都从 127.0.0.1 发、源端口各不相同且是临时的）。
        int mainPort = localPort;
        _endpoint.PacketReceived += (h, d, r) => Dispatch(h, d, r, mainPort);
        _endpoint.Start();
    }

    /// <summary>
    /// 再开一个监听端点，绑另一个本机端口。
    /// 双路并行必需：每台设备各有一条采集桥、各自往不同的本机端口发流
    /// （9010 / 9011），若只有一个端点，第二台的包会投进第一台会话。
    /// </summary>
    public SessionManager AddEndpoint(int localPort)
    {
        var ep = new HwpEndpoint(localPort);
        ep.PacketReceived += (h, d, r) => Dispatch(h, d, r, localPort);
        ep.Start();
        lock (_gate)
        {
            _extraEndpoints.Add(ep);
        }
        return this;
    }

    private readonly List<HwpEndpoint> _extraEndpoints = new();

    /// <summary>
    /// 新增会话。**同一 host:port 只允许一个会话** —— 重复添加会返回已有实例。
    ///
    /// 为什么必须去重（2026-11-09 实测踩坑）：UI 的「连接设备」每点一次就 Add 一个 device-N，
    /// 用户连不上就会反复点，于是出现 device-1/2/3 指向同一 IP。后果是灾难性的：
    ///   ① Dispatch 按 host 匹配，三个同 IP 会话只会把包投给**第一个**，另外两个永远黑屏
    ///      —— 用户看到的是「切过去只有一张静止的图」；
    ///   ② 三个会话各自周期性发 CMD_STREAM，设备端收到「已在推流又收到 CMD_STREAM」，
    ///      判定为「PC 重新接入」并重放缓存 IDR，形成每秒几十次的死循环，
    ///      真实数据发不出去（实测「已发 182 包 / 重放 74 次」）。
    /// </summary>
    public DeviceSession Add(string id, string host, int port = Hwp.DevicePort,
        byte role = DeviceRole.Host, int localPort = 0)
    {
        if (localPort <= 0)
        {
            localPort = _endpoint.LocalPort;
        }
        lock (_gate)
        {
            // 同 host:port 已存在 → 直接复用，不新建（否则必然引发上面的双会话故障）
            foreach (var existing in _sessions.Values)
            {
                if (existing.Host == host && existing.Port == port)
                {
                    return existing;
                }
            }

            if (_sessions.Count >= MaxDevices)
            {
                throw new InvalidOperationException($"最多 {MaxDevices} 台设备并发");
            }
            var remote = new IPEndPoint(IPAddress.Parse(host), port);
            // 选对发送端点：优先用「这台设备自己的收流端点」（localPort），
            // 这样控制包的 ACK 会回到同一个端点上，路由闭合不会串台。
            HwpEndpoint ep = _endpoint.LocalPort == localPort
                ? _endpoint
                : (_extraEndpoints.FirstOrDefault(e => e.LocalPort == localPort) ?? _endpoint);
            Func<byte[], Task> send = packet => ep.SendAsync(packet, remote);
            var s = new DeviceSession(id, host, port, role, (byte)(_sessions.Count + 1), send, localPort);
            s.Log += msg => Log?.Invoke(msg);
            _sessions[id] = s;
            ActiveId ??= id;
            return s;
        }
    }

    public bool Switch(string id)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(id))
            {
                return false;
            }
            ActiveId = id; // 只切呈现，不拆流
            return true;
        }
    }

    public DeviceSession? Active()
    {
        lock (_gate)
        {
            return ActiveId is not null && _sessions.TryGetValue(ActiveId, out var s) ? s : null;
        }
    }

    /// <summary>
    /// 按地址查找已有会话（host 匹配即可，兼容只写 IP 与写 IP:port 两种输入）。
    /// UI 用它实现「重复点连接 → 切回原会话」而不是新建。
    ///
    /// ⚠ 只传一个参数时行为不变（严格相等）。多设备并行下每台 PC 各有自己的
    /// 本地 UDP 端口（9010/9011…），调用方应带上自己的 localPort 精确匹配，
    /// 否则两台回环会话（Host 都是 127.0.0.1）会被误判成同一台。
    /// </summary>
    public DeviceSession? FindByHost(string host, int? localPort = null)
    {
        lock (_gate)
        {
            return _sessions.Values.FirstOrDefault(
                s => s.Host == host && (localPort is null || s.Port == localPort.Value));
        }
    }

    public IReadOnlyCollection<DeviceSession> All()
    {
        lock (_gate)
        {
            return _sessions.Values.ToArray();
        }
    }

    /// <summary>
    /// 把收到的包投递给对应会话。
    /// **必须按 host + port 一起匹配**：只比 host 的话，一旦出现多个同 IP 会话，
    /// FirstOrDefault 会把所有包都喂给第一个，其余会话永远收不到数据（2026-11-09 踩坑）。
    /// 配合 Add 的同 host:port 去重，正常情况下这里只会命中唯一会话。
    ///
    /// ⚠ 退回「按 host 匹配」这一条，对**回环采集桥**（host 都是 127.0.0.1、只有
    /// 本地端口不同）是致命的：两台设备的包会全部被投给第一台会话，表现是
    /// 「切到第二台只看到一张静止画面」。所以这里按 (host, port) 精确定位，
    /// 精确定位不到就**不投**，交给会话自己的超时机制去发现（宁可这台没画面，
    /// 也不能把包喂错会话让两台都花）。真正的设备直连场景（host 是设备 IP）
    /// 源端口本来就不固定，那时才退回 host 匹配。
    /// </summary>
    private void Dispatch(HwpHeader header, byte[] data, IPEndPoint remote, int recvPort)
    {
        DeviceSession? target;
        lock (_gate)
        {
            string fromHost = remote.Address.ToString();
            int fromPort = remote.Port;

            // 1) 控制面 ACK：采集桥的 _rx 绑在 9020/9021，源端口 = 会话的 Port，能精确命中。
            target = _sessions.Values.FirstOrDefault(
                s => s.Host == fromHost && s.Port == fromPort);

            // 2) 视频面：采集桥出流用的是临时源端口，只能按「本机哪个端点收上来的」认。
            target ??= _sessions.Values.FirstOrDefault(
                s => s.Host == fromHost && s.LocalPort == recvPort);

            if (target is null && !IPAddress.Loopback.Equals(remote.Address))
            {
                // 容错：真实设备换了发送源端口时，退回按 host 匹配（仅非回环，避免双路串台）。
                target = _sessions.Values.FirstOrDefault(s => s.Host == fromHost);
            }
        }
        target?.HandlePacket(header, data, remote);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
            {
                s.Dispose();
            }
            _sessions.Clear();
        }
        _endpoint.Dispose();
        lock (_gate)
        {
            foreach (var ep in _extraEndpoints)
            {
                ep.Dispose();
            }
            _extraEndpoints.Clear();
        }
    }
}
