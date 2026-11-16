using System.Collections.Concurrent;
using System.Text.Json;
using HarmOsWin.Core;
using HarmOsWin.Transport;

namespace HarmOsWin.Probe;

/// <summary>
/// GATE-06 / NEXT-07：验证 INV-13 收窄后的低频输入（三键导航 + 单点点击）能否落地。
/// 判据：动作在设备端可达，且单次操作端到端 &lt;200ms（能力收窄后重定的判据）。
/// 链路：PC → HWP CONTROL(CMD_NAV / CMD_INPUT) → 设备端 LowFreqBridge(本地 UDP 桥)
///       → AccessibilityExtensionAbility.performAction / injectGestureSync
///       → CMD_INJECT_RESULT(0x80) 回传 PC。
///
/// 用法: HarmOsWin.Probe gate06 [deviceHost] [clickCount] [outPath]
/// 前置：平板启动统一会话，且已在「设置 → 辅助功能 → 扩展服务 → 已安装的服务」开启 HarmLink 低频操作。
/// </summary>
public static class Gate06
{
    public static async Task RunAsync(string[] args)
    {
        string host = args.Length > 1 ? args[1] : Hwp.DefaultDeviceHost;
        int clickCount = args.Length > 2 ? int.Parse(args[2]) : 5;
        string outPath = args.Length > 3 ? args[3] : "gate06_result.json";

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

        Console.WriteLine($"GATE-06 | PC 0.0.0.0:{Hwp.LocalPort} → {host}:{Hwp.DevicePort} | " +
                          $"{clickCount} 次单点 + 3 个三键");
        Console.WriteLine("判据：动作可达 + 单次端到端 <200ms（INV-13 收窄后重定，原 <30ms 是高频判据）");

        bool handshake = await session.ConnectAsync();
        if (!handshake)
        {
            Console.WriteLine("首包 ACK 超时（冷启动 RTO=20ms），重试一次…");
            handshake = await session.ConnectAsync();
        }
        Console.WriteLine($"握手 {(handshake ? "PASS" : "FAIL")}");

        // 1) 低频单点点击：先在会话页内完成，避免后续三键切走页面
        for (int i = 0; i < clickCount; i++)
        {
            var ev = new InputEvent
            {
                Type = EventKind.Touch,
                Action = EventAction.Down,
                X = (ushort)(1400 + i * 5), // 右上角空白区
                Y = 120,
                Key = 0,
                TimestampMs = Hwp.Now()
            };
            pending.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            bool ack = await session.SendInputAsync(ev);
            Console.WriteLine($"  单点 #{i + 1} CONTROL ACK={ack}");
            await Task.Delay(350); // 覆盖设备端 250ms 服务未响应超时窗口
        }

        // 2) 三键导航：Home 放最后，避免应用退后台影响前面的测量
        string[] navNames = { "back", "recents", "home" };
        byte[] navKeys = { NavKey.Back, NavKey.Recents, NavKey.Home };
        for (int i = 0; i < navKeys.Length; i++)
        {
            pending.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            bool ack = await session.SendNavAsync(navKeys[i]);
            Console.WriteLine($"  三键 {navNames[i]} CONTROL ACK={ack}");
            await Task.Delay(350);
        }

        await Task.Delay(800); // 收集尾部回传

        List<InjectReport> rs;
        List<double> e2e;
        lock (reports) { rs = reports.ToList(); }
        lock (e2eMs) { e2e = e2eMs.OrderBy(x => x).ToList(); }

        int ok = rs.Count(r => r.Ok);
        var errCodes = rs.GroupBy(r => r.ErrCode)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var stages = rs.GroupBy(r => r.Stage)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var localMs = rs.Select(r => r.ElapsedUs / 1000.0).OrderBy(x => x).ToList();
        double e2eP50 = Percentile(e2e, 0.5);
        double e2eP95 = Percentile(e2e, 0.95);

        string verdict;
        string note;
        if (rs.Count == 0)
        {
            verdict = "INCOMPLETE";
            note = "未收到任何 CMD_INJECT_RESULT；检查会话页是否启动、防火墙握手是否成功";
        }
        else if (ok == 0)
        {
            verdict = "FAIL";
            note = errCodes.ContainsKey("-2")
                ? "本地低频桥 250ms 无响应：无障碍服务未在系统设置中开启（或 ExtensionAbility 未连上）"
                : (errCodes.ContainsKey("9300003")
                    ? "9300003：无无障碍权限，performAction / injectGestureSync 被拒"
                    : (errCodes.ContainsKey("9300005")
                        ? "9300005：该 Action 不被支持（performAction 已 deprecated since 12）"
                        : $"动作全部失败，错误码分布 {string.Join(",", errCodes.Select(kv => kv.Key + ":" + kv.Value))}"));
        }
        else if (e2eP95 >= 200.0)
        {
            verdict = "FAIL";
            note = $"动作可达但端到端 p95={e2eP95:F2}ms ≥200ms";
        }
        else
        {
            verdict = "PASS";
            note = "三键导航 + 低频点击在无障碍路径下可达且延迟达标，NEXT-07 结案";
        }

        var report = new
        {
            gate = "GATE-06",
            timestamp = DateTimeOffset.Now.ToString("O"),
            device = host,
            purpose = "NEXT-07 / INV-13 低频输入落地判据（单次 <200ms）",
            handshake,
            samples = clickCount + navKeys.Length,
            resultsReceived = rs.Count,
            ok,
            errCodes,
            stages,
            localActionMs = new { p50 = Percentile(localMs, 0.5), p95 = Percentile(localMs, 0.95) },
            endToEndMs = new { p50 = e2eP50, p95 = e2eP95 },
            serviceConnected = rs.Count > 0 && rs.Any(r => r.Syscap),
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
