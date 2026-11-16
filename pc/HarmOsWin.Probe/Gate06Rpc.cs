using System.Text.Json;
using HarmOsWin.Core;

namespace HarmOsWin.Probe;

/// <summary>
/// GATE-06 验收：uitest 持久 socket（RPC）通道。
/// 用法: HarmOsWin.Probe gate06rpc [target] [tapCount] [navCount] [outPath] [tapX] [tapY]
/// target 形如 192.168.1.100:38803。
/// 注意：tapX/tapY 必须落在「点了没反应」的位置（如日志区空白），否则测的是 UI 动画负载下的延迟。
/// </summary>
internal static class Gate06Rpc
{
    public static async Task RunAsync(string[] args)
    {
        string target = args.Length > 1 ? args[1] : "192.168.1.100:38803";
        // 只给 IP 时自动补无线调试端口：hdc 的 target 必须是 ip:port 形式，
        // 纯 IP 会让所有 -t 命令报 "Not match target founded"（2026-11-09 踩到）。
        if (target.IndexOf(':') < 0)
        {
            target = target + ":38803";
        }
        int tapCount = args.Length > 2 ? int.Parse(args[2]) : 20;
        int navCount = args.Length > 3 ? int.Parse(args[3]) : 3;
        string outPath = args.Length > 4 ? args[4] : "gate06rpc.json";
        int tapX = args.Length > 5 ? int.Parse(args[5]) : 800;
        int tapY = args.Length > 6 ? int.Parse(args[6]) : 1900;
        // 本地转发端口：默认 28571。若该端口被上一轮残留的 hdc 进程占着，
        // fport 会报 "TCP Port listen failed"，此时换一个端口即可（本机残留 hdc 常杀不掉）。
        int localPort = args.Length > 7 ? int.Parse(args[7]) : 28571;

        string hdc = Environment.GetEnvironmentVariable("HARMOS_HDC")
                     ?? @"E:\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe";

        Console.WriteLine($"GATE-06 RPC | target={target} | port={localPort} | tap {tapCount}× @({tapX},{tapY}), nav {navCount}×");

        await using var client = new UitestRpcClient(hdc, target, localPort)
        {
            Verbose = Environment.GetEnvironmentVariable("HARMOS_RPC_VERBOSE") == "1"
        };

        // 建链成本（tconn + fport + TCP），一次性
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool up = await client.ConnectAsync();
        sw.Stop();
        double connectMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"建链: {(up ? "OK" : "FAIL")} {connectMs:F0}ms");
        if (!up)
        {
            Console.WriteLine("=== GATE-06 判定: FAIL（通道建不起来）===");
            return;
        }

        // 预热两次（首条命令要唤醒 agent）
        for (int i = 0; i < 2; i++)
        {
            await client.TapAsync(tapX, tapY);
        }

        var tapMs = new List<double>();
        int tapFail = 0;
        for (int i = 0; i < tapCount; i++)
        {
            var (ms, ok) = await client.TapAsync(tapX, tapY);
            if (ok)
            {
                tapMs.Add(ms);
            }
            else
            {
                tapFail++;
            }
            await Task.Delay(120);
        }

        var backMs = new List<double>();
        var homeMs = new List<double>();
        for (int i = 0; i < navCount; i++)
        {
            var b = await client.PressNavAsync(NavKey.Back);
            if (b.Ok) backMs.Add(b.ElapsedMs);
            await Task.Delay(300);
            var h = await client.PressNavAsync(NavKey.Home);
            if (h.Ok) homeMs.Add(h.ElapsedMs);
            await Task.Delay(300);
        }

        Stats tap = Stats.Of(tapMs);
        Stats back = Stats.Of(backMs);
        Stats home = Stats.Of(homeMs);

        Console.WriteLine($"单点 touchDown+Up : {tap}");
        Console.WriteLine($"返回 pressBack    : {back}");
        Console.WriteLine($"主页 pressHome    : {home}");
        Console.WriteLine($"点击失败 {tapFail}/{tapCount} | 重连次数 {client.ConnectCount - 1}");

        // 判据：单点 p95 < 200ms
        bool pass = tap.Count > 0 && tap.P95 < 200;
        Console.WriteLine($"\n=== GATE-06 判定: {(pass ? "PASS（单点达标）" : "FAIL")} ===");
        Console.WriteLine("判据：单点端到端 p95 <200ms。三键走 hypium 的 pressBack/pressHome，");
        Console.WriteLine("含「等界面切完」的语义耗时（100~400ms），与触摸注入不属同一量纲，单列不参与判定。");

        var report = new
        {
            generated_at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            channel = "uitest persistent socket (RPC over hdc fport localabstract:uitest_socket)",
            target,
            connect_ms = Math.Round(connectMs, 1),
            verdict = pass ? "PASS" : "FAIL",
            criteria = "tap p95 < 200ms",
            tap = new { count = tap.Count, fail = tapFail, min = tap.Min, p50 = tap.P50, p95 = tap.P95, max = tap.Max },
            press_back = new { count = back.Count, p50 = back.P50, p95 = back.P95, max = back.Max },
            press_home = new { count = home.Count, p50 = home.P50, p95 = home.P95, max = home.Max },
            reconnects = client.ConnectCount - 1
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"结果已写入 {Path.GetFullPath(outPath)}");
    }

    private sealed record Stats(int Count, double Min, double P50, double P95, double Max)
    {
        public static Stats Of(List<double> xs)
        {
            if (xs.Count == 0)
            {
                return new Stats(0, 0, 0, 0, 0);
            }
            var s = xs.OrderBy(x => x).ToList();
            double P(double q) => s[Math.Clamp((int)Math.Ceiling(q * s.Count) - 1, 0, s.Count - 1)];
            return new Stats(s.Count,
                Math.Round(s[0], 2),
                Math.Round(P(0.50), 2),
                Math.Round(P(0.95), 2),
                Math.Round(s[^1], 2));
        }

        public override string ToString() =>
            $"n={Count} min={Min} p50={P50} p95={P95} max={Max} ms";
    }
}
