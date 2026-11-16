using System.Collections.Concurrent;
using System.Text.Json;
using HarmOsWin.Core;
using HarmOsWin.Transport;

namespace HarmOsWin.Probe;

/// <summary>
/// GATE-05 / DEC-08：验证「HWP CONTROL 送达 → 设备端本地注入」是否成立。
/// 与 GATE-03 的区别：GATE-03 测的是 hdc 通道（每条命令要 2~3s 重连，已判 FAIL）；
/// 本探针让 InputEvent 走已达标的 HWP CONTROL（ACK p50=10.88ms），注入在设备端 HAP 内完成，
/// 端到端 = PC 发出 → UDP → 设备端注入 → 回传 CMD_INJECT_RESULT → PC。
///
/// 用法: HarmOsWin.Probe gate05 [deviceHost] [count] [outPath]
/// 前置：平板先启动「统一会话」页；PC 必须先发 CONTROL 握手（Windows 防火墙入站规则）。
/// </summary>
public static class Gate05
{
    public static async Task RunAsync(string[] args)
    {
        string host = args.Length > 1 ? args[1] : Hwp.DefaultDeviceHost;
        int count = args.Length > 2 ? int.Parse(args[2]) : 10;
        string outPath = args.Length > 3 ? args[3] : "gate05_result.json";

        using SessionManager manager = new();
        manager.Log += msg => Console.WriteLine("  " + msg);
        DeviceSession session = manager.Add("tablet", host);

        var reports = new List<InjectReport>();
        var e2eMs = new List<double>();
        var pending = new ConcurrentQueue<long>();

        session.InjectResultReceived += r =>
        {
            lock (reports) { reports.Add(r); }
            if (pending.TryDequeue(out long sent))
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
                lock (e2eMs) { e2eMs.Add(ms); }
            }
        };

        Console.WriteLine($"GATE-05 | PC 0.0.0.0:{Hwp.LocalPort} → {host}:{Hwp.DevicePort} | {count} 次注入");
        Console.WriteLine("判据：syscap 落地 + 注入成功 + 本地注入 p95 <30ms（DEC-02 原判据）");

        bool handshake = await session.ConnectAsync();
        if (!handshake)
        {
            // 冷启动首包常因 RTO=20ms 过短而超时；设备端 UDP 已 bind、防火墙需首包放行，重试一次即可。
            Console.WriteLine("首包 ACK 超时（冷启动 RTO=20ms），重试一次…");
            handshake = await session.ConnectAsync();
        }
        Console.WriteLine($"握手 {(handshake ? "PASS" : "FAIL")}");

        for (int i = 0; i < count; i++)
        {
            var ev = new InputEvent
            {
                Type = EventKind.ProbeNative, // GATE-05 回归：只触发设备端原生注入探针
                Action = EventAction.Down,
                X = (ushort)(1400 + i * 5),  // 右上角空白区，避免误触页面按钮
                Y = 120,
                Key = 0,
                TimestampMs = Hwp.Now()
            };
            pending.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            bool ok = await session.SendInputAsync(ev);
            Console.WriteLine($"  #{i + 1} CONTROL ACK={ok}");
            await Task.Delay(120);
        }

        await Task.Delay(1500); // 收集尾部回传

        List<InjectReport> rs;
        List<double> e2e;
        lock (reports) { rs = reports.ToList(); }
        lock (e2eMs) { e2e = e2eMs.OrderBy(x => x).ToList(); }

        int syscapOk = rs.Count(r => r.Syscap);
        int injected = rs.Count(r => r.Ok);
        var errCodes = rs.GroupBy(r => r.ErrCode)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var injectMs = rs.Select(r => r.ElapsedUs / 1000.0).OrderBy(x => x).ToList();
        double injectP50 = Percentile(injectMs, 0.5);
        double injectP95 = Percentile(injectMs, 0.95);
        double e2eP50 = Percentile(e2e, 0.5);
        double e2eP95 = Percentile(e2e, 0.95);

        string verdict;
        string note;
        if (rs.Count == 0)
        {
            verdict = "INCOMPLETE";
            note = "未收到任何 CMD_INJECT_RESULT 回传；检查会话页是否启动、防火墙握手是否成功";
        }
        else if (syscapOk == 0)
        {
            verdict = "FAIL";
            note = "设备 ROM 未实现 SystemCapability.MultimodalInput.Input.InputSimulator";
        }
        else if (injected == 0)
        {
            verdict = "FAIL";
            note = errCodes.ContainsKey("201")
                ? "ohos.permission.CONTROL_DEVICE 未授予；需 ACL 或 manual_settings 手动开启（deviceTypes 仅 2in1，平板可能白名单排除）"
                : (errCodes.ContainsKey("801")
                    ? "createTouchController 返回 801：SDK syscap=true 但平板 ROM 未实现 InputSimulator；CONTROL_DEVICE 仅 2in1、reqPermissionStates=-1 且 bm grant 不存在"
                    : $"注入失败，错误码分布 {string.Join(",", errCodes.Select(kv => kv.Key + ":" + kv.Value))}");
        }
        else if (injectP95 >= 30.0)
        {
            verdict = "FAIL";
            note = $"注入可用但本地 p95={injectP95:F2}ms ≥30ms，不满足 DEC-02 原判据";
        }
        else
        {
            verdict = "PASS";
            note = "HWP CONTROL + 设备端本地注入成立，DEC-08 可结案为原生 inputEventClient 通道";
        }

        var report = new
        {
            gate = "GATE-05",
            timestamp = DateTimeOffset.Now.ToString("O"),
            device = host,
            protocol = "HWP v1.1 / UDP over LAN + CMD_INJECT_RESULT(0x80)",
            purpose = "DEC-08 输入注入通道重新选型",
            handshake,
            samples = count,
            resultsReceived = rs.Count,
            syscapOk,
            injected,
            errCodes,
            stages = rs.GroupBy(r => r.Stage)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
            localInjectMs = new { p50 = injectP50, p95 = injectP95 },
            endToEndMs = new { p50 = e2eP50, p95 = e2eP95 },
            first = rs.Count == 0 ? null : new
            {
                rs[0].Syscap, rs[0].Ok, rs[0].ErrCode, rs[0].Stage, rs[0].ElapsedUs
            },
            verdict,
            note
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
        int idx = Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1));
        return Math.Round(sorted[idx], 2);
    }
}
