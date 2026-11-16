using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HarmOsWin.Core;

/// <summary>
/// hdc 统一调用入口（2026-11-15 重构版：改走桥进程）。
///
/// **为什么改成过桥进程**：WPF 里直接跑 hdc 会随机卡死。多轮实测对照——
///   同一段代码、同一条命令、同一台设备：
///     · 控制台程序里 8/8 全过，fport add 稳定 114-429ms；
///     · WPF 进程里必然在某一两步卡住，位置随机。
///   试过并全部失败的排法：ReadToEndAsync+WaitForExit 并行、先等后读、
///   cmd.exe 重定向（引号剥离 + 安全策略拦截）、Git sh.exe（残留僵尸）、
///   FileOptions.Asynchronous（静默 0 字节）、BeginOutputReadLine（第 8 次起卡）、
///   Task.Run 同步读、独立 Thread+MTA、ConPTY。
///   根因是 WPF 的 GUI 子系统 + SynchronizationContext + 无效 stdin 句柄
///   干扰了 hdc 子进程的 stdio 行为，从外部无法修正。
///
/// 解决方案：把 hdc 调用整体搬进一个**普通控制台进程**（HarmOsWin.HdcBridge），
/// hdc 在那边看到的环境与手工命令行完全一致。本类只负责
/// 「发一行 JSON → 收一行 JSON」，自己不开任何子进程、不碰任何管道语义。
///
/// 桥进程生命周期与 App 相同：首次调用时启动，App 退出时关闭。
/// 桥进程同时兼任 hdcd server 的宿主（server 由它 `hdc start` 派生），
/// 保证 fport 规则不会因 server 消失而失效。
/// </summary>
public static class HdcRunner
{
    private static readonly object Gate = new();
    private static Process? _bridge;
    private static StreamWriter? _bridgeIn;
    private static StreamReader? _bridgeOut;
    private static string _hdcPath = "";
    private static int _seq;

    /// <summary>
    /// 桥进程可执行文件路径。默认取 App 输出目录下的 HarmOsWin.HdcBridge.exe，
    /// 可用环境变量 HARMOS_HDC_BRIDGE 覆盖（调试用）。
    /// </summary>
    private static string BridgePath()
    {
        string? env = Environment.GetEnvironmentVariable("HARMOS_HDC_BRIDGE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }
        return Path.Combine(AppContext.BaseDirectory, "HarmOsWin.HdcBridge.exe");
    }

    /// <summary>确保桥进程已启动。</summary>
    private static bool EnsureBridge(string hdcPath, Action<string>? log)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(hdcPath))
            {
                _hdcPath = hdcPath;
            }
            if (_bridge is not null && !SafeHasExited(_bridge) && _bridgeIn is not null)
            {
                return true;
            }

            // 旧桥残留：先收掉
            try
            {
                _bridgeIn?.Dispose();
                _bridgeOut?.Dispose();
                if (_bridge is not null && !SafeHasExited(_bridge))
                {
                    _bridge.Kill(false);
                }
                _bridge?.Dispose();
            }
            catch
            {
                // ignored
            }
            _bridge = null;
            _bridgeIn = null;
            _bridgeOut = null;

            string exe = BridgePath();
            if (!File.Exists(exe))
            {
                log?.Invoke($"[hdc] 桥进程不存在：{exe}");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                Process? p = Process.Start(psi);
                if (p is null)
                {
                    log?.Invoke("[hdc] 桥进程启动失败");
                    return false;
                }
                _bridge = p;
                _bridgeIn = p.StandardInput;
                _bridgeIn.AutoFlush = true;
                _bridgeOut = p.StandardOutput;
                // stderr 排空：桥进程日志写满管道会把它自己堵住
                Task.Run(() =>
                {
                    try
                    {
                        while (p.StandardError.ReadLine() is { } l)
                        {
                            log?.Invoke($"[bridge] {l}");
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                });
                log?.Invoke("[hdc] 桥进程已启动（hdc 在控制台进程内执行）");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[hdc] 桥进程启动异常：{ex.Message}");
                return false;
            }
        }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    /// <summary>执行一条 hdc 命令。全局串行（hdc 并发会互踢）。</summary>
    public static async Task<(bool Ok, string Output)> RunAsync(
        string hdcPath,
        IEnumerable<string> args,
        string? workDir = null,
        int timeoutMs = 5000,
        bool acceptFailMarker = false,
        Action<string>? log = null)
    {
        string[] argArr = args is string[] a ? a : new List<string>(args).ToArray();
        // 串行由 HdcSerial 统一保证（调用方都持着它），这里不再叠一层，
        // 否则 AcquireAsync + Io 双重加锁必然死锁。
        using IDisposable lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
        return await RunCoreAsync(hdcPath, argArr, timeoutMs, acceptFailMarker, log, workDir)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 旧签名保留。内部同样走桥；若调用方自行持锁，注意不要与本类的 Io 叠加。
    /// </summary>
    public static Task<(bool Ok, string Output)> RunLockedAsync(
        string hdcPath,
        string[] argArr,
        string? workDir = null,
        int timeoutMs = 5000,
        bool acceptFailMarker = false,
        Action<string>? log = null)
    {
        return RunCoreAsync(hdcPath, argArr, timeoutMs, acceptFailMarker, log, workDir);
    }

    private static Task<(bool Ok, string Output)> RunCoreAsync(
        string hdcPath, string[] argArr, int timeoutMs, bool acceptFailMarker, Action<string>? log,
        string? workDir = null)
    {
        return Task.Run(() =>
        {
            if (!EnsureBridge(hdcPath, log))
            {
                return (false, "bridge unavailable");
            }
            var req = new BridgeReq
            {
                Id = Interlocked.Increment(ref _seq),
                Args = argArr,
                TimeoutMs = timeoutMs,
                AcceptFail = acceptFailMarker,
                HdcPath = _hdcPath,
                // hdc file send 用相对文件名，必须把调用方的工作目录带过去
                WorkDir = workDir,
            };
            try
            {
                _bridgeIn!.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
                // ⚠ 桥的读**必须带超时**：ReadLine 是阻塞读，桥一旦不回应，
                // 这个 Task 永不完成 → await 永不返回 → HdcSerial 锁永不释放
                // → 后续所有 hdc 调用全部卡在等锁上，表现就是「日志突然停住」。
                // 超时设成「命令超时 + 5s 余量」，保证比桥内部超时更宽。
                string? respLine = ReadBridgeLine(_bridgeOut!, timeoutMs + 5000);
                if (respLine is null)
                {
                    // 桥挂掉：重连一次再重试
                    log?.Invoke("[hdc] 桥无响应，重连后重试一次");
                    ResetBridge();
                    if (!EnsureBridge(_hdcPath, log))
                    {
                        return (false, "bridge lost");
                    }
                    req.Id = Interlocked.Increment(ref _seq);
                    _bridgeIn!.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
                    respLine = ReadBridgeLine(_bridgeOut!, timeoutMs + 5000);
                    if (respLine is null)
                    {
                        return (false, "bridge lost");
                    }
                }
                BridgeResp? resp = JsonSerializer.Deserialize<BridgeResp>(respLine, JsonOpts);
                if (resp is null)
                {
                    return (false, "bad bridge response");
                }
                return (resp.Ok, resp.Output ?? "");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[hdc] 桥调用异常：{ex.Message}");
                ResetBridge();
                return (false, ex.Message);
            }
        });
    }

    /// <summary>
    /// 带超时地从桥读一行响应。超时抛 TimeoutException，由调用方转成失败结果。
    /// 不能用裸 ReadLine：桥挂住时它会永久阻塞，进而卡死整条调用链与那把锁。
    /// </summary>
    private static string? ReadBridgeLine(StreamReader reader, int timeoutMs)
    {
        Task<string?> t = Task.Run(() => reader.ReadLine());
        if (!t.Wait(timeoutMs))
        {
            throw new TimeoutException($"桥响应超时（{timeoutMs}ms）");
        }
        return t.Result;
    }

    private static void ResetBridge()
    {
        lock (Gate)
        {
            try { _bridgeIn?.Dispose(); } catch { }
            try { _bridgeOut?.Dispose(); } catch { }
            try
            {
                if (_bridge is not null && !SafeHasExited(_bridge))
                {
                    _bridge.Kill(false);
                }
            }
            catch
            {
                // ignored
            }
            try { _bridge?.Dispose(); } catch { }
            _bridge = null;
            _bridgeIn = null;
            _bridgeOut = null;
        }
    }

    /// <summary>
    /// 确保 hdcd server 常驻。server 由桥进程派生，桥活着 server 就受它掩护，
    /// fport 规则不会因 server 消失而失效。
    /// </summary>
    public static void EnsureServer(string hdcPath, Action<string>? log = null)
    {
        try
        {
            if (!EnsureBridge(hdcPath, log))
            {
                return;
            }
            // ⚠ 这里**不能**同步等 HdcSerial 锁。
            // 之前用 GetAwaiter().GetResult() 在 WPF 线程上阻塞，而锁的释放方
            // 可能正需要 WPF 线程推进续体 → 死锁（日志停在 EnsureServer 之后）。
            // 本方法只由启动早期调用，此时没有任何 hdc 调用在并发，
            // 桥的读写天然串行；后续并发全部由 RunAsync 的 HdcSerial 覆盖。
            {
                var req = new BridgeReq
                {
                    Id = Interlocked.Increment(ref _seq),
                    Args = new[] { "__ENSURE_SERVER__" },
                    HdcPath = _hdcPath,
                };
                _bridgeIn!.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
                string? respLine = _bridgeOut!.ReadLine();
                if (respLine is not null)
                {
                    BridgeResp? r = JsonSerializer.Deserialize<BridgeResp>(respLine, JsonOpts);
                    log?.Invoke($"[hdc] server：{(r?.Ok == true ? r.Output : "失败 " + r?.Output)}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[hdc] EnsureServer 异常：{ex.Message}");
        }
    }

    /// <summary>本机端口是否 LISTEN（用来识破 fport 的假成功）。</summary>
    public static bool IsPortListening(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            Task t = c.ConnectAsync("127.0.0.1", port);
            return t.Wait(600) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>hdcd server（8710）是否在监听。</summary>
    public static bool IsServerListening() => IsPortListening(8710);

    /// <summary>关闭桥进程（App 退出时调用）。</summary>
    public static void Shutdown()
    {
        try
        {
            if (_bridgeIn is not null && _bridgeOut is not null && _bridge is not null
                && !SafeHasExited(_bridge))
            {
                var req = new BridgeReq
                {
                    Id = Interlocked.Increment(ref _seq),
                    Args = new[] { "__EXIT__" },
                };
                _bridgeIn.WriteLine(JsonSerializer.Serialize(req, JsonOpts));
                _bridge.WaitForExit(1500);
            }
        }
        catch
        {
            // ignored
        }
        ResetBridge();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private sealed class BridgeReq
    {
        public int Id { get; set; }
        public string[]? Args { get; set; }
        public int TimeoutMs { get; set; }
        public bool AcceptFail { get; set; }
        public string? HdcPath { get; set; }
        public string? WorkDir { get; set; }
    }

    private sealed class BridgeResp
    {
        public int Id { get; set; }
        public bool Ok { get; set; }
        public string? Output { get; set; }
        public long Ms { get; set; }
    }
}