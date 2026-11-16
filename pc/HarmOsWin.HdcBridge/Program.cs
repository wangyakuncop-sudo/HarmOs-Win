using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarmOsWin.HdcBridge;

/// <summary>
/// hdc 桥接进程（2026-11-15 新增）。
///
/// 存在的唯一理由：**WPF 进程里跑 hdc 会随机卡死**。
/// 多轮实测对照 —— 同一段代码、同一条命令：
///   · 在这个控制台程序里 8/8 全过，fport add 稳定 114-429ms；
///   · 在 WPF 里必然在某一两步卡住，位置随机，且 ReadToEnd/WaitForExit/ConPTY
///     各种排法都救不回来。
/// 根因是 WPF 的 GUI 子系统 + SynchronizationContext + 无效 stdin 句柄
/// 共同干扰了 hdc 子进程的 stdio 行为，而我们无法从外部修正它。
///
/// 所以把 hdc 调用整体搬到一个普通控制台进程里，WPF 只通过 stdin/stdout
/// 传 JSON 请求。hdc 看到的环境与手工命令行完全一致，稳定性直接对齐。
///
/// 协议（行式 JSON，一问一答，严格串行）：
///   请求  {"id":1,"args":["-t","192.168.1.102:40663","shell","ps -ef"],"timeoutMs":5000,"acceptFail":false}
///   响应  {"id":1,"ok":true,"output":"...","ms":203}
/// 特殊请求：
///   {"id":2,"args":["__ENSURE_SERVER__"],"hdcPath":"..."}  → 确保 hdcd 常驻
///   {"id":3,"args":["__PROBE_PORT__","8710"]}              → 探测本机端口是否 LISTEN
///   {"id":0,"args":["__EXIT__"]}                           → 退出
/// </summary>
internal static class Program
{
    private static string _hdcPath = "";
    private static readonly object WriteLock = new();

    private static int Main()
    {
        // 清掉代理变量：本机 WorkBuddy 会话注入的 http_proxy=127.0.0.1:<port>
        // 会让 hdc 的某些路径去连一个不存在的本机代理而挂住。
        foreach (string key in new[]
                 {
                     "http_proxy", "https_proxy", "all_proxy",
                     "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                 })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
        Environment.SetEnvironmentVariable("no_proxy", "*");
        Environment.SetEnvironmentVariable("NO_PROXY", "*");

        // stdin/stdout 用不缓冲的字节流，保证一问一答低延迟
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        using var stdout = Console.OpenStandardOutput();
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            Response resp;
            try
            {
                Req? req = JsonSerializer.Deserialize<Req>(line, JsonOpts);
                if (req is null || req.Args is null || req.Args.Length == 0)
                {
                    resp = new Response { Id = req?.Id ?? 0, Ok = false, Output = "bad request" };
                }
                else if (!string.IsNullOrWhiteSpace(req.HdcPath))
                {
                    _hdcPath = req.HdcPath;
                    resp = Handle(req);
                }
                else
                {
                    resp = Handle(req);
                }
            }
            catch (Exception ex)
            {
                resp = new Response { Ok = false, Output = "bridge error: " + ex.Message };
            }
            writer.WriteLine(JsonSerializer.Serialize(resp, JsonOpts));
        }
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 大小写不敏感：请求方（C# 侧 / Python 调试脚本）用小写 json 字段，
        // 响应统一输出小写，双方都不用关心大小写。
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static Response Handle(Req req)
    {
        string cmd = req.Args![0];
        switch (cmd)
        {
            case "__EXIT__":
                Environment.Exit(0);
                return new Response { Id = req.Id, Ok = true };

            case "__ENSURE_SERVER__":
                return EnsureServer(req);

            case "__PROBE_PORT__":
                {
                    int port = req.Args.Length > 1 && int.TryParse(req.Args[1], out int p) ? p : 8710;
                    bool ok = ProbePort(port);
                    return new Response { Id = req.Id, Ok = ok, Output = ok ? "LISTEN" : "CLOSED" };
                }

            default:
                return RunHdc(req);
        }
    }

    /// <summary>
    /// 确保 hdcd server 常驻。
    ///
    /// 这里的 server 跑在**本进程**里（`hdc start` 派生的独立实例），
    /// 而 bridge 进程的生命周期与 WPF 一致，所以只要 bridge 活着，server 就受它掩护。
    /// 顺带清理 %TEMP%/.HDCServer.pid 的陈旧记录 —— 它指向已死进程时，
    /// 后续每条 hdc 调用都会走一遍「探测→等超时→重启」的慢路径。
    /// </summary>
    private static Response EnsureServer(Req req)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            CleanStalePid();

            if (ProbePort(8710))
            {
                return new Response { Id = req.Id, Ok = true, Output = "already", Ms = sw.ElapsedMilliseconds };
            }

            var psi = new ProcessStartInfo(_hdcPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                Arguments = "start",
            };
            using Process? pr = Process.Start(psi);
            if (pr is not null)
            {
                // start 是短命令：读完即退，server 由它另起
                Task.WaitAll(new[]
                {
                    Task.Run(() => { try { pr.StandardOutput.ReadToEnd(); } catch { } }),
                    Task.Run(() => { try { pr.StandardError.ReadToEnd(); } catch { } }),
                }, 6000);
                try { pr.WaitForExit(2000); } catch { }
            }

            // 轮询等待端口真的 LISTEN，最多 3 秒
            for (int i = 0; i < 20; i++)
            {
                if (ProbePort(8710))
                {
                    return new Response
                    {
                        Id = req.Id, Ok = true, Output = "started",
                        Ms = sw.ElapsedMilliseconds,
                    };
                }
                Thread.Sleep(150);
            }
            return new Response
            {
                Id = req.Id, Ok = false, Output = "server not listening",
                Ms = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new Response { Id = req.Id, Ok = false, Output = ex.Message, Ms = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>执行一条 hdc 命令并返回输出。</summary>
    /// <summary>
    /// 执行一次 hdc 命令（不含重连逻辑）。重连由 <see cref="RunHdc"/> 负责。
    /// </summary>
    private static Response RunHdcOnce(Req req)
    {
        var sw = Stopwatch.StartNew();
        int timeout = req.TimeoutMs <= 0 ? 5000 : req.TimeoutMs;
        try
        {
            var psi = new ProcessStartInfo(_hdcPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 控制台程序里有有效 stdin，但仍显式重定向，保证行为一致
                RedirectStandardInput = true,
            };
            if (!string.IsNullOrWhiteSpace(req.WorkDir) && Directory.Exists(req.WorkDir))
            {
                // hdc file send 用相对文件名时必须靠工作目录定位，
                // 否则会在进程 cwd 找文件 → `no such file or directory`。
                psi.WorkingDirectory = req.WorkDir;
            }
            foreach (string a in req.Args!)
            {
                psi.ArgumentList.Add(a);
            }

            using Process? pr = Process.Start(psi);
            if (pr is null)
            {
                return new Response { Id = req.Id, Ok = false, Output = "start failed", Ms = sw.ElapsedMilliseconds };
            }

            // 读与等**必须并行**：ReadToEnd 只在管道 EOF 时返回，
            // 先读后等会双向死锁；先等后读会让大输出写满管道而死锁。
            Task<string> ro = Task.Run(() => { try { return pr.StandardOutput.ReadToEnd(); } catch { return ""; } });
            Task<string> re = Task.Run(() => { try { return pr.StandardError.ReadToEnd(); } catch { return ""; } });

            bool exited = pr.WaitForExit(timeout);
            if (!exited)
            {
                try { pr.Kill(false); } catch { }
                try { Task.WaitAll(new[] { ro, re }, 1000); } catch { }
                return new Response
                {
                    Id = req.Id, Ok = false, Output = "timeout", Ms = sw.ElapsedMilliseconds,
                };
            }
            try { Task.WaitAll(new[] { ro, re }, 3000); } catch { }

            string text = "";
            try
            {
                text = ((ro.Status == TaskStatus.RanToCompletion ? ro.Result : "")
                        + (re.Status == TaskStatus.RanToCompletion ? re.Result : "")).Trim();
            }
            catch
            {
                // ignored
            }

            int code;
            try { code = pr.ExitCode; } catch { code = -1; }

            bool ok = code == 0
                      && !text.Contains("Not match target", StringComparison.Ordinal)
                      && !text.Contains("need connect-key", StringComparison.Ordinal);
            if (!req.AcceptFail)
            {
                ok = ok && !text.Contains("[Fail]", StringComparison.Ordinal);
            }

            return new Response { Id = req.Id, Ok = ok, Output = text, Ms = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            return new Response { Id = req.Id, Ok = false, Output = ex.Message, Ms = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>
    /// 执行 hdc 命令，**带会话失效自动重连**（2026-11-15 新增）。
    ///
    /// hdc 的 TCP 会话跨进程就会掉：无线调试开关抖一下、hdc server 重启、空闲超时、
    /// 或另一路 hdc 并发介入互踢，都会让 session 失效。失效后所有针对该目标的命令
    /// 都返回 `Not match target founded` / `Connect failed`，`list targets` 里也看不到设备
    /// —— 但设备本身是在线的（ping 得通），一条 tconn 就能恢复。
    ///
    /// 这里识别出「会话失效」特征后自动 tconn 一次再重试，**只重试一次**，
    /// 避免设备真离线时陷入循环（真离线时 tconn 会失败，直接返回原结果）。
    /// </summary>
    private static Response RunHdc(Req req)
    {
        var sw = Stopwatch.StartNew();
        Response first = RunHdcOnce(req);
        if (first.Ok || req.Args is null || req.Args.Length == 0)
        {
            return first;
        }
        if (!IsSessionLost(first.Output ?? ""))
        {
            return first;
        }
        // tconn 命令本身失败时不要递归重连 —— 它就是重连手段
        if (req.Args[0].Equals("tconn", StringComparison.OrdinalIgnoreCase))
        {
            return first;
        }
        string? tgt = ExtractTarget(req.Args);
        if (tgt is null)
        {
            return first;
        }
        int timeout = req.TimeoutMs <= 0 ? 5000 : req.TimeoutMs;
        if (!TryReconnect(tgt, req.WorkDir, timeout))
        {
            return first;
        }
        Response retried = RunHdcOnce(req);
        retried.Output = "[auto-reconnect] " + (retried.Output ?? "");
        retried.Ms = sw.ElapsedMilliseconds;
        return retried;
    }

    /// <summary>
    /// 是否是「目标会话已失效」这类可恢复错误。
    /// 不含设备真离线的判定 —— 那种情况 tconn 会失败，由 TryReconnect 返回 false 兜住。
    /// </summary>
    private static bool IsSessionLost(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        return text.Contains("Not match target", StringComparison.Ordinal)
               || text.Contains("Connect failed", StringComparison.Ordinal)
               || text.Contains("[Empty]", StringComparison.Ordinal)
               || text.Contains("target not founded", StringComparison.OrdinalIgnoreCase)
               || text.Contains("connect-key", StringComparison.Ordinal)
               || text.Contains("Device offline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从参数里取出 tconn 目标（-t 后面的值，或首参本身是 ip:port）。</summary>
    private static string? ExtractTarget(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-t")
            {
                return args[i + 1];
            }
        }
        // 本身是 `tconn <target>` 的形式，或首参就是 ip:port
        foreach (string a in args)
        {
            if (a.Contains(':') && !a.StartsWith("tcp:", StringComparison.Ordinal)
                && a.Count(c => c == '.') == 3)
            {
                return a;
            }
        }
        return null;
    }

    /// <summary>尝试重新建立目标会话。成功返回 true。</summary>
    private static bool TryReconnect(string target, string? workDir, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(_hdcPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
            {
                psi.WorkingDirectory = workDir;
            }
            psi.ArgumentList.Add("tconn");
            psi.ArgumentList.Add(target);

            using Process? pr = Process.Start(psi);
            if (pr is null)
            {
                return false;
            }
            Task<string> ro = Task.Run(() => { try { return pr.StandardOutput.ReadToEnd(); } catch { return ""; } });
            Task<string> re = Task.Run(() => { try { return pr.StandardError.ReadToEnd(); } catch { return ""; } });
            if (!pr.WaitForExit(Math.Min(timeoutMs, 8000)))
            {
                try { pr.Kill(false); } catch { }
                return false;
            }
            try { Task.WaitAll(new[] { ro, re }, 2000); } catch { }
            string text = ((ro.Status == TaskStatus.RanToCompletion ? ro.Result : "")
                           + (re.Status == TaskStatus.RanToCompletion ? re.Result : ""));
            return text.Contains("Connect OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本机端口是否在 LISTEN。</summary>
    private static bool ProbePort(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            Task t = c.ConnectAsync("127.0.0.1", port);
            return t.Wait(400) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 清理 %TEMP%/.HDCServer.pid —— 只在它指向的进程已不存在时。
    /// hdc 不会自己清这个文件，失效记录会让后续每条调用都走慢路径。
    /// </summary>
    private static void CleanStalePid()
    {
        try
        {
            string pidFile = Path.Combine(Path.GetTempPath(), ".HDCServer.pid");
            if (!File.Exists(pidFile))
            {
                return;
            }
            string raw = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(raw, out int pid) || pid <= 0)
            {
                try { File.Delete(pidFile); } catch { }
                return;
            }
            bool alive;
            try
            {
                using Process p = Process.GetProcessById(pid);
                alive = !p.HasExited;
            }
            catch
            {
                alive = false;
            }
            if (!alive)
            {
                try { File.Delete(pidFile); } catch { }
            }
        }
        catch
        {
            // ignored
        }
    }

    private sealed class Req
    {
        public int Id { get; set; }
        public string[]? Args { get; set; }
        public int TimeoutMs { get; set; }
        public bool AcceptFail { get; set; }
        public string? HdcPath { get; set; }
        public string? WorkDir { get; set; }
    }

    private sealed class Response
    {
        public int Id { get; set; }
        public bool Ok { get; set; }
        public string? Output { get; set; }
        public long Ms { get; set; }
    }
}