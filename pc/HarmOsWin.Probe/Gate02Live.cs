// GATE-02 实时链路验证：真机实时 H.264 → UDP → 重组 → Media Foundation 硬解 → 落一帧 BMP。
// 与 GUI 无关，可直接给出「是否真的解出画面 / 解码帧率 / 首帧延迟」的量化结论。
// 用法: HarmOsWin.Probe gate02live [deviceHost] [seconds] [outJson] [outBmp]
using System.Text.Json;
using HarmOsWin.Core;
using HarmOsWin.Transport;

namespace HarmOsWin.Probe;

public static class Gate02Live
{
    public static async Task RunAsync(string[] args)
    {
        string host = args.Length > 1 ? args[1] : Hwp.DefaultDeviceHost;
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 10;
        string outJson = args.Length > 3 ? args[3] : "gate02live.json";
        string outBmp = args.Length > 4 ? args[4] : "gate02_frame.bmp";

        using SessionManager manager = new();
        manager.Log += msg => Console.WriteLine("  " + msg);
        DeviceSession session = manager.Add("tablet", host);

        var decoder = new MediaFoundationDecoder();
        session.Decoder = decoder;

        long frames = 0;
        long frameBytes = 0;
        long decodedFirst = 0;
        double firstFrameMs = -1;
        int outW = 0;
        int outH = 0;
        byte[]? firstPixels = null;
        long dumped = 0;
        var dumpLock = new object();
        string dumpPath = Path.ChangeExtension(outJson, ".h264");
        FileStream? dumpStream = null;
        try
        {
            dumpStream = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch
        {
            dumpStream = null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 每帧解码时刻（毫秒）。用来算「最大 1 秒窗口帧数」，避免授权等待与静止画面把平均帧率稀释。
        var frameTicks = new long[300_000];
        long frameTickCount = 0;

        session.ResolutionChanged += (w, h, folded) =>
        {
            Console.WriteLine($"  设备分辨率 {w}x{h} folded={folded}");
            decoder.Configure(w, h);
        };

        decoder.FrameDecoded += f =>
        {
            Interlocked.Increment(ref frames);
            long slot = Interlocked.Increment(ref frameTickCount) - 1;
            if (slot < frameTicks.Length)
            {
                frameTicks[slot] = sw.ElapsedMilliseconds;
            }
            if (Interlocked.Read(ref decodedFirst) == 0)
            {
                firstFrameMs = sw.Elapsed.TotalMilliseconds;
                outW = f.Width;
                outH = f.Height;
                firstPixels = f.Pixels;
                Interlocked.Exchange(ref decodedFirst, 1);
                Console.WriteLine($"  首帧解码成功 {f.Width}x{f.Height} 用时 {firstFrameMs:F0}ms");
            }
        };

        session.StreamFrameReady += (_, data) =>
        {
            Interlocked.Add(ref frameBytes, data.Length);            // 落盘一段真实 Annex-B 码流，供离线调试解码器（不必每次上真机）
            if (dumpStream is not null && Interlocked.Read(ref dumped) < 8_000_000)
            {
                lock (dumpLock)
                {
                    dumpStream.Write(data);
                    dumped += data.Length;
                }
            }
        };

        Console.WriteLine($"GATE-02 LIVE | PC UDP {Hwp.LocalPort} → {host}:{Hwp.DevicePort} | {seconds}s");
        sw.Restart();
        // 首次 ACK 常因 RTO 冷启动 20ms 超时误判失败，重试到 3 次（不影响后续 CMD_STREAM 的可靠性）
        bool handshake = false;
        for (int i = 0; i < 3 && !handshake; i++)
        {
            handshake = await session.ConnectAsync();
            if (!handshake)
            {
                await Task.Delay(200);
            }
        }
        Console.WriteLine($"握手 {(handshake ? "PASS" : "FAIL")} {sw.Elapsed.TotalMilliseconds:F1}ms");

        bool streamAck = await session.StartStreamAsync();
        Console.WriteLine($"CMD_STREAM ACK={streamAck}，开始计时…");
        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        // HWP_RESUME_AT=<秒>：在该秒数暂停发送再恢复，验证「暂停→恢复」的首帧延迟。
        // 这是真实重连场景（PC 端长连接），不制造「中途接入」的缺片假象。
        int resumeAt = 0;
        int.TryParse(Environment.GetEnvironmentVariable("HWP_RESUME_AT") ?? "", out resumeAt);
        if (resumeAt > 0 && resumeAt + 4 < seconds)
        {
            await Task.Delay(resumeAt * 1000);
            Console.WriteLine("[resume] CMD_STREAM=0 暂停发送…");
            await session.StopStreamAsync();
            await Task.Delay(2500);
            long before = decoder.DecodedFrames;
            var resumeSw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine("[resume] CMD_STREAM=1 恢复发送…");
            await session.StartStreamAsync();
            while (decoder.DecodedFrames == before && resumeSw.Elapsed.TotalMilliseconds < 15000)
            {
                await Task.Delay(5);
            }
            Console.WriteLine($"[resume] 恢复后首帧 {resumeSw.Elapsed.TotalMilliseconds:F0}ms" +
                $"（期间解码 {decoder.DecodedFrames - before} 帧）");
            int restMs = (seconds - resumeAt - 2) * 1000 - (int)resumeSw.Elapsed.TotalMilliseconds;
            if (restMs > 0)
            {
                await Task.Delay(restMs);
            }
        }
        else
        {
            await Task.Delay(seconds * 1000);
        }
        await session.StopStreamAsync();
        runWatch.Stop();
        await Task.Delay(300);
        dumpStream?.Flush();
        dumpStream?.Dispose();
        Console.WriteLine($"码流已落盘 {dumpPath}（{Interlocked.Read(ref dumped)} B）");

        var st = session.Stats();
        long decoded = decoder.DecodedFrames;
        double fps = runWatch.Elapsed.TotalSeconds > 0 ? decoded / runWatch.Elapsed.TotalSeconds : 0;

        // 三个口径，避免再被「授权等待 + 静止画面」误判：
        //   avgFps      全程平均，保留但只作参考（分母含授权等待与静止段）
        //   activeFps   首帧到末帧的平均帧率（剔除授权等待）
        //   peakFps     最大 1 秒窗口帧数（真实能力上限，动态画面下应接近 30/60）
        int tickN = (int)Math.Min(Interlocked.Read(ref frameTickCount), frameTicks.Length);
        double activeFps = 0;
        long peakFps = 0;
        if (tickN >= 2)
        {
            double spanSec = (frameTicks[tickN - 1] - frameTicks[0]) / 1000.0;
            if (spanSec > 0.2)
            {
                activeFps = (tickN - 1) / spanSec;
            }
            int j = 0;
            for (int i = 0; i < tickN; i++)
            {
                while (j < tickN && frameTicks[j] - frameTicks[i] <= 1000)
                {
                    j++;
                }
                if (j - i > peakFps)
                {
                    peakFps = j - i;
                }
            }
        }

        // 流畅度指标：帧间隔分位数（p95/p99 才是卡顿体感来源），以及解码各阶段耗时
        long iP50 = 0, iP95 = 0, iP99 = 0, iMax = 0;
        if (tickN >= 3)
        {
            var deltas = new long[tickN - 1];
            for (int i = 1; i < tickN; i++)
            {
                deltas[i - 1] = frameTicks[i] - frameTicks[i - 1];
            }
            Array.Sort(deltas);
            iP50 = deltas[(int)(deltas.Length * 0.50)];
            iP95 = deltas[(int)(deltas.Length * 0.95)];
            iP99 = deltas[(int)(deltas.Length * 0.99)];
            iMax = deltas[^1];
        }

        if (firstPixels is not null && outW > 0 && outH > 0)
        {
            WriteBmp(outBmp, firstPixels, outW, outH);
            Console.WriteLine($"首帧已写入 {outBmp}");
        }

        var report = new
        {
            gate = "GATE-02-LIVE",
            timestamp = DateTimeOffset.Now.ToString("O"),
            device = host,
            handshake,
            streamAck,
            seconds = runWatch.Elapsed.TotalSeconds,
            transport = new
            {
                frames,
                bytes = Interlocked.Read(ref frameBytes),
                droppedFrames = st.Dropped,
                packets = st.Packets,
                videoLost = st.VideoLost,
                rtoMs = st.RtoMs,
                distinctFrames = session.AssemblerDiag().DistinctFrames,
                lastFragCount = session.AssemblerDiag().LastFragCount
            },
            decode = new
            {
                frames = decoded,
                fps = Math.Round(fps, 2),
                activeFps = Math.Round(activeFps, 2),
                peakFps,
                errors = decoder.Errors,
                width = decoder.OutWidth,
                height = decoder.OutHeight,
                firstFrameMs = Math.Round(firstFrameMs, 1),
                intervalP50Ms = iP50,
                intervalP95Ms = iP95,
                intervalP99Ms = iP99,
                intervalMaxMs = iMax,
                convertMs = Math.Round(decoder.AvgConvertMs, 2),
                feedMs = Math.Round(decoder.AvgFeedMs, 2),
                latencyAvgMs = Math.Round(session.AvgFrameLatencyMs, 1),
                latencyLastMs = Math.Round(session.LastFrameLatencyMs, 1),
                decoder = decoder.Name
            },
            verdict = decoded > 0
                ? ((peakFps >= 24 || activeFps >= 24) ? "PASS(实时解码+上屏能力)" : "PARTIAL(能解码但帧率不足)")
                : "FAIL(未解出画面)"
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outJson, json);
        Console.WriteLine(json);
        Console.WriteLine($"\n报告已写入 {outJson}");
    }

    /// <summary>写 32 位 BMP（BGRA，行优先、自底向上），避免引入 System.Drawing 依赖。</summary>
    private static void WriteBmp(string path, byte[] bgra, int width, int height)
    {
        int stride = width * 4;
        int imageSize = stride * height;
        int fileSize = 14 + 40 + imageSize;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write(14 + 40);
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);
        for (int y = height - 1; y >= 0; y--)
        {
            bw.Write(bgra, y * stride, stride);
        }
    }
}
