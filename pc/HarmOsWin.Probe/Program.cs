using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HarmOsWin.Transport;

// GATE-L0 端到端延迟测量：依次下发 IDLE / LIGHT / FULL 三档，逐档采样 RTT 与丢包。
// 用法: HarmOsWin.Probe [deviceHost] [idleCount] [activeCount] [outPath]
//       HarmOsWin.Probe gate02 [deviceHost] [seconds] [outPath]  —— NEXT-02~06 端到端验证
//       HarmOsWin.Probe gate02live [deviceHost] [seconds] [outJson] [outBmp] —— GATE-02 实时硬解验证
//       HarmOsWin.Probe gate05 [deviceHost] [count] [outPath]    —— GATE-05 原生注入探针回归
//       HarmOsWin.Probe gate06 [deviceHost] [count] [outPath]    —— GATE-06 / NEXT-07 低频输入验证

if (args.Length > 0 && args[0] == "gate02live")
{
    await HarmOsWin.Probe.Gate02Live.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "refcap")
{
    await HarmOsWin.Probe.RefCaptureProbe.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "mfde")
{
    HarmOsWin.Probe.MfDecodeFile.Run(args);
    return;
}

if (args.Length > 0 && args[0] == "clippush")
{
    await HarmOsWin.Probe.ClipPush.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "gate02")
{
    await HarmOsWin.Probe.Gate02.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "gate05")
{
    await HarmOsWin.Probe.Gate05.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "gate06")
{
    await HarmOsWin.Probe.Gate06.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "gate06hdc")
{
    await HarmOsWin.Probe.Gate06Hdc.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "gate06rpc")
{
    await HarmOsWin.Probe.Gate06Rpc.RunAsync(args);
    return;
}

string deviceHost = args.Length > 0 ? args[0] : Hwp.DefaultDeviceHost;
int idleCount = args.Length > 1 ? int.Parse(args[1]) : 12;
int activeCount = args.Length > 2 ? int.Parse(args[2]) : 16;
string outPath = args.Length > 3 ? args[3] : "l0_result.json";

IPEndPoint device = new(IPAddress.Parse(deviceHost), Hwp.DevicePort);
using HwpEndpoint endpoint = new(Hwp.LocalPort);
VideoReceiveChannel video = new();
ReliableControlChannel control = new(p => endpoint.SendAsync(p, device));
RttSampler phaseRtt = new();
ConcurrentDictionary<uint, uint> pending = new();

long heartbeatRx = 0;
long ackRx = 0;
long videoRx = 0;
int probeSeq = 0;

endpoint.PacketReceived += (header, _, _) =>
{
    if (header.Channel == Hwp.ChAck)
    {
        Interlocked.Increment(ref ackRx);
        control.OnAck(header.Seq);
        if (pending.TryRemove(header.Seq, out uint sent))
        {
            phaseRtt.Add(Hwp.Elapsed(Hwp.Now(), sent));
        }
    }
    else if (header.Channel == Hwp.ChVideo)
    {
        video.OnPacket(header.Seq);
        Interlocked.Increment(ref videoRx);
    }
    else if (header.Channel == Hwp.ChHeartbeat && !header.IsProbe)
    {
        Interlocked.Increment(ref heartbeatRx);
    }
};
endpoint.Start();

Console.WriteLine($"GATE-L0 | PC 0.0.0.0:{Hwp.LocalPort} → {device} | " +
    $"IDLE {idleCount}×2s, LIGHT/FULL {activeCount}×1s");

(string Name, byte Cmd, int Count, int IntervalMs)[] phases =
{
    ("IDLE", Hwp.CmdIdle, idleCount, 2000),
    ("LIGHT", Hwp.CmdLight, activeCount, 1000),
    ("FULL", Hwp.CmdFull, activeCount, 1000)
};

List<PhaseResult> results = new();

foreach ((string name, byte cmd, int count, int interval) in phases)
{
    video.Reset();
    phaseRtt.Clear();
    long hbStart = Interlocked.Read(ref heartbeatRx);
    long videoStart = Interlocked.Read(ref videoRx);

    bool commandAcked = await control.SendAsync(new[] { cmd });
    Console.WriteLine($"[{name}] 控制命令 ACK={commandAcked} 累计重传={control.Retries}");
    await Task.Delay(1200);

    for (int i = 0; i < count; i++)
    {
        uint seq = (uint)Interlocked.Increment(ref probeSeq);
        uint ts = Hwp.Now();
        pending[seq] = ts;
        byte[] packet = HwpPacket.Build(Hwp.ChHeartbeat, Hwp.FlagProbe, seq, ts, ReadOnlySpan<byte>.Empty);
        await endpoint.SendAsync(packet, device);
        await Task.Delay(interval);
    }
    await Task.Delay(600);

    RttStats rtt = phaseRtt.Snapshot();
    long hb = Interlocked.Read(ref heartbeatRx) - hbStart;
    long vid = Interlocked.Read(ref videoRx) - videoStart;
    PhaseResult result = new(
        name,
        rtt.Count,
        count - rtt.Count,
        Math.Round(rtt.Min, 2),
        Math.Round(rtt.P50, 2),
        Math.Round(rtt.P95, 2),
        Math.Round(rtt.P99, 2),
        Math.Round(rtt.Max, 2),
        Math.Round(rtt.Mean, 2),
        vid,
        video.Lost,
        video.OutOfOrder,
        hb,
        commandAcked,
        control.Retries);
    results.Add(result);
    Console.WriteLine($"[{name}] {rtt} | 丢包 {result.LostProbes}/{count} | " +
        $"视频 {vid} (丢 {video.Lost}, 乱序 {video.OutOfOrder}) | 心跳 {hb}");
}

await control.SendAsync(new[] { Hwp.CmdStop });
Console.WriteLine($"总 ACK={Interlocked.Read(ref ackRx)} 控制超时={control.Timeouts} 视频总包={Interlocked.Read(ref videoRx)}");

List<PhaseResult> active = results.Where(r => r.Name != "IDLE").ToList();
bool pass = active.Count > 0 && active.All(r => r.P95 < 100.0 && r.LostProbes == 0);
Console.WriteLine($"\n=== GATE-L0 判定: {(pass ? "达标（不上 P2P）" : "未达标（需进 GATE-L1）")} ===");
Console.WriteLine("判据：LIGHT/FULL 两档 RTT p95 <100ms 且 probe 零丢失；RTT 为往返，单向≈RTT/2，故该判据对端到端 <100ms 是保守的。");

var report = new
{
    generated_at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
    device = device.ToString(),
    local_port = Hwp.LocalPort,
    protocol = "HWP v1",
    heartbeat_ms = Hwp.HeartbeatIntervalMs,
    control_rto_ms = Hwp.ControlRtoMs,
    verdict = pass ? "PASS" : "FAIL",
    phases = results
};
File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"结果已写入 {Path.GetFullPath(outPath)}");

internal sealed record PhaseResult(
    string Name,
    int Samples,
    int LostProbes,
    double Min,
    double P50,
    double P95,
    double P99,
    double Max,
    double Mean,
    long VideoPackets,
    long VideoLost,
    long VideoOutOfOrder,
    long Heartbeats,
    bool CommandAcked,
    int ControlRetries);
