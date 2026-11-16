using System.Net;
using System.Net.Sockets;

namespace HarmOsWin.Transport;

/// <summary>视频不可靠通道：丢包不重传、乱序直接丢帧，仅统计损失。</summary>
public sealed class VideoReceiveChannel
{
    private long _expected = -1;

    public long Received { get; private set; }
    public long Lost { get; private set; }
    public long OutOfOrder { get; private set; }

    public void OnPacket(uint seq)
    {
        long value = seq;
        if (_expected < 0)
        {
            _expected = value;
            Received++;
            return;
        }
        if (value == _expected + 1)
        {
            _expected = value;
            Received++;
        }
        else if (value > _expected + 1)
        {
            Lost += value - _expected - 1;
            _expected = value;
            Received++;
        }
        else
        {
            OutOfOrder++;
        }
    }

    public void Reset()
    {
        _expected = -1;
        Received = 0;
        Lost = 0;
        OutOfOrder = 0;
    }
}

public readonly record struct RttStats(
    int Count,
    double Min,
    double P50,
    double P95,
    double P99,
    double Max,
    double Mean)
{
    public override string ToString() =>
        $"n={Count} min={Min:F1} p50={P50:F1} p95={P95:F1} p99={P99:F1} max={Max:F1} mean={Mean:F1} (ms)";
}

public sealed class RttSampler
{
    private readonly List<double> _samples = new();
    private readonly object _gate = new();

    public void Add(double ms)
    {
        lock (_gate)
        {
            _samples.Add(ms);
        }
    }

    public RttStats Snapshot()
    {
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return new RttStats(0, 0, 0, 0, 0, 0, 0);
            }
            var sorted = _samples.OrderBy(x => x).ToList();
            double Quantile(double p) => sorted[Math.Min(sorted.Count - 1,
                Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1))];
            return new RttStats(
                sorted.Count,
                sorted[0],
                Quantile(0.50),
                Quantile(0.95),
                Quantile(0.99),
                sorted[^1],
                sorted.Average());
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }
}

/// <summary>控制可靠有序通道：滑动窗口 = 1，ACK + 自适应 RTO（下限 20ms，无样本兜底 200ms），最多重传 3 次。</summary>
public sealed class ReliableControlChannel
{
    private readonly Func<byte[], Task> _send;
    private readonly RtoEstimator _rto = new();
    private readonly object _gate = new();
    private int _seqBase;
    private TaskCompletionSource<bool>? _pending;
    private uint _pendingSeq;

    public int Retries { get; private set; }
    public int Timeouts { get; private set; }
    public int Acked { get; private set; }
    public int RtoMs => _rto.RtoMs;

    /// <summary>seqBase 按会话区分，避免多设备共用 seq 空间导致 ACK 串台（≤3 台并发）。</summary>
    public ReliableControlChannel(Func<byte[], Task> send, int seqBase = 1_000_000)
    {
        _send = send;
        _seqBase = seqBase;
    }

    /// <summary>喂入 ACK/心跳测得的 RTT 样本，驱动自适应 RTO。</summary>
    public void ObserveRtt(double ms) => _rto.Add(ms);

    public async Task<bool> SendAsync(byte[] payload)
    {
        uint seq = (uint)Interlocked.Increment(ref _seqBase);
        byte[] packet = HwpPacket.Build(Hwp.ChControl, Hwp.FlagNeedAck, seq, Hwp.Now(), payload);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending = tcs;
            _pendingSeq = seq;
        }
        for (int attempt = 0; attempt <= Hwp.ControlMaxRetry; attempt++)
        {
            await _send(packet);
            uint sentAt = Hwp.Now();
            Task winner = await Task.WhenAny(tcs.Task, Task.Delay(_rto.RtoMs));
            if (winner == tcs.Task)
            {
                _rto.Add(Hwp.Elapsed(Hwp.Now(), sentAt));
            }
            if (winner == tcs.Task)
            {
                lock (_gate)
                {
                    _pending = null;
                }
                Acked++;
                return true;
            }
            Retries++;
        }
        lock (_gate)
        {
            if (_pending == tcs)
            {
                _pending = null;
            }
        }
        Timeouts++;
        return false;
    }

    public void OnAck(uint seq)
    {
        lock (_gate)
        {
            if (_pending is not null && seq == _pendingSeq)
            {
                _pending.TrySetResult(true);
            }
        }
    }
}

/// <summary>UDP 收发端点：收到合法 HWP 包后同步分发事件。</summary>
public sealed class HwpEndpoint : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>实际绑定的本机端口。多设备并行时上层据此为每个会话选对发送端点。</summary>
    public int LocalPort { get; }

    public event Action<HwpHeader, byte[], IPEndPoint>? PacketReceived;

    public HwpEndpoint(int localPort)
    {
        LocalPort = localPort;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        // 60fps 时每帧约 60 个小包、70~90KB，接收循环还要做重组与分发；
        // 系统默认 64KB 收缓冲在这种突发下会溢出丢包（实测 0.59% 视频丢包的主因）。
        try
        {
            _udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;
            _udp.Client.SendBufferSize = 4 * 1024 * 1024;
        }
        catch
        {
            // 个别系统会拒绝过大值，保持默认即可
        }
    }

    public void Start() => _loop = Task.Run(ReceiveLoopAsync);

    public Task SendAsync(byte[] packet, IPEndPoint remote) =>
        _udp.SendAsync(packet, packet.Length, remote);

    private async Task ReceiveLoopAsync()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            if (!HwpPacket.TryParse(result.Buffer, out HwpHeader header))
            {
                continue;
            }
            PacketReceived?.Invoke(header, result.Buffer, result.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // 接收循环已退出，忽略
        }
        _udp.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// RFC 6298 风格 RTO 估计。GATE-L0 实测固定 20ms 在 Wi-Fi 省电态每阶段触发 1~3 次无谓重传，
/// 改为 srtt + 4*rttvar，钳位 [20, 200]ms；无样本时回退 20ms。
/// </summary>
public sealed class RtoEstimator
{
    private readonly object _gate = new();
    private double _srtt = -1;
    private double _rttvar;

    public int RtoMs
    {
        get
        {
            lock (_gate)
            {
                if (_srtt < 0)
                {
                    // 还没有任何 RTT 样本：用安全兜底，别拿 20ms 去赌 Wi-Fi
                    return Hwp.ControlRtoInitMs;
                }
                double rto = _srtt + 4 * _rttvar;
                return (int)Math.Clamp(rto, Hwp.ControlRtoMs, 200);
            }
        }
    }

    public double SrttMs
    {
        get
        {
            lock (_gate)
            {
                return _srtt < 0 ? 0 : _srtt;
            }
        }
    }

    public void Add(double rtt)
    {
        if (rtt < 0 || double.IsNaN(rtt))
        {
            return;
        }
        lock (_gate)
        {
            if (_srtt < 0)
            {
                _srtt = rtt;
                _rttvar = rtt / 2;
            }
            else
            {
                _rttvar = 0.75 * _rttvar + 0.25 * Math.Abs(_srtt - rtt);
                _srtt = 0.875 * _srtt + 0.125 * rtt;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _srtt = -1;
            _rttvar = 0;
        }
    }
}

/// <summary>
/// CH_STREAM 帧重组：丢包/乱序直接丢整帧，绝不等待重传（否则推高端到端延迟）。
/// payload 布局 frameId:u32 + fragment:u16 + fragCount:u16 + 码流字节。
/// </summary>
public sealed class VideoAssembler
{
    private readonly object _gate = new();
    private uint _frameId;
    private int _fragCount;
    private byte[]?[] _parts = Array.Empty<byte[]?>();
    private int _received;
    private bool _active;

    public long FramesCompleted { get; private set; }
    public long FramesDropped { get; private set; }
    public long BytesReceived { get; private set; }
    public long PacketsReceived { get; private set; }
    /// <summary>见到过的不同 frameId 个数。与 PacketsReceived 一起诊断「重组是否卡在同一帧」。</summary>
    public long DistinctFrames { get; private set; }
    /// <summary>最近一包的 fragCount，用来核对分片数是否符合预期。</summary>
    public int LastFragCount { get; private set; }

    public event Action<uint, byte[]>? FrameReady;

    public void OnPacket(HwpHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Hwp.StreamMetaSize)
        {
            return;
        }
        uint frameId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        int fragment = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        int fragCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        if (fragCount <= 0 || fragment >= fragCount)
        {
            return;
        }
        ReadOnlySpan<byte> data = payload[Hwp.StreamMetaSize..];
        byte[]? completed = null;
        uint completedId = 0;
        lock (_gate)
        {
            PacketsReceived++;
            LastFragCount = fragCount;
            if (_active && frameId != _frameId)
            {
                // 新帧到达，旧帧未完成 → 丢弃，不等重传
                FramesDropped++;
                ResetFrame();
            }
            if (!_active)
            {
                _frameId = frameId;
                _fragCount = fragCount;
                _parts = new byte[]?[fragCount];
                _active = true;
                DistinctFrames++;
            }
            else if (fragCount != _fragCount)
            {
                FramesDropped++;
                ResetFrame();
                return;
            }
            if (_parts[fragment] is null)
            {
                _parts[fragment] = data.ToArray();
                _received++;
                BytesReceived += data.Length;
            }
            if (_received != _fragCount)
            {
                return;
            }
            int total = 0;
            foreach (byte[]? part in _parts)
            {
                total += part?.Length ?? 0;
            }
            byte[] frame = new byte[total];
            int off = 0;
            foreach (byte[]? part in _parts)
            {
                if (part is null)
                {
                    continue;
                }
                part.CopyTo(frame, off);
                off += part.Length;
            }
            completedId = _frameId;
            completed = frame;
            FramesCompleted++;
            ResetFrame();
        }
        if (completed is not null)
        {
            FrameReady?.Invoke(completedId, completed);
        }
    }

    private void ResetFrame()
    {
        _active = false;
        _received = 0;
        _fragCount = 0;
        _parts = Array.Empty<byte[]?>();
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetFrame();
        }
    }
}
