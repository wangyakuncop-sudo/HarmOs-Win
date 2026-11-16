using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HarmOsWin.Core;

/// <summary>
/// 参考方案的独立触控通道：复刻 HOScrcpy-python 的
/// hos_scrcpy/bridge/native_stream.py 中 HosTouchChannel（第 470~695 行）。
///
/// 与视频通道完全并列、互不复用（参考项目 HosStreamBridge.start() 就是
/// touch.start() 先、video.start() 后，两条 singleness daemon 各自独立）：
///   ① /data/local/tmp/agent.so（随包嵌入的 uitest_agent_1.2.3.so）
///   ② 不带 --extension-name 的系统 uitest daemon
///   ③ hdc fport tcp:&lt;本地口&gt; localabstract:uitest_socket
///   ④ 持久 TCP + TCP_NODELAY，发**裸 compact JSON**（无帧头、无换行、不等响应）
///      {"module":"com.ohos.devicetest.hypiumApiHelper","method":"Gestures",
///       "params":{"api":"touchDown","args":{"x":100,"y":200}}}
///   ⑤ 后台排空线程持续读走 daemon 回复（不读会积压撑爆缓冲）
///
/// 为什么不能复用本工程既有的 UitestRpcClient：
///   - 它用 "_uitestkit_rpc_message_head_" + sid/len + tail 的带帧格式（jar 口径），
///     与参考项目的裸 JSON 不是同一套报文；
///   - 更关键的是它在 EnsureDaemonAsync 里会 `pkill -f 'uitest start-daemon'`，
///     把视频侧的**扩展** daemon 一起杀掉，参考模式会立刻断流。
///   所以参考模式下注入绝不调用 EnsureRpcAsync / UitestRpcClient。
///
/// 协议口径待真机判定（不写成已验证事实）：
///   - 裸 JSON 是参考项目原文口径；若本机 agent.so 只认带帧格式，只需把
///     UseFraming 打开即可切换，不需要重写通道。
///   - pressBack / pressHome 是本工程既有 UitestRpcClient 用过的 hypium api，
///     参考项目只用 uinput -K 发系统键；在裸 JSON 通道上尚未真机验证。
///   - 参考项目没有「最近任务」键（全库检索无匹配），PressKeyAsync 走
///     keyboard.py / device.py:236 的 uinput -K -d/-u 口径，键码取鸿蒙官方
///     KEYCODE_VIRTUAL_MULTITASK = 2210（@ohos.multimodalInput.keyCode.d.ts:1040）。
/// </summary>
public sealed class ReferenceTouchChannel : IAsyncDisposable
{
    private const string RemoteAgent = "/data/local/tmp/agent.so";
    private const string AbstractSocket = "localabstract:uitest_socket";
    private const string LegacySocket = "tcp:8012";
    private const string Module = "com.ohos.devicetest.hypiumApiHelper";
    private const string VersionTag = "UITEST_AGENT_LIBRARY";

    private const string FrameHead = "_uitestkit_rpc_message_head_";
    private const string FrameTail = "_uitestkit_rpc_message_tail_";

    private readonly string _hdcPath;
    private readonly string _target;
    private readonly string _agentSoPath;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _drainTask;
    private int _localPort;
    private string _forwardTarget = AbstractSocket;
    private uint _sid;
    private volatile bool _healthy;
    private bool _disposed;

    /// <summary>
    /// 默认 false = 参考项目裸 JSON 口径。真机若判定 agent.so 只认 jar 的带帧格式，
    /// 改为 true 即可，无需改动其它代码。
    /// </summary>
    public bool UseFraming { get; set; }

    public bool IsHealthy => _healthy && !_disposed && _stream is not null;

    public ReferenceTouchChannel(string hdcPath, string target, string agentSoPath,
        Action<string>? log = null)
    {
        _hdcPath = hdcPath;
        _target = target;
        _agentSoPath = agentSoPath;
        _log = log;
    }

    /// <summary>启动期一次性建链。失败返回 false，由上层回退 hdc shell，不阻断投屏。</summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }
        if (IsHealthy)
        {
            return true;
        }
        try
        {
            if (!File.Exists(_agentSoPath))
            {
                Log($"agent.so 不存在：{_agentSoPath}");
                return false;
            }
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 先确保 hdcd server 常驻（见 HdcRunner.EnsureServer 注释）
            HdcRunner.EnsureServer(_hdcPath, Log);
            // 无线 TCP 会话跨 shell 进程会掉，每次启动先握手。
            Log("① tconn 握手…");
            await HdcAsync("tconn", _target).ConfigureAwait(false);
            Log("② 部署 agent.so…");
            await DeployAgentAsync().ConfigureAwait(false);
            Log("③ 确保系统 daemon…");
            await EnsureSystemDaemonAsync().ConfigureAwait(false);
            Log("④ 解析转发目标…");
            _forwardTarget = await ResolveForwardTargetAsync().ConfigureAwait(false);
            Log($"④ 完成：{_forwardTarget}");

            _localPort = FreeTcpPort();
            Log($"⑤ 选本地端口 {_localPort}，先清旧 fport 规则…");
            // rm 在规则不存在时必然返回 [Fail]，这是常态，不能当失败处理。
            var (rok, rout) = await HdcAsync("-t", _target, "fport", "rm",
                $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
            Log($"⑤ 清规则：{(rok ? "OK" : rout)}");
            // 清规则失败（多半是残留被别处占用）就换一个端口重试一次，
            // 否则下一句 add 会报 "TCP Port listen failed" 然后被同一个端口卡死。
            if (!rok)
            {
                _localPort = FreeTcpPort();
                Log($"⑤ 换端口重试 {_localPort}");
                await HdcAsync("-t", _target, "fport", "rm",
                    $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
            }
            // fport add 实测约 50% 概率挂住（同一条命令手工 0.1s，代码里卡到超时）。
            // hdcd 侧偶发，命令本身没问题 —— 所以按「换端口重来」处理，最多 3 次。
            Log("⑥ 建立 fport 规则…");
            bool fok = false;
            string fout = "";
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var (ok, outText) = await HdcAsync("-t", _target, "fport",
                    $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
                fok = ok;
                fout = outText;
                if (fok)
                {
                    if (attempt > 1)
                    {
                        Log($"⑥ 第 {attempt} 次成功");
                    }
                    break;
                }
                Log($"⑥ 第 {attempt} 次失败：{FirstLine(outText)}");
                if (attempt < 3)
                {
                    // 换端口再试：同一个端口上残留的失败规则会一直被拒。
                    await HdcAsync("-t", _target, "fport", "rm",
                        $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
                    _localPort = FreeTcpPort();
                    Log($"⑥ 换端口重试 {_localPort}");
                }
            }
            Log($"fport 127.0.0.1:{_localPort} → {_forwardTarget}：{(fok ? "OK" : fout)}");

            // fport 返回 OK 不代表规则真的建立了：规则存在 hdcd server 内存里，
            // server 若在这一刻已经退出，规则就丢了，本机端口不会 LISTEN。
            // 这里实测端口，不行就重建 server + 重来一次（2026-11-15 实测的假成功）。
            if (fok && !IsLocalPortListening(_localPort))
            {
                Log("⑥ fport 报 OK 但本机端口未监听，重建 server 后重试");
                HdcRunner.EnsureServer(_hdcPath, Log);
                await HdcAsync("tconn", _target).ConfigureAwait(false);
                _localPort = FreeTcpPort();
                await HdcAsync("-t", _target, "fport", "rm",
                    $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
                var (r2, o2) = await HdcAsync("-t", _target, "fport",
                    $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
                fok = r2 && IsLocalPortListening(_localPort);
                Log($"⑥ 重试：{(fok ? "OK" : "仍失败")} {FirstLine(o2)}");
            }

            // fport 返回码不可信（残留规则会报 "TCP Port listen failed"），
            // 以 TCP 能否真正连上为准。
            Log($"⑦ TCP 连本机 {_localPort}…");
            var client = new TcpClient { NoDelay = true };
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(6));
                await client.ConnectAsync("127.0.0.1", _localPort, connectCts.Token)
                    .ConfigureAwait(false);
            }
            Log("⑦ TCP 已连上");
            _client = client;
            _stream = client.GetStream();
            _healthy = true;
            CancellationTokenSource cts = _cts!;
            _drainTask = Task.Run(() => DrainAsync(cts.Token));
            Log($"参考触控通道就绪（{(UseFraming ? "带帧" : "裸 JSON")} / 持久 TCP）");
            return true;
        }
        catch (Exception ex)
        {
            Log($"参考触控通道启动失败：{ex.Message}");
            await StopAsync().ConfigureAwait(false);
            return false;
        }
    }

    // ── 发送（fire-and-forget，低延迟） ──

    /// <summary>发一条 Gestures 事件。argsJson 为 params.args 的原样 JSON。</summary>
    public async Task<bool> SendGesturesAsync(string api, string argsJson = "{}")
    {
        if (!IsHealthy)
        {
            return false;
        }
        string json = $"{{\"module\":\"{Module}\",\"method\":\"Gestures\"," +
                      $"\"params\":{{\"api\":\"{api}\",\"args\":{argsJson}}}}}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 带帧模式下 _sid 递增必须也在锁内，避免并发发送撞号。
            byte[] packet = UseFraming ? BuildFramed(body) : body;
            NetworkStream? s = _stream;
            if (s is null)
            {
                return false;
            }
            await s.WriteAsync(packet.AsMemory()).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // 参考项目口径：写失败只标记不健康，本层不重连（避免运行期 hdc 干扰视频 fport）。
            _healthy = false;
            Log($"触控发送失败（通道判死）：{ex.Message}");
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private byte[] BuildFramed(byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes(FrameHead);
        byte[] tail = Encoding.ASCII.GetBytes(FrameTail);
        var packet = new byte[head.Length + 8 + body.Length + tail.Length];
        int o = 0;
        Buffer.BlockCopy(head, 0, packet, o, head.Length);
        o += head.Length;
        uint sid = ++_sid;
        packet[o++] = (byte)(sid >> 24);
        packet[o++] = (byte)(sid >> 16);
        packet[o++] = (byte)(sid >> 8);
        packet[o++] = (byte)sid;
        uint len = (uint)body.Length;
        packet[o++] = (byte)(len >> 24);
        packet[o++] = (byte)(len >> 16);
        packet[o++] = (byte)(len >> 8);
        packet[o++] = (byte)len;
        Buffer.BlockCopy(body, 0, packet, o, body.Length);
        o += body.Length;
        Buffer.BlockCopy(tail, 0, packet, o, tail.Length);
        return packet;
    }

    public Task<bool> TouchAsync(string api, int x, int y, int? value = null)
    {
        string args = value.HasValue
            ? $"{{\"x\":{x},\"y\":{y},\"value\":{value.Value}}}"
            : $"{{\"x\":{x},\"y\":{y}}}";
        return SendGesturesAsync(api, args);
    }

    /// <summary>参考项目 fast_touch.py:54-57 的 click(duration=0.05)。</summary>
    public async Task<bool> TapAsync(int x, int y, int holdMs = 50)
    {
        if (!await TouchAsync("touchDown", x, y).ConfigureAwait(false))
        {
            return false;
        }
        if (holdMs > 0)
        {
            await Task.Delay(holdMs).ConfigureAwait(false);
        }
        return await TouchAsync("touchUp", x, y).ConfigureAwait(false);
    }

    /// <summary>参考项目 fast_touch.py:59-75 的 swipe：down → 插值 move → up。</summary>
    public async Task<bool> SwipeAsync(int x1, int y1, int x2, int y2,
        int durationMs = 150, int steps = 10)
    {
        if (!await TouchAsync("touchDown", x1, y1).ConfigureAwait(false))
        {
            return false;
        }
        steps = Math.Max(1, steps);
        int per = Math.Max(1, durationMs / steps);
        for (int i = 1; i <= steps; i++)
        {
            double f = (double)i / steps;
            int ix = (int)(x1 + (x2 - x1) * f);
            int iy = (int)(y1 + (y2 - y1) * f);
            await Task.Delay(per).ConfigureAwait(false);
            if (!await TouchAsync("touchMove", ix, iy).ConfigureAwait(false))
            {
                return false;
            }
        }
        await Task.Delay(20).ConfigureAwait(false);
        return await TouchAsync("touchUp", x2, y2).ConfigureAwait(false);
    }

    /// <summary>
    /// Back / Home：持久 TCP，运行期零 hdc 调用。
    /// Recents 返回 false 交给调用方走 PressKeyAsync（参考项目无此键）。
    /// </summary>
    public Task<bool> PressNavAsync(byte key)
    {
        string api = key switch
        {
            NavKey.Back => "pressBack",
            NavKey.Home => "pressHome",
            _ => string.Empty
        };
        return api.Length == 0 ? Task.FromResult(false) : SendGesturesAsync(api);
    }

    /// <summary>
    /// 「多任务 / 最近任务」—— 真机上唯一有效的通道。
    ///
    /// 实测记录（2026-11-11 00:29，MatePad Mini MLR-AL00 / HarmonyOS 7.0.0.111，手势导航）：
    ///   `uinput -K -d 2210 -u 2210`（KEYCODE_VIRTUAL_MULTITASK）→ 桌面画面零变化
    ///   `uinput -K -d 2717 -u 2717`（KEYCODE_TASKMANAGER）      → 零变化
    ///   `uitest uiInput keyEvent 2210`                          → 零变化
    ///   `uinput -K -d 1 -u 1`（KEYCODE_HOME，对照组）           → 正常回桌面
    /// 即：键码存在、命令也被接受（回显 you raised the key），但系统不消费。
    /// 手势导航下系统真正的多任务入口是「从底部上滑并悬停」，用 -m 的 keep time 模拟。
    /// 这一条实测稳定进入最近任务卡片视图（可见应用缩略图 + 底部清理按钮）。
    /// </summary>
    public async Task<bool> PressRecentsGestureAsync(int screenW, int screenH)
    {
        var (ok, text) = await HdcAsync("-t", _target, "shell",
            BuildRecentsGesture(screenW, screenH)).ConfigureAwait(false);
        Log($"多任务手势 {screenW}x{screenH}：{(ok ? "OK" : text)}");
        return ok;
    }

    /// <summary>
    /// 生成「底部上滑悬停」命令：x 取屏幕中线，y1 贴底（留 20~32px 防越界），
    /// y2 取屏高 55%（实测 2560 屏 → 1400），-k 800 为悬停时长、300 为滑动时长。
    /// </summary>
    public static string BuildRecentsGesture(int screenW, int screenH)
    {
        int w = Math.Max(2, screenW);
        int h = Math.Max(2, screenH);
        int x = w / 2;
        int y1 = h - Math.Clamp(h / 128, 12, 32);
        int y2 = (int)(h * 0.55);
        return $"uinput -T -m {x} {y1} {x} {y2} -k 800 300";
    }

    /// <summary>
    /// 参考项目通道 C（keyboard.py:24-38 / device.py:234-236）：hdc shell uinput -K -d/-u。
    /// 仅用于 Gestures 没有的键；注意「多任务」不要用这里（键码实测无效，见 PressRecentsGestureAsync）。
    /// </summary>
    public async Task<bool> PressKeyAsync(int keyCode)
    {
        var (ok, text) = await HdcAsync("-t", _target, "shell",
            $"uinput -K -d {keyCode} -u {keyCode}").ConfigureAwait(false);
        Log($"uinput key={keyCode}：{(ok ? "OK" : text)}");
        return ok;
    }

    // ── 设备侧部署（全部在启动期，运行期不产生 hdc 调用） ──

    /// <summary>
    /// 参考项目 _ensure_agent：本地/设备 md5 一致就跳过；需要更新时先杀掉
    /// **不带** --extension-name 的系统 daemon（它占着旧库，rm 会失败）。
    /// 绝不碰视频侧的扩展 daemon。
    /// </summary>
    private async Task DeployAgentAsync()
    {
        byte[] local = await File.ReadAllBytesAsync(_agentSoPath).ConfigureAwait(false);
        string md5 = Convert.ToHexString(MD5.HashData(local)).ToLowerInvariant();
        var (ok, text) = await HdcAsync("-t", _target, "shell", $"md5sum {RemoteAgent}")
            .ConfigureAwait(false);
        if (ok && text.Contains(md5, StringComparison.OrdinalIgnoreCase))
        {
            Log($"agent.so md5 一致（{md5[..8]}…），跳过推送");
            return;
        }

        await KillSystemDaemonsAsync().ConfigureAwait(false);
        await HdcAsync("-t", _target, "shell", $"rm -f {RemoteAgent}").ConfigureAwait(false);

        // hdc file send 对含中文/空格的绝对路径会拼到 cwd 后面 → 相对文件名 + WorkingDirectory。
        string name = Path.GetFileName(_agentSoPath);
        var (sent, sout) = await HdcAsync("-t", _target, "file", "send", name, RemoteAgent)
            .ConfigureAwait(false);
        if (!sent)
        {
            throw new InvalidOperationException($"推送 agent.so 失败：{sout}");
        }
        await HdcAsync("-t", _target, "shell", $"chmod 755 {RemoteAgent}")
            .ConfigureAwait(false);
        Log("agent.so 已推送到设备");
    }

    /// <summary>
    /// 参考项目 _kill_daemon_pids(extension=False) / _daemon_running(extension=False)：
    /// 只筛「含 singleness、含 uitest、不含 extension-name」的行，取 fields[1] 为 PID。
    /// 带 --extension-name 的投屏扩展 daemon 一律不动。
    /// </summary>
    private async Task<List<int>> SystemDaemonPidsAsync()
    {
        var pids = new List<int>();
        // ⚠ 必须用 `ps -ef`，不能用 `ps -A`（2026-11-15 实测）：
        //    ps -A  → 0 条（漏掉 shell 起的后台 daemon）
        //    ps -ef → 3 条（正确）
        // ⚠ 设备端**不要用任何管道**（2026-11-15 实测三条路都堵）：
        //   · `ps -ef | grep uitest` → 设备端 grep 等 hdc 转发的空 stdin，输出 0 字节；
        //   · `... < /dev/null` → 本机 ROM 的 SELinux 直接报 Permission denied；
        //   · 多级管道 → 同上。
        // 改为拿 ps -ef 全量（约 17KB），过滤放在 C# 侧。
        // 输出走流式落盘（HdcRunner），17KB 不会把任何缓冲写满。
        var (ok, text) = await HdcAsync("-t", _target, "shell", "ps -ef")
            .ConfigureAwait(false);
        if (!ok)
        {
            return pids;
        }
        foreach (string line in text.Split('\n'))
        {
            if (!line.Contains("singleness", StringComparison.Ordinal) ||
                !line.Contains("uitest", StringComparison.Ordinal) ||
                line.Contains("extension-name", StringComparison.Ordinal))
            {
                continue;
            }
            // ps -ef 的列是「USER PID PPID ...」，PID 在**第 2 列**（f[1]）
            string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && int.TryParse(f[1], out int pid) && pid > 1)
            {
                pids.Add(pid);
            }
        }
        return pids;
    }

    private async Task KillSystemDaemonsAsync()
    {
        List<int> pids = await SystemDaemonPidsAsync().ConfigureAwait(false);
        if (pids.Count == 0)
        {
            return;
        }
        await HdcAsync("-t", _target, "shell", "kill -9 " + string.Join(" ", pids))
            .ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        Log($"清理系统 uitest daemon {pids.Count} 个（投屏扩展 daemon 未动）");
    }

    private async Task EnsureSystemDaemonAsync()
    {
        List<int> pids = await SystemDaemonPidsAsync().ConfigureAwait(false);
        if (pids.Count > 0)
        {
            Log("系统 uitest daemon 已在跑，复用");
            return;
        }
        // 不带 --extension-name，与视频扩展 daemon 是两个独立进程。
        //
        // 后台化必须做全三条，少一条 hdc 就会卡到 8s 超时（2026-11-15 实测）：
        //   ① `< /dev/null`   —— 断开继承来的 stdin，否则 daemon 等输入
        //   ② `>/dev/null 2>&1` —— 断开继承来的 stdout/stderr，否则 hdc 等管道关闭
        //   ③ 末尾补 `echo`   —— 给 hdc 一个立即到达的输出，使它马上返回
        // 只写 ② 不够：本机实测 `start-daemon ... &` 仍会偶发挂死，
        // 因为 hdc 的 shell 通道要等**所有**子进程释放描述符。
        // 这条命令拉起的是**常驻 daemon**，它会继承设备端 shell 的描述符。
        // hdc 会一直等到那个子进程释放句柄才返回 —— 在 WPF 里表现为卡到超时
        // （控制台里 0.48s 就返回，环境差异，2026-11-15 实测）。
        // 因此这里**不依赖它的返回值**：只要设备端 daemon 真的起来了，
        // 后面的 socket 探测会确认；这条命令超时属于预期，不当失败。
        var (_, startOut) = await HdcAsync("-t", _target, "shell",
            "nohup /system/bin/uitest start-daemon singleness " +
            "</dev/null >/dev/null 2>&1 & echo DAEMON_STARTED")
            .ConfigureAwait(false);
        if (startOut.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            Log("启动命令超时（daemon 常驻会占住 shell 句柄，属预期），继续探测 socket");
        }
        // ⚠ 这里**不做 socket 轮询**（2026-11-15 实测的坑）：
        //   设备端那条 `nohup ... start-daemon ... &` 拉起的 daemon 会继承
        //   hdc 的 stdout 写端，hdc 退出后管道仍不 EOF。跟着再发一条
        //   `grep xxx /proc/net/unix` 时，新 hdc 会卡在旧 daemon 占着的会话上，
        //   整个桥调用链跟着停住（日志停在这条命令，进程随后被回收）。
        //   改法：不探测，固定等一小段让 daemon 自己就绪；socket 是否可用
        //   由后面真正建连接时验证（⑦ TCP 连本机 会给出明确结论）。
        await Task.Delay(800).ConfigureAwait(false);
        Log("系统 uitest daemon 已下发，等待 800ms 后继续（socket 可用性由 ⑤⑥⑦ 验证）");
    }

    /// <summary>
    /// 参考项目 _device_agent_version：agent.so 内嵌
    /// UITEST_AGENT_LIBRARY@v0.0.0#1.2.3，版本在 '#' 之后；
    /// &gt;=1.2.0 走 localabstract:uitest_socket，老版走 tcp:8012。
    /// 读不到标记时按随包 1.2.3 走 abstract，并明确留痕（不是已验证结论）。
    /// </summary>
    private async Task<string> ResolveForwardTargetAsync()
    {
        // 直读文件，不用 `cat | grep`：管道在 hdc shell 上会多一层，
        // 二进制内容偶尔让 grep 卡住（2026-11-15 实测 4s 超时）。grep -a 同样能读二进制。
        var (ok, text) = await HdcAsync("-t", _target, "shell",
            $"grep -a {VersionTag} {RemoteAgent}").ConfigureAwait(false);
        if (ok)
        {
            Match m = Regex.Match(text, VersionTag + @"[^#]*#(\d+(?:\.\d+){1,3})");
            if (m.Success)
            {
                string[] parts = m.Groups[1].Value.Split('.');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out int major) &&
                    int.TryParse(parts[1], out int minor))
                {
                    bool modern = major > 1 || (major == 1 && minor >= 2);
                    Log($"agent 版本 {m.Groups[1].Value} → {(modern ? AbstractSocket : LegacySocket)}");
                    return modern ? AbstractSocket : LegacySocket;
                }
            }
        }
        Log($"agent 版本标记读不到，随包 agent.so 按 1.2.x 走 {AbstractSocket}（待真机验证）");
        return AbstractSocket;
    }

    // ── 后台排空（参考项目 _drain_loop） ──

    private async Task DrainAsync(CancellationToken ct)
    {
        byte[] buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            NetworkStream? s = _stream;
            if (s is null)
            {
                break;
            }
            int n;
            try
            {
                n = await s.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (!ct.IsCancellationRequested)
                {
                    _healthy = false;
                }
                break;
            }
            if (n <= 0)
            {
                _healthy = false;
                Log("触控通道对端关闭（daemon 可能已退出）");
                break;
            }
            string text = Encoding.UTF8.GetString(buf, 0, n);
            if (text.Contains("exception", StringComparison.Ordinal))
            {
                Log($"触控通道回复异常：{(text.Length > 200 ? text[..200] : text)}");
            }
        }
    }

    // ── 停止（参考项目 stop()：关 socket + fport rm，系统 daemon 保留） ──

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _healthy = false;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }
        try
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
        catch
        {
            // ignored
        }
        _stream = null;
        _client = null;
        if (_drainTask is not null)
        {
            try
            {
                await _drainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 阻塞读会被 socket Dispose 打断
            }
        }
        try
        {
            if (_localPort > 0)
            {
                await HdcAsync("-t", _target, "fport", "rm",
                    $"tcp:{_localPort}", _forwardTarget).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log($"清理告警：{ex.Message}");
        }
        _cts?.Dispose();
        Log("参考触控通道已关闭（系统 daemon 按参考项目口径保留）");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    // ── hdc ──

    private async Task<(bool Ok, string Output)> HdcAsync(params string[] args)
    {
        string tag = string.Join(' ', args);
        Log($"[hdc→] {tag}");
        // 统一走 HdcRunner：输出重定向到文件，不用管道（管道会被后台 daemon 继承而卡死）。
        var r = await HdcRunner.RunAsync(
            _hdcPath, args,
            workDir: Path.GetDirectoryName(_agentSoPath) ?? AppContext.BaseDirectory,
            timeoutMs: 5000,
            acceptFailMarker: ArgsTolerateFail(args),
            log: Log).ConfigureAwait(false);
        Log($"[hdc←] {tag} → {(r.Ok ? "OK" : "FAIL")}");
        return r;
    }

    /// <summary>
    /// `fport rm` 在规则不存在时必定回 `[Fail]Remove forward ruler failed`，
    /// 这是常态而非错误，调用方按输出内容自行判断，所以放行 Fail 标记。
    /// </summary>
    private static bool ArgsTolerateFail(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "fport" && args[i + 1] == "rm")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>本机端口是否在 LISTEN（用来识破 fport 的假成功）。</summary>
    private static bool IsLocalPortListening(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync("127.0.0.1", port);
            return t.Wait(600) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        string line = nl < 0 ? text : text[..nl];
        return line.Length > 120 ? line[..120] : line;
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void Log(string msg)
    {
        string line = $"[参考触控] {msg}";
        try
        {
            _log?.Invoke(line);
        }
        catch
        {
            // 窗口关闭时 Dispatcher 已停，日志不该打断清理
        }
        try
        {
            string? path = Environment.GetEnvironmentVariable("HARMOSWIN_REF_LOG");
            if (!string.IsNullOrWhiteSpace(path))
            {
                // 用共享句柄 + 锁，不能 File.AppendAllText（多线程会 IO 冲突而卡住）
                LogFile.WriteLine(path, $"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
        }
        catch
        {
            // ignored
        }
    }
}
