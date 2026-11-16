using System.Diagnostics;
using System.Text;

namespace HarmOsWin.Core;

/// <summary>
/// 常驻 hdc shell 会话：把「每条命令起一个 hdc.exe 进程 + 一次设备侧 shell 冷启动」
/// 摊平成「一条长连接多命令」，用于压缩键盘 / 剪贴板这类低延迟路径的固定开销。
///
/// ## 为什么之前判死、现在为什么又能成立
///
/// 2026-11-14 第一次尝试用 <c>ProcessStartInfo.RedirectStandardInput</c> 喂命令，hdc 回：
/// <c>[W] Not support stdio TTY mode</c>，只吐一个提示符，第二条命令无响应。
/// 当时据此判定「hdc 不支持常驻交互 shell」——**这个结论是错的**。
/// 报错含义是「stdin 不是终端」，hdc 在**非 TTY** 下拒绝进入交互模式。
///
/// 现在改用 [[ConPty]] 给 hdc 套一个 Windows 伪终端（Win10 1809+），
/// isatty(stdin) 为真，hdc 正常进入交互 shell，同一条连接可反复执行命令。
///
/// ## 成本对照（无线，平板 192.168.1.100，2026-11-14 实测）
///
///   echo x                    217~244ms  ← 每条起进程的硬底
///   uinput -K -d N -u N       325~418ms
///   uitest uiInput text 'a'   556~604ms
///   uitest uiInput keyEvent   634~663ms
///
/// 本类要砍掉的是上面每一条里的「hdc.exe 进程启动 + 会话重建」，
/// 剩下的只有设备侧执行时间。
///
/// ## 为什么需要哨兵
///
/// 交互模式没有「命令结束」标记，stdout 何时读到头无法判断。
/// 每条命令追加 <c>; printf 'HC%s\n' END{n}</c>，读到 <c>HCEND{n}</c> 即认为本条返回。
/// marker 故意拆成两段拼接：终端会回显输入行，若 marker 以完整形式出现在命令行里，
/// 回显就会造成误命中。
///
/// ## 已知风险
///
/// <c>ReferenceCapture.WatchdogAsync</c> 记录过「运行期 hdc shell 会踢掉 fport 转发」
/// （2026-11-09：加唤屏后 30s 出帧 8 → 0）。所以本类仍**默认不启用**，
/// 只有 <c>HARMOS_HDC_SESSION=1</c> 才走；会话开不起来 / 超时 / 进程退出即永久降级回逐进程路径。
///
/// 设计约束：
///   · 严格串行（hdc 并发必互踢，INV-02）；
///   · 单条命令硬超时，超时即销毁会话，绝不挂住调用方；
///   · 空闲超时自动关闭；
///   · 任何异常只降级、不抛出。
/// </summary>
public sealed class HdcSession : IDisposable
{
    private readonly string _hdcPath;
    private readonly string _target;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    private ConPty.Session? _pty;
    private int _seq;
    private long _lastUseMs;
    private string _pendingErr = "";
    private string _eol = "\r";
    private bool _disposed;

    // ⚠ 2026-11-14 19:55 回滚说明：曾尝试改成「一条常驻专用读线程 + 共享 _rx 缓冲 + AutoResetEvent」，
    // 想解决 Task.Run 包裹阻塞 Read 导致的线程泄漏。但那个方案有**竞态缺陷**：
    // 读线程 Set() 之后、等待方 Clear() 之前若又有新数据追加，marker 会被一起清掉，
    // 导致探针必超时 —— 实测 19:50 连续 3 次 session timeout，**会话完全建不起来**（比原问题更糟）。
    // 已回滚到原实现。线程泄漏问题留待后续用「每条命令独立 ManualResetEventSlim + 独立缓冲」重做。

    /// <summary>空闲多久自动关闭（毫秒）。</summary>
    public int IdleCloseMs { get; set; } = 120_000;

    /// <summary>单条命令默认超时（毫秒）。</summary>
    public int CommandTimeoutMs { get; set; } = 2_500;

    /// <summary>会话是否处于打开状态（仅诊断用）。</summary>
    public bool IsOpen => _pty is { HasExited: false };

    public HdcSession(string hdcPath, string target, Action<string>? log = null)
    {
        _hdcPath = hdcPath;
        _target = target;
        _log = log;
    }

    /// <summary>
    /// 跑一条 shell 命令。返回 (墙钟耗时, 输出, 是否成功)。
    /// 会话不可用时返回 Ok=false，由调用方回退到逐命令路径 —— 本方法自身不抛异常。
    /// </summary>
    public async Task<(double ElapsedMs, string Output, bool Ok)> RunAsync(
        string command, int? timeoutMs = null)
    {
        var sw = Stopwatch.StartNew();
        if (_disposed)
        {
            return (0, "session disposed", false);
        }
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            // 被占用时立刻返回失败，让调用方走串行进程路径，绝不在这里排队（会放大延迟）
            return (0, "session busy", false);
        }
        try
        {
            // 🔴 必须和采集/注入的 hdc 调用共用同一把全进程闸门（INV-02：hdc 客户端并发必互踢）。
            // 2026-11-14 实测踩到：启动期采集正在跑 tconn/fport/file send，这里同时起 ConPTY 会话，
            // 结果探针 4 秒超时、连试两次 `\r`/`\n` 全部 session timeout，而同一个脚本在
            // 「采集已稳定」之后再跑就是 70~190ms。加锁后即可共存。
            using var lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
            if (IsOpen && Environment.TickCount64 - _lastUseMs > IdleCloseMs)
            {
                _log?.Invoke($"[hdc会话] 空闲 {(Environment.TickCount64 - _lastUseMs) / 1000}s，关闭");
                Close("idle");
            }
            if (!IsOpen && !await OpenAsync().ConfigureAwait(false))
            {
                return (sw.Elapsed.TotalMilliseconds, _pendingErr, false);
            }
            return await SendAndReadAsync(command, timeoutMs ?? CommandTimeoutMs, sw).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 真正收发的那一段。**不再取 _gate** —— 调用方（RunAsync / OpenAsync 的探针）
    /// 已经持有串行权。
    /// </summary>
    /// <param name="eol">
    /// 行结束符。建链探针必须显式传入正在尝试的那个，**不能读字段 <c>_eol</c>**：
    /// 字段只有建链成功后才赋值，否则第二次尝试（<c>\n</c>）实际仍发的是 <c>\r</c>，
    /// 「自动回退另一种行结束符」形同虚设（2026-11-14 修正）。
    /// </param>
    private async Task<(double ElapsedMs, string Output, bool Ok)> SendAndReadAsync(
        string command, int timeoutMs, Stopwatch sw, string? eol = null)
    {
        string terminator = eol ?? _eol;
        var pty = _pty;
        if (pty is null)
        {
            return (sw.Elapsed.TotalMilliseconds, "session not open", false);
        }

        int seq = ++_seq;
        string marker = $"HCEND{seq}";
        // marker 拆成 'HC%s' + END{n}，命令行回显里不会出现完整 marker
        string line = command + $"; printf 'HC%s\\n' END{seq}" + terminator;
        string echoSource = command.Trim();

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await pty.Input.WriteAsync(bytes).ConfigureAwait(false);
            await pty.Input.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Close($"写入失败: {ex.Message}");
            return (sw.Elapsed.TotalMilliseconds, ex.Message, false);
        }

        // 挂上「等待者」，等专用读线程把 marker 投回来
        var sb = new StringBuilder();
        byte[] buf = new byte[8192];
        char[] chars = new char[8192];
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            int n;
            try
            {
                // FileStream 包的是同步管道句柄，Read 会阻塞；用 Task.Run + WaitAsync
                // 保证超时能立刻返回（真取消靠 Close 关句柄让 Read 抛异常）。
                n = await Task.Run(() => pty.Output.Read(buf, 0, buf.Length), CancellationToken.None)
                                .WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Close("命令超时");
                return (sw.Elapsed.TotalMilliseconds, "session timeout", false);
            }
            catch (Exception ex)
            {
                Close($"读取失败: {ex.Message}");
                return (sw.Elapsed.TotalMilliseconds, ex.Message, false);
            }
            if (n <= 0)
            {
                Close("hdc 进程已退出");
                return (sw.Elapsed.TotalMilliseconds, "session eof", false);
            }

            int cn = _decoder.GetChars(buf, 0, n, chars, 0);
            sb.Append(chars, 0, cn);

            if (sb.Length > 1_000_000)
            {
                Close("输出过大");
                return (sw.Elapsed.TotalMilliseconds, "session output overflow", false);
            }
            if (sb.ToString().Contains(marker, StringComparison.Ordinal))
            {
                string text = CleanOutput(sb.ToString(), marker, echoSource);
                _lastUseMs = Environment.TickCount64;
                bool lost = text.Contains("Not match target", StringComparison.Ordinal)
                            || text.Contains("need connect-key", StringComparison.Ordinal)
                            || text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Contains("target", StringComparison.OrdinalIgnoreCase);
                return (sw.Elapsed.TotalMilliseconds, text, !lost);
            }
        }
    }

    /// <summary>
    /// 清理终端产物：截到 marker 之前、去掉 ANSI 序列、去掉命令行回显与提示符行。
    /// 交互 shell 会把输入原样回显，不去掉的话调用方会在「输出」里看到自己的命令，
    /// 进而误判（例如把命令里的 `not found` 当成执行结果）。
    /// </summary>
    private static string CleanOutput(string raw, string marker, string echoSource)
    {
        int idx = raw.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            raw = raw[..idx];
        }
        raw = StripAnsi(raw);

        var kept = new List<string>();
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').TrimEnd();
            string t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }
            // 提示符单独成行
            if (t is "$" or "#" or ">" or "> ")
            {
                continue;
            }
            // 命令行回显（可能带提示符前缀）：包含我们发出的命令原文即丢弃
            if (echoSource.Length > 0 && t.Contains(echoSource, StringComparison.Ordinal))
            {
                continue;
            }
            // marker 那条 printf 的回显
            if (t.Contains("printf 'HC%s", StringComparison.Ordinal))
            {
                continue;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }

    /// <summary>剥掉 ANSI / VT 控制序列（ConPTY 会输出光标与颜色指令）。</summary>
    private static string StripAnsi(string s)
    {
        if (s.IndexOf('\u001b') < 0)
        {
            return s;
        }
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\u001b')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= s.Length)
            {
                break;
            }
            char kind = s[++i];
            if (kind == '[')
            {
                // CSI: 参数字节 0x30-0x3F，中间字节 0x20-0x2F，结束字节 0x40-0x7E
                while (i + 1 < s.Length)
                {
                    char d = s[++i];
                    if (d >= '@' && d <= '~')
                    {
                        break;
                    }
                }
            }
            else if (kind == ']')
            {
                // OSC: 到 BEL 或 ST 结束
                while (i + 1 < s.Length)
                {
                    char d = s[++i];
                    if (d == '\a')
                    {
                        break;
                    }
                    if (d == '\u001b' && i + 1 < s.Length && s[i + 1] == '\\')
                    {
                        i++;
                        break;
                    }
                }
            }
        }
        return sb.ToString();
    }

    private async Task<bool> OpenAsync()
    {
        _pendingErr = "";
        // 交互 shell 的行结束符：优先 \r（终端回车），失败再试 \n
        foreach (string eol in new[] { "\r", "\n" })
        {
            if (await TryOpenAsync(eol).ConfigureAwait(false))
            {
                _eol = eol;
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryOpenAsync(string eol)
    {
        try
        {
            if (!ConPty.Available)
            {
                _pendingErr = "系统不支持 ConPTY";
                return false;
            }

            string args = $"-t {_target} shell";
            var pty = ConPty.Start(_hdcPath, args);
            _pty = pty;
            _decoder.Reset();

            // 🔴 必须先等 shell 就绪并把启动残留读掉（2026-11-14 02:1x 定位）。
            // 独立验证脚本与这里的唯一差别就在这里：脚本 `Start` 之后 sleep 800ms + Read 一次
            // 把 hdc 的启动横幅 / 首个提示符清掉，然后才发命令，实测 68~194ms；
            // 原来这里 Start 完立刻发探针，此时交互 shell 还没准备好消化输入，
            // 探针必然读不到 marker → 4 秒超时 → 会话被判死并永久降级。
            await Task.Delay(600).ConfigureAwait(false);
            DrainStartupOutput(pty);

            // 探针：验证会话真的进到了交互 shell（显式传入当前尝试的 eol）
            var probe = await SendAndReadAsync("echo hc_session_ready", 4_000,
                                               Stopwatch.StartNew(), eol).ConfigureAwait(false);
            if (!probe.Ok || !probe.Output.Contains("hc_session_ready", StringComparison.Ordinal))
            {
                Close($"探针失败: {Short(probe.Output)}");
                _pendingErr = $"会话不可用: {Short(probe.Output)}";
                return false;
            }
            _lastUseMs = Environment.TickCount64;
            _log?.Invoke($"[hdc会话] 已建立（ConPTY，eol={(eol == "\r" ? "CR" : "LF")}，探针 {probe.ElapsedMs:F0}ms）");
            return true;
        }
        catch (Exception ex)
        {
            Close($"打开失败: {ex.Message}");
            _pendingErr = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把 hdc 启动横幅与首个提示符读掉：这些残留会混进第一条命令的输出，
    /// 更糟的是「还没进入交互循环时」发的命令会被丢弃，导致探针永远读不到 marker。
    /// 与独立验证脚本的 warmup 一步等价。
    /// </summary>
    private static void DrainStartupOutput(ConPty.Session pty)
    {
        try
        {
            byte[] buf = new byte[8192];
            // 反复读到「一次读不出东西」为止（管道为空时同步 Read 会阻塞，交给短超时兜底）
            for (int i = 0; i < 8; i++)
            {
                var t = Task.Run(() => pty.Output.Read(buf, 0, buf.Length));
                if (!t.Wait(150))
                {
                    break;   // 150ms 内没有新数据，认为启动输出已读完
                }
                if (t.Result <= 0)
                {
                    break;
                }
            }
        }
        catch
        {
            // 读残留失败不影响建链，后续探针会暴露真实问题
        }
    }

    private static string Short(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 120 ? s : s[..120] + "…";
    }

    private void Close(string why)
    {
        var pty = _pty;
        _pty = null;
        try { pty?.Dispose(); } catch { }
        _log?.Invoke($"[hdc会话] 关闭（{why}）");
    }

    public void Dispose()
    {
        _disposed = true;
        Close("dispose");
        _gate.Dispose();
    }
}
