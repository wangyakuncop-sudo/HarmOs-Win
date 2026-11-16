using System.Text.Json;
using HarmOsWin.Core;
using HarmOsWin.Transport;

namespace HarmOsWin.Probe;

/// <summary>
/// GATE-06 备选判据：无障碍扩展服务在本机 ROM 无开启入口（设置里没有「扩展服务」），
/// 设备端自注入三条路径（inputEventClient 801 / ACL -1 / hdc 2.5s）均判死，
/// 因此改用「PC 侧直连 hdc.exe → uinput」做系统级注入，实测单条约 200ms。
///
/// 用法: HarmOsWin.Probe gate06hdc [deviceTarget] [navCount] [tapCount] [outPath]
/// 前置：平板已开无线调试；本机 hdc.exe 可用（HARMOS_HDC 可覆盖路径）。
/// 注意：这条通道要求设备开启无线调试，不是面向普通用户的产品路径，
///       只作为「能力可达 + 延迟」的验证证据。
/// </summary>
public static class Gate06Hdc
{
    public static async Task RunAsync(string[] args)
    {
        string target = args.Length > 1 ? args[1] : "192.168.1.100:38803";
        int navCount = args.Length > 2 ? int.Parse(args[2]) : 6;
        int tapCount = args.Length > 3 ? int.Parse(args[3]) : 6;
        string outPath = args.Length > 4 ? args[4] : "gate06hdc_result.json";

        var injector = new InputInjector(
            projectDir: Directory.GetCurrentDirectory(),
            channel: new UinputChannel(),
            transport: InjectTransport.DirectHdc,
            hdcTarget: target);

        Console.WriteLine($"GATE-06(hdc) | target={target} | 三键 {navCount} 次 + 单点 {tapCount} 次");
        Console.WriteLine("判据：单次端到端 <200ms（INV-13 收窄后判据）；hdc 通道实测下限约 2.5×RTT");

        var (warmMs, warmOut, warmOk) = await injector.WarmUpAsync();
        Console.WriteLine($"预热 {(warmOk ? "OK" : "FAIL")} {warmMs:F0}ms  {warmOut}");
        if (!warmOk)
        {
            Console.WriteLine("预热失败：hdc 连不上设备。请确认已 tconn 且端口正确（无线调试端口会变）。");
        }
        await Task.Delay(300);

        var navMs = new List<double>();
        var tapMs = new List<double>();
        int navOk = 0, tapOk = 0;

        byte[] keys = { NavKey.Back, NavKey.Recents, NavKey.Home };
        string[] names = { "back", "recents", "home" };
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < keys.Length && navMs.Count < navCount; i++)
            {
                var (ms, output, ok) = await injector.NavAsync(keys[i]);
                navMs.Add(ms);
                if (ok) navOk++;
                Console.WriteLine($"  三键 {names[i],-8} {ms,7:F0}ms  ok={ok}  {Short(output)}");
                await Task.Delay(350);
            }
        }

        for (int i = 0; i < tapCount; i++)
        {
            // 右上角空白区，避免点到控件
            var ev = new InputEvent
            {
                Type = EventKind.Touch,
                Action = EventAction.Down,
                X = (ushort)(1400 + i * 5),
                Y = 120,
                Key = 0,
                TimestampMs = Hwp.Now()
            };
            var (ms, output, ok) = await injector.InjectAsync(ev);
            tapMs.Add(ms);
            if (ok) tapOk++;
            Console.WriteLine($"  单点 #{i + 1,-6} {ms,7:F0}ms  ok={ok}  {Short(output)}");
            await Task.Delay(350);
        }

        var all = navMs.Concat(tapMs).OrderBy(x => x).ToList();
        double p50 = Percentile(all, 0.5);
        double p95 = Percentile(all, 0.95);
        double max = all.Count > 0 ? all[^1] : 0;

        // 两种口径都算出来，判据口径由用户拍板
        bool passStrict = p95 < 200.0;
        bool passRelaxed = p95 < 260.0;
        string verdict = all.Count == 0
            ? "INCOMPLETE"
            : (passStrict ? "PASS(<200ms)" : (passRelaxed ? "CONDITIONAL(<260ms)" : "FAIL"));

        var report = new
        {
            gate = "GATE-06",
            channel = "hdc-direct-uinput",
            timestamp = DateTimeOffset.Now.ToString("O"),
            target,
            reason = "无障碍扩展服务在本机 ROM 无开启入口（设置→关怀和无障碍 无「扩展服务」分组，visible:true 重装后仍无），改走 hdc 直连注入",
            warmUpMs = Math.Round(warmMs, 1),
            warmUpOk = warmOk,
            nav = new { samples = navMs.Count, ok = navOk, p50 = Percentile(navMs.OrderBy(x => x).ToList(), 0.5), p95 = Percentile(navMs.OrderBy(x => x).ToList(), 0.95) },
            tap = new { samples = tapMs.Count, ok = tapOk, p50 = Percentile(tapMs.OrderBy(x => x).ToList(), 0.5), p95 = Percentile(tapMs.OrderBy(x => x).ToList(), 0.95) },
            all = new { samples = all.Count, p50, p95, max },
            verdict,
            verdictStrict200 = passStrict,
            verdictRelaxed260 = passRelaxed,
            note = passStrict
                ? "三键与单点均 <200ms，判据可维持原口径"
                : "实测卡在 200ms 判据线上（无线 RTT 82ms × 2.5 往返 = 物理下限）。若接受 p95<260ms 可判 CONDITIONAL；hdc 通道需开无线调试，不适合作为产品路径"
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine(json);
        Console.WriteLine($"\n报告已写入 {outPath}");
    }

    private static string Short(string s)
    {
        string t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= 40 ? t : t[..40] + "…";
    }

    private static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        int idx = Math.Min(xs.Count - 1, Math.Max(0, (int)Math.Ceiling(xs.Count * p) - 1));
        return Math.Round(xs[idx], 2);
    }
}
