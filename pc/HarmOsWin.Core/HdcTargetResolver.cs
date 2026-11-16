using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HarmOsWin.Core;

/// <summary>
/// hdc 无线调试目标的自动解析器。
///
/// 为什么需要它：HarmonyOS 的无线调试端口是**动态**的，平板重启、或开关一次无线调试，
/// 端口就会变（实测见过 38803 / 36445 / 40001）。以前目标只来自 harmoswin.json 的
/// `hdcTarget` 字段，端口一变「双击打开」就连不上，还得人工改文件 —— 这是「昨天能连
/// 今天不能」的真正来源之一。
///
/// 解析策略（先新鲜、后配置、最后兜底缓存，逐个用真命令验证，不靠猜）：
///   1. 显式指定（--hdctarget 命令行参数 / HARMOS_HDC_TARGET 环境变量）
///   2. `hdc list targets -v` 里**在线**的 TCP 目标，IP 与配置相同者优先
///   3. harmoswin.json 里的 hdcTarget / 带端口的 device
///   4. 上一次成功后缓存的目标
///   5. 其余任意在线 TCP 目标
///
/// 每个候选都用 `hdc -t &lt;target&gt; shell "param get const.product.model"` 真连一次，
/// 拿到非空型号才算通过 —— 「列表里挂着但已掉线」的陈旧条目会被自动跳过。
/// </summary>
public static class HdcTargetResolver
{
    /// <summary>解析结果。Target 为 null 表示全部候选都连不上。</summary>
    public sealed class Result
    {
        public string? Target { get; set; }
        /// <summary>命中来源：explicit / live / config / cache。</summary>
        public string Source { get; set; } = "";
        /// <summary>
        /// 真机型号（Probe 用 `param get const.product.model` 实测拿到，如 MLR-AL00 / HOP-AL10）。
        /// 2026-11-16：以前只写进 Trace，UI 因此只能用配置里写死的 name/model，
        /// 出现过「显示手机 HOP-AL10、实际连的是平板 MLR-AL00」。上层应以此校正显示。
        /// </summary>
        public string Model { get; set; } = "";
        /// <summary>逐步决策记录，直接写进启动日志，出问题时不用猜。</summary>
        public List<string> Trace { get; } = new();
        public bool Ok => !string.IsNullOrWhiteSpace(Target);
    }

    public static Result Resolve(
        string hdcPath,
        string? explicitTarget,
        string? configuredTarget,
        string? cacheFile = null,
        int probeTimeoutMs = 8000) =>
        ResolveAsync(hdcPath, explicitTarget, configuredTarget, cacheFile, probeTimeoutMs)
            .GetAwaiter().GetResult();

    /// <summary>
    /// 异步版：全部候选都失败时还会扫描设备开放端口（最后手段），所以必须异步。
    /// 扫描只在「有设备 IP」时进行，限定 hdc 无线调试常见端口段，不会全表乱扫。
    /// </summary>
    public static async Task<Result> ResolveAsync(
        string hdcPath,
        string? explicitTarget,
        string? configuredTarget,
        string? cacheFile = null,
        int probeTimeoutMs = 8000)
    {
        var res = new Result();
        string? cached = ReadCache(cacheFile);
        if (cached is not null)
        {
            res.Trace.Add($"缓存目标: {cached}");
        }

        List<string> online = ListOnlineTargets(hdcPath, res.Trace);
        string? configuredIp = HostOf(configuredTarget);

        // 在线列表可能为空（hdcd 会话掉了）。先用缓存目标 tconn 建链再看一次，
        // 否则「设备其实在，只是会话断了」会被误判成「没有设备」。
        if (online.Count == 0 && cached is not null
            && (configuredIp is null
                || HostOf(cached) is not { } cacheHost
                || string.Equals(cacheHost, configuredIp, StringComparison.OrdinalIgnoreCase)))
        {
            res.Trace.Add("在线列表为空，先用缓存目标 tconn 建链…");
            RunHdc(hdcPath, new[] { "tconn", cached }, probeTimeoutMs / 2, out _, out _);
            online = ListOnlineTargets(hdcPath, res.Trace);
        }

        var candidates = new List<(string Target, string Source)>();
        void Add(string? t, string source, bool allowForeign = false)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            t = t.Trim();
            // 🔴 绝不能把一台设备解析成另一台的地址。
            // 2026-11-13 真机事故：手机离线（ping 100% 丢包）时，候选里只剩平板，
            // Probe 平板成功 → 「手机」被解析成 192.168.1.100:38803，两条采集桥
            // 抢同一个 scrcpy_grpc_socket，gRPC 流每 2.3 秒被对方踢一次，画面持续闪断，
            // 而且这条错误结果还被写回缓存，下次启动直接命中 cache。
            // 规则：只要配置里给了 host，非同一 IP 的候选一律丢弃（显式指定除外）。
            if (!allowForeign && configuredIp is not null)
            {
                string? h = HostOf(t);
                if (h is not null && !string.Equals(h, configuredIp, StringComparison.OrdinalIgnoreCase))
                {
                    res.Trace.Add($"丢弃跨设备候选 {t}（{source}）：配置 host 是 {configuredIp}");
                    return;
                }
            }
            if (candidates.Any(c => string.Equals(c.Target, t, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add((t, source));
        }

        Add(explicitTarget, "explicit", allowForeign: true);
        foreach (string t in online.Where(t => configuredIp is not null && HostOf(t) == configuredIp))
        {
            Add(t, "live");   // 同一台设备换了端口，这一步能直接认出来
        }
        Add(configuredTarget, "config");
        Add(cached, "cache");
        foreach (string t in online) Add(t, "live");

        if (candidates.Count == 0)
        {
            res.Trace.Add("没有可尝试的目标：显式 / 配置 / 缓存 / 在线列表全空");
            return res;
        }

        res.Trace.Add($"候选目标 {candidates.Count} 个: " +
                      string.Join(", ", candidates.Select(c => $"{c.Target}({c.Source})")));

        foreach (var (target, source) in candidates)
        {
            if (Probe(hdcPath, target, probeTimeoutMs, out string detail, out string model))
            {
                res.Target = target;
                res.Source = source;
                res.Model = model.Trim();
                res.Trace.Add($"[OK] {target} 来源={source} 设备型号={model}");
                WriteCache(cacheFile, target);
                return res;
            }
            res.Trace.Add($"[FAIL] {target} 来源={source}: {detail}");
        }

        res.Trace.Add("所有候选目标均连接失败");

        // 最后手段：设备重启 / 开关过无线调试 → 端口变了，而 hdcd 又不记得旧连接时，
        // 列表和配置里都拿不到新端口。此时扫描该 IP 上 hdc 常用端口段，用开放端口逐个试连。
        string? scanHost = configuredIp ?? HostOf(cached) ?? HostOf(explicitTarget);
        if (scanHost is not null && IsIPv4(scanHost))
        {
            res.Trace.Add($"开始扫描 {scanHost} 的 hdc 端口（{ScanFrom}-{ScanTo}）…");
            var sw = Stopwatch.StartNew();
            List<int> open = await FindOpenPortsAsync(scanHost, ScanFrom, ScanTo).ConfigureAwait(false);
            res.Trace.Add($"扫描完成 {sw.ElapsedMilliseconds}ms，开放端口 " +
                          (open.Count == 0 ? "无" : string.Join(", ", open)));
            foreach (int port in open.Take(MaxScanCandidates))
            {
                string cand = $"{scanHost}:{port}";
                if (Probe(hdcPath, cand, 4000, out string detail, out string model))
                {
                    res.Target = cand;
                    res.Source = "scan";
                    res.Model = model.Trim();
                    res.Trace.Add($"[OK] {cand} 来源=scan 设备型号={model}");
                    WriteCache(cacheFile, cand);
                    return res;
                }
                res.Trace.Add($"[FAIL] {cand} 来源=scan: {detail}");
            }
        }
        else
        {
            res.Trace.Add("无有效 IPv4 设备地址，跳过端口扫描");
        }

        return res;
    }

    // hdc 无线调试端口的实测取值范围：见过 36445 / 38803 / 40001。
    // 扫描段取 30000-45099，覆盖已观测的全部样本并留出余量。
    private const int ScanFrom = 30000;
    private const int ScanTo = 45099;
    private const int MaxScanCandidates = 6;

    private static bool IsIPv4(string host) =>
        System.Net.IPAddress.TryParse(host, out var ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>
    /// 并发 TCP 连接扫描开放端口。只做 connect，不发任何数据，对设备无副作用。
    /// 约 1.5 万个端口 / 512 并发 / 250ms 超时 ≈ 8 秒内出结果。
    /// </summary>
    private static async Task<List<int>> FindOpenPortsAsync(string host, int from, int to)
    {
        var open = new List<int>();
        object gate = new();
        int next = from;
        const int Concurrency = 512;
        const int TimeoutMs = 250;

        var workers = new List<Task>(Concurrency);
        for (int i = 0; i < Concurrency; i++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (true)
                {
                    int port;
                    lock (gate)
                    {
                        if (next > to) return;
                        port = next++;
                    }
                    try
                    {
                        using var c = new System.Net.Sockets.TcpClient();
                        Task conn = c.ConnectAsync(host, port);
                        // 被超时放弃的连接任务稍后会在 finalizer 里抛「已中止 I/O 操作」。
                        // 必须在这里把异常标记为已观察，否则会刷爆 TaskScheduler.UnobservedTaskException
                        // —— 20:53 实测因此产生 1.2MB 垃圾日志。
                        _ = conn.ContinueWith(static t => { _ = t.Exception; },
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted
                            | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        Task done = await Task.WhenAny(conn, Task.Delay(TimeoutMs)).ConfigureAwait(false);
                        if (done == conn && c.Connected)
                        {
                            lock (gate) open.Add(port);
                        }
                    }
                    catch (Exception)
                    {
                        // 关着的端口抛异常是常态
                    }
                }
            }));
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        open.Sort();
        return open;
    }

    /// <summary>列出在线（Connected / Ready）的 TCP 目标，过滤掉 COM/UART 串口占位。</summary>
    public static List<string> ListOnlineTargets(string hdcPath, List<string>? trace = null)
    {
        var list = new List<string>();
        if (!RunHdc(hdcPath, new[] { "list", "targets", "-v" }, 8000, out string output, out string err))
        {
            trace?.Add($"hdc list targets -v 失败: {Clip(err)}");
            return list;
        }
        foreach (string raw in output.Split('\n'))
        {
            string[] parts = raw.TrimEnd('\r')
                .Split('\t', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
            if (parts.Length < 3) continue;
            if (!parts[0].Contains(':')) continue;                                      // 只认 TCP
            if (!parts[1].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[2].Equals("Connected", StringComparison.OrdinalIgnoreCase)
                && !parts[2].Equals("Ready", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(parts[0]);
        }
        trace?.Add(list.Count == 0
            ? "在线 TCP 目标: 无"
            : "在线 TCP 目标: " + string.Join(", ", list));
        return list;
    }

    /// <summary>真连一次拿设备型号。成功说明该 target 可用且会话已建好。</summary>
    public static bool Probe(string hdcPath, string target, int timeoutMs,
                             out string detail, out string model)
    {
        model = "";
        const string Cmd = "param get const.product.model";
        if (RunHdc(hdcPath, new[] { "-t", target, "shell", Cmd }, timeoutMs, out string o1, out string e1))
        {
            return Accept(o1, out detail, out model);
        }
        // 会话可能还没建：tconn 后重试一次
        if (!RunHdc(hdcPath, new[] { "tconn", target }, Math.Max(3000, timeoutMs / 2), out _, out string e2))
        {
            detail = $"连接失败: {Clip(e1)} / tconn: {Clip(e2)}";
            return false;
        }
        if (RunHdc(hdcPath, new[] { "-t", target, "shell", Cmd }, timeoutMs, out string o2, out string e3))
        {
            return Accept(o2, out detail, out model);
        }
        detail = Clip(string.IsNullOrWhiteSpace(e1) ? e3 : e1);
        return false;
    }

    private static bool Accept(string output, out string detail, out string model)
    {
        model = output.Trim();
        if (model.Length == 0)
        {
            detail = "命令返回空（目标存在但无响应）";
            return false;
        }
        detail = "ok";
        return true;
    }

    /// <summary>把目标写进缓存，下次启动即使在线列表为空也能先试它。</summary>
    public static void WriteCache(string? cacheFile, string target)
    {
        if (string.IsNullOrWhiteSpace(cacheFile)) return;
        try
        {
            string? dir = Path.GetDirectoryName(cacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(cacheFile, target.Trim());
        }
        catch (Exception)
        {
            // 缓存写不进去只影响启动速度，不影响功能
        }
    }

    private static string? ReadCache(string? cacheFile)
    {
        if (string.IsNullOrWhiteSpace(cacheFile)) return null;
        try
        {
            if (!File.Exists(cacheFile)) return null;
            string v = File.ReadAllText(cacheFile).Trim();
            return v.Contains(':') ? v : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? HostOf(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        int i = target.LastIndexOf(':');
        return i <= 0 ? target.Trim() : target.Substring(0, i).Trim();
    }

    private static string Clip(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 160 ? s : s.Substring(0, 160) + "…";
    }

    private static bool RunHdc(string hdcPath, string[] args, int timeoutMs, out string stdout) =>
        RunHdc(hdcPath, args, timeoutMs, out stdout, out _);

    /// <summary>跑一条 hdc 命令，带硬超时（进程卡死必须杀掉，否则启动会永远挂住）。</summary>
    private static bool RunHdc(string hdcPath, string[] args, int timeoutMs,
                               out string stdout, out string error)
    {
        stdout = "";
        error = "";
        // hdc 全局串行（同步版）：双路并行下并发调用会互踢（INV-02），
        // 这里只是启动期探测，抢不到锁就照跑，不能把启动流程挂住。
        using IDisposable? lease = HdcSerial.TryAcquire();
        try
        {
            var psi = new ProcessStartInfo(hdcPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process? p = Process.Start(psi);
            if (p is null)
            {
                error = "无法启动 hdc";
                return false;
            }
            // 异步读，避免管道塞满导致子进程阻塞
            Task<string> so = p.StandardOutput.ReadToEndAsync();
            Task<string> se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                error = $"超时 {timeoutMs}ms";
                return false;
            }
            stdout = so.GetAwaiter().GetResult() + se.GetAwaiter().GetResult();
            bool ok = p.ExitCode == 0
                      && !stdout.Contains("[Fail]", StringComparison.Ordinal)
                      && !stdout.Contains("Not match target", StringComparison.Ordinal)
                      && !stdout.Contains("need connect-key", StringComparison.Ordinal);
            if (!ok && error.Length == 0) error = Clip(stdout);
            return ok;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
