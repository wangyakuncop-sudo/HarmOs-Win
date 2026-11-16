using System.Diagnostics;

namespace HarmOsWin.Core;

/// <summary>统一输入事件（参考官方 InputEventInjector 的分层思路，代码自研）。</summary>
public struct InputEvent
{
    public byte Type;
    public byte Action;
    public ushort X;
    public ushort Y;
    public ushort Key;
    public uint TimestampMs;
}

public interface IInputChannel
{
    string Name { get; }
    string BuildShellCommand(InputEvent ev);
    string BuildShellCommand(byte navKey);
}

/// <summary>uitest uiInput 通道：一次一条 hdc shell，实测开销最小（&lt;1ms socket 级）。</summary>
public sealed class UitestChannel : IInputChannel
{
    public string Name => "uitest";

    // 鸿蒙系统键码，取值全部来自本机 SDK 原文
    //   E:/DevEco Studio/sdk/default/openharmony/ets/api/@ohos.multimodalInput.keyCode.d.ts
    //   KEYCODE_HOME              = 1    (d.ts:47  "Function (Home) key")
    //   KEYCODE_BACK              = 2    (d.ts:54  "Back key")
    //   KEYCODE_VIRTUAL_MULTITASK = 2210 (d.ts:1040 "Multi-task key")
    //   KEYCODE_TASKMANAGER       = 2717 (d.ts:1955 "Task Manager key")
    //
    // 两处历史错误，已按 SDK 原文纠正（此前是照搬 Android，属错误取值）：
    //   ① 旧 KeyHome = 3，鸿蒙 3 是 KEYCODE_CALL（拨号），不是 Home；
    //   ② 旧 KeyRecents = 187，187 是 Android 的 KEYCODE_APP_SWITCH，
    //      鸿蒙 KeyCode 全表没有 187 这个值（grep " 187," 结果为 0）。
    public const ushort KeyBack = 2;
    public const ushort KeyHome = 1;

    // ⚠ 下面两个多任务键码**实测无效**，仅作留档，不要再用：
    // 2026-11-11 00:29 在 MatePad Mini（HarmonyOS 7.0.0.111，手势导航）上，
    // `uinput -K -d 2210 -u 2210` 与 `uinput -K -d 2717 -u 2717`、`uitest uiInput keyEvent 2210`
    // 桌面画面全部零变化；同批次 `uinput -K -d 1 -u 1`（HOME）正常回桌面，说明命令通道没问题，
    // 是系统在手势导航模式下不消费这些键。多任务请用
    // ReferenceTouchChannel.BuildRecentsGesture()（底部上滑悬停），该条实测稳定生效。
    public const ushort KeyRecents = 2210;
    public const ushort KeyTaskManager = 2717;

    /// <summary>
    /// 【已弃用】多任务键码覆盖开关。真机实测键码无效后，多任务改走
    /// ReferenceTouchChannel.BuildRecentsGesture()，本属性只为兼容旧调用路径保留。
    /// </summary>
    public static ushort RecentsKey
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("HARMOS_RECENTS_KEY");
            return ushort.TryParse(env, out ushort v) && v > 0 ? v : KeyRecents;
        }
    }

    public string BuildShellCommand(InputEvent ev)
    {
        if (ev.Type == EventKind.Key)
        {
            return $"uitest uiInput keyEvent {ev.Key}";
        }
        if (ev.Action == EventAction.Move)
        {
            return $"uitest uiInput swipe {ev.X} {ev.Y} {ev.X} {ev.Y} 20";
        }
        return $"uitest uiInput click {ev.X} {ev.Y}";
    }

    public string BuildShellCommand(byte navKey)
    {
        ushort key = navKey switch
        {
            NavKey.Back => KeyBack,
            NavKey.Home => KeyHome,
            _ => RecentsKey
        };
        return $"uitest uiInput keyEvent {key}";
    }
}

/// <summary>
/// uinput 通道：独立进程注入。
/// 命令形态以 `uinput -h` 为准：键盘用 `-K -d &lt;key&gt; -u &lt;key&gt;`（按下+抬起），
/// 触摸点击用 `-T -c &lt;x&gt; &lt;y&gt;`。旧版写的 `-K -k` 是非法参数，会被 uinput 直接拒绝，
/// 这是 GATE-03 当年误判「hdc 通道不可用」的原因之一。
/// </summary>
public sealed class UinputChannel : IInputChannel
{
    public string Name => "uinput";

    public string BuildShellCommand(InputEvent ev) =>
        ev.Type == EventKind.Key
            ? $"uinput -K -d {ev.Key} -u {ev.Key}"
            : $"uinput -T -c {ev.X} {ev.Y}";

    public string BuildShellCommand(byte navKey) =>
        $"uinput -K -d {ResolveKey(navKey)} -u {ResolveKey(navKey)}";

    // 键码同 UitestChannel，全部取鸿蒙官方 KeyCode 真值（Back=2 / Home=1 / 多任务=2210）
    private static ushort ResolveKey(byte navKey) => navKey switch
    {
        NavKey.Back => UitestChannel.KeyBack,
        NavKey.Home => UitestChannel.KeyHome,
        _ => UitestChannel.RecentsKey
    };
}

/// <summary>
/// 注入传输方式。
/// BashScript：走 scripts/hdcw.sh（内含 tconn 重连 + 串行锁），实测每条 2.5s，适合脚本运维。
/// DirectHdc：直连 hdc.exe，跳过 tconn 与 bash，实测每条约 200ms（2.5 个无线 RTT）。
/// 二者都是 hdc 通道；DirectHdc 是 GATE-06 唯一还能达标的路径（无障碍扩展服务在本机 ROM 无开启入口）。
/// </summary>
public enum InjectTransport
{
    BashScript,
    DirectHdc
}

/// <summary>
/// 通过 scripts/hdcw.sh 注入。INV-02：所有 hdc 调用必须严格串行，这里用信号量全局串行化。
/// 返回值是「从发起命令到 shell 返回」的墙钟耗时，用于 GATE-03 通道择优。
/// </summary>
public sealed class InputInjector : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _projectDir;
    private readonly string _bash;
    private readonly string _hdcPath;
    private string _hdcTarget;

    // 常驻 hdc shell 会话（可选路径，默认关闭，见 HdcSession 类注释里的风险说明）。
    private HdcSession? _session;
    private bool _sessionDisabled;
    private int _sessionFails;
    private int _sessionHits;
    private int _processHits;

    /// <summary>诊断日志出口（由 App 层接到 AppLog）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>会话复用是否启用（`HARMOS_HDC_SESSION=1` 才开）。</summary>
    public bool SessionEnabled => _session is not null && !_sessionDisabled;

    public IInputChannel Channel { get; set; }

    /// <summary>传输方式，可在运行期切换（默认直连 hdc）。</summary>
    public InjectTransport Transport { get; set; } = InjectTransport.DirectHdc;

    /// <summary>直连模式下是否已完成过一次预热（首条命令要建链，约 1.5s，不计入统计）。</summary>
    public bool WarmedUp { get; private set; }

    /// <summary>hdc.exe 绝对路径（供 UitestRpcClient 复用同一份配置）。</summary>
    public string HdcPath => _hdcPath;

    /// <summary>
    /// hdc 无线调试目标，形如 192.168.1.100:38803。
    /// 端口是动态的（设备重启 / 开关无线调试就变），所以启动时会由
    /// <see cref="HdcTargetResolver"/> 自动探测并回写到这里，运行期也可改。
    /// </summary>
    public string HdcTarget
    {
        get => _hdcTarget;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                string v = value.Trim();
                if (!string.Equals(v, _hdcTarget, StringComparison.OrdinalIgnoreCase))
                {
                    // 目标变了，旧会话指向的是另一台设备/另一个端口，必须重建
                    _session?.Dispose();
                    _session = NewSessionIfEnabled();
                }
                _hdcTarget = v;
            }
        }
    }

    public InputInjector(string projectDir, IInputChannel? channel = null, string? bash = null,
                         InjectTransport transport = InjectTransport.DirectHdc,
                         string? hdcPath = null, string? hdcTarget = null)
    {
        _projectDir = projectDir;
        _bash = bash ?? Environment.GetEnvironmentVariable("HARMOS_BASH") ?? "bash";
        _hdcPath = hdcPath
            ?? Environment.GetEnvironmentVariable("HARMOS_HDC")
            ?? @"E:\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe";
        // 无线调试端口是动态的，必须由外部给出；默认值对应当前平板
        _hdcTarget = hdcTarget
            ?? Environment.GetEnvironmentVariable("HARMOS_HDC_TARGET")
            ?? "192.168.1.100:38803";
        Channel = channel ?? new UitestChannel();
        Transport = transport;
        _session = NewSessionIfEnabled();
    }

    /// <summary>
    /// 建常驻 hdc 会话（ConPTY）。**2026-11-14 02:12 起默认开启**；
    /// 显式设 <c>HARMOS_HDC_SESSION=0</c> 可关闭。
    ///
    /// ## 新结论（推翻此前「会话维持不住」的判断）
    ///
    /// 02:12 在**投屏满跑**的状态下用独立 ConPTY 脚本复测同一台平板：
    ///
    ///   echo（第 1~4 条）  194 / 97 / 78 / 68 ms
    ///
    /// 通道可用，**fport 没有被踢**，画面也没断。此前「会话必然 session timeout」的真实原因
    /// 不是 tconn 重置，而是 <see cref="HdcSession.TryOpenAsync"/> 里少了一步 warmup：
    /// 独立脚本 `Start` 之后会 sleep 800ms 并读走启动残留，程序里却是建完链立刻发探针，
    /// 交互 shell 还没就绪 → 探针读不到 marker → 4 秒超时 → 被判死永久降级。
    /// 已在该处补上「等 600ms + DrainStartupOutput」。详见补记 61。
    /// </summary>
    private HdcSession? NewSessionIfEnabled()
    {
        string? flag = Environment.GetEnvironmentVariable("HARMOS_HDC_SESSION");
        bool off = flag is "0" or "false" or "FALSE" or "no";
        if (off)
        {
            return null;
        }
        var s = new HdcSession(_hdcPath, _hdcTarget, msg => Log?.Invoke(msg));
        return s;
    }

    /// <summary>当前通道统计（会话命中 / 逐进程命中），用于 A/B 对比。</summary>
    public (int Session, int Process) ChannelStats => (_sessionHits, _processHits);

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        // 刻意不 Dispose _serial：关闭阶段可能还有命令在飞，
        // 让 GC 处理这个信号量即可（进程马上就退出了）。
    }

    public async Task<(double ElapsedMs, string Output, bool Ok)> InjectAsync(InputEvent ev) =>
        await RunAsync(Channel.BuildShellCommand(ev));

    public async Task<(double ElapsedMs, string Output, bool Ok)> NavAsync(byte navKey) =>
        await RunAsync(Channel.BuildShellCommand(navKey));

    /// <summary>
    /// 执行任意 shell 命令（走同一条串行通道）。
    /// 用途：UitestRpcClient 建链前拉起设备侧 daemon（`uitest start-daemon singleness &amp;`）。
    /// </summary>
    public async Task<(double ElapsedMs, string Output, bool Ok)> ShellAsync(string command) =>
        await RunAsync(command);

    /// <summary>
    /// 对**指定 hdc target** 执行 shell 命令（不走本注入器当前绑定的那台）。
    /// 用途：双路并行里手机走自有 HAP 采集时，PC 要按它自己的 target 把它拉起来
    /// （`aa start ... --ps stream 1 --ps peerport 9011`），而 _hdcTarget 此刻可能指着平板。
    /// 仍走同一条 HdcSerial 串行闸门 —— hdc 并发调用会互踢。
    /// </summary>
    public async Task<(double ElapsedMs, string Output, bool Ok)> ShellOnAsync(string target, string command)
    {
        await _serial.WaitAsync();
        try
        {
            var psi = NewHdcStartInfo();
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add("shell");
            psi.ArgumentList.Add(command);
            return await RunProcessAsync(psi);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>预热：先把 hdc 到设备的会话建起来，避免首条命令的 1.5s 建链开销进统计。</summary>
    public async Task<(double ElapsedMs, string Output, bool Ok)> WarmUpAsync()
    {
        var r = await RunAsync("echo warm");
        WarmedUp = r.Ok;
        return r;
    }

    /// <summary>
    /// 推送本地文件到设备（`hdc file send`）。
    /// 用途：PC → 平板 的文本注入改走「设备端读文件」而不是命令行传参 ——
    /// Windows 到 hdc 的命令行是 ANSI 编码，emoji 等非 GBK 字符会被替换成 '?'，
    /// 而文件是纯字节流，原样到达（2026-11-14 实测：文件里 😀🎉 展开完好）。
    /// </summary>
    /// <remarks>
    /// **hdc file send 的两个坑（2026-11-14 真机定位，此前文件通道一直静默失效）**：
    /// ① 远端路径一律当**目录**处理；目标不存在时它会新建一个同名**目录**把文件塞进去，
    ///    之后 <c>cat</c> 永远报 <c>Is a directory</c>，而命令本身仍返回 No Error。
    /// ② 本地路径是**原样拼接**到远端目录后面的，不取 basename：
    ///    <c>file send tmp/a.txt /data/local/tmp/</c> 会落成 <c>/data/local/tmp/tmp/a.txt</c>；
    ///    传 Windows 绝对路径（还带中文用户名）拼得更歪。
    /// 所以这里必须：cd 到文件所在目录 + 只传裸文件名 + 远端只给已存在的目录，
    /// 并且先清掉可能残留的同名目录。
    /// </remarks>
    public async Task<(double ElapsedMs, string Output, bool Ok)> SendFileAsync(string localPath, string remotePath)
    {
        await _serial.WaitAsync();
        try
        {
            string rp = remotePath.Replace('\\', '/');
            int slash = rp.LastIndexOf('/');
            string remoteDir = slash > 0 ? rp[..slash] : "/data/local/tmp";
            string remoteName = slash >= 0 ? rp[(slash + 1)..] : rp;

            string full = Path.GetFullPath(localPath);
            string dir = Path.GetDirectoryName(full) ?? Path.GetTempPath();
            string name = Path.GetFileName(full);
            if (!string.Equals(name, remoteName, StringComparison.Ordinal))
            {
                // 远端落地名由本地文件名决定，名字不一致就中转一份
                string staged = Path.Combine(Path.GetTempPath(), "harmos-send-" + remoteName);
                File.Copy(full, staged, true);
                dir = Path.GetDirectoryName(staged)!;
                name = Path.GetFileName(staged);
            }
            string target = remoteDir.TrimEnd('/') + "/";

            if (!_sendPathPrepared || !string.Equals(_sendPreparedFor, _hdcTarget, StringComparison.Ordinal))
            {
                await ExecHdcAsync($"rm -rf {remoteDir.TrimEnd('/')}/{remoteName}");
                _sendPathPrepared = true;
                _sendPreparedFor = _hdcTarget;
            }

            var first = await ExecHdcRawAsync(new[] { "file", "send", name, target }, dir);
            if (first.Ok)
            {
                return first;
            }
            if (first.Output.Contains("Not a directory", StringComparison.Ordinal))
            {
                // 上次残留了同名目录，清掉再来一次（并把标记复位，下次也先清）
                _sendPathPrepared = false;
                await ExecHdcAsync($"rm -rf {remoteDir.TrimEnd('/')}/{remoteName}");
                first = await ExecHdcRawAsync(new[] { "file", "send", name, target }, dir);
                _sendPathPrepared = true;
                if (first.Ok)
                {
                    return first;
                }
            }
            if (!IsSessionLost(first.Output))
            {
                return first;
            }
            await ExecHdcAsync(null);
            return await ExecHdcRawAsync(new[] { "file", "send", name, target }, dir);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>设备端文本中转文件是否已清过（见 <see cref="SendFileAsync"/> 的 ① 号坑）。</summary>
    private bool _sendPathPrepared;

    /// <summary>上面那次清理发生在哪台设备上（双设备时每台都要各自清一次）。</summary>
    private string? _sendPreparedFor;

    private async Task<(double ElapsedMs, string Output, bool Ok)> RunAsync(string shellCommand)
    {
        if (Transport == InjectTransport.DirectHdc)
        {
            HdcSession? sess = _session;
            if (sess is not null && !_sessionDisabled)
            {
                var viaSess = await sess.RunAsync(shellCommand).ConfigureAwait(false);
                if (viaSess.Ok)
                {
                    _sessionHits++;
                    // 前几条必记，之后每 10 条记一次，方便和逐进程路径对比
                    if (_sessionHits <= 3 || _sessionHits % 10 == 0)
                    {
                        Log?.Invoke($"[注入] hdc会话 {viaSess.ElapsedMs:F0}ms（累计 {_sessionHits} 条）");
                    }
                    return viaSess;
                }
                // "session busy" 只是本次并发撞车，会话本身没坏；其余失败先重试，
                // 连续 3 次才永久降级 —— 单次失败常见于启动期抢 hdc 闸门，不该判死。
                if (!viaSess.Output.Contains("busy", StringComparison.Ordinal))
                {
                    _sessionFails++;
                    if (_sessionFails >= 3)
                    {
                        _sessionDisabled = true;
                        Log?.Invoke($"[注入] hdc 会话连续 {_sessionFails} 次失败，永久回退逐进程路径：{FirstLine(viaSess.Output)}");
                    }
                    else
                    {
                        Log?.Invoke($"[注入] hdc 会话第 {_sessionFails} 次失败（继续尝试）：{FirstLine(viaSess.Output)}");
                    }
                }
            }
            _processHits++;
            return await RunDirectHdcAsync(shellCommand).ConfigureAwait(false);
        }
        return await RunViaBashAsync(shellCommand).ConfigureAwait(false);
    }

    private static string FirstLine(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }

    /// <summary>
    /// 直连 hdc.exe。跳过 bash 与 hdcw.sh 的 tconn：实测 2.5s → 200ms。
    /// 代价是失去了 hdcw.sh 的自动重连，会话掉了要重新 tconn（失败时返回 Ok=false，由上层提示）。
    /// </summary>
    private async Task<(double ElapsedMs, string Output, bool Ok)> RunDirectHdcAsync(string shellCommand)
    {
        await _serial.WaitAsync();
        try
        {
            var first = await ExecHdcAsync(shellCommand);
            if (first.Ok || !IsSessionLost(first.Output))
            {
                return first;
            }
            // TCP 会话跨进程会掉（hdc 的已知行为），掉线时补一次 tconn 再重试。
            // tconn 约 1.5s，属冷启动成本，不进稳态统计。
            await ExecHdcAsync(null);
            var second = await ExecHdcAsync(shellCommand);
            return (first.ElapsedMs + second.ElapsedMs, second.Output, second.Ok);
        }
        finally
        {
            _serial.Release();
        }
    }

    private static bool IsSessionLost(string output) =>
        output.Contains("Not match target", StringComparison.Ordinal)
        || output.Contains("need connect-key", StringComparison.Ordinal)
        || output.Contains("connect failed", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Cannot reach", StringComparison.OrdinalIgnoreCase);

    /// <summary>shellCommand 为 null 时执行 tconn（重建会话）。</summary>
    private async Task<(double ElapsedMs, string Output, bool Ok)> ExecHdcAsync(string? shellCommand)
    {
        var psi = NewHdcStartInfo();
        if (shellCommand is null)
        {
            psi.ArgumentList.Add("tconn");
            psi.ArgumentList.Add(_hdcTarget);
        }
        else
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(_hdcTarget);
            psi.ArgumentList.Add("shell");
            psi.ArgumentList.Add(shellCommand);
        }
        return await RunProcessAsync(psi);
    }

    /// <summary>执行 `hdc -t &lt;target&gt; &lt;args…&gt;`（file send / file recv 这类非 shell 子命令）。</summary>
    /// <param name="workDir">
    /// 必须指向 <paramref name="args"/> 里本地文件的所在目录：hdc 会把本地参数**原样拼**到远端目录后，
    /// 传绝对路径或带子目录的相对路径都会拼歪（详见 <see cref="SendFileAsync"/>）。
    /// </param>
    private async Task<(double ElapsedMs, string Output, bool Ok)> ExecHdcRawAsync(
        string[] args, string? workDir = null)
    {
        var psi = NewHdcStartInfo();
        if (!string.IsNullOrEmpty(workDir))
        {
            psi.WorkingDirectory = workDir;
        }
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(_hdcTarget);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        return await RunProcessAsync(psi);
    }

    private ProcessStartInfo NewHdcStartInfo() => new(_hdcPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static async Task<(double ElapsedMs, string Output, bool Ok)> RunProcessAsync(ProcessStartInfo psi)
    {
        // hdc 全局串行（含 bash 兜底路径，它内部同样是 hdc）：双路并行下并发必互踢，
        // 且会打乱采集桥 fport 的目标设备，详见 HdcSerial。
        using IDisposable lease = await HdcSerial.AcquireAsync().ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        using Process? p = Process.Start(psi);
        if (p is null)
        {
            return (sw.Elapsed.TotalMilliseconds, "failed to start hdc", false);
        }
        string stdout = await p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        sw.Stop();
        string text = (stdout + stderr).Trim();
        bool ok = p.ExitCode == 0
                  && !text.Contains("need connect-key", StringComparison.Ordinal)
                  && !text.Contains("[Fail]", StringComparison.Ordinal)
                  && !text.Contains("Not match target", StringComparison.Ordinal);
        return (sw.Elapsed.TotalMilliseconds, text, ok);
    }

    private async Task<(double ElapsedMs, string Output, bool Ok)> RunViaBashAsync(string shellCommand)
    {
        await _serial.WaitAsync();
        try
        {
            var psi = new ProcessStartInfo(_bash)
            {
                WorkingDirectory = _projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-lc");
            // hdcw.sh shell 的参数只能是单条命令；统一走脚本，禁止手敲 hdc（INV-01）
            psi.ArgumentList.Add($"MSYS2_ARG_CONV_EXCL='*' ./scripts/hdcw.sh shell \"{shellCommand}\"");
            var sw = Stopwatch.StartNew();
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return (sw.Elapsed.TotalMilliseconds, "failed to start bash", false);
            }
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, (stdout + stderr).Trim(), p.ExitCode == 0);
        }
        finally
        {
            _serial.Release();
        }
    }
}
