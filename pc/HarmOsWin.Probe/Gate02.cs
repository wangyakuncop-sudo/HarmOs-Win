using System.Text.Json;
using HarmOsWin.Core;
using HarmOsWin.Transport;

namespace HarmOsWin.Probe;

/// <summary>
/// GATE-02 / NEXT-02~06 端到端验证控制台。
/// 用法: HarmOsWin.Probe gate02 [deviceHost] [seconds] [outPath]
/// 前置：平板上先启动「统一会话」页，PC 必须先发 CONTROL 握手（Windows 防火墙入站规则）。
/// </summary>
public static class Gate02
{
    public static async Task RunAsync(string[] args)
    {
        string host = args.Length > 1 ? args[1] : Hwp.DefaultDeviceHost;
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 8;
        string outPath = args.Length > 3 ? args[3] : "gate02_result.json";

        using SessionManager manager = new();
        manager.Log += msg => Console.WriteLine("  " + msg);
        DeviceSession session = manager.Add("tablet", host);

        long frames = 0;
        long frameBytes = 0;
        session.StreamFrameReady += (_, data) =>
        {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref frameBytes, data.Length);
        };

        Console.WriteLine($"GATE-02 | PC 0.0.0.0:{Hwp.LocalPort} → {host}:{Hwp.DevicePort} | {seconds}s");

        // 1) 握手（必须先 CONTROL 后收流）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool handshake = await session.ConnectAsync();
        sw.Stop();
        Console.WriteLine($"握手 {(handshake ? "PASS" : "FAIL")} {sw.Elapsed.TotalMilliseconds:F1}ms");

        // 2) 角色/分辨率协商（设备端已在启动时上报，这里主动再确认一次）
        bool roleOk = await session.SendClipTextAsync(string.Empty); // 空文本仅用于连通性探测

        // 3) 输入事件 10 次，记录 ACK 往返
        var inputLatency = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var ev = new InputEvent
            {
                Type = EventKind.Touch,
                Action = EventAction.Down,
                X = (ushort)(800 + i),
                Y = 400,
                Key = 0,
                TimestampMs = Hwp.Now()
            };
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            bool ok = await session.SendInputAsync(ev);
            t0.Stop();
            if (ok)
            {
                inputLatency.Add(t0.Elapsed.TotalMilliseconds);
            }
        }

        // 4) 三键导航
        bool navBack = await session.SendNavAsync(NavKey.Back);
        bool navHome = await session.SendNavAsync(NavKey.Home);
        bool navRecents = await session.SendNavAsync(NavKey.Recents);

        // 5) 剪贴板文本 + PNG 图片（无损）
        string clipText = $"HarmOs-Win GATE-02 {DateTime.Now:HH:mm:ss}";
        bool clipTextOk = await session.SendClipTextAsync(clipText);
        byte[] png = MakeTinyPng();
        bool clipImageOk = await session.SendClipImageAsync(png);

        // 6) 真实 H.264 码流：CH_STREAM → VideoAssembler 重组
        bool streamStart = await session.StartStreamAsync();
        Console.WriteLine($"码流启动 ACK={streamStart}，采集 {seconds}s…");
        await Task.Delay(seconds * 1000);
        await session.StopStreamAsync();
        await Task.Delay(300);

        var st = session.Stats();
        var report = new
        {
            gate = "GATE-02",
            timestamp = DateTimeOffset.Now.ToString("O"),
            device = host,
            protocol = "HWP v1.1 / UDP over LAN",
            handshake,
            handshake_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            control = new
            {
                inputEvents = inputLatency.Count,
                inputAckP50 = Percentile(inputLatency, 0.5),
                inputAckMax = inputLatency.Count == 0 ? 0 : Math.Round(inputLatency.Max(), 2),
                navAcked = new[] { navBack, navHome, navRecents }.Count(x => x),
                clipTextAcked = clipTextOk,
                clipImageAcked = clipImageOk,
                clipProbeAcked = roleOk,
                adaptiveRtoMs = st.RtoMs
            },
            stream = new
            {
                frames,
                bytes = frameBytes,
                droppedFrames = st.Dropped,
                packets = st.Packets,
                videoLost = st.VideoLost,
                decoder = session.Decoder.Name,
                realtimeSource = false
            },
            verdict = frames > 0 && st.Dropped == 0 && handshake ? "PASS(传输/重组链路)" : "INCOMPLETE"
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine(json);
        Console.WriteLine($"\n报告已写入 {outPath}");
    }

    private static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0)
        {
            return 0;
        }
        var sorted = xs.OrderBy(x => x).ToList();
        return Math.Round(sorted[Math.Min(sorted.Count - 1,
            Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1))], 2);
    }

    // 1x1 红色 PNG，用于验证图片通道无损（设备端重组后长度必须一致）
    private static byte[] MakeTinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
}
