using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HarmOsWin.Transport;

namespace HarmOsWin.Core;

/// <summary>
/// 参考方案免授权采集（集成版）：设备端 libscreen_casting.z.so 扩展守护进程 →
/// gRPC ScrcpyService/onStart 裸 H.264 → Annex-B 切帧 → HWP CH_STREAM 发到本机客户端。
///
/// 为什么这样走：该扩展走 NativeStartScreenCopy，不经过 OH_AVScreenCapture，
/// 因此不会弹录屏授权框，也不需要安装/启动自有 HAP。对应参考项目
/// hos_scrcpy/bridge/native_stream.py 的 HosVideoStream，但用纯 BCL 实现，
/// 不依赖 Python / grpcio / NuGet 包，单文件发布形态不变。
///
/// 三处本项目真机实测修正（2026-11-08/09，勿按参考项目默认值改回去）：
///   ① so 用 6.8-20260801 —— 6.5/6.9 在本机通道就绪但永不出帧；
///   ② 真参数是 -frameRefreshInterval，参考项目写的 -frameRate 不生效；
///   ③ scale 1 / 20Mbps / iFrame 1000 / repeat 16 → 1600x2560；refresh=1 时设备
///     出 ~89fps（探针实测），但 MF_LOW_LATENCY 下 PC 单帧 Feed 约 18.3ms（~55fps），
///     89fps 会打爆解码队列并触发 _awaitKeyframe 丢到 IDR → 滑动周期性冻结。
///     故默认 refresh=20（--refresh 1 可还原 89fps 做 A/B），把输入压进消费能力内。
///
/// 生命周期：StartAsync() 起链路 → 后台推送 → StopAsync() 无条件清理
/// （cancel gRPC + fport rm + 杀扩展 daemon）。任何失败都必须调用 StopAsync，
/// 否则设备端会残留守护进程。
/// </summary>
public sealed class ReferenceCapture : IAsyncDisposable
{
    private const string RemoteSo = "/data/local/tmp/libscreen_casting.z.so";
    private const string ExtensionName = "libscreen_casting.z.so";
    private const string AbstractSocket = "localabstract:scrcpy_grpc_socket";
    private const int Chunk = Hwp.PayloadMax - Hwp.StreamMetaSize;   // 1176

    // 帧保持（仅兜底，默认关闭）：MF H.264 MFT 未设 MF_LOW_LATENCY 时内部有
    // 约 37 帧输出缓冲，真机实测设备静止 0.77fps 下完全停掉补帧会 0 帧出图、
    // 只补 5 秒则出 55 帧后冻结。MainWindow 探测到低延迟生效后不再启动本机制，
    // 静止画面由 WPF 上屏层自然保持；--hold on 可强制开启做 A/B 与回退。
    // ⚠ 绝不能循环重放整段缓存 GOP：那等于把历史画面倒带重播，静止期整屏
    // 反复跳变（实测差分 ≈95 的跳变 10 次 / 回跳 4 次），用户一操作真实帧
    // 涌入还会打断参考链导致花屏。只重复「最近的 IDR 单帧」——画面恒定、
    // 每帧都能独立解码、参考链始终处于复位状态。
    private const int HoldIdleMs = 400;      // 超过 400ms 无真实帧才判 IDLE 补帧
    private const int HoldIntervalMs = 16;   // ~60fps，维持 MF 输出流水
    private const int HoldMaxAus = 8;        // 只需保留最近 IDR 及其后少量 AU

    private readonly string _hdcPath;
    private readonly string _target;
    private readonly string _soPath;
    private readonly Action<string>? _log;
    private readonly Stream? _dump;   // 可选：录下设备端裸 Annex-B，供离线回放验证

    private CancellationTokenSource? _cts;
    private Task? _streamTask;
    private Task? _controlTask;
    private Task? _watchdogTask;
    private Task? _holdTask;     // IDLE 期帧保持（只重复 IDR 单帧）
    private readonly object _holdGate = new();
    private readonly List<byte[]> _holdGop = new();  // 从 IDR 起的一段连续 AU
    private int _needKeyframe;   // 1 = 参考链停在补帧 IDR，须等真实 IDR 才恢复转发
    // 补帧等待期主动催 IDR 的节流时间戳（Environment.TickCount64）。
    // 2026-11-13：原来这段只丢 P 帧、干等设备自然 IDR（实测 >2s），见下方 OnDeviceAu。
    private long _lastIdrRequestTicks;
    private const int HoldIdrRequestGapMs = 500;   // 每 500ms 最多催一次，防止请求风暴
    private readonly bool _holdEnabled;
    private readonly int _frameRefreshInterval;
    private readonly int _frameRate;   // -frameRate，<=0 时不传
    private readonly int _bitRate;     // -bitRate，字节/秒
    private readonly int _scale;       // -scale，设备端缩小倍数（1=原始）
    private long _lastAuTicks = Stopwatch.GetTimestamp();
    private GrpcH2Client? _h2;   // 明文 HTTP/2 gRPC 连接（onStart 与 IDR 共用）
    private UdpClient? _rx;      // 绑定 DevicePort：收 PC 控制命令并回 ACK
    private UdpClient? _tx;      // 发 HWP 流到 LocalPort
    // 本机 HWP 端口。单设备时就是 Hwp.LocalPort/DevicePort；多设备并行时每台
    // 各占一对（9010/9020、9011/9021…）—— 因为 SessionManager 里所有会话共用
    // 一个 UDP 端点、按「源 IP:端口」派发，端口撞车会让两台画面抢同一个会话。
    private readonly int _localPort;
    private readonly int _devicePort;
    private int _fportPort;
    private bool _started;
    private bool _disposed;

    private uint _seq = 1000;
    private uint _frameId;
    private int _width = 1600;   // 初值按本机平板实测；收到 SPS 后由 H264Sps 校正
    private int _height = 2560;
    private long _framesSent;
    private long _packetsSent;
    private long _lastChunkTicks = Stopwatch.GetTimestamp();
    // 1 = 启动后至少收到过一帧 gRPC 数据；看门狗据此在「等首帧」阶段用快速轮询。
    private int _gotAnyChunk;
    // 上次「主动重建 gRPC 流」的时刻，3s 内只允许一次，防重建风暴
    private long _lastResyncMs;
    // 1 = 判定设备端采集 daemon 已死，重连前必须先重启它（只重连 socket 会永远连不上）
    private int _needDeviceRestart;
    // 上次重启设备端流水线的时刻，20s 内只允许一次，防「重启风暴」
    private long _lastDeviceRestartMs;
    // IDR 探活宽限：请求 IDR 后必须在这段时间内看到至少一个 chunk（IDR 是强制整帧）
    private const int ProbeGraceMs = 1200;

    // ── 同一台设备上的采集桥互斥（双路并行必需）──
    // 设备端的扩展守护进程绑定**固定**抽象 socket（localabstract:scrcpy_grpc_socket），
    // 同一 target 上只能存在一个。而 StartAsync 第 1 步的 KillDaemonsAsync 会把
    // 该设备上所有 --extension-name 的 daemon 全杀掉 —— 两台的采集桥若同时启动，
    // 后启动的那台会把先启动那台的 daemon 杀掉，先者链路立刻断流。
    // 所以「同一设备」的桥必须共用一个 daemon，用引用计数 + 锁串起来：
    // 第一台负责 kill → deploy → start，其余台等它就绪后直接复用。
    private static readonly Dictionary<string, int> DeviceRefs = new();
    private static readonly Dictionary<string, Task> DeviceReady = new();
    private static readonly object DeviceGate = new();

    /// <summary>标记本 target 即将占用设备端采集 daemon。返回 true = 本实例负责拉起。</summary>
    internal static bool BeginDevice(string target, string hdcPath, Action<string> log)
    {
        lock (DeviceGate)
        {
            DeviceRefs.TryGetValue(target, out int n);
            DeviceRefs[target] = n + 1;
            if (n == 0)
            {
                DeviceReady.Remove(target);
                return true;   // 第一个：负责 kill + deploy + start
            }
            // 后续实例：等设备上已有 daemon 就绪（最多 20s），然后复用。
            Task wait = WaitDeviceAsync(target, hdcPath, log);
            DeviceReady[target] = wait;
            return false;
        }
    }

    /// <summary>释放本 target 的引用；归零时把设备端 daemon 一并清掉（进程退出路径）。</summary>
    internal static void EndDevice(string target)
    {
        lock (DeviceGate)
        {
            if (DeviceRefs.TryGetValue(target, out int n) && n > 1)
            {
                DeviceRefs[target] = n - 1;
                return;
            }
            DeviceRefs.Remove(target);
            DeviceReady.Remove(target);
        }
    }

    private static async Task WaitDeviceAsync(string target, string hdc, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        string socketName = AbstractSocket[(AbstractSocket.IndexOf(':') + 1)..];
        try
        {
            while (sw.ElapsedMilliseconds < 20_000)
            {
                await Task.Delay(300).ConfigureAwait(false);
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = hdc,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.StartInfo.ArgumentList.Add("-t");
                p.StartInfo.ArgumentList.Add(target);
                p.StartInfo.ArgumentList.Add("shell");
                p.StartInfo.ArgumentList.Add($"grep -c {socketName} /proc/net/unix");
                if (!p.WaitForExit(4000))
                {
                    try { p.Kill(true); } catch { }
                    continue;
                }
                string text = (await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();
                if (text.Length > 0 && text != "0" &&
                    !text.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    log($"[采集桥] 复用设备上已有的扩展守护进程（{sw.ElapsedMilliseconds}ms 就绪）");
                    return;
                }
            }
            log("[采集桥] 等设备端 daemon 就绪超时（20s），仍继续尝试");
        }
        catch (Exception ex)
        {
            log($"[采集桥] 等待设备 daemon 异常：{FirstLine(ex.Message)}");
        }
    }

    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public bool IsRunning => _started && !_disposed;

    /// <param name="hdcPath">hdc.exe 绝对路径。</param>
    /// <param name="target">无线调试 target，必须带端口，例如 192.168.1.100:38803。</param>
    /// <param name="soPath">本地 libscrcpy_server_unix_6.8-20260801.z.so 路径（嵌入资源释放后）。</param>
    /// <param name="enableHold">是否启用帧保持兜底。MF 低延迟生效时默认关闭；
    /// --hold on 可强制开启做 A/B 判别，--hold off 可强制关闭。</param>
    /// <param name="frameRefreshInterval">设备端最小出帧间隔（ms），--refresh 可覆盖。
    /// ⚠ 2026-11-09 真机实测：`/proc/&lt;pid&gt;/cmdline` 确认该参数确实传到设备，但
    /// 滑动等动态场景出帧率仍为 ~90fps（屏幕 VSYNC 上限），只影响静止时的重复帧
    /// 间隔。故它**不能**用来把动态输入压进 PC 解码吞吐（实测 ≈56fps）。</param>
    /// <param name="frameRate">-frameRate（fps），参考项目 native_stream.py 用它。
    /// &lt;= 0 时不传该参数（保持现状）。本机 so 同时导出 frameRate 与
    /// frameRefreshInterval 两个解析项，真实语义由真机 A/B 判定。</param>
    /// <param name="bitRate">-bitRate，单位字节/秒。实测 20_000_000 时折算
    /// 1600x2560 只有 0.041 bit/pixel（文字界面通常需 0.08~0.15，明显偏低），
    /// 这是「画质有点差」的直接来源之一。默认 40_000_000（约 0.08 bpp）。
    /// 注意参考项目 native_stream.py:71-77 的注释提醒：该参数走 MB 通道时
    /// 历史版本会 int 溢出，故必须传「字节/秒」量级的值，不能传 MB 数。</param>
    /// <param name="scale">-scale，设备端出图前的缩小倍数（1=原始，数值越大越小）。
    /// 2026-11-10 真机 A/B 决定性结果（14s 滑动窗口，四组同一场景）：
    /// | 分辨率 | 码率 | 单帧解码 | presentFps | 排队丢帧 | 实际码流 |
    /// | 1600x2560 | 40M | 18.12ms | 38 | 214 | 20.95Mbps |
    /// | 800x1280  | 40M |  8.25ms | 67 |   0 | 24.76Mbps |
    /// | 800x1280  | 20M |  5.90ms | 66 |   0 | 11.98Mbps |
    /// | 800x1280  | 12M |  4.91ms | 67 |   0 |  7.25Mbps |
    /// 原因：平板 1600x2560（4.10M 像素），而 PC 窗口画面区仅约 442x708
    /// （0.31M 像素）——真正能显示的只占 **7.6%**，其余 92% 白传白解码。
    /// 降到 800x1280 后解码量剩 25%，`queueDropped` 从 214 直接归零，
    /// 帧率 38→67，且相对显示需求仍有 3.3 倍像素余量。
    /// 默认 **2**（800x1280）：2026-11-10 三档真机实测最优点 —— 解码 5.9ms、
    /// 滑动零丢帧，且源内彩色小字笔画 2px（devscale 3 只有 1px，4:2:0 后红字崩 47%）。
    /// --devscale 1 画质再高 14% 但解码 18.1ms、排队丢 73 帧；--devscale 3 更省但小字发糊。</param>
    /// <param name="localPort">本机 HWP 收流端口（发流目标）。默认 Hwp.LocalPort(9010)；
    /// 多设备并行时每台必须不同，否则两台画面会投到同一个会话上。</param>
    /// <param name="devicePort">本机控制面监听端口（收设备回 ACK / 发 CMD）。
    /// 默认 Hwp.DevicePort(9020)，同样需按设备区分。</param>
    public ReferenceCapture(string hdcPath, string target, string soPath,
        Action<string>? log = null, bool enableHold = false, int frameRefreshInterval = 20,
        int frameRate = 0, int bitRate = 20_000_000, int scale = 2,
        int localPort = Hwp.LocalPort, int devicePort = Hwp.DevicePort)
    {
        _hdcPath = hdcPath;
        _target = target;
        _soPath = soPath;
        _log = log;
        _localPort = localPort;
        _devicePort = devicePort;
        _holdEnabled = enableHold;
        _frameRefreshInterval = frameRefreshInterval > 0 ? frameRefreshInterval : 20;
        _frameRate = frameRate;
        _bitRate = bitRate > 0 ? bitRate : 20_000_000;
        _scale = scale is >= 1 and <= 5 ? scale : 2;
        // HARMOSWIN_REF_DUMP=<path> 时把每条 gRPC 消息里的裸 H.264 顺序写盘，
        // 便于用 tmp/client_replay.py 在设备静止时也能独立验证「收到的码流」
        // 是否是可解码的真实画面。不设置时零开销。
        string? dumpPath = Environment.GetEnvironmentVariable("HARMOSWIN_REF_DUMP");
        if (!string.IsNullOrWhiteSpace(dumpPath))
        {
            try
            {
                _dump = File.Create(dumpPath);
            }
            catch
            {
                // 录盘失败不影响采集
            }
        }
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_started || _disposed)
        {
            return _started;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (!File.Exists(_soPath))
            {
                throw new FileNotFoundException("投屏扩展 so 不存在", _soPath);
            }

            // 同一台设备只允许一个扩展 daemon（抽象 socket 固定）。第一台负责
            // 清理 + 推送 + 启动，后续台等它就绪后复用 —— 否则后启动的会把先启动的
            // daemon 杀掉，先者链路立刻断流（双路并行的核心冲突点）。
            bool ownsDaemon = BeginDevice(_target, _hdcPath, Log);
            try
            {
                // 先确保 hdcd server 常驻：这台机器上 server 不会自动常驻，
                // 每次 hdc 调用都要现拉一次，是 WPF 里随机卡死的主因（2026-11-15 实测）。
                HdcRunner.EnsureServer(_hdcPath, Log);
                Log($"tconn {_target}");
                await EnsureTconnAsync(_cts.Token).ConfigureAwait(false);

                if (ownsDaemon)
                {
                    // 1) 杀旧扩展守护进程。本机 ROM 的 pkill -f 实测无效：连跑 6 次测试
                    //    后设备端残留 6 个 singleness daemon，localabstract 只有第一个
                    //    能绑定，fport 实际连到最老的死 daemon → 后面几轮 0 帧。
                    //    参考项目 _kill_daemon_pids() 的做法是 ps -ef 解析 PID 再 kill -9。
                    Log("采集① 清理残留扩展 daemon…");
                    await KillDaemonsAsync().ConfigureAwait(false);
                    Log("采集① 完成");
                    // 不带 CancellationToken：带 token 的 Delay 在取消时会抛
                    // OperationCanceledException 并被上层当作「启动失败」静默吞掉，
                    // 真实原因就被掩盖了（2026-11-15 排查中发现日志断在这里）。
                    await Task.Delay(400).ConfigureAwait(false);
                    Log("采集① 等待 400ms 完成");

                    // 2) so 部署（md5 一致就跳过，避免每次启动都传 248KB）
                    Log("采集② 部署扩展 so…");
                    await DeploySoAsync().ConfigureAwait(false);
                    Log("采集② 完成");
                }
                else
                {
                    Log("[采集桥] 本机已有实例拉起该设备的采集 daemon，直接复用（不重复 kill/deploy）");
                }

            // 3) 起扩展守护进程。整条命令重定向到 /dev/null，否则后台进程会一直
            //    占着 hdc shell 的 stdout 管道，hdc 命令不返回（实测踩过）。
            //    -frameRate 仅在显式指定时附加（--framerate），默认不发，避免改变
            //    已验证可用的启动命令。
            if (ownsDaemon)
            {
                string rateArg = _frameRate > 0 ? $"-frameRate {_frameRate} " : "";
                // 后台化三件套，缺一条 hdc 就会卡到 8s 超时（2026-11-15 实测）：
                //   nohup + `</dev/null` 断开 stdin、`>/dev/null 2>&1` 断开 stdout/stderr、
                //   末尾 `echo` 给 hdc 一个立即返回的输出。
                string startCmd =
                    "nohup /system/bin/uitest start-daemon singleness " +
                    $"--extension-name {ExtensionName} -scale {_scale} {rateArg}-frameRefreshInterval {_frameRefreshInterval} " +
                    $"-bitRate {_bitRate} -p 5000 -iFrameInterval 1000 -repeatInterval 16 " +
                    "</dev/null >/dev/null 2>&1 & echo DAEMON_STARTED";
                Log("采集③ 拉起扩展 daemon…");
                // 这条命令的超时属预期：daemon 常驻会占住设备端 shell 句柄，
                // hdc 一直等到它释放才返回（控制台 0.3s、WPF 里会卡到超时）。
                // 真正的判据是后面的 WaitDaemonReadyAsync（探测 socket 是否 listen）。
                await HdcAsync("-t", _target, "shell", startCmd).ConfigureAwait(false);
                Log("采集③ 已下发启动命令，等 socket 就绪…");
                await WaitDaemonReadyAsync(_cts.Token).ConfigureAwait(false);
            }
            else
            {
                // 复用已有 daemon：只需要确认它还绑着 socket（BeginDevice 已等过，
                // 这里是二次确认，daemon 若在等待期间死掉就自己拉一个）。
                await WaitDaemonReadyAsync(_cts.Token).ConfigureAwait(false);
            }

            // 不在启动阶段做 power-shell wakeup：真机实测加了这一句后 30s 出帧
            // 从 8 掉到 0（integrated4/5），疑为唤屏改变了采集目标状态。
            // 参考项目 _wait_first_chunk 的唤屏做法在本机不成立，不照搬。

            // 4) fport：tconn 定位目标 → 清陈旧规则 → 建新规则，三步必须在同一把锁里完成。
            //    原因见 HdcSerial：`hdc -t X fport ...` 的 -t 会被忽略，规则一律落到
            //    server 的「当前默认目标」，双路并行时会互相覆盖成乒乓。
            Log("采集④ 清理陈旧 fport…");
            _fportPort = FreeTcpPort();
            await CleanStaleFportsAsync().ConfigureAwait(false);
            Log($"采集④ 完成，端口 {_fportPort}");
            // fport add 可能因 hdcd 状态不干净而假成功（返回 OK 但本机 PORT 不 LISTEN）。
            // 按「换端口重来」处理，最多 3 次；tconn 只在第一轮做。
            // 每条命令显式构造成 string[]，不做数组拆解 —— 之前那段按 6 个一组切分，
            // 而 add 那条只有 5 个元素，直接下标越界（Index was outside the bounds）。
            bool fok = false;
            string fout = "";
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var cmds = new List<string[]>();
                if (attempt == 1)
                {
                    cmds.Add(new[] { "tconn", _target });
                }
                // 清旧规则：规则不存在会回 [Fail]，HdcBatchAsync 里不判 fail 标记，
                // 这里靠后面的 add 结果定成败。
                cmds.Add(new[] { "-t", _target, "fport", "rm",
                    $"tcp:{_fportPort}", AbstractSocket });
                cmds.Add(new[] { "-t", _target, "fport",
                    $"tcp:{_fportPort}", AbstractSocket });

                var (ok, outText) = await HdcBatchAsync(cmds.ToArray()).ConfigureAwait(false);
                fok = ok || outText.Contains("OK", StringComparison.OrdinalIgnoreCase);
                fout = outText;
                if (fok)
                {
                    if (attempt > 1)
                    {
                        Log($"采集④ fport 第 {attempt} 次成功");
                    }
                    break;
                }
                Log($"采集④ fport 第 {attempt} 次失败：{FirstLine(outText)}");
                if (attempt < 3)
                {
                    _fportPort = FreeTcpPort();
                    Log($"采集④ 换端口重试 {_fportPort}");
                }
            }
            if (!fok)
            {
                throw new InvalidOperationException($"fport 建立失败：{fout}");
            }

            // 5) UDP 收发分离。共用一个 socket 时接收超时会把 sendto 也变成
            //    带超时操作，实测把 60fps 压到十几帧。
            //    _rx 绑 _devicePort（每台一对，9020/9021…），_tx 用系统随机端口 ——
            //    两者必须分开：控制面的 ACK 由 _rx 收，流由 _tx 发。
            _rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, _devicePort));
            _tx = new UdpClient();
            try
            {
                _tx.Client.SendBufferSize = 8 * 1024 * 1024;
            }
            catch
            {
                // 个别系统拒绝过大值，保持默认
            }

            _started = true;
            _controlTask = Task.Run(() => ControlLoopAsync(_cts.Token));
            _streamTask = Task.Run(() => StreamLoopAsync(_fportPort, _cts.Token));
            _watchdogTask = Task.Run(() => WatchdogAsync(_cts.Token));
            if (_holdEnabled)
            {
                _holdTask = Task.Run(() => HoldLoopAsync(_cts.Token));
            }
            else
            {
                Log("帧保持未启用（MF 低延迟模式）：只转发设备真实帧，静止画面由上屏层保持");
            }
            Log($"参考采集已启动：gRPC 127.0.0.1:{_fportPort} → HWP :{_localPort}");
            return true;
            }
            catch
            {
                // 设备引用必须在这里退掉：否则该设备上的引用计数永远 >0，
                // 之后再没有任何采集桥敢去 kill 这个 daemon，重连会一直连到旧进程上。
                EndDevice(_target);
                throw;
            }
        }
        catch (Exception ex)
        {
            Log($"参考采集启动失败：{ex.Message}");
            await StopAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>停止并复原设备状态。幂等，可重复调用。</summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _started = false;
        // 设备引用先减：归零才允许杀设备端 daemon，否则会把仍在推流的那台一起干掉。
        bool lastOnDevice = ReleaseDeviceRef();
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }
        try
        {
            if (_streamTask is not null || _controlTask is not null ||
                _watchdogTask is not null || _holdTask is not null)
            {
                await Task.WhenAll(_streamTask ?? Task.CompletedTask,
                    _controlTask ?? Task.CompletedTask,
                    _watchdogTask ?? Task.CompletedTask,
                    _holdTask ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch
        {
            // 后台任务可能卡在阻塞式 socket 读上，Dispose 会让它抛异常退出
        }
        if (_h2 is not null)
        {
            await _h2.DisposeAsync().ConfigureAwait(false);
            _h2 = null;
        }
        if (_dump is not null)
        {
            try
            {
                _dump.Flush();
            }
            catch
            {
                // ignored
            }
            _dump.Dispose();
        }
        _rx?.Dispose();
        _tx?.Dispose();
        try
        {
            if (_fportPort > 0)
            {
                // 同样要先 tconn 定位目标：不带 -t 的 rm 删的是「当前默认目标」上的规则，
                // 双路下那个目标可能是另一台设备，删错了会把健康的一路踢断。
                await HdcBatchAsync(
                    new[] { "tconn", _target },
                    new[] { "-t", _target, "fport", "rm", $"tcp:{_fportPort}", AbstractSocket })
                    .ConfigureAwait(false);
            }
            // 只有本机该设备上的最后一个采集桥退出时才杀 daemon：
            // 双路并行下任一台的 daemon 都是两台共用，提前杀会让另一台立刻断流。
            if (lastOnDevice)
            {
                await KillDaemonsAsync().ConfigureAwait(false);
            }
            else
            {
                Log("[采集桥] 该设备仍有其他采集实例在推流，保留设备端守护进程");
            }
        }
        catch (Exception ex)
        {
            Log($"清理告警：{ex.Message}");
        }
        _cts?.Dispose();
        Log("参考采集已停止（fport 已清理）");
    }

    /// <summary>
    /// 释放本实例对设备端 daemon 的引用。返回 true 表示「本机该设备上已无采集实例」，
    /// 调用方才被允许杀 daemon。未登记过（例如构造后从未 StartAsync）时返回 true，
    /// 保持单设备旧行为。
    /// </summary>
    private bool ReleaseDeviceRef()
    {
        try
        {
            lock (DeviceGate)
            {
                if (!DeviceRefs.TryGetValue(_target, out int n))
                {
                    return true;
                }
                if (n > 1)
                {
                    DeviceRefs[_target] = n - 1;
                    return false;
                }
                DeviceRefs.Remove(_target);
                DeviceReady.Remove(_target);
                return true;
            }
        }
        catch
        {
            return true;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    // ── 设备侧部署 ──

    /// <summary>
    /// tconn 必须验证结果，不能只发不管（首启卡住原因之一）。
    /// hdc tconn 在目标写成纯 IP、或设备没开无线调试时照样退出码 0，
    /// 输出里只有一行 `[Fail]Connect failed`；以前把返回值丢掉，于是后续每条
    /// hdc 都报 `Not match target founded`，用户看到的就是「一直连不上」。
    /// </summary>
    private async Task EnsureTconnAsync(CancellationToken ct)
    {
        string last = "";
        for (int i = 1; i <= 3; i++)
        {
            var (ok, text) = await HdcAsync("tconn", _target).ConfigureAwait(false);
            last = text;
            if (ok && !text.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            {
                if (i > 1)
                {
                    Log($"tconn 第 {i} 次成功");
                }
                return;
            }
            Log($"tconn 未成功（第 {i} 次）：{FirstLine(text)}");
            if (i < 3)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException(
            $"无法连接 {_target}：tconn 连续 3 次失败（{FirstLine(last)}）。" +
            "请确认平板已开无线调试、与本机同一 Wi-Fi，端口变了可重新点一次「无线调试」。");
    }

    /// <summary>
    /// 轮询扩展守护进程是否已绑定抽象 socket，替代原来写死的 1.5s 等待。
    /// 首启慢、二次快（首启卡住原因之一）就是因为它：冷启动时 so 加载 + 编码器
    /// 初始化偶尔超过 1.5s，fport 抢在 daemon 就绪前建立，转发指向一个还没
    /// listen 的地址 → 握手必失败，用户只能退出重开（第二次 daemon 已就绪就快）。
    /// 现在改为「真的listen 了才往下走」，超时 3s 仍继续（不把启动卡死）。
    /// </summary>
    private async Task WaitDaemonReadyAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string socketName = AbstractSocket[(AbstractSocket.IndexOf(':') + 1)..];
        try
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            while (sw.ElapsedMilliseconds < 3000)
            {
                var (ok, text) = await HdcAsync("-t", _target, "shell",
                    $"grep -c {socketName} /proc/net/unix").ConfigureAwait(false);
                string trimmed = text.Trim();
                if (ok && trimmed.Length > 0 && trimmed != "0"
                    && !trimmed.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"扩展守护进程已就绪（{sw.ElapsedMilliseconds}ms）");
                    return;
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            Log($"扩展守护进程就绪探测超时（{sw.ElapsedMilliseconds}ms），仍继续建立 fport");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"就绪探测异常，按旧口径继续：{FirstLine(ex.Message)}");
        }
    }

    private static string FirstLine(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }

    /// <summary>
    /// 清理全部本扩展的残留 daemon。按参考项目 native_stream.py 的口径：
    /// ps -ef | grep singleness，限定含 --extension-name 与 libscreen_casting.z.so，
    /// 取 fields[1] 为 PID，合并成一条 kill -9（不依赖设备上的 pkill/awk/xargs）。
    /// </summary>
    private async Task KillDaemonsAsync()
    {
        // ⚠ 必须用 `ps -ef`，不能用 `ps -A`：本机实测（2026-11-15）
        //    ps -A | grep uitest  → 0 条（漏掉 shell 起的后台 daemon）
        //    ps -ef | grep uitest → 3 条（正确）
        // 用 ps -A 会导致 KillDaemonsAsync 永远认为「没有残留」，旧 daemon 不被清理，
        // 新 daemon 绑不上 localabstract socket，fport 转发到一个死进程 → 0 帧。
        // 只用**一级**管道（多级管道在 hdc shell 上会偶发挂住），过滤放在 C# 侧。
        // `|` 管道必须显式断开 stdin，否则设备端 grep 一直等 hdc 转发的空 stdin，
        // hdc 不返回、输出 0 字节（只在 WPF 复现，2026-11-15 实测）。
        // ⚠ 设备端**不要用任何管道**（2026-11-15 实测三条路都堵）：
        //   · `ps -ef | grep uitest` → 设备端 grep 等 hdc 转发的空 stdin，输出 0 字节；
        //   · `... < /dev/null` → 本机 ROM 的 SELinux 直接报 Permission denied；
        //   · 多级管道 → 同上。
        // 改为拿 ps -ef 全量（约 17KB），过滤放在 C# 侧。
        // 输出走流式落盘（HdcRunner），17KB 不会把任何缓冲写满。
        var (ok, text) = await HdcAsync("-t", _target, "shell", "ps -ef")
            .ConfigureAwait(false);
        if (!ok)
        {
            return;
        }
        var pids = new List<int>();
        foreach (string line in text.Split('\n'))
        {
            if (!line.Contains("extension-name", StringComparison.Ordinal) ||
                !line.Contains(ExtensionName, StringComparison.Ordinal) ||
                !line.Contains("uitest", StringComparison.Ordinal))
            {
                continue;
            }
            // ps -ef 的列是「USER PID PPID ...」，PID 在**第 2 列**（f[1]）。
            // 用 ps -A 时才在第 1 列；这里已确认走 ps -ef，必须取 f[1]。
            string[] f = line.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && int.TryParse(f[1], out int pid) && pid > 1)
            {
                pids.Add(pid);
            }
        }
        if (pids.Count == 0)
        {
            return;
        }
        await HdcAsync("-t", _target, "shell",
            "kill -9 " + string.Join(" ", pids)).ConfigureAwait(false);
        Log($"清理残留扩展守护进程 {pids.Count} 个：{string.Join(",", pids)}");
    }

    /// <summary>
    /// 运行期自愈：重启设备端采集流水线（杀残留 daemon → 重新 start-daemon →
    /// 等 listen → 重建 fport）。只有 MarkDeviceDead() 判死之后才会被调用。
    /// 20s 节流，避免「判死 → 重启 → 判死」的打转风暴把设备拖死。
    /// </summary>
    private async Task<bool> RestartDevicePipelineAsync(CancellationToken ct)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastDeviceRestartMs) < 20000)
        {
            Log("[自愈] 距上次重启设备端不足 20s，本次只重连不重启");
            return false;
        }
        Interlocked.Exchange(ref _lastDeviceRestartMs, now);
        try
        {
            Log("[自愈] 重启设备端采集流水线");
            await KillDaemonsAsync().ConfigureAwait(false);
            // 参考项目教训：旧实例没退出就重连会互踢，必须留出退出时间。
            await Task.Delay(600, ct).ConfigureAwait(false);
            string rateArg = _frameRate > 0 ? $"-frameRate {_frameRate} " : "";
            string startCmd =
                "/system/bin/uitest start-daemon singleness " +
                $"--extension-name {ExtensionName} -scale {_scale} {rateArg}-frameRefreshInterval {_frameRefreshInterval} " +
                $"-bitRate {_bitRate} -p 5000 -iFrameInterval 1000 -repeatInterval 16 " +
                ">/dev/null 2>&1 &";
            await HdcAsync("-t", _target, "shell", startCmd).ConfigureAwait(false);
            await WaitDaemonReadyAsync(ct).ConfigureAwait(false);
            var (fok, fout) = await HdcBatchAsync(
                new[] { "tconn", _target },
                new[] { "-t", _target, "fport", "rm", $"tcp:{_fportPort}", AbstractSocket },
                new[] { "-t", _target, "fport", $"tcp:{_fportPort}", AbstractSocket })
                .ConfigureAwait(false);
            if (fok || fout.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                Log($"[自愈] 设备端采集已重启，fport tcp:{_fportPort} 已重建");
                return true;
            }
            Log($"[自愈] fport 重建异常：{fout}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[自愈] 重启设备端失败：{ex.Message}");
            return false;
        }
    }

    private async Task DeploySoAsync()
    {
        byte[] local = File.ReadAllBytes(_soPath);
        string md5 = Convert.ToHexString(MD5.HashData(local)).ToLowerInvariant();
        var (ok, text) = await HdcAsync("-t", _target, "shell", $"md5sum {RemoteSo}")
            .ConfigureAwait(false);
        if (ok && text.Contains(md5, StringComparison.OrdinalIgnoreCase))
        {
            Log("扩展库 md5 一致，跳过推送");
            return;
        }
        // hdc file send 对含中文的绝对路径会拼到 cwd 后面，必须用相对文件名 + WorkingDirectory。
        string name = Path.GetFileName(_soPath);
        // 先删旧文件再推：目标已存在且正被 SELinux 上下文保护时，
        // `file send` 会直接报 `operation not permitted`（2026-11-15 实测）。
        await HdcAsync("-t", _target, "shell", $"rm -f {RemoteSo}").ConfigureAwait(false);
        var (sent, sout) = await HdcAsync("-t", _target, "file", "send", name, RemoteSo)
            .ConfigureAwait(false);
        if (!sent)
        {
            // 删不掉就换路径写，避免整个采集卡在部署阶段
            Log($"推送扩展库失败（{FirstLine(sout)}），改用副本路径重试");
            string alt = RemoteSo + ".new";
            await HdcAsync("-t", _target, "shell", $"rm -f {alt}").ConfigureAwait(false);
            var (sent2, sout2) = await HdcAsync("-t", _target, "file", "send", name, alt)
                .ConfigureAwait(false);
            if (!sent2)
            {
                throw new InvalidOperationException($"推送扩展库失败：{sout2}");
            }
            await HdcAsync("-t", _target, "shell",
                $"rm -f {RemoteSo}; mv {alt} {RemoteSo}").ConfigureAwait(false);
        }
        await HdcAsync("-t", _target, "shell", $"chmod 755 {RemoteSo}")
            .ConfigureAwait(false);
        Log("扩展库已推送到设备");
    }

    /// <summary>
    /// 单条 hdc 命令（自动串行）。双路并行下所有 hdc 调用必须过 HdcSerial，
    /// 否则两路的 hdc 客户端会在同一个 hdcd server 上互踢（见 HdcSerial 注释）。
    /// </summary>
    private async Task<(bool Ok, string Output)> HdcAsync(params string[] args)
    {
        string tag = string.Join(' ', args);
        Log($"[hdc→采集] {tag}");
        IDisposable lease;
        try
        {
            lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            Log($"[hdc✗采集] 等锁超时：{ex.Message}");
            return (false, "lock timeout");
        }
        using (lease)
        {
        var r = await HdcOnceAsync(args).ConfigureAwait(false);
        Log($"[hdc✓采集] {tag} → {(r.Ok ? "OK" : "FAIL")} {r.Output.Split('\n')[0]}");

        // 超时/会话失效时兜底重试一次：本机实测 hdcd 的 TCP 会话会被并发客户端互踢，
        // 表现为「同一条命令手工 0.1s、代码里卡到超时」。项目 hdcw.sh 的既有结论是
        // 「每次调用前必须重新 tconn」，这里按需补一次即可（不做无条件 tconn，太慢）。
        // 不重试 tconn 自身，否则会递归。
        bool sessionLost = !r.Ok &&
            (r.Output.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
             r.Output.Contains("Not match target", StringComparison.Ordinal) ||
             r.Output.Contains("connect-key", StringComparison.OrdinalIgnoreCase));
        if (sessionLost && args.Length > 0 && args[0] != "tconn")
        {
            Log($"[hdc↻采集] 会话疑似失效，重连后重试：{tag}");
            var (cok, _) = await HdcOnceAsync(new[] { "tconn", _target }).ConfigureAwait(false);
            if (cok)
            {
                r = await HdcOnceAsync(args).ConfigureAwait(false);
                Log($"[hdc↻采集] 重试结果 {(r.Ok ? "OK" : "FAIL")}");
            }
        }
        return r;
        }
    }

    /// <summary>
    /// 原子段：整段持锁连续执行多条 hdc 命令，中间不允许另一路插进来。
    /// 只因 `hdc -t X fport ...` 的目标参数会被忽略（规则落到 server 的当前默认目标），
    /// 所以「tconn 定位目标 → 删旧规则 → 建新规则」三步必须是一个不可分割的整体。
    /// 返回最后一条命令的结果。
    /// </summary>
    private async Task<(bool Ok, string Output)> HdcBatchAsync(params string[][] commands)
    {
        using IDisposable lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
        var last = (Ok: true, Output: "");
        foreach (string[] cmd in commands)
        {
            last = await HdcOnceAsync(cmd).ConfigureAwait(false);
            if (cmd.Length >= 1 && cmd[0] == "tconn" && !last.Ok)
            {
                // tconn 失败就没必要继续：后续 fport 会落到别的设备上
                return last;
            }
        }
        return last;
    }

    /// <summary>
    /// 单条 hdc 命令（**不加锁**，调用方必须已持有 HdcSerial 锁）。
    /// 实体走 <see cref="HdcRunner.RunLockedAsync"/>：输出重定向到临时文件而不是管道，
    /// 因为管道写端会被 `start-daemon` 拉起的常驻子进程继承，hdc 退出后仍不 EOF，
    /// `ReadToEndAsync` 会永久挂起（2026-11-15 定位的卡死真因）。
    /// </summary>
    private Task<(bool Ok, string Output)> HdcOnceAsync(string[] args)
    {
        return HdcRunner.RunLockedAsync(
            _hdcPath, args,
            workDir: Path.GetDirectoryName(_soPath) ?? AppContext.BaseDirectory,
            timeoutMs: 5000,
            acceptFailMarker: ArgsTolerateFail(args),
            log: Log);
    }

    /// <summary>
    /// `fport rm` 在规则不存在时必定回 `[Fail]Remove forward ruler failed`，
    /// 属常态而非错误，放行 Fail 标记，由调用方按输出内容自行判断。
    /// </summary>
    private static bool ArgsTolerateFail(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "fport" && args[i + 1] == "rm")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 清理本设备上属于采集链路的陈旧 fport 规则。
    ///
    /// 为什么需要：每次重连都会 FreeTcpPort() 换一个新本地端口，旧规则从来不删，
    /// 2026-11-13 实测一台设备上堆到 **44 条** tcp:xxxx → localabstract:scrcpy_grpc_socket。
    /// 规则一多，hdc 建新规则/解析目标的耗时跟着涨，而且旧规则指向已经死掉的
    /// daemon，很容易被后续的连接误用。
    ///
    /// 只删「属于本设备 + socket 名匹配 + 不是当前端口」的规则，绝不碰别的设备。
    /// </summary>
    private async Task CleanStaleFportsAsync()
    {
        try
        {
            var (ok, text) = await HdcBatchAsync(
                new[] { "tconn", _target },
                new[] { "fport", "ls" }).ConfigureAwait(false);
            if (!ok)
            {
                return;
            }
            string socketName = AbstractSocket[(AbstractSocket.IndexOf(':') + 1)..];
            var remove = new List<string[]>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0
                    || !line.Contains(_target, StringComparison.Ordinal)
                    || !line.Contains(socketName, StringComparison.Ordinal))
                {
                    continue;
                }
                // 形如：192.168.1.100:38803  tcp:2409 localabstract:scrcpy_grpc_socket  [Forward]
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[1].StartsWith("tcp:", StringComparison.Ordinal))
                {
                    continue;
                }
                if (parts[1] == $"tcp:{_fportPort}")
                {
                    continue;   // 当前要用的端口，保留
                }
                remove.Add(new[] { "fport", "rm", parts[1], AbstractSocket });
            }
            if (remove.Count == 0)
            {
                return;
            }
            var batch = new List<string[]>(remove.Count + 1) { new[] { "tconn", _target } };
            batch.AddRange(remove);
            await HdcBatchAsync(batch.ToArray()).ConfigureAwait(false);
            Log($"[采集桥] 清理 {_target} 上陈旧 fport 规则 {remove.Count} 条");
        }
        catch (Exception ex)
        {
            // 清理失败不影响采集，只是规则会继续堆着
            Log($"清理陈旧 fport 规则失败（忽略）：{ex.Message}");
        }
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ── 本机 HWP 控制面：回 ACK + 周期性重报分辨率 ──

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        Task ack = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    r = await _rx!.ReceiveAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                if (r.Buffer.Length < Hwp.HeaderSize ||
                    !HwpPacket.TryParse(r.Buffer, out HwpHeader h))
                {
                    continue;
                }
                if (h.Channel == Hwp.ChControl && h.NeedAck)
                {
                    byte[] p = HwpPacket.Build(Hwp.ChAck, Hwp.FlagIsAck, h.Seq,
                        h.TimestampMs, ReadOnlySpan<byte>.Empty);
                    try
                    {
                        _tx!.Send(p, p.Length, r.RemoteEndPoint);
                    }
                    catch
                    {
                        // 客户端可能已退出
                    }
                }
            }
        }, ct);

        Task announce = Task.Run(async () =>
        {
            uint seq = 1;
            var pc = new IPEndPoint(IPAddress.Loopback, _localPort);
            while (!ct.IsCancellationRequested)
            {
                int w = Volatile.Read(ref _width);
                int h = Volatile.Read(ref _height);
                if (w > 0 && h > 0 && w <= ushort.MaxValue && h <= ushort.MaxValue)
                {
                    byte[] body = new byte[6];
                    body[0] = Hwp.CmdResolution;
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1, 2), (ushort)w);
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3, 2), (ushort)h);
                    body[5] = 0; // folded
                    byte[] packet = HwpPacket.Build(Hwp.ChControl, 0, seq++, Hwp.Now(), body);
                    try
                    {
                        _tx!.Send(packet, packet.Length, pc);
                    }
                    catch
                    {
                        // ignored
                    }
                }
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }, ct);

        await Task.WhenAll(ack, announce).ConfigureAwait(false);
    }

    // ── gRPC 明文 HTTP/2（h2c）流 ──

    private async Task StreamLoopAsync(int port, CancellationToken ct)
    {
        int attempt = 0;
        int restarts = 0;   // 已补重启设备端的次数（每次成功重连后不清零，属于兜底额度）
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // .NET 8 HttpClient 不支持明文 h2c（实测两次均失败），改用自实现的
                // GrpcH2Client 直连 fport 转发的 TCP 端口。
                GrpcH2Client h2 = await GrpcH2Client.ConnectAsync("127.0.0.1", port,
                    msg => Log($"H2 {msg}"), ct).ConfigureAwait(false);
                _h2 = h2;
                Log("gRPC h2c 已连接（自实现 HTTP/2）");
                var splitter = new AnnexBSplitter();
                Task done = await h2.OpenRpcAsync("/ScrcpyService/onStart", msg =>
                {
                    Interlocked.Exchange(ref _lastChunkTicks, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _gotAnyChunk, 1);
                    // 真的收到数据 = 这条流是健康的，重连计数归零，
                    // 否则「几个月里偶发断 6 次」会被累计成永久放弃。
                    attempt = 0;
                    restarts = 0;   // 恢复出帧即视为设备端彻底康复，重启额度重新发放
                    byte[]? h264 = ExtractValBytes(msg);
                    if (h264 is null || h264.Length == 0)
                    {
                        return;
                    }
                    if (_dump is not null)
                    {
                        try
                        {
                            // 回调只在 HTTP/2 读帧线程上串行触发，无需加锁
                            _dump.Write(h264, 0, h264.Length);
                        }
                        catch
                        {
                            // 录盘失败不影响推流
                        }
                    }
                    // 回调只在 HTTP/2 读帧线程上串行触发，splitter 无需加锁。
                    // 转发与补帧统一走 OnDeviceAu / _holdGate，保证 HWP 帧号与
                    // H.264 参考链在两条线程间不会交错。
                    foreach (byte[] au in splitter.Feed(h264))
                    {
                        OnDeviceAu(au);
                    }
                }, ct).ConfigureAwait(false);
                // 握手完成立刻主动催一次 IDR，不等看门狗轮询。
                // 架构文档「首帧耗时已定位」：原策略「每 3s 轮询 + 空闲超 3s 才催」会让
                // 启动后最多纯等 6s 才发第一次催帧，是首帧 ~10.2s 的主因。
                // ⛔ 不在此处加 power-shell wakeup（真机实测出帧 8 → 0）。
                try
                {
                    await h2.RequestAsync("/ScrcpyService/onRequestIDRFrame", ct)
                        .ConfigureAwait(false);
                    Log("gRPC 握手完成，已主动请求首帧 IDR");
                }
                catch (OperationCanceledException)
                {
                    // 正常停止
                }
                catch (Exception ex)
                {
                    Log($"首帧 IDR 请求失败：{ex.Message}");
                }
                await done.WaitAsync(ct).ConfigureAwait(false);
                Log("gRPC onStart 流结束");
            }
            catch (OperationCanceledException)
            {
                break;   // 正常停止
            }
            catch (Exception ex)
            {
                Log($"gRPC 流结束：{LastLine(ex.Message)}");
            }
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // 判死过（daemon 被 kill / 被系统回收）就先重启设备端再重连。
            // 只重连 socket 会打到「能 accept 但永远没数据」的死 daemon 上，
            // 表现就是画面永久定格、日志静默 —— 2026-11-12 真机实测结论。
            attempt++;
            if (attempt > 6)
            {
                Log("gRPC 连续重连 6 次失败，停止重连（画面可能定格，请重新连接设备）");
                break;
            }
            // 第 2 次起就怀疑设备端采集已经死了：daemon 被 kill 之后 fport 转发会
            // 直接「积极拒绝」，光重连永远连不上。2026-11-12 实测就是这样——连试
            // 6 次全被拒，然后彻底放弃重连，画面永久定格。这里主动重启设备端，
            // 重启成功即算新一轮（最多补 3 次，避免设备真离线时无限打转）。
            bool judgedDead = Interlocked.Exchange(ref _needDeviceRestart, 0) != 0;
            if (judgedDead || attempt >= 2)
            {
                if (restarts < 3)
                {
                    if (await RestartDevicePipelineAsync(ct).ConfigureAwait(false))
                    {
                        restarts++;
                        attempt = 1;   // 设备端已重建，从新一轮开始数
                    }
                    // 节流跳过时不重置 attempt，让它继续增长到 6 次上限后放弃
                }
                else if (judgedDead)
                {
                    Log("[自愈] 本轮已补重启 3 次，后续只重连不再重启设备端");
                }
            }

            // 重连：首启时 fport 可能抢在 daemon listen 之前建立 → 第一次握手失败，
            // 以前 StreamLoop 直接返回，画面就永久停在「等待设备画面」（首启卡住原因之一）。
            // 现在按参考项目口径做指数退避；注意参考项目教训：重连前必须等设备侧
            // 旧进程退出（sleep 2000），否则新旧实例互踢，越重连越糟。
            attempt++;
            if (attempt > 6)
            {
                Log("gRPC 连续重连 6 次失败，停止重连（画面可能定格，请重新连接设备）");
                break;
            }
            int waitMs = Math.Min(1000 << (attempt - 1), 8000);
            Log($"gRPC 第 {attempt} 次重连，等待 {waitMs}ms…");
            try
            {
                await Task.Delay(waitMs, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            // 重连即视为「等首帧」重新开始，让看门狗回到 500ms 快轮询
            Interlocked.Exchange(ref _gotAnyChunk, 0);
            GrpcH2Client? old = Interlocked.Exchange(ref _h2, null);
            if (old is not null)
            {
                try { await old.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    private static string LastLine(string s)
    {
        s = s.Replace('\r', ' ').Trim();
        return s.Length <= 160 ? s : s[^160..];
    }

    /// <summary>
    /// 解码链停滞（DeviceSession 判定「等 IDR 超时且反复催帧无效」）时，
    /// 主动断开 h2c，让 StreamLoopAsync 的重连循环重新拉一条干净的新流。
    /// 3s 内只允许一次，避免重建风暴。
    /// </summary>
    public void ForceResync()
    {
        if (_disposed)
        {
            return;
        }
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastResyncMs) < 3000)
        {
            return;
        }
        Interlocked.Exchange(ref _lastResyncMs, now);
        Log("解码链停滞，主动重建 gRPC 流");
        GrpcH2Client? old = Interlocked.Exchange(ref _h2, null);
        if (old is not null)
        {
            _ = old.DisposeAsync();
        }
    }

    /// <summary>
    /// 立即请求一个 IDR（参考项目 onRequestIDRFrame）。解码队列溢出时由
    /// DeviceSession.KeyframeNeeded 触发，把冻结窗口从「等设备自然 IDR
    /// （iFrameInterval=1000ms）」缩短到一次 h2c 往返。心跳式调用，
    /// 不等待响应体；失败只记日志，绝不影响视频流。
    /// </summary>
    public void RequestKeyframeImmediate()
    {
        GrpcH2Client? h2 = _h2;
        if (h2 is null || !h2.IsAlive)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await h2.RequestAsync("/ScrcpyService/onRequestIDRFrame", _cts?.Token ?? default)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                Log($"溢出催 IDR 失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 停滞看门狗：设备画面静止时扩展编码器几乎不出帧（实测 0.9fps），
    /// 参考项目用 onRequestIDRFrame 催帧 + 唤屏解决。这里只走 gRPC 催帧，
    /// 不再额外调 hdc —— 并发 hdc 会踢掉 TCP 会话（本项目已记录的坑）。
    /// </summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        int fastPolls = 0;
        int deadProbes = 0;   // 连续「请求本身失败（无回执）」的次数
        int idleNotes = 0;    // 息屏/静止时不刷屏，每 12 轮（约 60s）提一次即可
        while (!ct.IsCancellationRequested)
        {
            // 「等首帧」阶段（还没收到过任何 chunk）用 500ms 轮询：原策略
            // 「每 3s 轮询 + 空闲超 3s 才催」最多要纯等 6s 才发第一次催帧，
            // 是首帧 ~10.2s 的主因。出帧后恢复 3s 慢轮询，不增加运行期开销。
            bool booting = Interlocked.CompareExchange(ref _gotAnyChunk, 0, 0) == 0;
            try
            {
                await Task.Delay(booting ? 500 : 3000, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            long now = Stopwatch.GetTimestamp();
            double idleMs = (now - Interlocked.Read(ref _lastChunkTicks)) * 1000.0
                            / Stopwatch.Frequency;
            if (!booting && idleMs < 3000)
            {
                deadProbes = 0;
                continue;
            }
            // 复用同一条 h2c 连接的新流发 unary 请求，避免再开 TCP 连接和 fport 并发。
            GrpcH2Client? h2 = _h2;
            if (h2 is null || !h2.IsAlive)
            {
                continue;   // 握手还没完成，启动期 500ms 后再试
            }
            long chunkBefore = Interlocked.Read(ref _lastChunkTicks);
            bool requestFailed = false;
            try
            {
                await h2.RequestAsync("/ScrcpyService/onRequestIDRFrame", ct)
                    .ConfigureAwait(false);
                // 不在这里调 hdc shell 唤屏：实测运行期 hdc 会踢掉 fport 转发会话，
                // 加了唤屏后 30s 出帧从 8 掉到 0（2026-11-09 integrated4）。
                if (booting)
                {
                    fastPolls++;
                    if (fastPolls <= 2 || fastPolls % 5 == 0)
                    {
                        Log($"正在等待设备首帧…（已请求 IDR {fastPolls} 次，gRPC 已收 {_h2?.DataBytes ?? 0} 字节）");
                    }
                }
                else
                {
                    // 静止/息屏期间每 3s 催一次 IDR 是常态，逐轮打日志只会刷屏；
                    // 统一由下面探活分支的「60s 心跳」汇报。
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 含 RequestAsync 自己 3s 超时：请求都发不出去/等不到回执，
                // 本身就是强死信号，不必再白等一个探活宽限。
                requestFailed = true;
                Log($"IDR 催帧失败：{ex.Message}");
            }

            // ── 探活 ──
            // 只看「帧间隔」不能判死：静态画面本来就不推帧（参考项目 v1.0.3 按帧间隔
            // 判死，每 ~25s 误杀健康流）。但 onRequestIDRFrame 请求的是**强制整帧**
            // IDR，设备活着就必然回数据。所以「请求后宽限内一个字节都没有」是强死信号。
            if (booting)
            {
                // 等首帧阶段不插 1.2s 等待，否则会把刚修好的首帧又拖慢。
                // 判死只认「请求本身失败」：请求有回执就说明设备端还在正常处理
                // gRPC，出不来帧几乎都是**息屏**（本项目「视觉息屏」正是让平板长期
                // 息屏）。绝不能用「等了多久还没首帧」判死 —— 程序启动时平板本来
                // 就常是息屏的，那样会每 20s 白杀一次 daemon（2026-11-12 实测到）。
                deadProbes = requestFailed ? deadProbes + 1 : 0;
                if (deadProbes >= 3)
                {
                    MarkDeviceDead("首帧阶段连续 3 次请求无回执");
                    deadProbes = 0;
                    continue;
                }
            }
            else
            {
                if (!requestFailed)
                {
                    try
                    {
                        await Task.Delay(ProbeGraceMs, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
                // ⚠ 判死只认「请求本身失败」，绝不认「请求成功但没数据」。
                // 前者说明设备端 daemon 已经没人处理 gRPC（被 kill / 被回收）；
                // 后者在**息屏**时是必然现象 —— 本项目「视觉息屏」就是让平板长期
                // 息屏，此时编码器不产帧但 gRPC 完全正常。2026-11-12 实测：按
                // 「IDR 无数据」判死会把息屏误当故障，每 20s 杀一次 daemon 重启，
                // 既救不回来又反复打断链路。
                deadProbes = requestFailed ? deadProbes + 1 : 0;
                if (requestFailed)
                {
                    Log($"[探活] IDR 请求无回执（连续 {deadProbes} 次）");
                }
                else if (++idleNotes % 12 == 1)
                {
                    // 息屏是常态：每 5s 打一条会把日志刷满，且会触发 AppLog 的重复抑制
                    Log("[探活] 画面无数据（请求已回执 → 静止/息屏），链路正常");
                }
                if (deadProbes >= 2)
                {
                    deadProbes = 0;
                    MarkDeviceDead("IDR 请求连续无回执");
                }
            }
        }
    }

    /// <summary>
    /// 判定设备端采集 daemon 已死（或被系统静默干掉）：断掉 h2c，并置位
    /// 「重连前必须重启设备端」。**只重连 socket 是不够的** —— daemon 已死时
    /// fport 转发仍会 accept，新连接照样打得通但永远没有数据，这就是
    /// 2026-11-12 真机 kill -9 之后「画面定格、日志静默、永不恢复」的成因。
    /// 不调 ForceResync()：它带 3s 节流，可能把这次救命的断开吞掉。
    /// </summary>
    private void MarkDeviceDead(string reason)
    {
        if (_disposed)
        {
            return;
        }
        Log($"[自愈] 判定设备端采集已死：{reason}；重启设备端流水线后重连");
        Interlocked.Exchange(ref _needDeviceRestart, 1);
        GrpcH2Client? old = Interlocked.Exchange(ref _h2, null);
        if (old is not null)
        {
            _ = old.DisposeAsync();
        }
    }

    /// <summary>
    /// 手写 protobuf 解析：ReplyMessage.payload(map field 3, key=="data")
    /// → ParamValue.val_bytes(field 5)。只依赖 BCL，不引入 Google.Protobuf。
    /// </summary>
    private static byte[]? ExtractValBytes(byte[] msg)
    {
        int pos = 0;
        while (pos < msg.Length)
        {
            ulong tag = ReadVarint(msg, ref pos);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire != 2)
            {
                SkipField(msg, ref pos, wire);
                continue;
            }
            int len = (int)ReadVarint(msg, ref pos);
            if (len < 0 || pos + len > msg.Length)
            {
                return null;
            }
            ReadOnlySpan<byte> value = msg.AsSpan(pos, len);
            pos += len;
            if (field != 3)
            {
                continue;
            }
            // map entry：field 1 = key(string)，field 2 = value(ParamValue message)
            string? key = null;
            byte[]? param = null;
            int p = 0;
            while (p < value.Length)
            {
                ulong t2 = ReadVarint(value, ref p);
                int f2 = (int)(t2 >> 3);
                int w2 = (int)(t2 & 7);
                if (w2 != 2)
                {
                    SkipField(value, ref p, w2);
                    continue;
                }
                int l2 = (int)ReadVarint(value, ref p);
                if (l2 < 0 || p + l2 > value.Length)
                {
                    break;
                }
                ReadOnlySpan<byte> v2 = value.Slice(p, l2);
                p += l2;
                if (f2 == 1)
                {
                    key = Encoding.UTF8.GetString(v2);
                }
                else if (f2 == 2)
                {
                    param = v2.ToArray();
                }
            }
            if (key == "data" && param is not null)
            {
                int q = 0;
                while (q < param.Length)
                {
                    ulong t3 = ReadVarint(param, ref q);
                    int f3 = (int)(t3 >> 3);
                    int w3 = (int)(t3 & 7);
                    if (w3 != 2)
                    {
                        SkipField(param, ref q, w3);
                        continue;
                    }
                    int l3 = (int)ReadVarint(param, ref q);
                    if (l3 < 0 || q + l3 > param.Length)
                    {
                        break;
                    }
                    if (f3 == 5)
                    {
                        return param.AsSpan(q, l3).ToArray();
                    }
                    q += l3;
                }
            }
        }
        return null;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int pos)
    {
        ulong v = 0;
        int shift = 0;
        while (pos < b.Length && shift < 70)
        {
            byte c = b[pos++];
            v |= (ulong)(c & 0x7F) << shift;
            if ((c & 0x80) == 0)
            {
                return v;
            }
            shift += 7;
        }
        return v;
    }

    private static void SkipField(ReadOnlySpan<byte> b, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0:
                ReadVarint(b, ref pos);
                break;
            case 1:
                pos += 8;
                break;
            case 2:
                int len = (int)ReadVarint(b, ref pos);
                pos += len;
                break;
            case 5:
                pos += 4;
                break;
            default:
                pos = b.Length;
                break;
        }
    }

    // ── Annex-B 流式切帧 / 帧保持 ──

    /// <summary>
    /// 设备真实帧到达。ACTIVE 期零额外延迟直接转发；IDLE 补帧期参考链停在
    /// 缓存 IDR 上，只认设备自己的 IDR 才恢复转发，避免新旧 GOP 错接花屏。
    /// 与 HoldLoopAsync 共用 _holdGate，保证帧号与参考链严格串行。
    /// </summary>
    private void OnDeviceAu(byte[] au)
    {
        Interlocked.Exchange(ref _lastAuTicks, Stopwatch.GetTimestamp());
        lock (_holdGate)
        {
            bool isKey = HasIdr(au);
            // 补帧期插入过 IDR，真实 P 帧不能接在这条参考链上，一律丢弃，
            // 直到收到设备自己的 IDR。真机现场这个等待可超过 2s（设备静止时
            // IDR 周期远大于标称值），正是「平板操作后电脑端更新很慢」的来源。
            if (Volatile.Read(ref _needKeyframe) == 1)
            {
                if (!isKey)
                {
                    // 2026-11-13：设备已经恢复出帧（用户刚操作过），只是参考链还停在补帧 IDR 上。
                    // 原实现在这里单纯 return，干等设备自然 IDR（静止时实测 >2s），是
                    // 「平板操作后电脑端很久才更新」的主因。改为主动催一帧 IDR：
                    // 仍然丢弃所有 P 帧、仍然只认设备自己的 IDR 才恢复转发，参考链拼接逻辑一行未动。
                    // 催帧无效时行为与改前完全一致 —— **单调不劣，不会引入花屏**。
                    long now = Environment.TickCount64;
                    long last = Interlocked.Read(ref _lastIdrRequestTicks);
                    if (now - last > HoldIdrRequestGapMs)
                    {
                        Interlocked.Exchange(ref _lastIdrRequestTicks, now);
                        RequestKeyframeImmediate();
                        if (last == 0)
                        {
                            Log("补帧等待期检测到真实帧 → 主动催 IDR（不再干等自然 IDR）");
                        }
                    }
                    return;
                }
                Volatile.Write(ref _needKeyframe, 0);
                Interlocked.Exchange(ref _lastIdrRequestTicks, 0);   // 下一段等待期允许立刻再催
            }
            if (isKey)
            {
                _holdGop.Clear();
            }
            _holdGop.Add(au);
            if (_holdGop.Count > HoldMaxAus)
            {
                // 裁剪后仍必须从 IDR 开始，否则补帧帧无法独立解码。
                int idr = -1;
                for (int k = _holdGop.Count - 1; k >= 0; k--)
                {
                    if (HasIdr(_holdGop[k]))
                    {
                        idr = k;
                        break;
                    }
                }
                _holdGop.RemoveRange(0, idr >= 0 ? idr : _holdGop.Count - 1);
            }
            SendAccessUnit(au);
        }
    }

    /// <summary>
    /// IDLE 帧保持：超过 HoldIdleMs 没有真实帧时，按 ~60fps 只重复缓存里最近
    /// 的 IDR 单帧，维持 MF 输出流水。画面恒定、不倒带、不闪屏；设备一旦恢复
    /// 出帧（帧间隔 &lt; HoldIdleMs）立即停止补帧，回到连续真实流。
    /// </summary>
    private async Task HoldLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HoldIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            double idleMs = (Stopwatch.GetTimestamp() -
                             Interlocked.Read(ref _lastAuTicks)) * 1000.0 /
                            Stopwatch.Frequency;
            if (idleMs < HoldIdleMs)
            {
                continue;
            }
            lock (_holdGate)
            {
                // 首帧必须是 IDR；否则整段无法独立解码，宁可不补帧也不能花屏。
                if (_holdGop.Count == 0 || !HasIdr(_holdGop[0]))
                {
                    continue;
                }
                Volatile.Write(ref _needKeyframe, 1);
                SendAccessUnit(_holdGop[0]);
            }
        }
    }

    /// <summary>Annex-B 中是否存在 IDR（NAL type 5）。</summary>
    private static bool HasIdr(byte[] au)
    {
        int i = 0;
        while (i + 3 < au.Length)
        {
            int sc;
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1)
            {
                sc = 4;
            }
            else if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                sc = 3;
            }
            else
            {
                i++;
                continue;
            }
            int p = i + sc;
            if (p < au.Length && (au[p] & 0x1F) == 5)
            {
                return true;
            }
            i = p;
        }
        return false;
    }

    /// <summary>
    /// 调用方必须持有 _holdGate：++_seq / _frameId 与多分片 sendto 不能并发，
    /// 否则 HWP 帧号与 H.264 参考链都会错乱。
    /// </summary>
    private void SendAccessUnit(byte[] au)
    {
        if (H264Sps.TryParse(au, out int w, out int h) && w > 0 && h > 0)
        {
            Interlocked.Exchange(ref _width, w);
            Interlocked.Exchange(ref _height, h);
        }
        int count = Math.Max(1, (au.Length + Chunk - 1) / Chunk);
        if (count > ushort.MaxValue)
        {
            return;
        }
        var pc = new IPEndPoint(IPAddress.Loopback, _localPort);
        for (int frag = 0; frag < count; frag++)
        {
            int off = frag * Chunk;
            int len = Math.Min(Chunk, Math.Max(0, au.Length - off));
            byte[] payload = new byte[Hwp.StreamMetaSize + len];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, _frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), (ushort)frag);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), (ushort)count);
            if (len > 0)
            {
                au.AsSpan(off, len).CopyTo(payload.AsSpan(Hwp.StreamMetaSize));
            }
            byte[] packet = HwpPacket.Build(Hwp.ChStream, 0, ++_seq, Hwp.Now(), payload);
            try
            {
                _tx!.Send(packet, packet.Length, pc);
                Interlocked.Increment(ref _packetsSent);
            }
            catch
            {
                // 客户端尚未启动/已退出，UDP 允许丢
            }
        }
        Interlocked.Increment(ref _frameId);
        Interlocked.Increment(ref _framesSent);
    }

    /// <summary>
    /// gRPC chunk 边界不是访问单元边界。保留尾部未完整 NAL 与当前未完成 AU；
    /// 非 VCL NAL（SPS/PPS/SEI）缓存到下一帧开头 —— 粘在上一帧尾部会花屏
    /// （2026-11-09 真机实测）。
    /// </summary>
    private sealed class AnnexBSplitter
    {
        private readonly List<byte> _tail = new();
        private readonly List<byte> _cur = new();
        private readonly List<byte> _hdr = new();
        private bool _haveVcl;

        public List<byte[]> Feed(byte[] chunk)
        {
            _tail.AddRange(chunk);
            byte[] data = _tail.ToArray();
            var starts = new List<(int Off, int Len)>();
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    starts.Add((i, 3));
                    i += 2;
                }
                else if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 &&
                         i + 3 < data.Length && data[i + 3] == 1)
                {
                    starts.Add((i, 4));
                    i += 3;
                }
            }
            var frames = new List<byte[]>();
            if (starts.Count < 2)
            {
                return frames;
            }
            int completeEnd = starts[^1].Off;
            for (int k = 0; k < starts.Count - 1; k++)
            {
                byte[] nal = data.AsSpan(starts[k].Off,
                    starts[k + 1].Off - starts[k].Off).ToArray();
                int idx = nal.Length >= 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1 ? 3 : 4;
                if (idx >= nal.Length)
                {
                    continue;
                }
                int type = nal[idx] & 0x1F;
                if (type == 1 || type == 5)
                {
                    // slice header 首字节最高位为 1 ⇒ ue(v) first_mb_in_slice == 0 ⇒ 新帧
                    bool pictureStart = idx + 1 < nal.Length && (nal[idx + 1] & 0x80) != 0;
                    if (_haveVcl && pictureStart)
                    {
                        frames.Add(_cur.ToArray());
                        _cur.Clear();
                        _haveVcl = false;
                    }
                    if (!_haveVcl)
                    {
                        _cur.AddRange(_hdr);
                        _hdr.Clear();
                    }
                    _cur.AddRange(nal);
                    _haveVcl = true;
                }
                else
                {
                    _hdr.AddRange(nal);
                }
            }
            _tail.Clear();
            _tail.AddRange(data.AsSpan(completeEnd).ToArray());
            return frames;
        }
    }

    private void Log(string msg)
    {
        string line = $"[参考采集] {msg}";
        try
        {
            _log?.Invoke(line);
        }
        catch
        {
            // 窗口正在关闭时 Dispatcher 可能已停，日志不该反过来打断清理流程
        }
        // 可选落盘：设 HARMOSWIN_REF_LOG=<path> 时逐条追加，便于真机排障。
        // 不设置时零额外开销，保持双击用户的默认行为不变。
        try
        {
            string? path = Environment.GetEnvironmentVariable("HARMOSWIN_REF_LOG");
            if (!string.IsNullOrWhiteSpace(path))
            {
                // ⚠ 不能直接用 File.AppendAllText：它每次都打开/关闭文件，
                // 多线程并发写会 FileShare 冲突 → 抛 IOException 或被系统阻塞，
                // 表现为「日志某处突然停住，后面代码也不再推进」（2026-11-15 定位）。
                // 改为进程内共享句柄 + 锁，写失败静默丢弃。
                LogFile.WriteLine(path, $"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
        }
        catch
        {
            // 日志写失败绝不影响采集链路
        }
    }
}
