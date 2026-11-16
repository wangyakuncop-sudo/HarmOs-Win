using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HarmOsWin.Core;

/// <summary>
/// uitest 持久 socket（RPC）注入通道 —— GATE-06 的达标路径。
///
/// 原理（逆向 + 真机实测确认）：设备侧 <c>/system/bin/uitest start-daemon singleness &amp;</c>
/// 加载 <c>/data/local/tmp/agent.so</c> 后，在本机抽象域名空间监听 <c>@uitest_socket</c>；
/// PC 用 <c>hdc fport tcp:&lt;local&gt; localabstract:uitest_socket</c> 把它映射到 127.0.0.1。
///
/// 报文格式（实测确认，必须大端）：
/// <code>
/// "_uitestkit_rpc_message_head_" | uint32 sessionId(BE) | uint32 bodyLen(BE) | JSON body | "_uitestkit_rpc_message_tail_"
/// </code>
/// JSON：<c>{"module":"com.ohos.devicetest.hypiumApiHelper","method":"Gestures|Captures","params":{"api":..,"args":..}}</c>
///
/// 实测延迟（无线，20 次）：touchDown p50 12.94ms / touchUp p50 9.13ms / 一次完整点击 p50 21.65ms。
/// pressBack / pressHome 走 hypium 的 UI 稳定等待，124~407ms（语义是「等界面切完」，不是通道慢）。
///
/// 注意：hdc 的 target 会话在 hdcd 侧维护，空闲 90s 实测不掉；掉了要重做 tconn(约1.8s)+fport，
/// 所以这里所有失败都走 <see cref="EnsureConnectedAsync"/> 统一自愈。
/// </summary>
public sealed class UitestRpcClient : IAsyncDisposable
{
    private const string Head = "_uitestkit_rpc_message_head_";
    private const string Tail = "_uitestkit_rpc_message_tail_";
    private const string ApiModule = "com.ohos.devicetest.hypiumApiHelper";
    private const string RemoteAgentPath = "/data/local/tmp/agent.so";

    private readonly string _hdcPath;
    private readonly string _target;
    private readonly int _localPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _headBytes = Encoding.ASCII.GetBytes(Head);
    private readonly byte[] _tailBytes = Encoding.ASCII.GetBytes(Tail);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private uint _sid;
    private bool _agentProvisioned;

    /// <summary>累计建立连接次数，用于诊断（正常应一直为 1）。</summary>
    public int ConnectCount { get; private set; }

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    /// <summary>
    /// 本地 agent.so 路径（随 PC 端分发，通常 &lt;输出目录&gt;/assets/agent.so）。
    /// 建链前会与设备的 /data/local/tmp/agent.so 比 md5，不一致或缺文件就自动推送。
    /// 置 null 表示不做自动部署（设备上已有 so 时可用）。
    /// </summary>
    public string? AgentSoPath { get; set; }

    /// <summary>最近一次 agent.so 部署的结果，给 UI / 自测用。</summary>
    public string AgentStatus { get; private set; } = "未检查";

    /// <summary>打开后把每条 hdc 命令的原始输出打到 stderr，用于排查连接问题。</summary>
    public bool Verbose { get; set; }

    public UitestRpcClient(string hdcPath, string target, int localPort = 28571)
    {
        _hdcPath = hdcPath;
        _target = target;
        _localPort = localPort;
    }

    /// <summary>建立 tconn → fport → TCP 长连接。可重复调用，已连则直接返回。</summary>
    public async Task<bool> ConnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await EnsureConnectedLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 通用指针手势：touchDown / touchMove / touchUp / AxisUp / AxisDown /
    /// ButtonLeftDown / ButtonLeftMove / ButtonLeftUp 等。
    /// 实测（2026-11-09）：touchMove p50 9.6ms / p95 13.5ms，20 步拖动 270ms（74 步/秒），
    /// 足以支撑接近原生的拖动与滚轮。
    /// </summary>
    public async Task<(double ElapsedMs, bool Ok)> PointerAsync(
        string api, int x, int y, int value = 0)
    {
        string args = value != 0
            ? $"{{\"x\":{x},\"y\":{y},\"value\":{value}}}"
            : $"{{\"x\":{x},\"y\":{y}}}";
        var r = await CallAsync("Gestures", api, args).ConfigureAwait(false);
        return (r.ElapsedMs, r.Ok);
    }

    /// <summary>一次完整点击：touchDown + 短间隔 + touchUp。返回墙钟耗时（ms）。</summary>
    public async Task<(double ElapsedMs, bool Ok)> TapAsync(int x, int y, int holdMs = 20)
    {
        var sw = Stopwatch.StartNew();
        var down = await CallAsync("Gestures", "touchDown", argsJson: $"{{\"x\":{x},\"y\":{y}}}")
            .ConfigureAwait(false);
        if (!down.Ok)
        {
            return (sw.Elapsed.TotalMilliseconds, false);
        }
        if (holdMs > 0)
        {
            await Task.Delay(holdMs).ConfigureAwait(false);
        }
        var up = await CallAsync("Gestures", "touchUp", argsJson: $"{{\"x\":{x},\"y\":{y}}}")
            .ConfigureAwait(false);
        return (sw.Elapsed.TotalMilliseconds, up.Ok);
    }

    /// <summary>三键导航。Back/Home 走 hypium 的 pressBack/pressHome（含 UI 稳定等待）。</summary>
    public async Task<(double ElapsedMs, bool Ok)> PressNavAsync(byte navKey)
    {
        string api = navKey switch
        {
            NavKey.Back => "pressBack",
            NavKey.Home => "pressHome",
            _ => string.Empty
        };
        if (api.Length == 0)
        {
            // 最近任务在 hypium 里没有稳定 api，交由调用方回退到 uinput
            return (0, false);
        }
        var r = await CallAsync("Gestures", api, argsJson: "{}").ConfigureAwait(false);
        return (r.ElapsedMs, r.Ok);
    }

    /// <summary>发一条 RPC 并等待 JSON 响应。失败自动重连一次再重试。</summary>
    public async Task<(double ElapsedMs, bool Ok, string Response)> CallAsync(
        string method, string api, string argsJson = "{}", int timeoutMs = 3000)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedLockedAsync().ConfigureAwait(false))
            {
                return (0, false, "connect failed");
            }
            var first = await SendRecvLockedAsync(method, api, argsJson, timeoutMs).ConfigureAwait(false);
            if (first.Ok)
            {
                return first;
            }
            // 通道异常：丢弃连接重建（tconn 约 1.8s，属自愈成本，不进稳态统计）
            TearDownLocked();
            if (!await EnsureConnectedLockedAsync().ConfigureAwait(false))
            {
                return first;
            }
            return await SendRecvLockedAsync(method, api, argsJson, timeoutMs).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(double ElapsedMs, bool Ok, string Response)> SendRecvLockedAsync(
        string method, string api, string argsJson, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _sid++;
            // 手写 JSON：args 均为数字/固定串，无需反射序列化（实测省掉约 30ms/次）
            string json = $"{{\"module\":\"{ApiModule}\",\"method\":\"{method}\"," +
                          $"\"params\":{{\"api\":\"{api}\",\"args\":{argsJson}}}}}";
            byte[] body = Encoding.UTF8.GetBytes(json);

            // 报文头：head + sid(BE) + len(BE) + body + tail
            var packet = new byte[_headBytes.Length + 8 + body.Length + _tailBytes.Length];
            int o = 0;
            Buffer.BlockCopy(_headBytes, 0, packet, o, _headBytes.Length);
            o += _headBytes.Length;
            WriteUInt32Be(packet, o, _sid);
            o += 4;
            WriteUInt32Be(packet, o, (uint)body.Length);
            o += 4;
            Buffer.BlockCopy(body, 0, packet, o, body.Length);
            o += body.Length;
            Buffer.BlockCopy(_tailBytes, 0, packet, o, _tailBytes.Length);

            var stream = _stream!;
            await stream.WriteAsync(packet).ConfigureAwait(false);

            string text = await ReadMessageAsync(stream, timeoutMs).ConfigureAwait(false);
            sw.Stop();

            if (text.Length == 0)
            {
                return (sw.Elapsed.TotalMilliseconds, false, "empty response");
            }
            // 服务端异常也是合法响应（exception 字段），只有空响应/IO 失败才算通道故障
            bool hasException = text.Contains("\"exception\"", StringComparison.Ordinal);
            return (sw.Elapsed.TotalMilliseconds, !hasException, text);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or ObjectDisposedException or TimeoutException)
        {
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>读一条完整响应：终止于 tail（带帧）或 JSON 换行（裸响应）。</summary>
    private async Task<string> ReadMessageAsync(NetworkStream stream, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var sb = new MemoryStream();
        var buf = new byte[8192];
        string tailStr = Tail;
        while (!cts.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            sb.Write(buf, 0, n);
            string sofar = Encoding.UTF8.GetString(sb.GetBuffer(), 0, (int)sb.Length);
            int t = sofar.IndexOf(tailStr, StringComparison.Ordinal);
            if (t >= 0)
            {
                int h = sofar.IndexOf(Head, StringComparison.Ordinal);
                return h >= 0 ? sofar[(h + Head.Length + 8)..t].Trim() : sofar[..t].Trim();
            }
            int brace = sofar.IndexOf('{');
            int close = sofar.LastIndexOf('}');
            if (brace >= 0 && close > brace)
            {
                string candidate = sofar[brace..(close + 1)];
                try
                {
                    using var _ = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException)
                {
                    // 还没收全，继续读
                }
            }
        }
        return string.Empty;
    }

    private static void WriteUInt32Be(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    /// <summary>调用方必须持有 _gate。设备端 daemon 不在时先拉起（幂等）。</summary>
    private async Task<bool> EnsureConnectedLockedAsync()
    {
        if (IsConnected)
        {
            return true;
        }
        TearDownLocked();

        // 0) 确保 /data/local/tmp/agent.so 就位。
        //    顺序不能反：daemon 若在 so 缺失时启动，会加载出「空壳」——socket 还在、请求全返回失败。
        bool agentChanged = await EnsureAgentAsync().ConfigureAwait(false);

        // 1) 确保 uitest daemon 在跑，且加载的是刚部署的 so
        _ = await EnsureDaemonAsync(agentChanged).ConfigureAwait(false);

        // 2) tconn 重连（无线 TCP 会话跨进程会掉，这是 hdc 的已知行为）。
        //    注意 tconn 不接受 -t 前缀，只能裸调用。
        var tconn = await ExecHdcAsync("tconn", _target).ConfigureAwait(false);
        if (!tconn.Ok)
        {
            return false;
        }

        // 3) 端口转发。策略是「先复用、后重建」：
        //    残留的 hdcd/hdc 进程会让 `fport rm` 删不掉规则、`fport add` 又报 "TCP Port listen failed"，
        //    但此时映射往往已经存在且可用（2026-11-09 实测踩到，白排查很久）。
        //    所以：先直接连一次 TCP，连得上就完全跳过 fport 操作（也省掉一次 hdc 往返）。
        if (await TryConnectTcpAsync().ConfigureAwait(false))
        {
            ConnectCount++;
            return true;
        }

        _ = await ExecHdcAsync("-t", _target, "fport", "rm", $"tcp:{_localPort}").ConfigureAwait(false);
        _ = await ExecHdcAsync(
            "-t", _target, "fport", $"tcp:{_localPort}", "localabstract:uitest_socket").ConfigureAwait(false);

        // 4) 二次尝试。fport 的返回码在这里不可信（见上），以 TCP 能否连上为准。
        if (await TryConnectTcpAsync().ConfigureAwait(false))
        {
            ConnectCount++;
            return true;
        }
        return false;
    }

    /// <summary>尝试建立 TCP 长连接；成功则装载到实例上。调用方需持有 _gate。</summary>
    private async Task<bool> TryConnectTcpAsync()
    {
        try
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", _localPort).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            TearDownLocked();
            return false;
        }
    }

    private void TearDownLocked()
    {
        try { _stream?.Dispose(); } catch { /* 忽略 */ }
        try { _client?.Dispose(); } catch { /* 忽略 */ }
        _stream = null;
        _client = null;
    }

    /// <summary>
    /// 保证 uitest daemon 在跑，且是「加载过当前 agent.so」的那一个。
    /// 只在 so 被换掉时重启：否则每次建链都 pkill 会白等一轮冷启动（实测约 300~800ms）。
    /// </summary>
    private async Task<bool> EnsureDaemonAsync(bool agentChanged)
    {
        // agent.so 被换掉时旧 daemon 仍持着旧 so，必须重启才能加载新的。
        // 注意：pkill 在这里可能因权限失败（daemon 以 root 身份跑），杀不掉时
        // 下面会检测到「已在跑」而直接复用 —— 结果就是加载了旧 so，属已知限制。
        if (agentChanged)
        {
            _ = await ExecHdcAsync("-t", _target, "shell", "pkill -f 'uitest start-daemon'").ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
        }

        // 判活用 @uitest_socket 是否存在，而不是 ps 的文本匹配：
        // `ps -ef | grep` 在无匹配时 hdc 会返回非 0，之前据此判「没跑」会重复拉起。
        // ⚠ 不用管道：设备端 `|` 会让 grep 等 stdin 且输出 0 字节（2026-11-15 实测）。
        // 直接让 grep 读文件，或拿全量再判断。
        var (ok, lsOut) = await ExecHdcAsync(
            "-t", _target, "shell", "grep uitest_socket /proc/net/unix").ConfigureAwait(false);
        if (ok && lsOut.Contains("uitest_socket", StringComparison.Ordinal))
        {
            return true;
        }

        // `&` 让 hdc shell 立即返回，daemon 由 init 收养（PPID=1）常驻
        _ = await ExecHdcAsync("-t", _target, "shell", "uitest start-daemon singleness &").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false); // 等 @uitest_socket 起来
        return true;
    }

    /// <summary>
    /// 把随包分发的 agent.so 部署到设备。设备端 /data/local/tmp 是 shell:shell 0771，
    /// 应用沙箱写不进去，所以只能由 PC 侧 hdc 推送 —— 这是「零手工步骤」的唯一可行通道。
    /// 同一实例内只做一次（成功后置位；失败保留重试机会）。
    /// </summary>
    /// <returns>true = 本次真的写了设备（daemon 需重启才能加载新 so）。</returns>
    private async Task<bool> EnsureAgentAsync()
    {
        if (_agentProvisioned)
        {
            return false;
        }

        string? local = AgentSoPath;
        if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
        {
            AgentStatus = string.IsNullOrWhiteSpace(local)
                ? "跳过：未指定本地 agent.so"
                : $"跳过：本地不存在 {local}";
            _agentProvisioned = true; // 与设备已有 so 兼容，不阻塞建链
            return false;
        }

        string localMd5;
        try
        {
            using var fs = File.OpenRead(local);
            localMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AgentStatus = $"跳过：本地 agent.so 读取失败（{ex.Message}）";
            _agentProvisioned = true;
            return false;
        }

        // hdc target 会话跨进程会掉，推送前先握手
        _ = await ExecHdcAsync("tconn", _target).ConfigureAwait(false);

        string remoteMd5 = await RemoteAgentMd5Async().ConfigureAwait(false);
        if (string.Equals(remoteMd5, localMd5, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatus = $"已就位（md5 {localMd5[..8]}…）";
            _agentProvisioned = true;
            return false;
        }

        var send = await SendFileAsync(local).ConfigureAwait(false);
        if (!send.Ok)
        {
            AgentStatus = $"推送失败：{Clip(send.Output)}";
            return false; // 不置位，下次建链再试
        }
        _ = await ExecHdcAsync("-t", _target, "shell", $"chmod 755 {RemoteAgentPath}").ConfigureAwait(false);

        string after = await RemoteAgentMd5Async().ConfigureAwait(false);
        if (string.Equals(after, localMd5, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatus = $"已推送并校验通过（md5 {localMd5[..8]}…）";
            _agentProvisioned = true;
            return true; // 设备侧变了 → 调用方负责重启 daemon
        }

        AgentStatus = after.Length == 0 ? "推送后读不到 md5" : $"推送后 md5 不符（设备 {after[..8]}…）";
        return false;
    }

    private async Task<string> RemoteAgentMd5Async()
    {
        var (ok, text) = await ExecHdcAsync("-t", _target, "shell", $"md5sum {RemoteAgentPath}").ConfigureAwait(false);
        if (!ok)
        {
            return string.Empty;
        }
        foreach (string token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 32 && token.All(Uri.IsHexDigit))
            {
                return token;
            }
        }
        return string.Empty;
    }

    private async Task<(bool Ok, string Output)> SendFileAsync(string localPath)
    {
        // hdc 全局串行：双路并行下并发调用会互踢，且会影响采集桥的 fport 目标（见 HdcSerial）
        using IDisposable lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
        var psi = new ProcessStartInfo(_hdcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 用「工作目录 + 相对文件名」，避开绝对路径里可能出现的空格/中文被拼接到 cwd 后的坑
            WorkingDirectory = Path.GetDirectoryName(localPath) ?? "."
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(_target);
        psi.ArgumentList.Add("file");
        psi.ArgumentList.Add("send");
        psi.ArgumentList.Add(Path.GetFileName(localPath));
        // hdc 的远端参数一律**当目录**处理（见 InputInjector.SendFileAsync 的 ① 号坑）：
        // 直接传 /data/local/tmp/agent.so 会把文件塞进同名目录，落地成 agent.so/agent.so。
        // 所以这里给目录，并先清掉可能残留的同名目录。
        _ = await ExecHdcAsync("-t", _target, "shell", $"rm -rf {RemoteAgentPath}").ConfigureAwait(false);
        psi.ArgumentList.Add("/data/local/tmp/");

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "start hdc failed");
            }
            string stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            string text = (stdout + stderr).Trim();
            bool ok = p.ExitCode == 0
                      && !text.Contains("[Fail]", StringComparison.Ordinal)
                      && !text.Contains("Not match target", StringComparison.Ordinal);
            return (ok, text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private static string Clip(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private async Task<(bool Ok, string Output)> ExecHdcAsync(params string[] args)
    {
        // hdc 全局串行：双路并行下并发调用会互踢，且会影响采集桥的 fport 目标（见 HdcSerial）
        using IDisposable lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
        var psi = new ProcessStartInfo(_hdcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "start hdc failed");
            }
            string stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            string text = (stdout + stderr).Trim();
            bool ok = p.ExitCode == 0
                      && !text.Contains("[Fail]", StringComparison.Ordinal)
                      && !text.Contains("Not match target", StringComparison.Ordinal)
                      && !text.Contains("need connect-key", StringComparison.Ordinal);
            if (Verbose)
            {
                Console.Error.WriteLine($"[hdc] {string.Join(' ', args)} => exit={p.ExitCode} ok={ok} | {text}");
            }
            return (ok, text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            TearDownLocked();
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }
}
