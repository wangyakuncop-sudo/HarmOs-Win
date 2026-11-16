using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace HarmOsWin.Core;

/// <summary>
/// 最小可用的 gRPC over 明文 HTTP/2（h2c prior-knowledge）客户端，纯 BCL。
///
/// 为什么不用 HttpClient：.NET 8 的 SocketsHttpHandler 只对 https:// 协商 HTTP/2，
/// 对 http:// 配 VersionPolicy=RequestVersionExact 会实测报
/// "unable to establish HTTP/2 connection"；AppContext 开关
/// Http2UnencryptedSupport 在 .NET 8 已无效（2026-11-09 真机两次实测确认）。
/// 设备端 libscreen_casting.z.so 的 ScrcpyService 只提供明文 gRPC
/// （参考项目用 grpc.insecure_channel 连接），因此必须自己发 HTTP/2 帧。
///
/// 只实现服务端流所需的子集：连接前言 / SETTINGS / HEADERS(HPACK 字面量) /
/// DATA(gRPC 消息) / WINDOW_UPDATE / PING / 流结束。不解析响应 HPACK（不需要
/// 读 grpc-status，帧内容与 protobuf 解析已足够），不做 Huffman 编码。
///
/// 流控：HTTP/2 连接与流的初始窗口都是 65535，20Mbps 视频流几毫秒就会耗尽。
/// 每累计收到 WindowBatch 字节的 DATA，就给连接和对应流各发一次 WINDOW_UPDATE，
/// 否则设备端会停在"通道建立但永不出帧"（本项目早期踩过的同类现象）。
/// </summary>
internal sealed class GrpcH2Client : IAsyncDisposable
{
    private static readonly byte[] Preface =
        Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    // 必须远小于 HTTP/2 初始流窗口 65535：真机实测阈值设 512KB 时设备端推满
    // 65535 字节就停下等 WINDOW_UPDATE，而阈值永远达不到 → 流控死锁，
    // 第一帧 H.264 被截断、gRPC 消息永远凑不齐（录盘只有 31 字节）。
    private const int WindowBatch = 16 * 1024;
    private const int MaxFramePayload = 16 * 1024 * 1024;
    private const int MaxMessage = 64 * 1024 * 1024;

    // HTTP/2 帧类型
    private const byte FrameData = 0;
    private const byte FrameHeaders = 1;
    private const byte FrameRstStream = 3;
    private const byte FrameSettings = 4;
    private const byte FramePing = 6;
    private const byte FrameGoAway = 7;
    private const byte FrameWindowUpdate = 8;

    private const byte FlagEndStream = 0x1;
    private const byte FlagAck = 0x1;
    private const byte FlagEndHeaders = 0x4;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _net;
    private readonly string _authority;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<int, RpcStream> _streams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _diag;
    private int _nextStreamId = 1;   // 客户端只能用奇数
    private long _credit;
    private long _dataBytes;
    /// <summary>
    /// 已收到的 gRPC DATA 累计字节。诊断「设备端到底有没有出帧」的唯一直接证据：
    /// 等待首帧的日志里带上它，就能一眼分清是「设备没发」还是「PC 收了解不出来」。
    /// </summary>
    public long DataBytes => Interlocked.Read(ref _dataBytes);
    // 控制帧诊断限流：静止/息屏时看门狗每 3~5s 催一次 IDR，每次回 SETTINGS/
    // WINDOW_UPDATE/HEADERS，原样全打的话一天能刷几万行，把真正有用的行冲掉
    // （AppLog 1.5MB 就轮转）。前 40 条全打供握手期排查，之后每 200 条一条。
    private int _diagFrames;
    private int _diagLines;
    private Task? _reader;
    private bool _disposed;

    private void Diag(string line)
    {
        if (Interlocked.Increment(ref _diagLines) <= 40)
        {
            _diag?.Invoke(line);
        }
    }

    public bool IsAlive => !_disposed && _tcp.Connected;

    private sealed class RpcStream
    {
        public readonly List<byte> Pending = new();
        public readonly Action<byte[]>? OnMessage;
        public readonly TaskCompletionSource<bool> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RpcStream(Action<byte[]>? onMessage) => OnMessage = onMessage;
    }

    private GrpcH2Client(TcpClient tcp, string authority, Action<string>? diag)
    {
        _tcp = tcp;
        _net = tcp.GetStream();
        _authority = authority;
        _diag = diag;
    }

    public static async Task<GrpcH2Client> ConnectAsync(
        string host, int port, Action<string>? diag = null,
        CancellationToken ct = default)
    {
        // 握手必须有硬超时（首启卡住原因之一）：fport 已建立但设备端 daemon
        // 还没 listen 时，TCP 会「假连上」，随后 SendPrefaceAsync 的写操作或
        // 等 SETTINGS 的读操作可以无限期挂住 —— 表现为启动进度永远停在
        // 「正在建立画面连接」，既不出帧也不报错。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var linked = timeout.Token;

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, linked).ConfigureAwait(false);
            tcp.NoDelay = true;
            var client = new GrpcH2Client(tcp, $"{host}:{port}", diag);
            await client.SendPrefaceAsync(linked).ConfigureAwait(false);
            // 读循环用调用方 token（不跟 5s 握手超时，否则连上 5 秒后就会被取消）
            client._reader = Task.Run(() => client.ReadLoopAsync(client._cts.Token));
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"gRPC 握手超时（{host}:{port} 5s 内未完成 HTTP/2 前言）");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task SendPrefaceAsync(CancellationToken ct)
    {
        await _net.WriteAsync(Preface, ct).ConfigureAwait(false);
        // 空 SETTINGS 是合法的：沿用默认值即可。设备端 gRPC 实现不要求特定参数。
        await WriteFrameAsync(FrameSettings, 0, 0, ReadOnlyMemory<byte>.Empty, ct)
            .ConfigureAwait(false);
        await _net.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 发起一次 RPC。返回在该流收到 END_STREAM 时完成的 Task。
    /// 服务端流会持续通过 onMessage 回调吐出每条 protobuf 消息。
    /// </summary>
    public async Task<Task> OpenRpcAsync(string path, Action<byte[]>? onMessage,
        CancellationToken ct = default)
    {
        int sid;
        Task done;
        lock (_gate)
        {
            sid = _nextStreamId;
            _nextStreamId += 2;
            var s = new RpcStream(onMessage);
            _streams[sid] = s;
            done = s.Done.Task;
        }

        byte[] hpack = HpackEncode(
            (":method", "POST"),
            (":scheme", "http"),
            (":path", path),
            (":authority", _authority),
            ("content-type", "application/grpc"),
            ("te", "trailers"),
            ("grpc-accept-encoding", "identity"),
            ("user-agent", "HarmOsWin/1.0"));
        await WriteFrameAsync(FrameHeaders, FlagEndHeaders, sid, hpack, ct)
            .ConfigureAwait(false);
        // Empty{} 的 gRPC 消息：compression=0 + 4 字节大端长度 0，并半关闭请求侧。
        await WriteFrameAsync(FrameData, FlagEndStream, sid, new byte[5], ct)
            .ConfigureAwait(false);
        return done;
    }

    /// <summary>单向请求（onRequestIDRFrame 用），不等待响应内容。</summary>
    /// <remarks>
    /// ⚠ 必须带上界：`OpenRpcAsync` 返回的 done 是「等这条流 END_STREAM」。
    /// 设备端 daemon 被 kill 之后 fport 转发并不会断（TCP 仍 ESTABLISHED），
    /// 响应永远不来、done 永远不完成。2026-11-12 真机实测：kill -9 daemon 后
    /// 看门狗整个卡死在这一行，此后既不催帧也不重连，画面永久定格且日志静默。
    /// 3s 无响应只记一条诊断，由调用方（看门狗探活）决定是否重启设备端。
    /// </remarks>
    public async Task RequestAsync(string path, CancellationToken ct = default)
    {
        // 上界必须包住**整个请求**，不能只包 done：
        // OpenRpcAsync 内部要过 _writeGate 再写 TCP，daemon 死后 fport 虽然仍
        // accept，但已经没人读，写侧会堵在 TCP 窗口上 —— 那时连 OPEN 都返回不了，
        // 只等 done 的超时根本不会触发。2026-11-12 实测：看门狗第二次请求就死在
        // 这里（此后 8s 无日志、无探活、无重连）。
        Task<Task> pending = OpenRpcAsync(path, null, ct);
        try
        {
            Task done = await pending.WaitAsync(TimeSpan.FromSeconds(3), ct)
                .ConfigureAwait(false);
            await done.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Diag($"H2 请求 {path} 3s 无响应");
            throw;   // 交给调用方按「探活失败」处理，绝不在这里装作成功
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            byte[] head = new byte[9];
            while (!ct.IsCancellationRequested)
            {
                await _net.ReadExactlyAsync(head.AsMemory(), ct).ConfigureAwait(false);
                int len = (head[0] << 16) | (head[1] << 8) | head[2];
                byte type = head[3];
                byte flags = head[4];
                int sid = (int)(BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(5, 4))
                                & 0x7FFFFFFF);
                if (len < 0 || len > MaxFramePayload)
                {
                    throw new InvalidDataException($"非法 HTTP/2 帧长度 {len}");
                }
                byte[] payload = new byte[len];
                if (len > 0)
                {
                    await _net.ReadExactlyAsync(payload.AsMemory(), ct)
                        .ConfigureAwait(false);
                }
                if (type == FrameData)
                {
                    Interlocked.Add(ref _dataBytes, len);
                }
                else
                {
                    int n = Interlocked.Increment(ref _diagFrames);
                    if (n <= 40 || n % 200 == 0)
                    {
                        Diag($"H2 RX type={type} flags={flags} sid={sid} len={len} " +
                             $"hex={Convert.ToHexString(payload.AsSpan(0, Math.Min(24, payload.Length)))}");
                    }
                }
                await HandleFrameAsync(type, flags, sid, payload, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception)
        {
            // 连接被设备端关闭 / GOAWAY / 帧异常：统一收敛到 CompleteAll
        }
        finally
        {
            _diag?.Invoke($"H2 连接结束：DATA 累计 {Interlocked.Read(ref _dataBytes)} 字节");
            CompleteAll();
        }
    }

    private async Task HandleFrameAsync(byte type, byte flags, int sid,
        byte[] payload, CancellationToken ct)
    {
        switch (type)
        {
            case FrameSettings:
                if ((flags & FlagAck) == 0)
                {
                    await WriteFrameAsync(FrameSettings, FlagAck, 0,
                        ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                }
                break;

            case FramePing:
                if ((flags & FlagAck) == 0)
                {
                    await WriteFrameAsync(FramePing, FlagAck, 0, payload, ct)
                        .ConfigureAwait(false);
                }
                break;

            case FrameWindowUpdate:
                break;   // 服务端扩大我方发送窗口，与接收无关

            case FrameData:
                await OnDataAsync(sid, payload, (flags & FlagEndStream) != 0, ct)
                    .ConfigureAwait(false);
                break;

            case FrameHeaders:
                if ((flags & FlagEndStream) != 0)
                {
                    MarkDone(sid);
                }
                break;

            case FrameRstStream:
                MarkDone(sid);
                break;

            case FrameGoAway:
                throw new EndOfStreamException("设备端 GOAWAY");

            default:
                break;   // PRIORITY / PUSH_PROMISE / CONTINUATION 等，本场景不需要
        }
    }

    private async Task OnDataAsync(int sid, byte[] payload, bool endStream,
        CancellationToken ct)
    {
        // 流控：收到多少就还多少窗口，连接级 + 流级各一次。
        long credit = Interlocked.Add(ref _credit, payload.Length);
        if (credit >= WindowBatch)
        {
            long inc = Interlocked.Exchange(ref _credit, 0);
            int v = (int)Math.Min(inc, 0x7FFFFFFF);
            if (v > 0)
            {
                await WriteWindowUpdateAsync(0, v, ct).ConfigureAwait(false);
                if (sid != 0)
                {
                    await WriteWindowUpdateAsync(sid, v, ct).ConfigureAwait(false);
                }
            }
        }

        List<byte[]>? messages = null;
        Action<byte[]>? callback = null;
        lock (_gate)
        {
            if (_streams.TryGetValue(sid, out RpcStream? s))
            {
                callback = s.OnMessage;
                s.Pending.AddRange(payload);
                while (s.Pending.Count >= 5)
                {
                    List<byte> b = s.Pending;
                    int msgLen = (b[1] << 24) | (b[2] << 16) | (b[3] << 8) | b[4];
                    if (b[0] != 0 || msgLen < 0 || msgLen > MaxMessage)
                    {
                        b.Clear();
                        break;
                    }
                    if (b.Count < 5 + msgLen)
                    {
                        break;   // 消息跨 HTTP/2 帧，等下一块
                    }
                    byte[] msg = new byte[msgLen];
                    b.CopyTo(5, msg, 0, msgLen);
                    b.RemoveRange(0, 5 + msgLen);
                    (messages ??= new List<byte[]>()).Add(msg);
                }
                if (endStream)
                {
                    s.Done.TrySetResult(true);
                }
            }
        }

        // 回调放在锁外：SendAccessUnit 会做 UDP 发送，不能阻塞读帧循环。
        if (callback is not null && messages is not null)
        {
            foreach (byte[] msg in messages)
            {
                callback(msg);
            }
        }
    }

    private void MarkDone(int sid)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(sid, out RpcStream? s))
            {
                s.Done.TrySetResult(true);
            }
        }
    }

    private void CompleteAll()
    {
        lock (_gate)
        {
            foreach (RpcStream s in _streams.Values)
            {
                s.Done.TrySetResult(true);
            }
        }
    }

    private async Task WriteWindowUpdateAsync(int sid, int increment,
        CancellationToken ct)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, increment & 0x7FFFFFFF);
        await WriteFrameAsync(FrameWindowUpdate, 0, sid, body, ct)
            .ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(byte type, byte flags, int streamId,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] head = new byte[9];
        int len = payload.Length;
        head[0] = (byte)(len >> 16);
        head[1] = (byte)(len >> 8);
        head[2] = (byte)len;
        head[3] = type;
        head[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(5, 4), (uint)streamId);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _net.WriteAsync(head, ct).ConfigureAwait(false);
            if (len > 0)
            {
                await _net.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            await _net.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// HPACK 字面头部字段（不索引、不 Huffman）。所有头名/值都是 ASCII 且
    /// 长度小于 127，用 7 位前缀整数即可；仍保留通用 varint 以便后续扩展。
    /// </summary>
    private static byte[] HpackEncode(params (string Name, string Value)[] headers)
    {
        using var ms = new MemoryStream();
        foreach ((string name, string value) in headers)
        {
            ms.WriteByte(0x00);   // 字面量 + 新名称 + 不加入动态表
            WriteHpackString(ms, name);
            WriteHpackString(ms, value);
        }
        return ms.ToArray();
    }

    private static void WriteHpackString(Stream s, string text)
    {
        byte[] b = Encoding.ASCII.GetBytes(text);
        WriteHpackInt(s, b.Length);
        s.Write(b, 0, b.Length);
    }

    private static void WriteHpackInt(Stream s, int value)
    {
        const int prefix = 127;   // 7 位前缀，最高位是 H 标志（0 = 不 Huffman）
        if (value < prefix)
        {
            s.WriteByte((byte)value);
            return;
        }
        s.WriteByte((byte)prefix);
        value -= prefix;
        while (value >= 128)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }
        CompleteAll();
        try
        {
            _tcp.Close();
        }
        catch
        {
            // ignored
        }
        _writeGate.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
