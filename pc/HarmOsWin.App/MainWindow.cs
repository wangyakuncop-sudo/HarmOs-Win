using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using HarmOsWin.Core;
using HarmOsWin.Transport;
using System.Text.Json;

namespace HarmOsWin.App;

/// <summary>
/// 单窗口多设备（DEC-03 / NEXT-05）。左侧栏承载设备列表与操作，右侧是画面与实时指标。
/// 切换设备只改呈现与 ActiveId，不拆 UDP、不停止推流。
/// 三键导航与 Q' 本地锁屏页均为 PC 本地绘制，参考官方 ClientWindow.xml 的交互但不复制代码。
///
/// UI 全部代码构建（本工程无 XAML）：原生 Button/TabControl 的 chrome 改不了圆角与配色，
/// 因此按钮、状态胶囊、指标磁贴都用 Border 自绘，样式集中在 Ui/Controls.cs。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly SessionManager _manager;
    private readonly InputInjector _injector;
    private readonly string? _autoHost;
    // 命令行 / 环境变量显式指定的 hdc 目标，优先级最高（调试或临时换设备时用）。
    private readonly string? _hdcTargetOverride;
    private readonly string? _metricsPath;
    private readonly string? _snapshotPath;
    private readonly BitmapScalingMode _scaleMode;
    // 集成参考方案免授权采集：默认开启，双击 exe 即走 NativeStartScreenCopy，
    // 不弹录屏授权框、不需要自有 HAP。--capture hap 可切回原路径。
    private readonly bool _useReferenceCapture;
    // 三键是否走 HWP CONTROL 主路（等设备端 InjectResult 回执）。默认 false = 直连参考触控通道。
    // 依据：本机 ROM 无无障碍扩展服务入口，startup.log 里「注入 ← 设备」回执 0 条，
    // 主路每击必等 700ms 超时才降级。--nav hwp 可切回带回执的旧主路（服务可用时）。
    private readonly bool _navUseHwpMain;
    // MF 低延迟生效时默认零补帧；--hold on / off 可强制覆盖，用于 A/B 判别与回退。
    private readonly bool _holdEnabled;
    private readonly bool _lowLatency;
    // 设备端最小出帧间隔；--refresh <ms> 可覆盖。实测该参数只影响静止时的
    // 重复帧间隔，不约束滑动时的出帧率（见 ReferenceCapture 注释）。
    private readonly int _refreshInterval;
    // -frameRate（fps）；--framerate <n> 设置，0 表示不传该参数。
    // 参考项目 native_stream.py 用的是 -frameRate，本机 so 也导出该符号，
    // 但真实语义待真机 A/B 判定。
    private readonly int _frameRate;
    // -bitRate（字节/秒）；--bitrate <bps> 可覆盖。
    // 2026-11-10 真机 A/B：配 devscale=2 时 12M 就能让 bpp 达到 0.236，
    // 实际码流 7.25Mbps、帧率 67、零丢帧。故默认 20_000_000（留余量）。
    private readonly int _bitRate;
    // -scale，设备端出图缩小倍数；--devscale <1..5> 可覆盖。
    // 注意与 --scale（WPF 缩放插值模式 linear/nearest/high/fant）不是一回事。
    // 默认 2（800x1280）：2026-11-10 三档真机实测（静态度量 + 滑动）——
    // | 档位 | 传输 | 转换ms | 解码ms | 合计ms | 理论fps | 滑动收帧 | 排队丢 |
    // |  3  |533x853 | 0.7~4.4| 1.7~4.8|  6.5  |  152   |    —     |   —    |
    // |  2  |800x1280| 0.6~1.2| 4.6~5.4|  5.9  |  168   |   708    |  0 ✅  |
    // |  1  |1600x2560|1.8~3.8|13.0~21.4| 18.1 |   55   |   233    | 73 ⚠️ |
    // devscale 2 解码耗时最低（余量最充足）且滑动零丢帧；相比 3，源内彩色小字
    // 笔画由 1px 变 2px，4:2:0 色度损失大幅减小（实测红字由崩 47% 变为几乎无损）。
    // devscale 1 清晰度仅再高 14%，但解码 18.1ms 导致排队丢 73 帧，会卡，不取。
    private readonly int _devScale;
    // 触控注入坐标系 = 设备「物理屏幕」像素，而 s.Width/s.Height 是采集端 -scale
    // 缩小后的编码尺寸（devscale 2 → 800x1280）。历史真机验收 tmp/taptest3.txt：
    // device=1600x2560、归一化 (0.8,0.5) → dev=(1279,1280) 正好落在图标中心，
    // 且 tmp/scroll2.py 用 y=2200 这类 >1280 的坐标滑动有效，都证明注入必须用物理坐标。
    // 默认乘回 devscale；--touchscale 可覆盖（设备端若改报物理尺寸就传 1）。
    private readonly double _touchScale;
    // 默认窗口贴合倍数：1.0 = 按工作区尽量撑满；2/3 ≈ 0.667。
    // 用户 2026-11-10 试过 8/15（4/5）后要求「还原为 2/3」。--winscale 可覆盖。
    private readonly double _fitScale;
    // ── 采集桥 / 触控通道：按设备各一份（双路并行）──
    // 单设备时就是字典里的唯一一项；两台同时投屏时各自独立推流、各自独立注入，
    // 互不干扰。⚠ 绝不能退回单例字段 —— 那样第二台会和第一台抢同一个
    // UDP 端口（9010/9020）与同一个 hdc fport，两台画面都会卡住。
    private readonly Dictionary<string, ReferenceCapture> _referenceCaps = new();
    // 参考方案的独立触控通道（native_stream.py 的 HosTouchChannel）：
    // agent.so → 系统 uitest daemon → localabstract:uitest_socket → 裸 JSON 持久 TCP。
    // 参考模式下绝不能走 UitestRpcClient/EnsureRpcAsync，它会 pkill -f 把视频扩展 daemon
    // 一起杀掉导致立刻断流。
    private readonly Dictionary<string, ReferenceTouchChannel> _referenceTouches = new();

    // 主框架引用
    private readonly StackPanel _deviceList = new();
    private readonly Grid _stage = new();          // 所有设备的画面叠放，靠 Visibility 切换
    // 2026-11-16：配置里写了但当前没接入的设备也要能点选。选中它时显示这块占位，
    // 而不是「点了没反应」或把上一台设备的画面留在屏幕上误导用户。
    private readonly Grid _offlineView = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _offlineText = new()
    {
        FontSize = 15,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20, 0, 20, 0)
    };
    private readonly Dictionary<string, FlatButton> _deviceRows = new();  // key → 侧栏条目（含未接入项）
    private string _activeDeviceKey = "";          // 当前选中的设备条目 key（可指向未接入项）
    private string _lastResolvedModel = "";       // 单设备分支解析到的真机型号
    private DispatcherTimer? _fitDebounce;         // 拖动/缩放结束后的比例吸附
    private bool _fitting;                         // 贴合自身改尺寸时抑制重入
    private double _windowAspect;                  // 窗口整体宽高比（含 chrome），拖动缩放时按它锁死

    // ── 拖动缩放锁比例（用户要求：缩放完全跟随设备屏幕比例）──
    // 拦截 WM_SIZING 直接改拖动矩形，而不是松手后再吸附：拖动过程中窗口就跟手走，
    // 不会出现「先被拉成横的、松手才跳回竖的」。WPF 没有原生 AspectRatio 约束。
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4,
                      WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7, WmszBottomRight = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left, Top, Right, Bottom;
    }
    private readonly StatusPill _pill = new();
    private readonly TextBlock _activeTitle = new();
    // 设备 → PC 剪贴板收件箱：局域网 UDP，与投屏通道无关，两种抓流模式都常驻。
    private ClipInbox? _clipInbox;
    // 自绘标题栏的最大化按钮：WindowState 变化时要把它在「方框 / 还原」两个图标之间切。
    private CaptionButton? _btnMaximize;
    private readonly TextBlock _activeSub = new();
    private readonly TextBlock _resolutionChip = new();
    private readonly TextBlock _detail = new();
    private readonly TextBlock _deviceCount = new();
    private readonly FlatButton _streamBtn;
    private readonly FlatButton _lockBtn;
    private readonly FlatButton _unlockBtn;
    // 设备亮度：真正作用在平板上（不是本机画面），走控制中心点击注入，见 DeviceBrightness。
    private readonly FlatButton _dimBtn;
    private readonly FlatButton _restoreBtn;
    private int _brightnessBeforeDim;      // 调暗前读到的实测亮度，0 = 还没记过
    private bool _brightnessBusy;
    private readonly TextBox _hostBox;
    // 只保留帧率并移到窗口系统标题栏；延迟 / 帧数 / 丢包 / 解码错误按用户要求从界面删除。
    // 用浅色的 TitleChip（不是画面 HUD 的 MiniStat）：标题栏是 #F3F3F3 浅底，
    // MiniStat 的深色主题文字在那上面几乎看不见，而且它还带一个多余的「帧率」标签。
    private readonly TitleChip _fpsChip = new("fps");
    private readonly TextBlock _placeholder = new()
    {
        Text = "等待设备画面…",
        FontFamily = Theme.UiFont,
        FontSize = 13,
        Foreground = Theme.TextMuted,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 460,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Dictionary<string, Page> _pages = new();
    private StreamWriter? _metricsWriter;

    // ── 多设备清单（双路并行）──
    // 来自 harmoswin.json 的 devices 数组；为空则退回单设备（device/hdcTarget）。
    // 每台设备分配一对本机 UDP 端口：index 0 → 9010/9020，index 1 → 9011/9021……
    // 因为 SessionManager 的所有会话共用一个 UDP 端点、按「源 IP:源端口」派发，
    // 两台若都发往 9010，第二台的流会投进第一台的会话，画面必然错乱。
    private sealed class DeviceEntry
    {
        public required string Name;
        public string? Model;
        public required string Host;
        public string? HdcTarget;
        public bool Enabled = true;
        public string? Note;                // 未接入时的原因说明（显示在图标的 tooltip 上）
        // 采集模式："ref" = libscreen_casting 参考采集（PC 拉流，走 127.0.0.1 采集桥）；
        // "hap" = 自有 HAP 采集（设备自己 AVScreenCapture 后按 HWP 推到 PC）。
        // 空 = 跟随全局开关（--capture hap 才变，缺省 ref）。
        public string Capture = "";
        public int LocalPort;      // 本机收流端口
        public int DevicePort;     // 本机控制面端口
        public string ResolvedTarget = "";   // 实际探到的 hdc target（含端口）
        public string Id = "";               // 会话 id，形如 device-1
        public bool Failed;
    }

    private readonly List<DeviceEntry> _devices = new();
    private static readonly string[] DeviceHints = { "手机", "平板" };

    /// <summary>
    /// 每台设备一个 hdc 目标缓存文件名后缀（用 IP，不用端口 —— 无线调试端口天天变）。
    /// 两台共用一个缓存文件会让「一台解析失败 → 写进另一台的地址」变成跨设备串台。
    /// </summary>
    private static string HostTag(DeviceEntry d)
    {
        string src = string.IsNullOrWhiteSpace(d.HdcTarget) ? d.Host : d.HdcTarget!;
        int colon = src.IndexOf(':');
        string host = colon > 0 ? src[..colon] : src;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in host)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.Length == 0 ? "default" : sb.ToString();
    }

    // ── 底部三键导航的状态（2026-11-11：修「点了没反应 / 按一次退两层」）──
    // 旧实现把同一次点击同时丢给 HWP CONTROL 和注入通道，两路都成功时设备执行两次；
    // 两条 Task 又都丢弃返回值，设备回执也无人订阅，所以失败时界面完全没有反馈。
    // 2026-11-13：700 → 250。实测主路 P50 ≈ 30ms，250ms 已留 8 倍余量；
    // 且默认已不走主路（--nav hwp 才走），这里只约束逃生口的等待上限。
    private const int NavAckTimeoutMs = 250;
    private readonly Dictionary<byte, NavKeyButton> _navButtons = new();

    // 剪贴板两枚标题栏按钮（2026-11-11 21:35 用户要求「用上传箭头和下载箭头区分方向」）：
    //   ↓ 下载 = 平板 → PC（取平板剪贴板，走 hdc 唤起 App + 注入点击系统 PasteButton）
    //   ↑ 上传 = PC → 平板（把电脑剪贴板推进平板光标处，走 uitest uiInput inputText）
    // 两者都是 PC 侧动作，跟设备三键不是一类东西，所以留在标题栏、与菜单 / 帧率并排。
    private TitleIconButton? _clipDownBtn;
    private TitleIconButton? _clipUpBtn;

    // 标题栏的设备切换图标（key = 会话 id）。用户 2026-11-13 22:16 要求
    // 「上方状态栏设置平板和电脑的图标，点击可以切换」——比左侧栏的设备列表更顺手：
    // 不用先展开侧栏，画面看一半也能直接跳。单设备时整组隐藏（没有「切换」这回事）。
    // 图标按设备名挑：带「平板/Pad」画宽矩形，其余画手机窄矩形。
    private readonly Dictionary<string, TitleIconButton> _deviceChips = new();
    private StackPanel? _deviceChipRow;
    private bool _clipPullBusy;
    private TaskCompletionSource<(int X, int Y)>? _clipReady;
    // 2026-11-12 01:20 浮窗会发**两条** READY：页面一加载就发一条（此时浮窗还没被系统摆好位置，
    // 坐标是偏的），落定后（主窗口退后台）再发一条准的。取「最后一条 + 静默 250ms」才动手，
    // 否则拿早发那条坏坐标点下去，正好点在还没退后台的主窗口上 —— 用户看到的就是「没复制、反而弹出整屏窗口」。
    private int _clipReadySeq;
    private (int X, int Y) _clipReadyLatest;
    // 按设备 IP 记住「最近一次 READY 报上来的粘贴按钮坐标」。
    // 与 _clipTapByDevice（点击成功的坐标）不同：这里连**没点成**的坐标也留，
    // 专门兜「第二次取剪贴板收不到 READY」那种情况（手机折叠屏实测必现）。
    private readonly System.Collections.Generic.Dictionary<string, (int X, int Y)> _clipReadyByIp =
        new(StringComparer.Ordinal);
    // 最近一条 READY 是否是「位置已确定」：只有它会给出屏幕绝对坐标。
    // 浮窗刚起来时量到的是窗口内偏移（143,143），拿它点必空 —— 用户报的「第一次点不动」。
    private bool _clipReadySettled;
    // 本次请求是否已经拿到可点的坐标（settled），供轮询判断。
    // 一次「取剪贴板」的端到端计时：从点按钮到平板内容回来，
    // 用来量化用户抱怨的「动画太长」到底花在哪一段（结果进 logs\startup.log）。
    private System.Diagnostics.Stopwatch? _clipPullWatch;
    // 上一次成功点击 PasteButton 的设备坐标。有了它，READY 广播没到时也能直接点，
    // 不必干等满超时窗口（这是「点完没反应、过两秒才动」的根源）。
    // 2026-11-13：改为**按设备分别缓存**。手机 914x1292、平板 1600x2560，
    // 同一套坐标跨设备必然点空（点空那一击会落到用户正在用的应用上），
    // 而缓存串台的代价是每次切设备后第一次取剪贴板都白点一下。键 = 会话 id。
    private readonly Dictionary<string, (int X, int Y)> _clipTapByDevice = new();
    // 本次「取剪贴板」的目标设备局域网 IP。只用来过滤 READY / BUSY：
    // 另一台若还停在剪贴板页，会把一套别的分辨率的坐标播回来。
    private string? _clipExpectIp;
    // 上次收到平板剪贴板内容的时刻（Environment.TickCount64）。
    // 合并命令省掉了 READY 往返，就得靠「内容到没到」判断那一下点击有没有命中。
    private long _clipTextTick;
    // 自测（--clip-pull）用：等内容回到 PC 时置位，让命令行能等到结果再退出。
    private TaskCompletionSource<string>? _clipPullDone;

    // ── 反向：PC → 平板（2026-11-11 用户选定「补 PC→平板」）──
    // 走 uitest uiInput inputText，本质是替用户敲字，不是写系统剪贴板。
    // 该命令的 x y 取设备端**最后一次触摸坐标**，没触摸过就直接丢弃（no anchor），
    // 所以必须记住用户在画面上最后点的那一下，拿它当锚点 + 当输入框定位。
    // 同样按设备分开：锚点既是输入框定位也是 uitest 的「最后一次触摸坐标」，
    // 两台设备各有一套坐标，混用会把字敲到另一台上去。
    private readonly Dictionary<string, (int X, int Y)> _lastTapByDevice = new();
    private bool _clipSendBusy;

    // ── 键盘直通（2026-11-14，用户选定「走参考项目的 uitest 路线」）──
    // 背景：DEC-08 只保留了三键 + 低频点击，投屏窗口从不转发键盘。用户要能打字，
    // 而平板的 HarmonyOS 7 设置里**已经没有「扩展服务」入口**（第三方无障碍服务无从开启），
    // 所以不走 AccessibilityExtensionAbility.setText，改走系统自带的 uitest（= hnscrcpy 的做法）。
    //
    // 2026-11-14 换命令（用户反馈「延迟太高」）—— 从 inputText 改成 text：
    //   uitest uiInput inputText <x> <y> '<text>'   1222 / 1430 / 1458 ms（内部先模拟一次点击）
    //   uitest uiInput text '<text>'                 533 / 732 / 752 ms  ← 现在走这条
    // 省 ~700ms，并且**不再需要锚点坐标**：字直接落到设备当前焦点，
    // 从根上消除了「坐标猜错把字敲进用户别的应用」这类风险。
    // 残留瓶颈（实测，无法在 PC 侧消除）：中文 1574/1630ms（IME 逐字上屏）、emoji 只能走文件通道。
    //
    // 攒批 2026-11-14 二次修正：不再用固定定时器窗口。
    //   旧做法（300ms → 120ms 固定 DispatcherTimer）在**逐字慢敲**下全是白等：
    //   字间 450~600ms 时每个字都单独发一条命令（日志里 `长度=1` 占绝大多数）。
    //   新做法见 ScheduleKbdFlush：队列空闲立即发，队列忙则合并进下一批。
    // 字符与特殊键合并进**同一条** shell（`uinput -K -d N -u N ...; ...`），省一次 hdc 通道开销。
    private readonly System.Text.StringBuilder _kbdBuf = new();
    private Task _kbdChain = Task.CompletedTask;
    private bool _kbdPassthrough = true;
    private TextBox? _kbdSink;

    // 平板剪贴板页上系统 PasteButton 的兜底点击坐标（px）。
    // 正常情况下由设备在 READY 包里自带实测坐标，这里只在设备没报时兜底；
    // 基准是 1600x2560 的 MatePad Mini，换设备用环境变量 HARMOS_CLIP_TAP="x,y" 覆盖。
    private static readonly (int X, int Y) ClipTap = ResolveClipTap();
    /// <summary>用户是否显式给了 HARMOS_CLIP_TAP。只有这种「他知道该点哪」的情况才允许盲点兜底。</summary>
    private static readonly bool ClipTapFromEnv =
        Environment.GetEnvironmentVariable("HARMOS_CLIP_TAP") is { Length: > 0 };

    private static (int X, int Y) ResolveClipTap()
    {
        string? env = Environment.GetEnvironmentVariable("HARMOS_CLIP_TAP");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string[] p = env.Split(',');
            if (p.Length == 2 && int.TryParse(p[0].Trim(), out int x) && int.TryParse(p[1].Trim(), out int y))
            {
                return (x, y);
            }
        }
        // 默认坐标 = 平板浮窗图标中心的实测值。
        //
        // 浮窗由系统摆位，应用侧无法改位置，实测始终是 (1240,514)-(1522,796)，中心 (1382,656)。
        // 2026-11-12 01:48 拖它的标题栏试过，纹丝不动 —— 所以这个值在这块屏上是稳定的。
        // 换设备（比如折叠屏手机）分辨率不同会偏，此时 READY 校准会自动补点一次，
        // 之后按设备各存一份缓存（_clipTapByDevice），不影响后续速度。也可用环境变量 HARMOS_CLIP_TAP=x,y 覆盖。
        return (1382, 656);
    }
    private long _navClickTicks;               // 最近一次三键点击时刻（用于落盘端到端耗时）

    // ── P0 输入→回显探针（2026-11-13）──
    // 静止态下记录一次 PC 输入时刻，等 WpfSurface 真的上屏新帧时算出「输入→首帧」耗时。
    // 动态画面里下一帧本来就会到，测不出响应延迟，所以只在静止超过 EchoIdleMs 后才起探针。
    // 超时分支只写日志 + 节流催一次 IDR，绝不重连：静止是采集语义，按帧间隔判死会误杀健康流。
    // 静止判据必须与 Refresh 的 idle 判定一致（SlowScore >= 6）。
    // 不能用「距上一帧 >N 秒」：设备静止期仍有 0.7~6fps 零星帧，那个条件永远不成立，
    // 实测第一次上线探针一条日志都没落，就是栽在这里。
    private const int EchoIdleScore = 6;
    private const int EchoMinMs = 50;       // 输入后 <50ms 的帧视为静止期背景帧，不算回显
    private const int EchoNoFrameMs = 1200;
    private const int EchoIdrGapMs = 500;

    // ── P0 首帧等待提示（2026-11-16）──
    // 从未上过帧且等待超过阈值时，明确告诉用户「点亮屏幕」，别让他对着
    // 「等待画面 / 0 fps」干等：设备静止/息屏不产帧是采集语义，链路其实是好的。
    // 纯 UI 文案，不改看门狗判死、不唤屏、不重连、不碰采集链。
    private const long NoFrameHintMs = 10_000;
    private const string ScreenOnHint = "设备可能已息屏 —— 请点亮屏幕或轻滑一下";

    private long _echoInputTicks;              // 0 = 无在途输入
    private long _echoLastIdrTicks;
    private int _echoMisses;

    private byte _navPendingKey;               // 正在等回执的键，0 = 空闲
    private string _navPendingSession = string.Empty;
    private long _navPendingTicks;
    private TextBlock? _navHint;               // 胶囊内的失败提示（纯画面下状态栏是隐藏的）
    private DispatcherTimer? _navHintTimer;

    // ── 输入注入：优先走 uitest 持久 socket（GATE-06 达标路径，p50 75ms）──
    // 通道细节见 HarmOsWin.Core/UitestRpcClient.cs。首用要拉起设备侧 daemon，
    // 失败一律回退到 InputInjector 的 hdc shell 通道，不让三键失效。
    private UitestRpcClient? _rpc;
    private Task<bool>? _rpcBoot;

    // ── 画面点击转发（按下即 touchDown + 拖动 touchMove；INV-13 的高频注入限制已由用户解除）──
    // 不再为「双击呼出控制条」留等待窗口：所有鼠标手势都直通设备，界面只由键盘切换。
    private const int TapMoveTolerancePx = 6;    // 按下→抬起位移超过它算拖动，不注入
    private const int TapMinIntervalMs = 80;     // 连点节流
    private const int TapClickDelayMs = 260;     // 旧版等第二击的窗口（已停用，保留常量供回退）
    private DispatcherTimer? _tapTimer;
    private Point _tapDownOnImage;

    /// <summary>一次按下→拖动→抬起的会话。只在拖动期间存在。</summary>
    private sealed class DragSession
    {
        public Page? Page;
        public int LastX;
        public int LastY;
        public int PendingX;
        public int PendingY;
        public bool InFlight;   // 有一条 touchMove 正在等回包
        public bool Dirty;      // 在途期间又产生了新坐标
    }

    private DragSession? _dragSession;
    private bool _tapDragging;
    private int _tapPendingX;
    private int _tapPendingY;
    private long _lastTapInjectedMs;

    // ── 「画面最大化」相关：侧栏可折叠、顶部/底部条可隐藏（沉浸全屏） ──
    private Grid? _rootGrid;
    private ColumnDefinition? _sidebarCol;
    private UIElement? _sidebarHost;
    private Grid? _headerHost;
    private StackPanel? _bottomHost;
    private FlatButton? _foldBtn;
    private FlatButton? _immersiveBtn;
    private FlatButton? _fitBtn;
    private FlatButton? _fillBtn;
    // 默认「铺满」：Stretch.Fill 把画面拉到填满整个画面区，四周不露任何底色（零黑框）。
    // 点「▣ 等比」才切回 Stretch.Uniform（完整画面、可能留边）。窗口比例本来就被
    // WM_SIZING 锁在设备比例附近，Fill 造成的形变 <1%，肉眼不可见。
    private bool _fillMode = true;
    // ── 上边框栏（悬浮胶囊，不占布局行）──
    // 左：菜单按钮（纯画面模式下唯一常驻鼠标入口）+ 右：只显示帧率。
    // 不画横贯窗口的深色条带，否则就是之前删掉的「上方黑框」。
    private Border? _topBar;
    private Popup? _menuPopup;
    private long _menuClosedMs;   // Popup 被外部点击关闭的时刻，用来防止「点按钮关不掉」
    private readonly bool _menuEnabled;
    private readonly List<(FlatButton Btn, Func<string> Label)> _menuDynamicLabels = new();
    // 默认折叠侧栏：ApplyChrome 的 hideSidebar = _sidebarCollapsed || _immersive，
    // 若这里留 false，一按 F11 退出沉浸就会弹出 258 DIP 深色面板（用户说的「两侧黑边框」）。
    // 折叠后侧栏只由用户点工具栏 › 主动展开，任何键盘/鼠标切换都不会把它带出来。
    private bool _sidebarCollapsed = true;
    // 默认纯画面：隐藏顶部工具栏 / 底部状态条 / 侧栏，窗口里只剩画面。
    // 控制条只认 F11 / Esc（键盘），鼠标单击 / 双击 / 拖动一律只注入设备、不改界面；
    // --chrome on 可让启动就带 UI。
    private bool _immersive = true;

    private sealed class Page
    {
        public required DeviceSession Session;
        public required Grid Root;
        public required WpfSurface Surface;
        public required Image View;
        public required Grid LockOverlay;
        public required FlatButton Row;
        public string DeviceName = "";   // 多设备时侧栏/标题栏显示的真实设备名
        public string DeviceIp = "";     // 该设备的无线调试地址（IP:端口），供副标题/标题栏显示
        public bool Streaming;
        public long LastPresented;
        public long LastTickMs;
        public long LastGrowMs;      // 最后一次「收到新帧」的时刻：静止 0 帧是采集语义，不是断流
        public int SlowScore;        // 低帧率积分（带迟滞），用于判定「画面静止」
        public double Fps;
        public long NoFrameSinceMs;     // 本设备「从未上过帧」的等待起点（0 = 未计时）
        public bool ScreenOnHintShown;  // 已提示过「点亮屏幕」，避免每轮刷状态栏
    }

    /// <summary>
    /// GATE-02 上屏：MF 解码出的 BGRA 写入 WriteableBitmap。
    /// 静态审查修正（2026-11-09）：
    ///  ① 解码器 _bgra 是复用缓冲，不能把 DecodedFrame 直接闭包给异步 BeginInvoke，
    ///     否则 UI 写位图前数组已被下一帧覆写 → 撕裂/花屏。现在每帧先拷进一块
    ///     独立缓冲，UI 取走引用后才写位图，写完归还 _pool 复用。
    ///  ② 旧的 _pending 单槽「保旧弃新」会累积滞后；改为最新帧覆盖 _ready，
    ///     拥挤时 Dropped++ 并合帧，方向参考 hos_scrcpy native_stream.py 的
    ///     「队满丢最旧，实时流保新弃旧」。
    ///  ③ 16MB 的 BlockCopy 与 WritePixels 都不在 _stagingGate 内执行，锁内只
    ///     交换引用，避免解码线程与 UI 线程互相阻塞（60fps 帧预算仅 16.7ms）。
    /// </summary>
    private sealed class WpfSurface : IRenderSurface
    {
        private readonly Image _image;
        private readonly Dispatcher _dispatcher;
        private readonly object _stagingGate = new();
        // 缓冲所有权：_staging 归解码线程独占写；_ready 是待上屏的引用；
        // _pool 是 UI 归还的空闲块。锁内只交换引用，不再持锁做 16MB 拷贝。
        private readonly Queue<byte[]> _pool = new();
        private byte[] _staging = Array.Empty<byte>();
        private byte[]? _ready;
        private int _rw;
        private int _rh;
        private int _scheduled;
        private WriteableBitmap? _bitmap;
        private long _presented;
        private long _presentTicks;
        private long _presentCount;
        private long _dropped;
        private readonly Action? _onPresent;   // 每帧上屏回调（输入→回显探针用，只在有在途输入时落一条日志）

        public WpfSurface(Image image, Dispatcher dispatcher, BitmapScalingMode scaleMode,
            Action? onPresent = null)
        {
            _image = image;
            _dispatcher = dispatcher;
            _onPresent = onPresent;
            // 1600×2560 → 窗口约 2.1 倍缩小。Linear 双线性叠加 EdgeMode.Aliased（明确关闭
            // 抗锯齿）会让文字发虚、边缘起锯齿，是清晰度上限的直接瓶颈。
            // 此前注释写的「Fant 每帧高质量重采样 GPU 明显更贵」没有实测依据，已撤销；
            // 保留 --scale linear|nearest|high|fant 供客户端 A/B 实测，不预先下结论。
            RenderOptions.SetBitmapScalingMode(_image, scaleMode);
        }

        public string Name => "WriteableBitmap (BGRA)";
        public long Presented => Interlocked.Read(ref _presented);
        public long Dropped => Interlocked.Read(ref _dropped);
        public double AvgPresentMs =>
            Interlocked.Read(ref _presentCount) == 0
                ? 0
                : Interlocked.Read(ref _presentTicks) * 1000.0
                  / Interlocked.Read(ref _presentCount) / System.Diagnostics.Stopwatch.Frequency;

        public void Present(DecodedFrame frame)
        {
            int len = frame.Width * frame.Height * 4;
            if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length < len)
            {
                return;
            }
            // 先取一块 UI 绝不触碰的缓冲。锁内只做引用交换；16MB 的 BlockCopy
            // 放在锁外，否则解码线程与 UI 线程会互相卡在 _stagingGate 上 ——
            // 1600x2560 时这一来一回足够吃掉 60fps 的整帧预算。
            byte[] dst;
            lock (_stagingGate)
            {
                if (_staging.Length != len)
                {
                    _staging = new byte[len];
                }
                dst = _staging;
                // 池里的块必须按当前帧长复核：设备改分辨率时（实测平板 1600x2560 ↔ 800x1280
                // 会来回切），残留的旧块比 len 小，后面的 BlockCopy 会越界抛
                // ArgumentException 并打断这一路的解码线程。长度不符就丢弃重建。
                byte[]? pooled = _pool.Count > 0 ? _pool.Dequeue() : null;
                _staging = pooled is not null && pooled.Length == len ? pooled : new byte[len];
            }
            Buffer.BlockCopy(frame.Pixels, 0, dst, 0, len);
            lock (_stagingGate)
            {
                if (_ready is not null)
                {
                    // 上一帧还没上屏就被覆盖：保新弃旧（合帧）
                    RecycleLocked(_ready);
                    Interlocked.Increment(ref _dropped);
                }
                _ready = dst;
                _rw = frame.Width;
                _rh = frame.Height;
            }
            if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            {
                try
                {
                    _dispatcher.BeginInvoke(new Action(Render), DispatcherPriority.Render);
                }
                catch
                {
                    Interlocked.Exchange(ref _scheduled, 0);
                }
            }
        }

        private void RecycleLocked(byte[] buf)
        {
            if (_pool.Count < 2)
            {
                _pool.Enqueue(buf);
            }
        }

        private void Render()
        {
            Interlocked.Exchange(ref _scheduled, 0);
            byte[] src;
            int w;
            int h;
            lock (_stagingGate)
            {
                if (_ready is null)
                {
                    return;
                }
                src = _ready;
                _ready = null;
                w = _rw;
                h = _rh;
            }
            // WritePixels 也在锁外执行：src 已被 UI 独占，解码线程复用不到它。
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _image.Source = _bitmap;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), src, w * 4, 0);
            Interlocked.Add(ref _presentTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref _presentCount);
            Interlocked.Increment(ref _presented);
            _onPresent?.Invoke();
            lock (_stagingGate)
            {
                RecycleLocked(src);
            }
        }
    }

    public MainWindow()
    {
        Title = "HarmLink — 鸿蒙投屏";
        // 窗口图标（2026-11-14 用户要求）：exe 的 ApplicationIcon 只管文件/任务栏，
        // 窗口左上角与 Alt+Tab 缩略图走的是 Window.Icon，必须显式给。
        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
        }
        catch
        {
            // 图标缺失不影响功能，静默降级到系统默认图标
        }
        // 窗口不能超过实际工作区：本机可用高度约 631 DIP，写死 840 会把底部指标行裁掉。
        Rect wa = SystemParameters.WorkArea;
        Width = Math.Min(1280, Math.Max(960, wa.Width - 40));
        Height = Math.Min(860, Math.Max(560, wa.Height - 40));
        // 竖屏设备贴合后会变成窄窗口，MinWidth 不能拦着（原来 900 会把画面顶出黑边）
        MinWidth = 380;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Bg;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        // 用户 2026-11-10 23:14 要求「菜单和帧率显示在这个窗口栏上」：把系统标题栏交给
        // WindowChrome 自绘，客户区延伸到整个窗口，BuildContent 的 Row0（30 DIP）正好
        // 对应标题栏。CaptionHeight 区域内只有显式标记 IsHitTestVisibleInChrome 的控件
        // 能收到点击，其余仍是标题栏（拖动 / 双击最大化），不会吃掉画面点击。
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 30,
            ResizeBorderThickness = new Thickness(4),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            // 关掉系统原生 caption 按钮，三键（最小化 / 最大化 / 关闭）改为自绘：
            // 原生按钮在这条自绘浅色栏上既无 hover 反馈、风格也割裂，
            // 用户 2026-11-11 00:59 要求右上角放叉号点击退出整个应用。
            UseAeroCaptionButtons = false
        });

        // 最大化 / 还原后切换图标（方框 ↔ 双层方框）。窗口只有一个，默认
        // ShutdownMode.OnLastWindowClose，所以关闭主窗口 = 退出整个应用。
        StateChanged += (_, _) =>
            _btnMaximize?.SetMaximized(WindowState == WindowState.Maximized);

        string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        _injector = new InputInjector(projectDir);
        // 注入层的诊断日志（hdc 会话复用命中/降级）统一进 AppLog，不用展开工具卡片也能查
        _injector.Log = AppLog.Info;

        // 旧版单击延时注入用的定时器（等第二击窗口）已停用，保留实例供 CancelPendingTap 兜底。
        // 现在是按下即发 touchDown、抬起补 touchUp，双击也只当作两次完整点击。
        _tapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TapClickDelayMs) };
        _tapTimer.Tick += (_, _) =>
        {
            _tapTimer!.Stop();
            _ = InjectTapAsync(_tapPendingX, _tapPendingY);
        };
        _manager = new SessionManager();
        _manager.Log += msg => PostUi(() => SetStatus(msg));

        // 多设备清单必须早于任何连接动作解析：BringUpStreamAsync 会按它决定
        // 「走单设备还是双路并行」，而那时已经进 Loaded 了。
        LoadDeviceList(Environment.GetCommandLineArgs());
        // 除主端口（9010）以外的每台设备的收流端口都要单独开监听：
        // 采集桥把帧发到各自的 LocalPort，没人听就是黑屏 + packets 恒 0。
        // 注意**必须按设备清单全量判断**，不能只从第 2 台起 —— 用 --single 2
        // 只留一台时，那台可能就是 9011（2026-11-13 实测踩到）。
        foreach (var d in _devices)
        {
            if (d.LocalPort != Hwp.LocalPort)
            {
                _manager.AddEndpoint(d.LocalPort);
            }
        }

        // 自测模式：HarmOsWin.App.exe --auto <设备IP>，自动连接并下发 CMD_STREAM，
        // 用于自动化验收「PC 窗口实际上屏是否流畅」，无需人工点按钮。
        // 可选 --metrics <jsonl> 每秒落一行指标；--snapshot <png> 导出窗口快照。
        // --snapshot-delay <秒> 控制快照时机，默认 12s。实测：设备静止画面下 PC 中途接入
        // 要等解码器就绪 + 首个 IDR，12s 常常还停在黑屏，快照会误判成「上屏失败」（2026-11-08）。
        string[] argv = Environment.GetCommandLineArgs();
        // 默认纯画面（无上下深色条、无两侧黑边）。--chrome on 还原成带工具栏/状态条的老界面。
        string? chromeArg = ArgValue(argv, "--chrome");
        _immersive = !string.Equals(chromeArg, "on", StringComparison.OrdinalIgnoreCase);
        // 上边框菜单按钮默认常驻；--menu off 可关掉，回退到「只有画面 + F11」的极简状态。
        _menuEnabled = !string.Equals(ArgValue(argv, "--menu"), "off", StringComparison.OrdinalIgnoreCase);
        _autoHost = ArgValue(argv, "--auto");
        // 没有 --auto 时读同目录 harmoswin.json 里的设备地址，这样双击就能直接用。
        // （不带参数时 --auto 为空，窗口会停在「等手动连」状态，对双击用户不友好）
        if (string.IsNullOrWhiteSpace(_autoHost))
        {
            _autoHost = ReadConfiguredHost();
        }
        _useReferenceCapture = !string.Equals(ArgValue(argv, "--capture"), "hap",
            StringComparison.OrdinalIgnoreCase);
        _navUseHwpMain = string.Equals(ArgValue(argv, "--nav"), "hwp",
            StringComparison.OrdinalIgnoreCase);
        // 显式覆盖：--hdctarget 192.168.1.100:38803（也认环境变量，方便脚本里设）。
        // 只覆盖「优先尝试哪个」，是否真能用仍由 HdcTargetResolver 实连验证。
        _hdcTargetOverride = ArgValue(argv, "--hdctarget")
                             ?? Environment.GetEnvironmentVariable("HARMOS_HDC_TARGET");
        AppLog.Info($"命令行: {string.Join(' ', argv.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        AppLog.Info($"配置: capture={(_useReferenceCapture ? "ref" : "hap")} " +
                    $"autoHost={_autoHost ?? "-"} hdcTarget={ReadConfiguredHdcTarget() ?? "-"} " +
                    $"override={_hdcTargetOverride ?? "-"} configFile=" +
                    (ResolveConfigPath() ?? "(未找到)"));
        string? holdArg = ArgValue(argv, "--hold");
        bool? holdForced = string.Equals(holdArg, "on", StringComparison.OrdinalIgnoreCase) ? true
                         : string.Equals(holdArg, "off", StringComparison.OrdinalIgnoreCase) ? false
                         : (bool?)null;
        // MF_LOW_LATENCY 生效 → 解码器不再有约 37 帧内部输出缓冲，静止画面由
        // WPF 上屏层自然保持，完全不需要补帧。补帧反而会置 _needKeyframe，把
        // 真实 P 帧全丢掉直到设备 IDR（用户反馈「操作后超过 2s 才更新」）。
        // 属性不被本机 MFT 接受时保留补帧兜底，避免双击直接黑屏。
        _lowLatency = MediaFoundationDecoder.ProbeLowLatency();
        _holdEnabled = holdForced ?? !_lowLatency;
        // 默认 20ms（约 ≤50fps）：低延迟 MFT 单帧 Feed 实测 18.3ms，设备 89fps 会
        // 打爆解码队列 → 丢到 IDR → 滑动周期性冻结。--refresh 1 可还原旧行为做 A/B。
        string? refreshArg = ArgValue(argv, "--refresh");
        _refreshInterval = int.TryParse(refreshArg, out int ri) && ri > 0 && ri <= 200 ? ri : 20;
        // -frameRate：参考项目用的帧率参数。默认 0（不传），避免改变已验证的启动命令。
        string? rateArg = ArgValue(argv, "--framerate");
        _frameRate = int.TryParse(rateArg, out int fr) && fr > 0 && fr <= 120 ? fr : 0;
        // -bitRate（字节/秒）：实测 20M 时 1600x2560 只有 0.041 bpp，文字界面偏低，
        // 是「画质有点差」的直接来源。默认 40M（约 0.08 bpp）；--bitrate 可覆盖。
        string? bitArg = ArgValue(argv, "--bitrate");
        _bitRate = int.TryParse(bitArg, out int br) && br >= 1_000_000 && br <= 200_000_000
            ? br : 20_000_000;
        // 设备端缩小倍数：默认 2（800x1280）—— 三档真机实测的最优点（见字段注释）：
        // 解码耗时最低（5.9ms）、滑动零丢帧，且彩色小字色度损失远小于 devscale 3。
        // --devscale 1 画质再高 14% 但会卡（排队丢 73 帧）；--devscale 3 更省但小字发糊。
        // 2026-11-12 边界定稿：本机 2560x1440 全屏时画面区约 806x1290，对 800x1280 仅放大 1.008 倍，
        // 当前配置全程 1:1 或缩小，不存在「拖大就糊」，**不做自动选档**（切 1 必然掉帧，见架构补记㊵）。
        string? devScaleArg = ArgValue(argv, "--devscale");
        _devScale = int.TryParse(devScaleArg, out int ds) && ds >= 1 && ds <= 5 ? ds : 2;
        // 注入坐标 = 归一化 × 物理屏幕尺寸。物理尺寸 = 采集输出 × devscale。
        // 不做这步，devscale 2 时点击会整体落在设备屏幕左上 1/4 区域（2026-11-10 用户实测）。
        string? touchScaleArg = ArgValue(argv, "--touchscale");
        _touchScale = double.TryParse(touchScaleArg, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double ts) && ts >= 0.2 && ts <= 8.0
            ? ts : _devScale;
        // 窗口贴合倍数：默认 2/3（用户 2026-11-10 试过 8/15 后要求还原）。
        // --winscale 1 还原「撑满工作区」；--winscale 0.5 再小一半。
        string? winScaleArg = ArgValue(argv, "--winscale");
        _fitScale = double.TryParse(winScaleArg, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double ws) && ws >= 0.2 && ws <= 1.0
            ? ws : 2.0 / 3.0;
        // 参考采集模式下，会话连的永远是本机采集桥 127.0.0.1:9010，
        // 真正的设备地址由 harmoswin.json 的 hdcTarget 交给 hdc（见 StartReferenceCaptureAsync）。
        //
        // 2026-11-11 修正：以前这里要求 _autoHost 非空才改写，等于「配置里没有 device 就停在等手动连接」。
        // 用户的实际期望是「双击打开就自动投屏」，所以改成无条件自动 —— 配置缺失也照样自动连，
        // 否则界面会停在需要手点的状态，用户去点「连接设备」就会凭空多出一个指向设备真实 IP
        // 的死会话（那次实测就是 device-1 127.0.0.1 与 device-2 192.168.1.100 并存、画面卡住）。
        if (_useReferenceCapture)
        {
            _autoHost = "127.0.0.1";
        }
        _metricsPath = ArgValue(argv, "--metrics");
        _snapshotPath = ArgValue(argv, "--snapshot");
        string? snapDelayArg = ArgValue(argv, "--snapshot-delay");
        int snapDelayMs = int.TryParse(snapDelayArg, out int snapSec) && snapSec > 0 ? snapSec * 1000 : 12000;
        // 缩放质量 A/B：--scale linear|nearest|high|fant，默认 fant（缩小 2 倍时文字最清晰）
        _scaleMode = ArgValue(argv, "--scale")?.ToLowerInvariant() switch
        {
            "linear" => BitmapScalingMode.Linear,
            "nearest" => BitmapScalingMode.NearestNeighbor,
            "high" => BitmapScalingMode.HighQuality,
            _ => BitmapScalingMode.Fant
        };

        _hostBox = new TextBox
        {
            Text = Hwp.DefaultDeviceHost,
            Background = Brushes.Transparent,
            Foreground = Theme.Text,
            BorderThickness = new Thickness(0),
            CaretBrush = Theme.Accent,
            FontFamily = Theme.MonoFont,
            FontSize = 12,
            Padding = new Thickness(9, 7, 9, 7),
            VerticalContentAlignment = VerticalAlignment.Center
        };

        _streamBtn = new FlatButton("开始推流", BtnKind.Primary, 40, 13.5);
        _streamBtn.Clicked += () => _ = ToggleStreamAsync();

        // 原名「模拟锁屏」有误导性：它不会让平板熄屏或锁屏，只是在本机画面上盖一层遮罩。
        // 平板侧的亮度/熄屏在 HarmonyOS 上对第三方完全封闭（见架构补记㊷㊸㊹㊺），此处不再暗示能控制设备。
        _lockBtn = new FlatButton("隐私遮罩", BtnKind.Ghost, 34);
        _lockBtn.Clicked += () =>
        {
            var s = _manager.Active();
            if (s is null)
            {
                SetStatus("先连接设备");
                return;
            }
            _ = s.SetLockStateAsync(true);   // 保留控制帧：设备端只记一行日志，无副作用，供将来联动预留
            SetLocked(s.Id, true);
        };

        _unlockBtn = new FlatButton("取消遮罩", BtnKind.Ghost, 34);
        _unlockBtn.Clicked += () =>
        {
            var s = _manager.Active();
            if (s is null)
            {
                return;
            }
            _ = s.SetLockStateAsync(false);
            SetLocked(s.Id, false);
        };

        // 这两个按钮作用在**平板**上，不是本机画面 —— 用户明确要求「调节设备，而不是调节电脑屏幕」。
        // 走控制中心点击注入（DeviceBrightness），不需要任何受限权限。
        _dimBtn = new FlatButton("调暗设备", BtnKind.Ghost, 34);
        _dimBtn.Clicked += () => _ = ApplyDeviceBrightnessAsync(dim: true);
        _restoreBtn = new FlatButton("恢复亮度", BtnKind.Ghost, 34);
        _restoreBtn.Clicked += () => _ = ApplyDeviceBrightnessAsync(dim: false);

        Content = BuildRoot();
        ApplyChrome();   // 把默认「纯画面」状态落到布局上（隐藏上下条、侧栏）

        // 设备条目 + 标题栏图标：配置里写了但被禁用/没接进来的那几台也要摆出来
        // （2026-11-14 20:37 要求两枚都在；2026-11-16 要求各自显示 IP 且都能点击切换）。
        AddPendingDeviceUi();

        // F11 / Esc 在「纯画面」与「带控制条」之间切换（只改可见性，不改窗口尺寸）
        PreviewKeyDown += (_, e) =>
        {
            // 上边框菜单开着时，Esc 只收菜单，不要顺手把控制条也呼出来。
            if (e.Key == Key.Escape && _menuPopup?.IsOpen == true)
            {
                _menuPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F11)
            {
                if (_menuPopup is not null)
                {
                    _menuPopup.IsOpen = false;
                }
                ToggleImmersive();
                e.Handled = true;
            }
            // Ctrl+Shift+V：把电脑剪贴板推进平板的光标处（PC → 平板）。
            // 用 Shift 拉开与 Ctrl+V 的距离：投屏窗口里 Ctrl+V 不该被我们吃掉，
            // 万一将来要做「按键直通」也不会误触。
            else if (e.Key == Key.V
                     && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                _ = SendClipboardToTabletAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _immersive)
            {
                ToggleImmersive();
                e.Handled = true;
            }
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();

        // 用户拖动/缩放窗口后把比例吸回去：竖屏设备放在横窗口里，等比例显示必然左右留黑边。
        // 用防抖等拖动真正结束再贴合，避免拖动过程中窗口跟手抖。
        SizeChanged += (_, _) =>
        {
            // _windowAspect 为 0 表示还没贴合过（启动初期侧栏还在、尺寸也在变），
            // 此时吸附会抢在侧栏收起之前把窗口贴成带侧栏的宽度，白跳一次。
            if (_fitting || _windowAspect <= 0.05 || _stage.ActualWidth <= 1)
            {
                return;
            }
            _fitDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            if (_fitDebounce.Tag is null)
            {
                _fitDebounce.Tick += (_, _) =>
                {
                    _fitDebounce.Stop();
                    // 只纠正被破坏的比例，不重新贴合：否则用户手动缩放后会被弹回最大尺寸
                    CorrectAspectIfNeeded();
                };
                _fitDebounce.Tag = "bound";
            }
            _fitDebounce.Stop();
            _fitDebounce.Start();
        };

        Loaded += async (_, _) =>
        {
            if (_metricsPath is not null)
            {
                try
                {
                    _metricsWriter = new StreamWriter(_metricsPath, false) { AutoFlush = true };
                    var mt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    mt.Tick += (_, _) => DumpMetrics();
                    mt.Start();
                }
                catch
                {
                    _metricsWriter = null;
                }
            }
            // 注入通道自测：--nav-test <out.txt>，把 RPC / hdc 两条路的三键耗时落盘。
            // 不依赖设备连接/推流，可单独用。
            string? navTestPath = ArgValue(argv, "--nav-test");
            if (navTestPath is not null)
            {
                await NavSelfTestAsync(navTestPath);
            }
            if (_autoHost is null)
            {
                return;
            }
            // ⚠ Loaded 是 async void：这里抛出的任何异常都会变成「未处理异常」直接终止进程，
            // 而且 WPF 不会写进我们的日志，表现就是「窗口一闪自己没了」（2026-11-14 凌晨踩到，
            // Windows 事件日志里是 0xE0434352 + NullReferenceException at BringUpStreamAsync）。
            // 必须就地捕获并把完整堆栈写日志，否则下次还是查不到。
            bool streamOk = false;
            try
            {
                streamOk = await BringUpStreamAsync();
                if (!streamOk && _useReferenceCapture)
                {
                    // 自愈重试一次：首轮失败常见于「设备端上一个采集 daemon 还没退干净」
                    // 或「无线调试端口刚好在这个间隙变了」。这里清干净、重新探测目标再拉一遍。
                    AppLog.Warn("首轮连接失败，开始自愈重试");
                    SetStatus("[自愈] 首轮连接失败，正在重新探测设备并重试…");
                    await StopCaptureQuietlyAsync();
                    await Task.Delay(1500);   // 等设备侧旧进程退出，否则新旧实例互踢
                    streamOk = await BringUpStreamAsync();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"[启动] 建立画面连接异常（已捕获，进程继续运行）：{ex}");
                SetStatus("启动流程异常，详见日志 " + AppLog.LogPath);
            }
            // 2026-11-16：启动结束后再补一次 —— 启用过但建桥失败的那台也要有可点条目
            // 和标题栏图标，不能因为启动失败就从界面上消失。建控件必须回 UI 线程。
            await InvokeUiAsync(AddPendingDeviceUi).ConfigureAwait(true);
            if (streamOk && _injector.SessionEnabled)
            {
                // 后台预热 hdc 常驻会话：ConPTY 第一次要建伪终端 + 跑一条探针（几百 ms），
                // 不预热的话用户第一次打字会额外等这一次建链。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 先等 8 秒：启动期采集还在跑 tconn / fport / file send 那一串 hdc 调用，
                        // 这时抢锁建会话会把两边都拖死（更糟的是探针超时会让会话被永久降级）。
                        await Task.Delay(8000).ConfigureAwait(false);
                        var w = await _injector.WarmUpAsync().ConfigureAwait(false);
                        AppLog.Info($"[注入] 常驻会话预热 {w.ElapsedMs:F0}ms ok={w.Ok}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[注入] 会话预热失败：{ex.Message}");
                    }
                });
            }
            if (!streamOk)
            {
                SetStatus("投屏未建立：请确认平板屏幕亮着、无线调试开着、" +
                          $"与电脑同一 Wi-Fi（详见 {AppLog.LogPath}）");
            }
            // 点击转发自测（需配合 --auto）：--tap-test 0.5,0.5 --tap-out <file>
            // 走完整的「归一化坐标 → 画面控件坐标 → 设备像素」映射，再真注入一次。
            string? tapTest = ArgValue(argv, "--tap-test");
            if (tapTest is not null)
            {
                string? tapDelayArg = ArgValue(argv, "--tap-delay");
                int tapDelayMs = int.TryParse(tapDelayArg, out int td) && td > 0 ? td : 2500;
                await TapSelfTestAsync(tapTest, ArgValue(argv, "--tap-out"), tapDelayMs);
            }
            // 自测：--active <n> 把第 n 台设备设为活动页（1 起）。
            // 用来程序化验证「点标签切换」——切换只改可见性、不拆流，
            // 切完立刻抓快照就应该看到那台自己的画面。
            // 2026-11-13：挪到剪贴板/其它自测**之前**。原来它在最后，
            // 于是「--active 2 --clip-pull」会先在默认的第 1 台上取剪贴板，
            // 根本测不到「剪贴板是否跟着设备走」。
            string? activeArg = ArgValue(argv, "--active");
            if (int.TryParse(activeArg, out int activeIdx) && activeIdx >= 1)
            {
                var ordered = _pages.Values.Select(p => p.Session.Id).ToList();
                if (activeIdx <= ordered.Count)
                {
                    SwitchTo(ordered[activeIdx - 1]);
                    AppLog.Info($"[自测] 已切到第 {activeIdx} 台：{ordered[activeIdx - 1]}");
                    await Task.Delay(800);
                }
            }
            // 剪贴板下拉自测：--clip-pull 1 [--clip-out <file>]
            // 走**产品真实路径**（aa start → 等 READY → 注入点击 → 平板回传），
            // 把端到端耗时与内容写出来，用来回归「用户抱怨的动画太长」有没有反弹。
            string? clipPullTest = ArgValue(argv, "--clip-pull");
            if (clipPullTest is not null)
            {
                await ClipPullSelfTestAsync(ArgValue(argv, "--clip-out"));
            }
            // 剪贴板上行自测（2026-11-12）：--clip-push <png>
            // 把指定 PNG 放进 Windows 剪贴板，再走**产品真实路径**「↑ 上传」，
            // 用来验证「图片下行 PC → 平板」整条链（aa start → TCP PUT → pasteboard.setData）。
            string? clipPushTest = ArgValue(argv, "--clip-push");
            if (clipPushTest is not null)
            {
                await ClipPushSelfTestAsync(clipPushTest);
            }
            // hdc 循环自测：在 WPF 进程里连续跑 fport add/rm，用来定位
            // 「控制台稳定、WPF 必卡」的环境差异（2026-11-15 排查用）。
            string? hdcLoop = ArgValue(argv, "--hdc-loop");
            if (hdcLoop is not null)
            {
                string hdcExe = @"E://DevEco Studio//sdk//default//openharmony//toolchains//hdc.exe";
                string tgt = @"192.168.1.102:40663";
                var lines = new List<string>();
                int n = 8;
                for (int i = 1; i <= n; i++)
                {
                    int port = 61000 + i;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var rm = HdcRunner.RunAsync(hdcExe,
                        new[] { "-t", tgt, "fport", "rm", $"tcp:{port}", "localabstract:uitest_socket" },
                        acceptFailMarker: true).GetAwaiter().GetResult();
                    long rmMs = sw.ElapsedMilliseconds;
                    sw.Restart();
                    var ad = HdcRunner.RunAsync(hdcExe,
                        new[] { "-t", tgt, "fport", $"tcp:{port}", "localabstract:uitest_socket" })
                        .GetAwaiter().GetResult();
                    long adMs = sw.ElapsedMilliseconds;
                    bool listen = HdcRunner.IsServerListening();
                    lines.Add($"#{i}: rm {rmMs}ms | add {adMs}ms ok={ad.Ok} | 8710={listen} | {FirstLineOf(ad.Output)}");
                    HdcRunner.RunAsync(hdcExe,
                        new[] { "-t", tgt, "fport", "rm", $"tcp:{port}", "localabstract:uitest_socket" },
                        acceptFailMarker: true).GetAwaiter().GetResult();
                }
                File.WriteAllLines(hdcLoop, lines);
                SetStatus($"[hdc-loop] 见 {hdcLoop}");
            }

            // 重复连接自测：模拟用户「连不上就反复点连接设备」。
            // 验证 AddDevice 不会产生多个同地址会话（多会话会让包只投给第一个、设备端死循环重放 IDR）。
            string? dupTest = ArgValue(argv, "--dup-test");
            if (dupTest is not null)
            {
                var lines = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    AddDevice(_autoHost);
                    await Task.Delay(400);
                    lines.Add($"第 {i + 1} 次连接后会话数 = {_manager.All().Count}" +
                              $"，会话 = [{string.Join(",", _manager.All().Select(s => s.Id))}]" +
                              $"，当前 = {_manager.ActiveId}");
                }
                File.WriteAllLines(dupTest, lines);
                SetStatus($"[dup-test] 见 {dupTest}");
            }
            if (_snapshotPath is not null)
            {
                await Task.Delay(snapDelayMs);
                CaptureWindow(_snapshotPath);
            }
        };

        // 剪贴板收件箱：与抓流模式无关，窗口一起来就监听。
        StartClipInbox();

        Closed += (_, _) =>
        {
            timer.Stop();
            _clipInbox?.Dispose();
            _manager.Dispose();
            // 必须同步等清理完成：fire-and-forget 会让进程先退出，
            // 设备端残留扩展守护进程与 fport 规则。
            try
            {
                // 先关触控（socket + fport），再关视频（杀扩展 daemon + fport），
                // 两条通道都复原，避免设备端残留进程。
                // 双路并行下必须全关：每台的 fport 与 daemon 都是独立的。
                foreach (var t in _referenceTouches.Values)
                {
                    t.StopAsync().GetAwaiter().GetResult();
                }
                foreach (var c in _referenceCaps.Values)
                {
                    c.StopAsync().GetAwaiter().GetResult();
                }
            }
            catch
            {
                // 关闭阶段不再抛异常
            }
            _ = _rpc?.DisposeAsync();
            // 关掉常驻 hdc 会话进程（未启用时是空操作），避免退出后留一个 hdc shell 子进程
            _injector?.Dispose();
            // 关掉 hdc 桥进程。它内部持有 hdcd server（fport 规则也存在那边），
            // 不关会给下次启动留一个「pid 文件指向已死进程」的陈旧状态，
            // 直接拖慢后续每一条 hdc 调用（2026-11-15 踩过）。
            try
            {
                HdcRunner.Shutdown();
            }
            catch
            {
                // 关闭阶段不再抛异常
            }
        };
    }

    // ── 布局 ────────────────────────────────────────────────────────────

    private UIElement BuildRoot()
    {
        var root = new Grid();
        _rootGrid = root;
        // Row0 = 自绘系统标题栏（30 DIP = WindowChrome.CaptionHeight），跨两列；
        // Row1 = 原内容区。启用 WindowChrome 后客户区高度 = 整个窗口高度，扣掉 Row0
        // 的 30 DIP，内容区 / 画面区高度与启用前完全一致 → 点击坐标映射不变。
        // 跨两列是关键：侧栏展开 258 DIP 时标题栏不会被切断成「左深右浅」。
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _sidebarCol = new ColumnDefinition { Width = new GridLength(258) };
        root.ColumnDefinitions.Add(_sidebarCol);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── 系统标题栏（自绘）：标题文字 + 菜单 + 帧率，右侧留给系统三大按钮 ──
        // 浅色 #F3F3F3 对齐系统栏；挂在上面的是浅底深字胶囊，跟栏融为一体，
        // 不再是两块贴在浅色底上的深色膏药。这是窗口装饰栏，不是画面内的横贯深色条带。
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 145, 0)   // 右侧 145 DIP 避开最小化 / 最大化 / 关闭
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = "HarmLink — 鸿蒙投屏",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.TitleText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        var titleBar = new Border
        {
            Background = Theme.TitleBar,
            BorderBrush = Theme.TitleBarBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleRow
        };
        Grid.SetRow(titleBar, 0);
        Grid.SetColumnSpan(titleBar, 2);
        Panel.SetZIndex(titleBar, 30);
        root.Children.Add(titleBar);

        // ── 右上角窗口按钮（自绘）：最小化 / 最大化 / 关闭 ──
        // 系统原生 caption 按钮已关（UseAeroCaptionButtons = false），这三个必须自己补齐，
        // 否则窗口既不能最小化也不能最大化。
        // 关闭按钮点下去走 Close()：MainWindow.Closed 里同步做 fport rm + 杀设备端扩展
        // daemon 的清理，ShutdownMode 默认 OnLastWindowClose → 关窗即退出整个应用。
        // 注意每个按钮都要 SetIsHitTestVisibleInChrome，否则 CaptionHeight 区域内
        // 点击会被 WindowChrome 当成「拖动标题栏」，按钮永远点不到。
        var captionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        var minBtn = new CaptionButton(CaptionKind.Minimize) { ToolTip = "最小化" };
        minBtn.Clicked += () => WindowState = WindowState.Minimized;

        var maxBtn = new CaptionButton(CaptionKind.Maximize) { ToolTip = "最大化 / 还原" };
        maxBtn.Clicked += () => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        _btnMaximize = maxBtn;

        var closeBtn = new CaptionButton(CaptionKind.Close) { ToolTip = "关闭并退出（会断开设备连接）" };
        closeBtn.Clicked += () => Close();

        foreach (var cb in new[] { minBtn, maxBtn, closeBtn })
        {
            WindowChrome.SetIsHitTestVisibleInChrome(cb, true);
            captionBar.Children.Add(cb);
        }

        // 内容区在最大化时被系统边框内缩，按钮组要跟栏一起贴到最右。
        Grid.SetRow(captionBar, 0);
        Grid.SetColumnSpan(captionBar, 2);
        Panel.SetZIndex(captionBar, 40);
        root.Children.Add(captionBar);

        var sidebar = BuildSidebar();
        _sidebarHost = sidebar;
        Grid.SetColumn(sidebar, 0);
        Grid.SetRow(sidebar, 1);
        root.Children.Add(sidebar);

        var content = BuildContent(titleRow);
        Grid.SetColumn(content, 1);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        return root;
    }

    private UIElement BuildSidebar()
    {
        var panel = new StackPanel { Margin = new Thickness(18, 18, 18, 14) };

        // 品牌
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal };
        brandRow.Children.Add(new TextBlock
        {
            Text = "HarmLink",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Text
        });
        var verChip = new Border
        {
            Background = Theme.AccentSoft,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "PC",
                FontSize = 10,
                Foreground = Theme.Accent,
                FontFamily = Theme.UiFont
            }
        };
        brandRow.Children.Add(verChip);
        panel.Children.Add(brandRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Windows 控制台 · HWP v1.1",
            FontSize = 11,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(0, 4, 0, 0)
        });

        panel.Children.Add(Divider.Make(16, 14));

        // 设备
        panel.Children.Add(Caption.Make("设备"));
        var hostWrap = new Border
        {
            Background = Theme.Surface,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = _hostBox
        };
        var connectBtn = new FlatButton("连接设备", BtnKind.Primary, 34, 12.5);
        connectBtn.Clicked += () => AddDevice(_hostBox.Text.Trim());
        connectBtn.Margin = new Thickness(0, 8, 0, 0);

        // 参考采集模式（默认走的那条）下，设备地址由 harmoswin.json 的 hdcTarget 交给 hdc，
        // 会话固定连本机采集桥 127.0.0.1。把「设备 IP + 连接设备」留在界面上只有一个后果：
        // 用户看到设备栏就去连，连出来的会话指向设备真实 IP，那条路上没人监听 → 死会话。
        // 所以这里直接换成一句只读说明，从界面层面断掉误操作（2026-11-11 用户实测）。
        if (_useReferenceCapture)
        {
            hostWrap.Visibility = Visibility.Collapsed;
            connectBtn.Visibility = Visibility.Collapsed;
            panel.Children.Add(new Border
            {
                Background = Theme.Surface,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 7, 9, 7),
                Child = new TextBlock
                {
                    Text = _devices.Count > 1
                        ? $"打开即自动连接 {_devices.Count} 台 · 两路同时投屏 · 点条目切换查看"
                        : "打开即自动连接 · 设备由 hdc 无线调试提供",
                    FontSize = 11,
                    Foreground = Theme.TextMuted,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }
        else
        {
            panel.Children.Add(hostWrap);
            panel.Children.Add(connectBtn);
        }

        var listHost = new ScrollViewer
        {
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 10, 0, 0),
            Content = _deviceList
        };
        panel.Children.Add(listHost);

        panel.Children.Add(Divider.Make(14, 12));

        // 推流
        panel.Children.Add(Caption.Make("推流"));
        panel.Children.Add(_streamBtn);

        panel.Children.Add(Divider.Make(14, 12));

        // 导航三键（DEC-08 曾降级为「三键 + 低频点击」，该限制已由用户解除）
        panel.Children.Add(Caption.Make("导航"));
        var navRow = new Grid();
        for (int i = 0; i < 3; i++)
        {
            navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        AddNav(navRow, 0, "返回", NavKey.Back);
        AddNav(navRow, 1, "主页", NavKey.Home);
        AddNav(navRow, 2, "多任务", NavKey.Recents);
        panel.Children.Add(navRow);

        panel.Children.Add(Divider.Make(14, 12));

        // 隐私遮罩（原名「锁屏」，实际不会让设备熄屏/锁屏）
        panel.Children.Add(Caption.Make("画面遮挡"));
        var lockRow = new Grid();
        lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_lockBtn, 0);
        _unlockBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_unlockBtn, 1);
        lockRow.Children.Add(_lockBtn);
        lockRow.Children.Add(_unlockBtn);
        panel.Children.Add(lockRow);

        panel.Children.Add(Divider.Make(14, 12));

        // 设备亮度（作用在平板上；与上面的「画面遮挡」不同，这个是真的改平板背光）
        panel.Children.Add(Caption.Make("设备亮度"));
        var dimRow = new Grid();
        dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_dimBtn, 0);
        _restoreBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_restoreBtn, 1);
        dimRow.Children.Add(_dimBtn);
        dimRow.Children.Add(_restoreBtn);
        panel.Children.Add(dimRow);

        // 底部：连接数
        _deviceCount.Text = $"已连接 0/{SessionManager.MaxDevices}";
        _deviceCount.FontSize = 11;
        _deviceCount.Foreground = Theme.TextMuted;
        _deviceCount.Margin = new Thickness(2, 0, 0, 0);
        var spacer = new Border { Height = 1 };
        panel.Children.Add(spacer);

        var sidebarInner = new DockPanel();
        DockPanel.SetDock(panel, Dock.Top);
        var foot = new Border
        {
            Padding = new Thickness(18, 0, 18, 14),
            Child = _deviceCount
        };
        DockPanel.SetDock(foot, Dock.Bottom);
        sidebarInner.Children.Add(foot);
        sidebarInner.Children.Add(panel);

        return new Border
        {
            Background = Theme.Panel,
            BorderBrush = Theme.BorderSoft,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebarInner
        };
    }

    private void AddNav(Grid row, int col, string text, byte key)
    {
        var btn = new FlatButton(text, BtnKind.Ghost, 34, 12.5);
        if (col > 0)
        {
            btn.Margin = new Thickness(8, 0, 0, 0);
        }
        btn.Clicked += () => SendNav(key);
        Grid.SetColumn(btn, col);
        row.Children.Add(btn);
    }

    private UIElement BuildContent(Panel titleRow)
    {
        var grid = new Grid
        {
            // 四周零边距：原来 (10,8,10,6) 露出的深色底就是用户说的「两侧黑边框」和上下黑框。
            // 画面区直接顶到窗口客户区边缘。
            Margin = new Thickness(0),
            Background = Theme.Bg
        };
        // 自绘系统标题栏画在 BuildRoot 的 Row0（跨两列），这里仍是「上下空 Auto 行 +
        // 中间 Star 画面行」：标题条 / 状态条 / 胶囊都是 Row1 浮层，不占布局行 ——
        // 这是「零黑框 + 点击坐标恒定」的关键；WindowChrome 前后画面区高度保持一致。
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 空行
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });     // 画面
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // 空行

        // ── 标题条：折叠侧栏 + 设备名 + 分辨率 + 状态胶囊 + 沉浸 ──
        var header = new Grid();
        _headerHost = header;
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _foldBtn = new FlatButton("\u2039", BtnKind.Ghost, 26, 14);
        _foldBtn.Width = 34;
        _foldBtn.Padding = new Thickness(0);
        _foldBtn.Margin = new Thickness(0, 0, 8, 0);
        _foldBtn.ToolTip = "收起 / 展开左侧栏";
        _foldBtn.Clicked += ToggleSidebar;
        header.Children.Add(_foldBtn);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _activeTitle.Text = "尚未连接设备";
        _activeTitle.FontSize = 16;
        _activeTitle.FontWeight = FontWeights.SemiBold;
        _activeTitle.Foreground = Theme.Text;
        _activeSub.Text = "在左侧输入平板 IP 后点「连接设备」";
        _activeSub.FontSize = 10.5;
        _activeSub.Foreground = Theme.TextMuted;
        _activeSub.Margin = new Thickness(0, 2, 0, 0);
        titleStack.Children.Add(_activeTitle);
        titleStack.Children.Add(_activeSub);
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);

        _resolutionChip.Text = "—";
        _resolutionChip.FontFamily = Theme.MonoFont;
        _resolutionChip.FontSize = 11;
        _resolutionChip.Foreground = Theme.TextSub;
        _resolutionChip.VerticalAlignment = VerticalAlignment.Center;
        _resolutionChip.Margin = new Thickness(12, 0, 0, 0);

        _fitBtn = new FlatButton("⤢ 贴合画面", BtnKind.Ghost, 26, 11.5);
        _fitBtn.Margin = new Thickness(10, 0, 0, 0);
        _fitBtn.ToolTip = "按设备屏幕比例调整窗口，让画面几乎铺满窗口（不改最大化）";
        _fitBtn.Clicked += FitWindowToDevice;

        _fillBtn = new FlatButton("▣ 等比", BtnKind.Ghost, 26, 11.5);
        _fillBtn.Margin = new Thickness(10, 0, 0, 0);
        _fillBtn.ToolTip = "默认铺满（Fill，画面填满、零黑边）；点这里切到等比完整画面（可能留边）";
        _fillBtn.Clicked += ToggleFill;

        _immersiveBtn = new FlatButton("⛶ 全屏", BtnKind.Ghost, 26, 11.5);
        _immersiveBtn.Margin = new Thickness(10, 0, 0, 0);
        _immersiveBtn.ToolTip = "纯画面：隐藏上下工具条与侧栏，画面铺满（F11 / Esc 切换；点击画面不会改变界面）";
        _immersiveBtn.Clicked += ToggleImmersive;

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_resolutionChip);
        right.Children.Add(new Border { Width = 10 });
        right.Children.Add(_pill);
        right.Children.Add(_fitBtn);
        right.Children.Add(_fillBtn);
        right.Children.Add(_immersiveBtn);
        Grid.SetColumn(right, 2);
        header.Children.Add(right);

        // 控制条浮在画面上（与画面同一行、靠 ZIndex 置顶），不占 Grid 行：
        // 呼出 / 收起都不改变画面区尺寸，点击坐标映射恒定，也不会把画面挤出上下深色边。
        // Grid / StackPanel 背景为 null，空白处不命中鼠标，点击照样穿透到画面。
        header.VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(header, 10);
        Grid.SetRow(header, 1);
        grid.Children.Add(header);

        // ── 画面区：撑满剩余空间，四周零留白、零底色 ──
        // 透明而不是 Theme.Surface：等比模式万一有 letterbox，也不会再露出一圈深色「黑框」。
        _stage.Background = Brushes.Transparent;
        _stage.Children.Add(_placeholder);
        // 未接入设备的占位页，叠在画面之上；只有选中未接入项时才显示。
        _offlineView.Background = Theme.Surface;
        _offlineText.Foreground = Theme.Text;
        _offlineView.Children.Add(_offlineText);
        _stage.Children.Add(_offlineView);

        // 底部 HUD 指标条已按用户要求移除：只保留帧率并挪到「上边框栏」，
        // 底部这个位置改放三键导航胶囊（两者都是浮层，不占布局行、不改画面区尺寸）。
        var stageLayer = new Grid();
        stageLayer.Children.Add(_stage);

        var stageWrap = new Border
        {
            // 零边框 / 零圆角 / 零上下留白：这三条原来是画面上下的深色「黑框」。
            // Background 保持 Transparent（不是 null）才能继续接收点击、拖动、滚轮事件。
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0),
            Child = stageLayer
        };
        // 双击不再劫持成「呼出控制条」：用户连点两下画面时，第一击已注入 touchDown，
        // 第二击却被 ToggleImmersive 吃掉并弹出上下深色工具条 —— 这正是「一点屏幕黑框
        // 就出现」和「点击像错乱」的来源。现在每次按下都只走设备注入，
        // 控制条只认 F11 / Esc（键盘），任何鼠标手势都不会改变界面。
        stageWrap.MouseLeftButtonDown += (_, e) => BeginTapCandidate(e);
        stageWrap.MouseMove += (_, e) => TrackTapDrag(e);
        stageWrap.MouseLeftButtonUp += (_, e) => EndTapCandidate(e);
        // 滚轮转发：设备侧有 AxisDown/AxisUp（实测 22~29ms），以前完全没接，导致「滚不动」
        stageWrap.MouseWheel += (_, e) => ForwardWheel(e);
        Grid.SetRow(stageWrap, 1);
        Panel.SetZIndex(stageWrap, 0);
        grid.Children.Add(stageWrap);

        // ── 底部：细节行 + 状态消息（各一行，字号收紧） ──
        _detail.Text = "等待数据";
        _detail.FontFamily = Theme.MonoFont;
        _detail.FontSize = 10.5;
        _detail.Foreground = Theme.TextMuted;
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        _detail.TextWrapping = TextWrapping.NoWrap;

        var statusBar = new TextBlock
        {
            Text = "",
            FontSize = 10.5,
            Foreground = Theme.TextMuted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        _statusHost = statusBar;

        // 底部留给三键导航胶囊约 66 DIP，避免状态条（F11 呼出时）压到导航按钮上。
        var bottom = new StackPanel { Margin = new Thickness(2, 4, 2, 66) };
        _bottomHost = bottom;
        bottom.Children.Add(_detail);
        bottom.Children.Add(statusBar);
        bottom.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.SetZIndex(bottom, 10);
        Grid.SetRow(bottom, 1);
        grid.Children.Add(bottom);

        // ── 标题栏菜单 / 帧率 + 底部三键导航 ──
        // 菜单与帧率已挪进 Row0 的自绘系统标题栏；底部三键仍是画面行上的局部胶囊
        // （ZIndex 20、不占布局行 → 画面区尺寸与点击坐标映射恒定）。
        // --menu off 只关标题栏上的菜单 / 帧率；三键导航独立保留。
        if (_menuEnabled)
        {
            BuildTopMenu(grid, titleRow);
        }
        BuildBottomNav(grid);

        // ── 隐藏输入宿主：键盘直通的入口 ──
        // 为什么必须有这么一个东西：WPF 窗口里没有任何可编辑控件时，输入法根本不会激活，
        // PreviewTextInput 只能拿到键盘直接产生的 ASCII，中文永远打不出来。
        // 放一个 1×1、全透明、不可命中的 TextBox，用户点画面时把焦点交给它，
        // IME 才会起来；文本由 TextChanged 收走并立刻清空（它只是中间人，不存内容）。
        // IsHitTestVisible=false：绝不能让它挡住画面点击。
        _kbdSink = new TextBox
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false,
            AcceptsReturn = false,
            AcceptsTab = false,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = true
        };
        _kbdSink.TextChanged += (_, _) => HarvestKbdSink();
        _kbdSink.PreviewKeyDown += (_, e) => HandleKbdSpecialKey(e);
        Grid.SetRow(_kbdSink, 1);
        Panel.SetZIndex(_kbdSink, 0);
        grid.Children.Add(_kbdSink);

        return grid;
    }

    // ── 键盘直通实现（uitest 路线）────────────────────────────────────────
    //
    // 数据流：按键 → 隐藏 TextBox（激活 IME）→ TextChanged 收字符进 _kbdBuf
    //        → 120ms 攒批 → 文件通道推设备 → uitest uiInput inputText。
    // 特殊键（Enter/Backspace/Tab）先 flush 字符，再单独发 keyEvent，
    // 全部经 _kbdChain 串行，保证「打 ab 再退格」的顺序不会反。

    /// <summary>把隐藏 TextBox 里新增的字符收进缓冲区并立刻清空它。</summary>
    private void HarvestKbdSink()
    {
        var sink = _kbdSink;
        if (sink is null || _kbdHarvesting)
        {
            return;
        }
        string t = sink.Text;
        if (t.Length == 0)
        {
            return;
        }
        _kbdHarvesting = true;
        try
        {
            sink.Text = string.Empty;
        }
        finally
        {
            _kbdHarvesting = false;
        }
        if (!_kbdPassthrough)
        {
            return;
        }
        _kbdBuf.Append(t);
        ScheduleKbdFlush();
    }

    private bool _kbdHarvesting;

    /// <summary>
    /// 攒批调度：**队列空闲立即发，队列忙则自然合并**（不再用固定定时器）。
    ///
    /// 2026-11-14 二次修正。原来用 120ms 固定 DispatcherTimer，实测（02:05 真机日志）
    /// 用户是**逐字慢敲**、字间 450~600ms，于是：
    ///   · 每个字都白等 120ms 才发；
    ///   · 而一条命令要 440ms，下个字到达时队列还满着，两个字永远合并不了。
    ///   日志里 `长度=1` 占绝大多数、只有一次 `长度=3` 就是证据。
    ///
    /// 现在的做法去掉了「等待」这一环：来字符时若没有在飞的批次，**立刻**发；
    /// 若上一批还在跑，就什么都不做 —— 那一批跑完会回来重新取缓冲区，
    /// 把它执行期间打的字一起带走。慢敲时零额外等待，快敲时自动合并。
    /// </summary>
    private void ScheduleKbdFlush()
    {
        if (_kbdPumping)
        {
            return;   // 已有泵在跑，它会回头把这次 Append 的字一起取走
        }
        _kbdPumping = true;
        EnqueueKbd(async () =>
        {
            try
            {
                while (true)
                {
                    string? t = TakeKbdBuf();
                    if (string.IsNullOrEmpty(t))
                    {
                        break;
                    }
                    await InjectKbdAsync(t, null);
                }
            }
            finally
            {
                _kbdPumping = false;
            }
        });
    }

    /// <summary>是否已有一条「泵」在飞（保证同一时刻只有一条在取缓冲区）。</summary>
    private bool _kbdPumping;

    /// <summary>取走缓冲区内容并清空（只在 UI 线程调用）。</summary>
    private string? TakeKbdBuf()
    {
        if (_kbdBuf.Length == 0)
        {
            return null;
        }
        string t = _kbdBuf.ToString();
        _kbdBuf.Clear();
        return t;
    }

    /// <summary>
    /// 特殊键：Enter / Backspace / Tab / 方向键。
    /// 这些不能靠 text（它只会追加文本），必须发独立键事件；
    /// 与缓冲区里的字符**合并进一条 shell**（`text ...; keyEvent ...`）：
    /// 一是保证「打 ab 再退格」的顺序，二是省掉一次 hdc 通道往返（实测 ~300ms）。
    /// </summary>
    private void HandleKbdSpecialKey(KeyEventArgs e)
    {
        if (!_kbdPassthrough)
        {
            return;
        }
        // 带 Ctrl / Alt 的组合一律放行给本机（Ctrl+C 复制画面、Alt+F4 关窗等），
        // 不然用户想复制个东西就会往设备里灌控制字符。
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }
        ushort code;
        switch (e.Key)
        {
            case Key.Enter: code = 2054; break;        // KEYCODE_ENTER
            case Key.Back: code = 2055; break;         // KEYCODE_DEL
            case Key.Tab: code = 2049; break;          // KEYCODE_TAB
            case Key.Delete: code = 2071; break;       // KEYCODE_FORWARD_DEL
            // 方向键：2012-2015（SDK @ohos.multimodalInput.keyCode.d.ts:261-282，
            // 与 hnscrcpy 真机标定值一致）。
            // ⚠ 2026-11-14 修正：此前填的是 2017/2018/2019/2020，那是**字母 a-d 的键码**
            // （KeyCharMap 里 'a'=2017），按方向键会在设备上打出 abcd 而不是移动光标。
            case Key.Left: code = 2014; break;         // KEYCODE_DPAD_LEFT
            case Key.Right: code = 2015; break;        // KEYCODE_DPAD_RIGHT
            case Key.Up: code = 2012; break;           // KEYCODE_DPAD_UP
            case Key.Down: code = 2013; break;         // KEYCODE_DPAD_DOWN
            default: return;
        }
        e.Handled = true;
        // 连按同一个键时先攒着（60ms 窗口），攒到多次就压成一条命令。
        // 单次按键仍走原路径，可与缓冲区文本合并。
        // 注意：**先把攒着的文本冲下去**再记这个键，保证「打 ab 再退格」的顺序。
        if (_kbdBuf.Length > 0 && !_kbdPumping)
        {
            EnqueueKbd(() => InjectKbdAsync(TakeKbdBuf(), null));
        }
        CoalesceSpecialKey(code);
    }

    // ── 连续特殊键合并（2026-11-14 19:45 修「连按退格卡 3.5 秒」）──
    //
    // 实测（19:35 日志）：连按 9 次退格，每次 295~406ms，累计约 3.5s。
    // 原因：每个按键都是一个独立任务，串行等前一个的 hdc 往返完成。
    // 但 uinput 支持「单命令多段 -d/-u」，所以连续同键完全可以压成一条命令：
    //   10 次退格 = 一条 `uinput -K -d 2055 -u 2055 -d 2055 -u 2055 ...`
    // 这样 10 次按键只花一次通道往返（约 350ms），而不是 3.5 秒。
    //
    // 合并窗口 60ms：用户连按时每次间隔通常 80~150ms，60ms 足够攒下连打的键，
    // 又不会让单独按一次多出可感知的延迟。
    private const int SpecialKeyCoalesceMs = 60;
    private ushort? _pendingSpecialKey;
    private long _pendingSpecialKeyAt;
    private DispatcherTimer? _specialFlushTimer;
    private ushort _specialKeyCount;

    /// <summary>连按同一个特殊键时累计并合并，窗口内只发一条命令。</summary>
    private void CoalesceSpecialKey(ushort code)
    {
        bool sameAsPending = _pendingSpecialKey == code;
        _specialKeyCount = sameAsPending ? (ushort)(_specialKeyCount + 1) : (ushort)1;
        _pendingSpecialKey = code;
        _pendingSpecialKeyAt = Environment.TickCount64;

        _specialFlushTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SpecialKeyCoalesceMs) };
        if (_specialFlushTimer.Tag is null)
        {
            _specialFlushTimer.Tick += (_, _) => FlushSpecialKey();
            _specialFlushTimer.Tag = "bound";
        }
        _specialFlushTimer.Stop();
        _specialFlushTimer.Start();
    }

    /// <summary>把攒下的连续特殊键一次发出（同键重复 N 次压成一条 uinput 命令）。</summary>
    private void FlushSpecialKey()
    {
        _specialFlushTimer?.Stop();
        if (_pendingSpecialKey is not ushort code)
        {
            return;
        }
        ushort n = _specialKeyCount == 0 ? (ushort)1 : _specialKeyCount;
        _pendingSpecialKey = null;
        _specialKeyCount = 0;

        if (n <= 1)
        {
            // 单次按键：走原路径（可与缓冲区文本合并，省一次往返）
            if (_kbdPumping)
            {
                EnqueueKbd(() => InjectKbdAsync(null, code));
            }
            else
            {
                EnqueueKbd(() => InjectKbdAsync(TakeKbdBuf(), code));
            }
            return;
        }

        // 连按 N 次：拼一条命令。用 StringBuilder 避免 N 很大时字符串拼接开销。
        var sb = new System.Text.StringBuilder(n * 24);
        sb.Append("uinput -K");
        for (int i = 0; i < n; i++)
        {
            sb.Append(" -d ").Append(code).Append(" -u ").Append(code);
        }
        string cmd = sb.ToString();
        if (_kbdPumping)
        {
            EnqueueKbd(() => InjectKbdAsync(null, code, cmd));
        }
        else
        {
            EnqueueKbd(() => InjectKbdAsync(TakeKbdBuf(), code, cmd));
        }
        AppLog.Info($"[键盘] 特殊键合并 {n} 次 → 1 条命令（键={code}）");
    }

    /// <summary>所有键盘动作排队执行，保证顺序（hdc 全进程锁只保并发安全，不保顺序）。</summary>
    /// <remarks>
    /// 入队后**回到 UI 线程**执行：链上的 SetStatus / ActivePage / _devices 都是 UI 状态，
    /// 在 TaskScheduler.Default 上直接调会跨线程访问 WPF 控件。await 不阻塞消息循环，
    /// 所以 hdc 往返期间界面照常响应。
    /// </remarks>
    private void EnqueueKbd(Func<Task> work)
    {
        _kbdChain = _kbdChain.ContinueWith(
            _ => Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[键盘] {FirstLine(ex.Message)}");
                }
            }).Task.Unwrap(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// 把文本推成设备上的文件，供 <c>$(cat …)</c> 展开（emoji 与长文本必须走这条）。
    /// 路径拼接与「同名目录」的坑都在 <see cref="InputInjector.SendFileAsync"/> 里统一处理了。
    /// </summary>
    private async Task<(bool Ok, string Output)> PushKbdFileAsync(string text)
    {
        var r = await _injector.SendFileAsync(
            ClipTextInjector.WriteTempFile(text), ClipTextInjector.RemotePath);
        return (r.Ok, r.Output);
    }

    /// <summary>
    /// 特殊键注入命令。
    ///
    /// 为什么用 uinput 而不是 uitest uiInput keyEvent（2026-11-14 实测，平板 MLR-AL00 无线，
    /// 每条 3 次取范围）：
    ///   echo x                    217~244ms   ← 纯 hdc 通道成本，这一层压不掉
    ///   uinput -K -d N -u N       325~418ms
    ///   uitest uiInput text 'a'   556~604ms
    ///   uitest uiInput keyEvent   634~663ms
    /// 同一套键码，uinput 比 uitest CLI 省约 300ms，差的就是 uitest 框架的启动。
    /// 键码语义不变（都是 HarmonyOS keyCode：Enter 2054 / DEL 2055 / TAB 2049），
    /// 所以调用方传来的码可以原样使用。
    /// </summary>
    private static string UinputKeyCommand(ushort code) => $"uinput -K -d {code} -u {code}";

    /// <summary>
    /// 把「待发文本 + 可选的特殊键」合并成一条 shell 发到设备。
    /// 走 <c>uitest uiInput text</c>（焦点注入），不需要坐标。
    /// </summary>
    /// <param name="overrideCmd">
    /// 直接指定的命令，绕过常规构造。用于「连按特殊键合并成一条 uinput 命令」的场景。
    /// 非 null 时 <paramref name="text"/> 与 <paramref name="keyCode"/> 只用于日志。
    /// </param>
    private async Task InjectKbdAsync(string? text, ushort? keyCode, string? overrideCmd = null)
    {
        if (!_kbdPassthrough || ActivePage() is not { Streaming: true })
        {
            return;
        }
        if (string.IsNullOrEmpty(text) && keyCode is null && overrideCmd is null)
        {
            return;
        }
        // 一层轻量兜底：text 落到设备「当前焦点」，若用户从未在画面里点过，
        // 焦点可能停在任意应用上。这不是坐标依赖，只是防手滑。
        // ⚠ 2026-11-14：粘贴通道（Ctrl+V）本身不需要坐标，但**焦点仍然重要** ——
        // 粘贴会落到设备当前焦点，从没点过画面时焦点可能在别的应用上。
        // 所以这个门槛保留：它防的是「字粘到错地方」，不是「注入失败」。
        if (!_lastTapByDevice.ContainsKey(DeviceKey()))
        {
            SetStatus("[键盘] 请先在画面里点一下要输入的输入框，再打字");
            return;
        }
        SyncInjectorToActiveDevice();
        try
        {
            string cmd;
            string path;
            if (overrideCmd is not null)
            {
                // 连按特殊键合并成的那一条命令（见 CoalesceSpecialKey）
                cmd = overrideCmd;
                path = "特殊键合并";
            }
            // ① 纯 ASCII 可映射 → 一条 `uinput -K` 串多段键码：免坐标、免 IME、只花一次通道往返。
            else if ((string.IsNullOrEmpty(text) ? null : KeyCharMap.BuildKeyScript(text)) is { } keyScript)
            {
                cmd = "uinput -K " + keyScript;
                if (keyCode is ushort k1)
                {
                    cmd += $" -d {k1} -u {k1}";
                }
                path = "uinput键码";
            }
            else if (string.IsNullOrEmpty(text))
            {
                cmd = UinputKeyCommand(keyCode!.Value);
                path = "uinput单键";
            }
            else
            {
                // ② 含中文/emoji 等非 ASCII 可映射字符 → 走 uitest uiInput text（焦点注入）。
                //
                // ⚠ 2026-11-14 19:30 实测结论：**不要改成「剪贴板 + Ctrl+V」**。
                // 凌晨那次测试给出 951~1107ms 是**没算 sleep 与通道开销**的乐观值；
                // 在程序里（经 ConPTY 常驻会话）同机同时对比实测：
                //   剪贴板 + Ctrl+V   1416 / 1475 / 1888 ms   ← 更慢
                //   uitest uiInput text 1261 / 1299 / 1389 ms  ← 更快
                // 原因：`aa start` 拉起 ability 与 pasteboard.setData() 是异步的，必须 sleep 等落盘，
                // 而这 500ms 纯等待 + 额外一次进程往返，比 uitest 单次调用还贵。
                // BuildPasteCommand 保留在 ClipTextInjector 里作参考实现，此处不启用。
                if (ClipTextInjector.NeedsFileChannel(text))
                {
                    // emoji（代理对）与超长文本：命令行会按 ANSI 截断，必须走字节流。
                    var up = await PushKbdFileAsync(text);
                    if (!up.Ok)
                    {
                        SetStatus($"[键盘] 推送文本失败：{FirstLine(up.Output)}");
                        return;
                    }
                    cmd = ClipTextInjector.BuildFileTextCommand();
                    if (keyCode is ushort k5)
                    {
                        cmd += $"; {UinputKeyCommand(k5)}";
                    }
                    path = "文件通道";
                }
                else
                {
                    cmd = ClipTextInjector.BuildInlineTextCommand(text);
                    if (keyCode is ushort k6)
                    {
                        cmd += $"; {UinputKeyCommand(k6)}";
                    }
                    path = "uitest文本";
                }
            }
            var r = await _injector.ShellAsync(cmd);
            AppLog.Info($"[键盘] {path} {r.ElapsedMs:F0}ms 长度={text?.Length ?? 0}" +
                        (keyCode is ushort kc ? $" 键={kc}" : "") + (r.Ok ? "" : $" 失败={FirstLine(r.Output)}"));
            if (!r.Ok)
            {
                SetStatus($"[键盘] 注入失败：{FirstLine(r.Output)}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[键盘] 注入异常：{FirstLine(ex.Message)}");
        }
    }

    /// <summary>构造系统标题栏里的菜单按钮 + 帧率（只显示帧率）。</summary>
    private void BuildTopMenu(Grid grid, Panel titleRow)
    {
        // 「菜单」刻意用纯文字：之前是「☰ 菜单」，11.5 号下 ☰ 被渲染成三条横线，
        // 读起来就是「三 菜单」，像多打了一个字。去掉图标反而更干净。
        var menuBtn = new FlatButton("菜单", BtnKind.TitleBar, 22, 11.5)
        {
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "打开菜单：控制条 / 推流 / 贴合画面 / 重新连接（F11 可切换控制条）"
        };

        // 帧率自己就是一枚浅色胶囊，只显示「数值 + 单位」：
        // 延迟 / 帧数 / 丢包 / 解码错误已按用户要求全部删除。
        _fpsChip.ToolTip = "实际上屏帧率（静止时设备不出帧，显示「静止」而非 0）";

        var barRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        barRow.Children.Add(_deviceChipRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            Visibility = Visibility.Collapsed   // 单设备时无「切换」可言，AddDeviceChip 里按台数放开
        });
        barRow.Children.Add(menuBtn);
        barRow.Children.Add(new Border { Width = 7 });
        barRow.Children.Add(_fpsChip);

        // 剪贴板两枚按钮与菜单 / 帧率并排（用户 21:35 要求）：↑ 上传 = 电脑 → 设备，
        // ↓ 下载 = 设备 → 电脑。顺序按「先上传后下载」摆，方向箭头本身就说明数据朝哪边走。
        // 2026-11-13：文案里的「平板」一律改成「设备」—— 动作恒指向**当前选中的那台**，
        // 写死平板会在切到手机后误导用户（实际发的确实是手机）。
        _clipUpBtn = new TitleIconButton(TitleGlyph.Upload)
        {
            Margin = new Thickness(7, 0, 0, 0),
            ToolTip = "上传剪贴板（电脑 → 当前设备）\n" +
                      "把电脑剪贴板里的文字/图片写进当前设备（快捷键 Ctrl+Shift+V）\n\n" +
                      "文字走设备端 ClipSetAbility 直写系统剪贴板；图片走 TCP 旁路"
        };
        _clipUpBtn.Clicked += () => _ = SendClipboardToTabletAsync();
        barRow.Children.Add(_clipUpBtn);

        _clipDownBtn = new TitleIconButton(TitleGlyph.Download)
        {
            Margin = new Thickness(5, 0, 0, 0),
            ToolTip = "下载剪贴板（当前设备 → 电脑）\n" +
                      "把当前设备上复制的文字/图片拉到电脑剪贴板，之后直接 Ctrl+V\n" +
                      "（设备会短暂显示一下粘贴按钮，点完自动退回，约 2~4 秒）"
        };
        _clipDownBtn.Clicked += () => _ = PullClipboardAsync(_clipDownBtn);
        barRow.Children.Add(_clipDownBtn);

        // WindowChrome 的 CaptionHeight 区域默认全被当成非客户区（点击 = 拖动窗口）。
        // 必须逐个放开命中测试，标题栏上的菜单按钮才能真正收到点击；帧率与两枚箭头同理，
        // 否则它们会在标题栏里形成「按住拖不动」的死区。
        WindowChrome.SetIsHitTestVisibleInChrome(menuBtn, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_fpsChip, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_clipUpBtn, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_clipDownBtn, true);

        // 浅色胶囊不做淡显：82% 不透明度会让它在 #F3F3F3 上发白发脏，
        // 所以这里只是给标题栏内的两枚胶囊裹一层容器，不再挂 hover 透明度。
        var host = new Border
        {
            Child = barRow,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            Background = null
        };
        _topBar = host;
        // 挂在标题栏 StackPanel 里，不再作为画面行的浮层。
        titleRow.Children.Add(host);

        var panel = new StackPanel { Margin = new Thickness(6) };

        void AddItem(string text, Action onClick, Func<string>? dynamicLabel = null)
        {
            var b = new FlatButton(text, BtnKind.Ghost, 30, 12)
            {
                Margin = new Thickness(0, 0, 0, 3)
            };
            b.SetLeftAligned();
            b.Clicked += () =>
            {
                if (_menuPopup is not null)
                {
                    _menuPopup.IsOpen = false;
                }
                // 菜单项是「主动选完就关」，不算外部点击：清掉防抖时间戳，
                // 让用户可以立刻再点按钮打开菜单。
                _menuClosedMs = 0;
                onClick();
            };
            panel.Children.Add(b);
            if (dynamicLabel is not null)
            {
                _menuDynamicLabels.Add((b, dynamicLabel));
            }
        }

        void AddSep() => panel.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Border,
            Margin = new Thickness(4, 4, 4, 5)
        });

        AddItem("⛶ 显示控制条", ToggleImmersive,
            () => _immersive ? "⛶ 显示控制条" : "⛶ 回到纯画面");
        AddItem("⤢ 贴合画面", FitWindowToDevice);
        AddItem("▣ 等比", ToggleFill,
            () => _fillMode ? "▣ 切到等比（完整画面）" : "▣ 切到铺满（零黑边）");
        AddSep();
        AddItem("▶ 开始推流", () => _ = ToggleStreamAsync(), StreamMenuLabel);
        AddItem("↻ 重新连接设备", () => AddDevice(_autoHost));
        // 剪贴板两项跟着当前选中的设备走：文案里直接带设备名，
        // 免得切到手机后还以为在操作平板（动作确实已经切过去了）。
        AddItem("↑ 上传剪贴板到当前设备", () => _ = SendClipboardToTabletAsync(),
            () => $"↑ 上传剪贴板到 {ActiveDeviceName()}");
        AddItem("↓ 下载当前设备剪贴板", () => _ = PullClipboardAsync(_clipDownBtn),
            () => $"↓ 下载 {ActiveDeviceName()} 剪贴板");
        // 键盘直通（2026-11-14）：投屏窗口里的按键直接敲进设备当前输入框。
        // 走 uitest（走设备 IME，中文可用），要求先在画面点过输入框提供锚点。
        // 默认开；关掉后按键完全不外发，适合「只想看画面、要按 F11/Esc 之外的快捷键」的场景。
        AddItem("⌨ 键盘直通", () =>
        {
            _kbdPassthrough = !_kbdPassthrough;
            if (!_kbdPassthrough)
            {
                _kbdBuf.Clear();
            }
            SetStatus(_kbdPassthrough
                ? "[键盘] 已开启：先在画面点一下输入框，然后直接打字"
                : "[键盘] 已关闭：按键不再发往设备");
        }, () => _kbdPassthrough ? "⌨ 键盘直通：开" : "⌨ 键盘直通：关");
        // 纯画面模式下 hideSidebar = _sidebarCollapsed || _immersive，单翻 _sidebarCollapsed
        // 不会有效果；要展开侧栏必须先退出沉浸，让控制条一起回来。
        AddItem("› 展开侧栏", () =>
        {
            _immersive = false;
            _sidebarCollapsed = false;
            ApplyChrome();
        });
        AddSep();
        AddItem("✕ 退出", Close);

        var card = new Border
        {
            Background = Theme.Alpha(0xF2, 0x15, 0x18, 0x1F),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Width = 202,
            Child = panel
        };

        _menuPopup = new Popup
        {
            Child = card,
            PlacementTarget = menuBtn,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -58,     // 面板 202、按钮约 86 → 大致居中
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };
        _menuPopup.Closed += (_, _) =>
        {
            _menuClosedMs = Environment.TickCount64;
        };

        // StaysOpen=false 时若在同一轮 mouse-up 内直接打开，可能被这次抬起顺手关掉；
        // 推迟到输入队列清空后再开，WPF Popup 的常规做法。
        menuBtn.Clicked += () => Dispatcher.BeginInvoke(
            DispatcherPriority.Input, new Action(ToggleTopMenu));
        grid.Children.Add(_menuPopup);
    }

    /// <summary>
    /// 底部三键导航：常驻显示（原来只在折叠的侧栏里，纯画面模式根本点不到）。
    /// 2026-11-11 00:45 按用户要求改为**纯图标 + 全透明**：去掉「多任务 / 主页 / 返回」文字，
    /// 去掉深色胶囊底与描边，三个白色自绘图标直接浮在画面上（带柔和投影保证浅色背景可读）。
    /// 容器与按钮都用 Brushes.Transparent（非 null）才能继续接收点击，
    /// 否则鼠标会穿透到底下的 stageWrap，变成「点了在操作画面」而不是按按钮。
    /// </summary>
    private void BuildBottomNav(Grid grid)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 顺序对齐设备「设置 → 三键导航」当前选中的组合 □ ○ ◁（多任务 / 主页 / 返回）。
        // 图标走 NavKeyButton 的自绘图形，不再写字符 —— □ ○ ◁ 在字体里字号 / 线宽 / 基线
        // 各不相同，「□ 多任务」的方框小得像个点，三个键摆一起根本不成套。
        // Text 仍保留：纯图标模式下不渲染，但 ToolTip 与日志文案要用它。
        var navs = new (NavGlyph Glyph, string Text, byte Key)[]
        {
            (NavGlyph.Recents, "多任务", NavKey.Recents),
            (NavGlyph.Home, "主页", NavKey.Home),
            (NavGlyph.Back, "返回", NavKey.Back)
        };
        for (int i = 0; i < navs.Length; i++)
        {
            var (glyph, text, key) = navs[i];
            // glyphScale 1.05：按实测红框（窗口内 x 451→543，可用宽 92px）反推——
            // 热区 56×46 → 27×22、图形 22px → 10.5px，三个加两个 4px 间距正好 92px，贴框不越界。
            var nb = new NavKeyButton(glyph, text, iconOnly: true, glyphScale: 1.05);
            if (i > 0)
            {
                nb.Margin = new Thickness(4, 0, 0, 0);
            }
            nb.ToolTip = $"设备三键导航：{text}";
            nb.Clicked += () => SendNav(key);
            _navButtons[key] = nb;
            row.Children.Add(nb);
        }
        // 未连接时三键压暗不可点，而不是点了静默什么都不发生。
        UpdateNavEnabled();
        _navHint = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 10.5,
            Foreground = Theme.Danger,
            VerticalAlignment = VerticalAlignment.Center,
            // 失败提示放在三键**左侧**（row 右对齐 + 它插在首位）：提示出现时三键位置不动，
            // 文字向左扩展，不会把按钮挤离右下角。
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 220,
            // 底部已经没有深色胶囊底了，红字直接压在画面上会糊；加一层零位移投影保证可读。
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.9
            }
        };
        row.Children.Insert(0, _navHint);

        var navBar = new Border
        {
            Child = row,
            // 全透明：不再有深色胶囊底与描边，图标直接浮在画面上。
            // 必须用 Brushes.Transparent 而不是 null —— null 会让整条丢出命中测试，点击穿透到画面。
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            // 2026-11-13 用户要求：三键挪到画面**右下角**，不要占画面中间那块面积。
            // 原来居中放底部，正好压住设备画面底部的应用栏 / 按钮区，影响点按。
            // 留边 4/4 贴角——用户圈的红框实测在窗口内 x 451→543（可用宽 92），三键实测占 88×22，不出框。
            Margin = new Thickness(0, 0, 4, 4)
        };
        Panel.SetZIndex(navBar, 20);
        Grid.SetRow(navBar, 1);
        grid.Children.Add(navBar);
    }

    /// <summary>按当前是否有活动会话刷新三键可用态。连接建立 / 断开时都要过一遍。</summary>
    private void UpdateNavEnabled()
    {
        bool on = _manager.Active() is not null;
        foreach (var btn in _navButtons.Values)
        {
            btn.Enabled = on;
        }
    }

    private string StreamMenuLabel()
    {
        var s = _manager.Active();
        bool streaming = s is not null && _pages.TryGetValue(s.Id, out var p) && p.Streaming;
        return streaming ? "■ 停止推流" : "▶ 开始推流";
    }

    private void ToggleTopMenu()
    {
        if (_menuPopup is null)
        {
            return;
        }
        if (_menuPopup.IsOpen)
        {
            _menuPopup.IsOpen = false;
            return;
        }
        // 菜单已被这一轮「外部按下」自动关掉（StaysOpen=false）：不要在同一轮
        // mouse-up 里又把它打开，否则点按钮看起来像「关不掉」。
        if (Environment.TickCount64 - _menuClosedMs < 350)
        {
            return;
        }
        foreach (var (btn, label) in _menuDynamicLabels)
        {
            btn.Text = label();
        }
        _menuPopup.IsOpen = true;
    }

    // ── 画面最大化：侧栏折叠 / 沉浸全屏 ─────────────────────────────

    /// <summary>
    /// 按设备屏幕比例收放窗口（保持普通窗口，不最大化）：竖屏设备在横屏窗口里
    /// 两侧会有大片黑边，画面只占中间一条；贴合后画面在窗口里接近满幅。
    /// </summary>
    /// <summary>
    /// 让「画面区」的宽高比等于设备屏幕比例（保持普通窗口，不最大化）。
    /// 黑边只有两个来源：画面区比设备宽（左右留边）/ 比设备高（上下留边）。
    /// chrome 只剩窗口非客户区（系统边框 + 标题栏）：上下工具条 / 状态条已改浮层，
    /// 不再占布局，所以纯画面与控制条两种状态下画面区尺寸完全一致。
    /// 量一次 chrome、一次算准，不用迭代（迭代读到的 ActualWidth 是上一轮布局的旧值）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWndProc);
    }

    /// <summary>
    /// 拖动缩放时实时锁住窗口比例（WM_SIZING）。改的是系统给我们的拖动矩形，
    /// 所以窗口在拖动过程中就按比例走，不存在「先拉变形、松手才跳回来」。
    /// </summary>
    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSizing || _windowAspect <= 0.05)
        {
            return IntPtr.Zero;
        }

        WinRect rc = Marshal.PtrToStructure<WinRect>(lParam);
        int edge = wParam.ToInt32();

        double sx = 1, sy = 1;
        PresentationSource? src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is not null)
        {
            Matrix m = src.CompositionTarget.TransformToDevice;
            sx = m.M11 > 0 ? m.M11 : 1;
            sy = m.M22 > 0 ? m.M22 : 1;
        }

        double ratio = _windowAspect;
        double minW = MinWidth * sx;
        double minH = MinHeight * sy;

        double w = rc.Right - rc.Left;
        double h = rc.Bottom - rc.Top;

        // 取「较大」的一维为准：窗口始终包得住鼠标拖到的位置，鼠标不会被甩到窗外
        if (edge == WmszLeft || edge == WmszRight)
        {
            h = w / ratio;
        }
        else if (edge == WmszTop || edge == WmszBottom)
        {
            w = h * ratio;
        }
        else if (w / ratio >= h)
        {
            h = w / ratio;
        }
        else
        {
            w = h * ratio;
        }

        // 最小尺寸必须整体放大，只顶一维会把比例顶破
        if (w < minW)
        {
            w = minW;
            h = w / ratio;
        }
        if (h < minH)
        {
            h = minH;
            w = h * ratio;
        }

        // 按鼠标所在的边/角锚定：拖左边就右边不动，拖上边就下边不动
        switch (edge)
        {
            case WmszLeft:
            case WmszTopLeft:
            case WmszBottomLeft:
                rc.Left = rc.Right - (int)Math.Round(w);
                break;
            default:
                rc.Right = rc.Left + (int)Math.Round(w);
                break;
        }
        switch (edge)
        {
            case WmszTop:
            case WmszTopLeft:
            case WmszTopRight:
                rc.Top = rc.Bottom - (int)Math.Round(h);
                break;
            default:
                rc.Bottom = rc.Top + (int)Math.Round(h);
                break;
        }

        Marshal.StructureToPtr(rc, lParam, false);
        // 只改矩形、不设 handled：让 DefWindowProc 照这条矩形继续走。
        // 若设 handled=true，WPF 就不再调 DefWindowProc，窗口会完全拉不动（最坏情况的兜底是退回自由缩放）。
        return IntPtr.Zero;
    }

    private void FitWindowToDevice()
    {
        var s = _manager.Active();
        double aspect = s is not null && s.Width > 0 && s.Height > 0
            ? (double)s.Width / s.Height
            : 1600.0 / 2560.0;

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        _fitting = true;
        try
        {
            UpdateLayout();

            double stageW = _stage.ActualWidth;
            double stageH = _stage.ActualHeight;
            if (stageW <= 1 || stageH <= 1 || ActualWidth <= 1 || ActualHeight <= 1)
            {
                return;
            }

            double chromeH = ActualWidth - stageW;
            double chromeV = ActualHeight - stageH;

            Rect wa = SystemParameters.WorkArea;
            double maxW = Math.Max(320, wa.Width - 40);
            double maxH = Math.Max(320, wa.Height - 40);

            // 画面区按设备比例尽量大：先看高度用满后宽度够不够，超了再按宽度反算。
            // 再乘 _fitScale（默认 2/3）：用户要求默认窗口比撑满小 1/3。
            double wantH = Math.Min(maxH - chromeV, (maxW - chromeH) / aspect) * _fitScale;
            double wantW = wantH * aspect;

            double targetW = Math.Min(maxW, wantW + chromeH);
            double targetH = Math.Min(maxH, wantH + chromeV);

            // 已经是目标尺寸（±2px）就别动：否则每次布局变化都会触发窗口跳动。
            // 注意不能只比比例 —— _fitScale 不改变比例，只比比例会导致缩放后的目标被跳过。
            if (Math.Abs(ActualWidth - targetW) <= 2 && Math.Abs(ActualHeight - targetH) <= 2)
            {
                return;
            }

            Width = targetW;
            Height = targetH;

            // 只有窗口整个跑出虚拟桌面才拉回主屏居中；正常贴合绝不挪动用户摆好的位置
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;
            if (Left + Width <= vl + 40 || Left >= vl + vw - 40 ||
                Top + Height <= vt + 40 || Top >= vt + vh - 40)
            {
                Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
            }

            // 记下「窗口整体」比例：拖动缩放时按它锁尺寸（含 chrome，所以不是纯设备比例）
            _windowAspect = Width / Height;

            SetStatus($"已按设备比例 {aspect:F2} 贴合窗口（{s?.Width}×{s?.Height}）");
            Console.WriteLine($"[fit] window={Width:F0}x{Height:F0} stage={stageW:F0}x{stageH:F0} " +
                              $"chrome={chromeH:F0}x{chromeV:F0} aspect={aspect:F3} fitScale={_fitScale:F3}");
        }
        finally
        {
            _fitting = false;
        }
    }

    /// <summary>
    /// 按「这台设备的比例 vs 当前画面区比例」决定填充方式：
    /// 比例接近（&lt;4%）才允许铺满，否则等比 —— 手机 0.707 塞进平板 0.625 的窗口，
    /// 铺满会把画面横向拉扁。两侧黑边比变形好看，也符合用户对比例差异的预期。
    /// </summary>
    private void ApplyStretchForAspect(Page page)
    {
        if (page.Session.Width <= 0 || page.Session.Height <= 0)
        {
            return;
        }
        double stageW = _stage.ActualWidth;
        double stageH = _stage.ActualHeight;
        if (stageW <= 1 || stageH <= 1)
        {
            return;
        }
        double devAspect = (double)page.Session.Width / page.Session.Height;
        double winAspect = stageW / stageH;
        bool closeEnough = Math.Abs(devAspect - winAspect) / winAspect < 0.04;
        var want = _fillMode && closeEnough ? Stretch.Fill : Stretch.Uniform;
        if (page.View.Stretch != want)
        {
            page.View.Stretch = want;
            AppLog.Info($"[比例] {page.Session.Id} {page.Session.Width}×{page.Session.Height} " +
                        $"比例 {devAspect:F3} vs 窗口 {winAspect:F3} → {want}");
        }
    }

    /// <summary>
    /// 手动缩放后的兜底：只在比例被破坏时把宽度拉回设备比例，**尺寸大小保持用户拖到的值**。
    /// 早先这里直接调 FitWindowToDevice()，它算的是「最大贴合尺寸」，等于用户一缩放就被弹回原大小。
    /// </summary>
    private void CorrectAspectIfNeeded()
    {
        if (_windowAspect <= 0.05 || ActualWidth <= 1 || ActualHeight <= 1 ||
            WindowState != WindowState.Normal)
        {
            return;
        }

        double want = Height * _windowAspect;
        if (Math.Abs(ActualWidth - want) <= 1.5)
        {
            return;   // 比例没坏（拖动时 WM_SIZING 已锁好），什么都别做
        }

        Rect wa = SystemParameters.WorkArea;
        _fitting = true;
        try
        {
            if (Left + want <= wa.Right - 8)
            {
                Width = want;
            }
            else
            {
                // 按当前高度定宽会顶出工作区，反过来按可用宽度定高
                Height = Math.Max(320, (wa.Right - 8 - Left) / _windowAspect);
            }
            Console.WriteLine($"[aspect] 修正比例 {ActualWidth:F0}x{ActualHeight:F0} → " +
                              $"{Width:F0}x{Height:F0}（锁定 {_windowAspect:F3}）");
        }
        finally
        {
            _fitting = false;
        }
    }

    /// <summary>铺满（Fill，零黑边）/ 等比（Uniform，完整画面但可能留边）切换。</summary>
    private void ToggleFill()
    {
        _fillMode = !_fillMode;
        foreach (var page in _pages.Values)
        {
            page.View.Stretch = _fillMode ? Stretch.Fill : Stretch.Uniform;
        }
        if (_fillBtn is not null)
        {
            // 按钮文字写的是「点下去会切到什么」，默认已是铺满，所以初始显示「等比」。
            _fillBtn.Text = _fillMode ? "▣ 等比" : "▣ 铺满";
        }
        SetStatus(_fillMode
            ? "铺满模式：画面填满窗口，无上下 / 左右黑边"
            : "等比模式：显示完整画面，比例不符时会留边（点「▣ 铺满」去掉）");
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplyChrome();
    }

    private void ToggleImmersive()
    {
        // 只切上下控制条，不动侧栏、不改窗口尺寸与流状态。
        // 原实现呼出时强制 _sidebarCollapsed=false，左侧会多出 258 DIP 深色面板，
        // 就是用户说的「两侧黑边框又出现」；要用侧栏请点工具栏左边的 › 自行展开。
        _immersive = !_immersive;
        ApplyChrome();
        SetStatus(_immersive
            ? "纯画面：无上下工具条、无两侧黑边（F11 / Esc 呼出控制条；点击画面不会触发）"
            : "已呼出控制条（F11 回到纯画面；点 › 可展开侧栏）");
    }

    /// <summary>按当前状态应用「哪些 chrome 可见」，沉浸时只留画面 + 上边框栏 + 底部三键导航。</summary>
    private void ApplyChrome()
    {
        if (_topBar is not null)
        {
            // 菜单 / 帧率已挪进 Row0 自绘标题栏，header 浮层在 Row1，两者不同行，
            // 不再需要为控制条呼出而下移 46 DIP。
            _topBar.Margin = new Thickness(0);
        }
        bool hideSidebar = _sidebarCollapsed || _immersive;
        if (_sidebarCol is not null)
        {
            _sidebarCol.Width = hideSidebar ? new GridLength(0) : new GridLength(258);
        }
        if (_sidebarHost is not null)
        {
            _sidebarHost.Visibility = hideSidebar ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_headerHost is not null)
        {
            _headerHost.Visibility = _immersive ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_bottomHost is not null)
        {
            _bottomHost.Visibility = _immersive ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_foldBtn is not null)
        {
            _foldBtn.Text = _sidebarCollapsed ? "\u203a" : "\u2039";
            _foldBtn.Visibility = _immersive ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_immersiveBtn is not null)
        {
            // 控制条可见时，按钮写的是「点下去会变成什么」：回到纯画面。
            _immersiveBtn.Text = _immersive ? "⛶ 呼出控制条" : "⛶ 纯画面";
        }
    }

    private TextBlock? _statusHost;

    // 只有这些面向用户的启动进度才写到画面中央。其余是运行期技术日志
    // （注入通道就绪、tconn、gRPC h2c、参考采集已启动…），它们仍写底栏供
    // F11 呼出查看，但不该盖掉画面中央那行可读的进度提示 —— 实测不加这层
    // 过滤时，中央最后停在「[注入通道] uitest RPC 就绪（agent.so 已就位…）」。
    private static bool IsStageMessage(string msg) =>
        msg.StartsWith("正在", StringComparison.Ordinal) ||
        msg.StartsWith("采集已就绪", StringComparison.Ordinal) ||
        msg.StartsWith("已下发推流", StringComparison.Ordinal) ||
        msg.StartsWith("设备可能已息屏", StringComparison.Ordinal) ||
        msg.StartsWith("推流指令未获确认", StringComparison.Ordinal) ||
        msg.StartsWith("设备采集服务启动失败", StringComparison.Ordinal);

    /// <summary>
    /// 粘贴按钮坐标的跨进程缓存（`clip-anchor.json`，与 exe 同目录）。
    ///
    /// 为什么必须落盘：手机（Pura X Max 折叠屏）上「一次点空 → 剪贴板页留在前台 →
    /// 下次 aa start 只走 onNewWant → 设备端补发链路不触发 → PC 收不到 READY」是必现的死循环，
    /// 而自测与重启都会换进程，纯内存缓存救不了。存下来就相当于「这台设备上次实测过的浮窗位置」。
    /// 浮窗位置由系统摆，只有折叠/展开切换才会变；真变了点空后仍需人工重来一次。
    /// </summary>
    private static string ClipAnchorPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "clip-anchor.json");

    private void LoadClipAnchors()
    {
        try
        {
            if (!System.IO.File.Exists(ClipAnchorPath))
            {
                return;
            }
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(
                System.IO.File.ReadAllText(ClipAnchorPath));
            if (raw is null)
            {
                return;
            }
            foreach (var kv in raw)
            {
                if (kv.Value is { Length: 2 } v && v[0] > 0 && v[1] > 0)
                {
                    _clipReadyByIp[kv.Key] = (v[0], v[1]);
                }
            }
            AppLog.Info($"[剪贴板] 已载入 {_clipReadyByIp.Count} 台设备的粘贴按钮坐标");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[剪贴板] 载入坐标缓存失败：{ex.Message}");
        }
    }

    private void SaveClipAnchors()
    {
        try
        {
            var d = new Dictionary<string, int[]>();
            foreach (var kv in _clipReadyByIp)
            {
                d[kv.Key] = new[] { kv.Value.X, kv.Value.Y };
            }
            System.IO.File.WriteAllText(ClipAnchorPath, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[剪贴板] 保存坐标缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动设备 → PC 剪贴板收件箱（UDP 18766，局域网广播）。
    ///
    /// 存在的理由：鸿蒙禁止应用静默读剪贴板，设备端只能靠系统 PasteButton 点一次换取一次性读权限，
    /// 所以这条数据不可能走 hdc（实测纯 hdc 读剪贴板在鸿蒙 7 商用版不存在），只能由平板主动发。
    /// 方向是单向的：PC 收到即写 Windows 剪贴板，**不需要用户在 PC 上做任何点击**。
    /// 与抓流模式无关：ReferenceCapture / HAP 两种模式都常驻监听。
    /// </summary>
    private void StartClipInbox()
    {
        try
        {
            LoadClipAnchors();
            _clipInbox = new ClipInbox();
            _clipInbox.TextReceived += (text, from) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _clipTextTick = Environment.TickCount64;
                    _clipDownBtn?.Flash(Theme.Accent, 600);
                    AppLog.Info($"[剪贴板] 端到端 {_clipPullWatch?.ElapsedMilliseconds ?? -1}ms（点按钮 → 内容到电脑剪贴板）");
                    SetStatus($"[剪贴板] 已从{DeviceNameByIp(from)}收到 {text.Length} 字，可直接 Ctrl+V 粘贴");
                    _clipPullDone?.TrySetResult(text);
                }));
            };
            // 平板剪贴板页进前台就广播 READY（含它自己量出的粘贴按钮坐标）：
            // PullClipboardAsync 据此决定何时、点哪里，不再靠固定 sleep 猜页面有没有切过去。
            _clipInbox.ReadyReceived += (from, x, y, settled) =>
            {
                // 只认「本次取用目标」那台的 READY：另一台若还停在剪贴板页（上一轮留下的），
                // 会把一套按它自己分辨率量出来的坐标播回来，采信它就会点错位置。
                if (_clipExpectIp is { Length: > 0 } want && !string.Equals(from, want, StringComparison.Ordinal))
                {
                    AppLog.Info($"[剪贴板] 忽略 {from} 的 READY（本次目标是 {want}）");
                    return;
                }
                AppLog.Info($"[剪贴板] READY 到达 ({x},{y}) settled={settled} 累计={_clipReadySeq + 1}");
                _clipReadyLatest = (x, y);
                // 只要源 IP 是本次目标就留档：下次这台发不出 READY 时拿它兜底。
                if (from is { Length: > 0 } && x > 0 && y > 0)
                {
                    _clipReadyByIp[from] = (x, y);
                    SaveClipAnchors();
                }
                _clipReadySettled = settled;
                System.Threading.Interlocked.Increment(ref _clipReadySeq);
                _clipReady?.TrySetResult((x, y));
            };
            // 设备端点了粘贴、开始编码图片时先播一条 BUSY：图片要 200~800ms 才发得出来，
            // 不压下去 PullClipboardAsync 会判定「点空了」而补点一次，那时浮窗已关，
            // 那一击会落到平板底层的应用上。收到 BUSY 即视为点击已命中。
            _clipInbox.BusyReceived += (from, tag) =>
            {
                if (_clipExpectIp is { Length: > 0 } wantB && !string.Equals(from, wantB, StringComparison.Ordinal))
                {
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _clipTextTick = Environment.TickCount64;
                    AppLog.Info($"[剪贴板] 设备已受理（{tag}）");
                    SetStatus($"[剪贴板] {ActiveDeviceName()} 正在发送图片…");
                }));
            };
            // 图片上行（2026-11-12 新增）：设备端编码 PNG → UDP 通知 → 本类走 TCP 取回字节 → 这里写剪贴板。
            // 必须回 UI 线程：WPF 的 Clipboard.SetImage 只能 STA 调用，
            // 且它内部会同时写 CF_DIB 与私有格式，Word / 微信 / 画图都能直接 Ctrl+V。
            _clipInbox.ImageReceived += (png, from) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var frame = BitmapFrame.Create(new MemoryStream(png),
                            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        Clipboard.SetImage(frame);
                        _clipTextTick = Environment.TickCount64;
                        _clipDownBtn?.Flash(Theme.Accent, 600);
                        AppLog.Info($"[剪贴板] 收到图片 {png.Length}B {frame.PixelWidth}x{frame.PixelHeight}" +
                                    $" 端到端 {_clipPullWatch?.ElapsedMilliseconds ?? -1}ms");
                        SetStatus($"[剪贴板] 已收到{DeviceNameByIp(from)}图片 {frame.PixelWidth}x{frame.PixelHeight}，可直接 Ctrl+V 粘贴");
                        _clipPullDone?.TrySetResult($"[image {png.Length}B]");
                    }
                    catch (Exception e)
                    {
                        SetStatus($"[剪贴板] 图片写入剪贴板失败：{FirstLine(e.Message)}");
                    }
                }));
            };
            _clipInbox.Start();

            if (_clipInbox.LastError is not null)
            {
                SetStatus($"[剪贴板] 监听未建立：{_clipInbox.LastError}");
            }
        }
        catch (Exception e)
        {
            SetStatus($"[剪贴板] 启动失败：{e.Message}");
        }
    }

    /// <summary>
    /// 「取剪贴板」：用户在投屏窗口点一下，把手不离开电脑地取回平板当前复制的内容。
    ///
    /// 为什么必须借 hdc：鸿蒙只在「用户亲手点 PasteButton」那一刻给剪贴板读权限，
    /// 后台 / 切走即撤销（2026-11-11 真机实测：后台能收到 update 事件但读内容必 FAIL）。
    /// 而 PasteButton 接受注入点击（实测 uitest uiInput click 可触发并拿到授权），
    /// 所以由电脑替用户点这一下。
    ///
    /// 时序不用固定 sleep 猜：ClipPage 一进前台就广播 HCLIP/1-READY，
    /// 收到 READY 再注入点击，避免「页面还没切过去就点」点错页。
    /// READY 超时仍要点一次 —— 覆盖「App 已经停在前台剪贴板页、不会重放 onPageShow」的情况。
    /// </summary>
    private async Task PullClipboardAsync(TitleIconButton? btn)
    {
        if (_clipPullBusy)
        {
            return;
        }
        _clipPullBusy = true;
        btn?.Flash(Theme.Accent);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _clipPullWatch = sw;
        try
        {
            // 剪贴板必须跟着当前选中的设备走：hdc 通道只有一条，先把目标切过去。
            SyncInjectorToActiveDevice();
            string devName = ActiveDeviceName();
            string key = DeviceKey();
            _clipExpectIp = DeviceLanIp();   // 只认这台的 READY / BUSY

            SetStatus($"[剪贴板] 正在唤起 {devName} 并点击「粘贴」…");
            _clipReady = new TaskCompletionSource<(int X, int Y)>(TaskCreationOptions.RunContinuationsAsynchronously);
            System.Threading.Interlocked.Exchange(ref _clipReadySeq, 0);
            _clipReadyLatest = (0, 0);   // 上一台/上一轮的坐标不能残留，否则会拿它盲点
            _clipReadySettled = false;

            // 2026-11-12 01:55 提速：把「唤起 → 等就绪 → 注入点击」压成**一条** hdc 命令。
            //
            // 原来三次往返（aa start / 等 READY / 注入），每次 hdc 在无线下约 450ms 握手，
            // 光传输就吃掉 900ms，端到端实测 2191ms —— 用户问「为什么这么慢」的根因在这里，
            // 不在注入命令本身（uinput 513ms 与 uitest 435ms 同一量级，换命令救不了）。
            //
            // 能压成一条，靠两条实测结论：
            //   ① 浮窗位置由系统固定。2026-11-12 01:48 拖它标题栏，位置纹丝不动，
            //      始终是 (1240,514)-(1522,796)，中心 (1382,656)；
            //   ② 设备侧从 aa start 到浮窗可点约 1100ms（STARTED 820 + 退后台 120 + 进程启动）。
            // 所以坐标不必等 READY 回报，设备端 sleep 到浮窗上屏后直接点下去即可。
            //
            // READY 仍然监听、但不参与主路径：只用来做「位置真的变了」的校准与兜底补点。
            //
            // ⚠ 前提是「这台的坐标已经有缓存」。缓存是**按设备分开**的：
            // 手机 914x1292、平板 1600x2560，拿平板那套坐标去点手机必然点空，
            // 而点空那一击会落到用户正在用的应用上。没有缓存就不盲点，先等这台的 READY
            // （ClipPage 用 getRectangleById 自己量）再点 —— 首轮慢 ~500ms，之后走缓存。
            (int X, int Y) pt;
            bool haveCache = _clipTapByDevice.TryGetValue(key, out var cached)
                             && cached.X > 0 && cached.Y > 0;
            pt = haveCache ? cached : default;

            // 设备侧从 aa start 到浮窗可点约 950ms（STARTED ~820 + 退后台 120，减去本地启动已用掉的部分）。
            // 留 700ms 是逐档实测的结论：1150/950/850/700 都能一次命中，550 出现过一次 7s 异常（首轮会话重建），
            // 所以停在 700ms —— 实测 1384~1465ms 三次稳定，比原来的 2191ms 快 35%。
            string launch = "aa start -a EntryAbility -b com.harmos.mirror --ps clip 1";
            string cmd = haveCache
                ? $"{launch}; sleep 0.70; uinput -T -c {pt.X} {pt.Y}"
                : launch;
            long t0 = Environment.TickCount64;
            var shot = await _injector.ShellAsync(cmd);
            // ⚠ `aa` 失败时**退出码仍是 0**（2026-11-13 真机实测：手机上没装 HAP，
            // `aa start` 打 "Error Code:10104001 The specified ability does not exist" 但 EXIT=0）。
            // 只看 shot.Ok 会当成唤起成功，接着去盲点一次 —— 那一击会落到用户正在用的应用上。
            // 所以必须再认一次输出里的错误字样。
            string shotText = shot.Output ?? "";
            bool launchFailed = !shot.Ok
                                || shotText.Contains("failed to start ability", StringComparison.Ordinal)
                                || shotText.Contains("Error Code:1010", StringComparison.Ordinal);
            if (launchFailed)
            {
                AppLog.Warn($"[剪贴板] 唤起 {devName} 失败：{FirstLine(shotText)}");
                SetStatus($"[剪贴板] 唤起 {devName} 失败：{FirstLine(shotText)}" +
                          "（这台可能没装 HarmLink HAP）");
                return;
            }

            if (!haveCache)
            {
                // 等这台的 READY（含 settled 后的真实坐标）。
                //
                // 2026-11-13 真机实测：**这个窗口必须按最慢的机器算**。
                // 平板 MLR-AL00 的 READY 约 1.5s 到，原本 2.5s 够用；但手机
                // （Pura X Max / HOP-AL10 折叠屏）要 5.3s 才报 —— 2.5s 会先放弃，
                // 随后的 READY 全被丢掉，表现成「切到手机点 ↓ 没反应、12s 超时」。
                // 所以放宽到 7s：慢机一次成功，比快机慢 5 秒但不会白跑一轮。
                long until = Environment.TickCount64 + 7000;
                while (Environment.TickCount64 < until && _clipReadyLatest.X <= 0)
                {
                    await Task.Delay(40);
                }
                string ptSrc;
                if (_clipReadyLatest.X > 0)
                {
                    pt = _clipReadyLatest;
                    ptSrc = "READY";
                }
                else if (ClipTapFromEnv)
                {
                    // 只有用户显式给过 HARMOS_CLIP_TAP 才盲点：那是他自己知道该点哪。
                    pt = ClipTap;
                    ptSrc = "环境变量";
                }
                else if (_clipExpectIp is { Length: > 0 } wantIp
                         && _clipReadyByIp.TryGetValue(wantIp, out var lastReady)
                         && lastReady.X > 0 && lastReady.Y > 0)
                {
                    // 2026-11-13 真机实测：**同一台设备的第二次取剪贴板往往收不到 READY**。
                    // 原因是「点空之后页面一直留在前台」，下次 aa start 只走 onNewWant，
                    // 设备端 ClipPage 的补发链路（AppStorage clipPing）在页面已显示时不触发，
                    // 于是 PC 白等 7s 后放弃 —— 手机上连续两轮都卡在这里。
                    // 兜底：用这台设备**上一次实测到**的 READY 坐标。浮窗位置由系统固定，
                    // 只有折叠/展开切换才会变，那种情况点空后仍会走下面的补点逻辑。
                    pt = lastReady;
                    ptSrc = $"上次实测坐标({wantIp})";
                }
                else
                {
                    // 没等到 READY 又没有任何可靠坐标 —— **不点**。
                    // 默认坐标 (1382,656) 是平板 1600×2560 上量的，换到手机上完全不是那回事，
                    // 盲点的结果是点到用户正在用的应用（本项目硬规则：不许动用户私人应用）。
                    AppLog.Warn($"[剪贴板] {devName} 没有 READY 也没有坐标缓存，放弃点击");
                    SetStatus($"[剪贴板] 没等到 {devName} 的剪贴板页（可能没装 HAP），已放弃点击以免误触");
                    return;
                }
                AppLog.Info($"[剪贴板] {devName}无点击缓存，按{ptSrc} {pt.X},{pt.Y} 点击");
                await _injector.ShellAsync($"uinput -T -c {pt.X} {pt.Y}");
            }

            // 命中判定：命令已经回来但内容还没到，说明那一下点空了（设备启动比预期慢，或浮窗换了位置）。
            // 正常路径下内容在命令返回前就到了，这段直接跳过，不花时间。
            long watchUntil = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < watchUntil && _clipTextTick <= t0)
            {
                await Task.Delay(40);
            }
            (int X, int Y) readyPt = _clipReadyLatest;
            if (_clipTextTick <= t0)
            {
                // 设备自己量过坐标就优先用它（换设备、换分辨率时靠这条自动纠正）
                if (readyPt.X > 0 && readyPt.Y > 0)
                {
                    pt = readyPt;
                }
                AppLog.Info($"[剪贴板] 首次点击未命中，补点一次 ({pt.X},{pt.Y})");
                await _injector.ShellAsync($"uinput -T -c {pt.X} {pt.Y}");
            }

            AppLog.Info($"[剪贴板] 合并命令 {shot.ElapsedMs:F0}ms | 设备 {devName} | 锚点 ({pt.X},{pt.Y}) " +
                        $"| READY {(readyPt.X > 0 ? $"{readyPt.X},{readyPt.Y}" : "未到")}");
            if (pt.X > 0 && pt.Y > 0)
            {
                // 记下来当这台下一轮的兜底坐标，也作为校准基准
                _clipTapByDevice[key] = pt;
            }
            SetStatus($"[剪贴板] 已替你点 {devName} 的「粘贴」，等它回传内容…");
        }
        catch (Exception e)
        {
            SetStatus($"[剪贴板] 取用失败：{e.Message}");
        }
        finally
        {
            _clipReady = null;
            _clipPullBusy = false;
        }
    }

    /// <summary>
    /// 图片下行：把电脑剪贴板里的位图送进平板系统剪贴板（2026-11-12 新增）。
    ///
    /// 为什么不能沿用文本那条 aa start --ps cliptext：图片 base64 后通常 1~4MB，
    /// 塞进 want 参数会超出命令行与 Binder 的长度上限，直接把 aa 调用打死。
    /// 所以只用一个"去监听"的空参数把 ClipSetAbility 拉起来，
    /// 真数据走 TCP（设备侧监听 18768，PC 连过去推 PUT）。
    ///
    /// 返回 false 表示"剪贴板里没有图片"，让调用方继续走文本路径。
    /// </summary>
    private async Task<bool> TrySendClipboardImageAsync()
    {
        BitmapSource? img = null;
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return false;
            }
            img = Clipboard.GetImage();
        }
        catch (Exception e)
        {
            SetStatus($"[上传] 读取电脑剪贴板图片失败：{FirstLine(e.Message)}");
            return false;
        }
        if (img is null)
        {
            return false;
        }
        if (ActivePage() is not { Streaming: true })
        {
            SetStatus("[上传] 还没有画面，先连上设备再发");
            return true;
        }
        // 图片下发走设备的局域网 IP，IP 取自 hdc 目标 —— 必须先把目标切到活动设备，
        // 否则切到手机后图片会推到平板去（ClipImagePump 是按 IP 直连的）。
        SyncInjectorToActiveDevice();
        string devName = ActiveDeviceName();
        string? ip = DeviceLanIp();
        if (ip is null)
        {
            SetStatus($"[上传] 当前 hdc 目标不是无线地址，拿不到 {devName} 的 IP，无法发送图片");
            return true;
        }

        _clipSendBusy = true;
        _clipUpBtn?.Flash(Theme.Accent);
        try
        {
            byte[] png;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                png = ms.ToArray();
            }
            SetStatus($"[上传] 正在发送图片到{devName} {img.PixelWidth}x{img.PixelHeight}（{png.Length / 1024} KB）…");

            // 拉起设备端无窗口 ability：它只为"起来后监听 18768"这一件事，
            // 收到图 → 写剪贴板 → 自己退出。
            var r = await _injector.ShellAsync(
                "aa start -a ClipSetAbility -b com.harmos.mirror --ps clipimg 1");
            // aa 返回不代表已经 listen，PushAsync 内部带连接重试（最多 20 次 × 150ms）
            bool ok = await ClipImagePump.PushAsync(ip, png);
            if (ok)
            {
                SetStatus($"[上传] 图片已写入 {devName} 剪贴板（{png.Length / 1024} KB）—— 在 {devName} 上长按粘贴即可");
                AppLog.Info($"[剪贴板] 图片下发成功（PC→{devName}）{png.Length}B → {ip}:{ClipImagePump.Port}");
            }
            else
            {
                AppLog.Info($"[剪贴板] 图片下发失败（PC→{devName}）→ {ip}:{ClipImagePump.Port} aa={FirstLine(r.Output)}");
                SetStatus($"[上传] 图片发送失败（{devName} 端需装新 HAP）：{FirstLine(r.Output)}");
            }
            return true;
        }
        catch (Exception e)
        {
            SetStatus($"[上传] 图片发送异常：{e.Message}");
            return true;
        }
        finally
        {
            _clipSendBusy = false;
        }
    }

    /// <summary>
    /// 从当前 hdc 目标里取平板局域网 IP。返回 null 表示目标是 USB 序列号或还没解析出来。
    /// 图片上下行都要用（设备端的一次性 TCP 服务监听在它自己的局域网上）。
    /// </summary>
    private string? DeviceLanIp()
    {
        string t = _injector.HdcTarget ?? string.Empty;
        int c = t.LastIndexOf(':');
        if (c <= 0)
        {
            return null;
        }
        string ip = t[..c];
        return System.Net.IPAddress.TryParse(ip, out var a)
               && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? ip
            : null;
    }

    /// <summary>
    /// 「上传剪贴板」（标题栏 ↑ 按钮 / Ctrl+Shift+V）：把电脑剪贴板的文字送到平板。
    ///
    /// 与 PullClipboardAsync（↓ 按钮）正好反向。**注意它不是写平板系统剪贴板**，
    /// 而是调 uitest uiInput inputText 替你「敲字」到当前光标处。原因：
    ///   · 写设备剪贴板只有 pasteboard.setData() 一条路，且必须由设备端应用执行；
    ///     我们 HAP 平时不在前台（参考采集模式下甚至没跑），拿不到这个执行点；
    ///   · 投屏通道（HWP）在参考采集模式下是 PC 本机的 127.0.0.1 采集桥，到不了设备。
    /// 代价：必须已有焦点输入框，且用户先在画面上点过它 —— 那一下既给焦点，
    /// 也提供 uitest 要求的「最后一次触摸坐标」锚点。
    /// 命令构造与转义见 Core/ClipTextInjector.cs。
    /// </summary>
    private async Task SendClipboardToTabletAsync()
    {
        if (_clipSendBusy)
        {
            return;
        }
        string text;
        try
        {
            // WPF 的剪贴板在被别的进程占用时会抛 COMException，不能让它冒到 UI 线程外
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception e)
        {
            SetStatus($"[上传] 读取电脑剪贴板失败：{FirstLine(e.Message)}");
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            // 没有文字就试图片（2026-11-12 新增）。图片走 TCP 旁路，见 Core/ClipImagePump.cs。
            if (await TrySendClipboardImageAsync())
            {
                return;
            }
            SetStatus("[上传] 电脑剪贴板里没有文字或图片");
            return;
        }
        if (ActivePage() is not { Streaming: true })
        {
            SetStatus("[上传] 还没有画面，先连上设备再发");
            return;
        }

        _clipSendBusy = true;
        _clipUpBtn?.Flash(Theme.Accent);
        // 上传同样跟着活动设备走：先把 hdc 目标切过去，再发。
        SyncInjectorToActiveDevice();
        string devName = ActiveDeviceName();
        string tapKey = DeviceKey();
        try
        {
            // 首选：真·写设备系统剪贴板。
            // aa start 拉起设备端无窗口的 ClipSetAbility，由它在设备侧执行 pasteboard.setData()。
            // 这条路不需要用户先在画面里点输入框，也不经过光标 —— 写完在设备任意位置长按就能粘贴。
            string? setCmd = ClipTextInjector.BuildSetClipboardCommand(text);
            if (setCmd is not null)
            {
                SetStatus($"[上传] 正在写入 {devName} 剪贴板（{text.Length} 字）…");
                var r = await _injector.ShellAsync(setCmd);
                if (r.Ok)
                {
                    SetStatus($"[上传] 已写入 {devName} 剪贴板 {text.Length} 字 —— 在 {devName} 上长按输入框选「粘贴」即可");
                    return;
                }
                // 设备端还是旧 HAP（没有 ClipSetAbility）、或 aa 拒绝启动时落到这里
                SetStatus($"[上传] 写剪贴板未成功：{FirstLine(r.Output)}（改用敲字）");
            }
            else
            {
                SetStatus($"[上传] 文本 {text.Length} 字过长，改走敲字通道");
            }

            // 敲字通道的锚点也是按设备分开的：两台分辨率不同，混用会敲到错位置。
            if (!_lastTapByDevice.TryGetValue(tapKey, out var tap) || tap.X < 0 || tap.Y < 0)
            {
                SetStatus("[上传] 请先在画面里点一下要输入的输入框，再点标题栏的 ↑");
                return;
            }

            SetStatus($"[上传] 正在敲入 {text.Length} 字…");

            // 首选：文本落文件 → 推设备 → 设备端 $(cat) 展开后交给 uitest。
            // 这样 emoji 不被 Windows 的 ANSI 命令行吃掉，也不需要任何转义与分片。
            // 失败再退回逐条命令行直传（纯 ASCII/中文场景仍然可用）。
            bool sent = false;
            try
            {
                var up = await PushKbdFileAsync(text);
                if (up.Ok)
                {
                    var r = await _injector.ShellAsync(ClipTextInjector.BuildFileTextCommand());
                    sent = r.Ok;
                    if (!sent)
                    {
                        SetStatus($"[上传] 注入失败：{FirstLine(r.Output)}");
                    }
                }
                else
                {
                    SetStatus($"[上传] 推送文本文件失败：{FirstLine(up.Output)}（改用逐条直传）");
                }
            }
            catch (Exception e)
            {
                SetStatus($"[上传] 文件通道异常：{e.Message}（改用逐条直传）");
            }

            if (!sent)
            {
                List<string> cmds = ClipTextInjector.BuildCommands(
                    tap.X, tap.Y, text);
                for (int i = 0; i < cmds.Count; i++)
                {
                    var r = await _injector.ShellAsync(cmds[i]);
                    if (!r.Ok)
                    {
                        SetStatus($"[上传] 第 {i + 1}/{cmds.Count} 条失败：{FirstLine(r.Output)}");
                        return;
                    }
                }
            }
            SetStatus($"[上传] 已敲入 {text.Length} 字");
        }
        catch (Exception e)
        {
            SetStatus($"[上传] 失败：{e.Message}");
        }
        finally
        {
            _clipSendBusy = false;
        }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return "无输出";
        }
        string t = s.Trim();
        int nl = t.IndexOfAny(new[] { '\r', '\n' });
        return nl > 0 ? t[..nl] : t;
    }

    /// <summary>
    /// 调暗 / 恢复**平板本身**的亮度（不是本机画面）。
    ///
    /// 走 DeviceBrightness 的「拉出控制中心 → 点亮度条」注入路径：平板对第三方关闭了
    /// 所有全局亮度 API（架构补记㊹），这是唯一不需要受限权限的通道。
    /// 全程只有输入注入 + hidumper 读取，不启动录屏。
    /// </summary>
    private async Task ApplyDeviceBrightnessAsync(bool dim)
    {
        if (_brightnessBusy)
        {
            return;
        }
        _brightnessBusy = true;
        try
        {
            var b = new DeviceBrightness(_injector);
            int? cur = await b.ReadAsync().ConfigureAwait(false);
            if (cur is null)
            {
                SetStatus("[亮度] 读不到设备亮度：确认平板在线（hidumper 无返回）");
                return;
            }

            int target;
            if (dim)
            {
                // 记住调暗前的实际值；用户中途自己调过亮度，就按最新值再记一次。
                if (cur.Value > DeviceBrightness.MinBrightness + 4)
                {
                    _brightnessBeforeDim = cur.Value;
                }
                target = DeviceBrightness.MinBrightness;
                SetStatus($"[亮度] 正在把平板调到最暗（当前 {cur.Value}/255）…");
            }
            else
            {
                target = _brightnessBeforeDim > 0 ? _brightnessBeforeDim : DeviceBrightness.FallbackBrightness;
                SetStatus($"[亮度] 正在把平板恢复到 {target}/255…");
            }

            var r = await b.SetAsync(target).ConfigureAwait(false);
            if (!r.Ok || r.Value < 0)
            {
                SetStatus($"[亮度] 设置未确认：{FirstLine(r.Output)}");
                return;
            }
            SetStatus(dim
                ? $"[亮度] 平板已调到最暗（实测 {r.Value}/255），恢复点 {_brightnessBeforeDim}"
                : $"[亮度] 平板亮度已恢复（实测 {r.Value}/255）");
            AppLog.Info($"[亮度] dim={dim} 目标={target} 实测={r.Value} 点击位 x={DeviceBrightness.BrightnessToX(target)}");
        }
        catch (Exception e)
        {
            SetStatus($"[亮度] 失败：{e.Message}");
        }
        finally
        {
            _brightnessBusy = false;
        }
    }

    /// <summary>
    /// 后台线程改 UI 的统一入口：UI 线程上直接执行（顺序不变），跨线程时异步投递。
    ///
    /// 2026-11-13：原来 20 处写的是同步 Dispatcher.Invoke，其中
    /// _manager.Log 回调挂在 UDP 收包线程、InjectPointerAsync 的引用通道回执挂在
    /// ConfigureAwait(false) 后的线程池线程。UI 线程一旦被 Render 的 WritePixels
    /// 占住，这些线程就被反压阻塞（最坏 16ms+ 一帧抖动），还会拖慢下一次输入。
    /// 这些调用全是 SetStatus / Flash / SetLocked 之类的呈现层动作，没有同步语义要求，
    /// 改成 BeginInvoke 不会改变功能；同文件 2777-2781 的采集 log 回调本来就是这么写的。
    /// </summary>
    private void PostUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        Dispatcher.BeginInvoke(action);
    }

    private static string FirstLineOf(string text)
    {
        int nl = text.IndexOf('\n');
        string line = nl < 0 ? text : text.Substring(0, nl);
        return line.Length > 100 ? line.Substring(0, 100) : line;
    }

    private void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(message)));
            return;
        }
        if (_statusHost is not null)
        {
            _statusHost.Text = message;
        }
        // 纯画面模式下 header / 底栏都被隐藏，_statusHost 根本看不见 ——
        // 启动那十几秒窗口里只有一行死板的「等待设备画面…」，用户会判成
        // 「连接不上 / 卡住」。出帧前把进度状态同步写到画面中央。
        if (_placeholder.Visibility == Visibility.Visible && IsStageMessage(message))
        {
            _placeholder.Text = message;
        }
    }

    // ── 设备 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 建一个会话（一台设备一条）。localPort 指定本机收流端口 —— 双路并行时
    /// 两台各不相同（9010/9011），这样 SessionManager 才能按「源 IP:源端口」
    /// 把流分派给正确的会话。单设备默认 9010，行为与改造前一致。
    /// </summary>
    private void AddDevice(string? host = null, int localPort = Hwp.LocalPort,
        DeviceEntry? dev = null)
    {
        string target = string.IsNullOrWhiteSpace(host) ? Hwp.DefaultDeviceHost : host;
        // 会话的 Port 存的是**设备侧控制端口**（采集桥 _rx 绑的 9020/9021），
        // 不是本机收流端口。两者搞反的后果很隐蔽：控制包能自己绕回本机自问自答
        // 假装 ok=True，而真正该收流的端点没人监听 → packets 恒为 0（2026-11-13 实测）。
        int devicePort = dev?.DevicePort ?? Hwp.DevicePort;
        if (localPort <= 0)
        {
            localPort = dev?.LocalPort ?? Hwp.LocalPort;
        }

        // 参考采集模式（默认）：画面数据是 PC 经 hdc 从设备拉流、在本机采集桥转成 HWP 后发到
        // 127.0.0.1:<localPort> 的。设备真实 IP 的 :9020 上没有任何监听，往那儿连只会建出一个
        // 永远等不到回执的死会话 —— 表现就是「CMD_STREAM 未获 ACK」+ 画面卡在
        // 「正在建立画面连接…」，同时白占一个并发位（2026-11-11 用户实际踩到）。
        // 所以这里直接改走已存在的回环会话，绝不再按设备 IP 新建。
        if (UseRefCapture(dev) && target != "127.0.0.1")
        {
            if (_manager.FindByHost("127.0.0.1", devicePort) is { } autoSession)
            {
                SwitchTo(autoSession.Id);
                SetStatus($"[{autoSession.Id}] 已自动连接（参考采集走本机采集桥），无需再连设备 IP");
                return;
            }
            target = "127.0.0.1";
        }

        // 该地址已有会话？直接切过去，不要新建。
        // 用户连不上时会反复点「连接设备」，若每次都新建 device-N，
        // 就会出现多个同 IP 会话互相抢包、设备端陷入「重新接入 + 重放 IDR」死循环
        // （2026-11-09 实测，表现为「只有一张静止的图」「完全连不上」）。
        if (_manager.FindByHost(target, devicePort) is { } existing)
        {
            SwitchTo(existing.Id);
            SetStatus($"[{existing.Id}] 该地址已连接，已切回原会话");
            return;
        }

        if (_pages.Count >= SessionManager.MaxDevices)
        {
            SetStatus($"最多 {SessionManager.MaxDevices} 台设备");
            return;
        }
        string id = $"device-{_pages.Count + 1}";
        if (dev is not null)
        {
            dev.Id = id;
        }
        var session = _manager.Add(id, target, devicePort, DeviceRole.Host, localPort);
        // ⛔ 2026-11-14 用户明确要求：**不再后台自动同步剪贴板**。
        // 原来这里无条件调 session.EnableClipboard()，效果是每 300ms 轮询一次 PC 剪贴板，
        // 一变就经 HWP 写进设备系统剪贴板（设备端 pasteboard.setData），且设备端
        // SessionService 一建会话就挂 on('update') 反向回传 —— 双向静默镜像。
        // 问题：① 电脑上随便复制什么都会进设备剪贴板（隐私面）；② 设备剪贴板被反复改写，
        // 输入法收到 update 通知，可能在打字时引起状态抖动。
        // 参考项目（hnscrcpy / lanlink / oink）全都只用「用户显式触发的一次性剪贴板动作」，
        // 没有任何一个做后台自动同步 —— 我们这条是自己加的，没有参考依据。
        //
        // 现在改为：**只有点标题栏 ↑/↓ 按钮才同步**。两个按钮走的是完全独立的路径：
        //   ↑ 上传 = `aa start ClipSetAbility`（设备端 ability 真写剪贴板）
        //   ↓ 下载 = ClipInbox(UDP 18766) + 设备端 ClipPage
        // 两者都不经过 ClipboardBridge，关掉自动同步对它们零影响。
        // 若将来要恢复自动同步，把下面这行注释掉即可（EnableClipboard 实现仍保留）。
        // session.EnableClipboard();
        // 解码队列溢出（参考链已断）时立刻催设备出一个 IDR。参考采集模式下
        // 设备端由 gRPC onRequestIDRFrame 接收；否则只能等设备自然 IDR
        // （iFrameInterval=1000ms），滑动时会明显冻结。
        // 按设备找它自己的采集桥 —— 双路并行下拿错实例会给另一台催帧。
        if (_referenceCaps.TryGetValue(dev?.ResolvedTarget ?? "", out var cap) ||
            (dev is null && _referenceCaps.Count == 1 && _referenceCaps.TryGetValue(_referenceCaps.Keys.First(), out cap)))
        {
            session.KeyframeNeeded += cap.RequestKeyframeImmediate;
            // 等 IDR 反复催帧仍无进展（约 4.7s）→ 参考链已死，主动重建 gRPC 流。
            // 这条补的是老账「_awaitKeyframe 无超时 → 画面永久定格」，
            // 判定依据是「我们自己正在丢帧」，不是帧间隔（静态画面不推帧是正常的）。
            session.KeyframeStarvation += cap.ForceResync;
        }
        // 自测模式把会话日志打到控制台：诊断「解码器拿不到分辨率」这类问题，
        // 只看黑屏窗口分辨不出是设备没发、还是 PC 没订阅（2026-11-08 踩过）。
        if (_autoHost is not null)
        {
            session.Log += m => Console.WriteLine(m);
        }
        session.LockStateChanged += locked => PostUi(() => SetLocked(id, locked));
        // 三键 / 点击的注入回执：以前只进日志，界面上完全看不到成败。
        session.InjectResultReceived += r => PostUi(() => OnInjectResult(id, r));

        // 默认 Fill：画面拉伸到填满整个画面区，四周不会露出任何底色（用户要求「删掉黑框」）。
        var image = new Image { Stretch = _fillMode ? Stretch.Fill : Stretch.Uniform };
        var surface = new WpfSurface(image, Dispatcher, _scaleMode, OnFramePresented);
        session.Renderer = surface;
        // 设备端 STREAM_FPS=60；解码器默认 30 会让喂给 MFT 的 sample duration / 时间戳
        // 按 33.3ms 走，与实际 16.7ms 帧间隔不符。虽然不解码错误，但会干扰 MFT 的输出节奏。
        var decoder = new MediaFoundationDecoder(0, 0, 60);
        session.Decoder = decoder;
        // 设备侧 ResolutionBeacon 会周期性重报同一分辨率；只有真的变了才重新贴合，
        // 否则用户每次手动缩放后都会被后续的重报顶回最大贴合尺寸。
        int lastW = -1, lastH = -1, lastFolded = -1;
        session.ResolutionChanged += (w, h, folded) =>
            PostUi(() =>
            {
                bool changed = w != lastW || h != lastH || folded != lastFolded;
                lastW = w;
                lastH = h;
                lastFolded = folded;

                decoder.Configure(w, h);
                _resolutionChip.Text = folded == 1 ? $"{w}×{h} 折叠" : $"{w}×{h}";
                SetStatus($"[{id}] 设备分辨率 {w}x{h} folded={folded}，解码器已就绪");
                if (changed && _pages.TryGetValue(id, out var pg) && pg.Streaming &&
                    _manager.ActiveId == id)
                {
                    // 只有「当前正在看的那台」分辨率变化才重新贴合窗口。
                    // 双路并行时另一台的 ResolutionBeacon 会周期性到达，若也贴合，
                    // 窗口会在 0.707（手机）与 0.625（平板）之间来回跳。
                    FitWindowToDevice();
                }
                // 比例刚到手时也要校一次填充方式（单台时窗口是按它贴的，双路时不是）。
                if (_pages.TryGetValue(id, out var pg2))
                {
                    ApplyStretchForAspect(pg2);
                }
            });

        var lockText = new TextBlock
        {
            Text = "画面已遮挡\n点「取消遮罩」恢复\n（仅遮住本机画面，设备不受影响）",
            Foreground = Theme.Text,
            FontSize = 18,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 30
        };
        var overlay = new Grid
        {
            Background = Theme.Alpha(0xF0, 0x12, 0x14, 0x19),
            Visibility = Visibility.Collapsed
        };
        overlay.Children.Add(lockText);

        var pageRoot = new Grid { Visibility = Visibility.Collapsed };
        pageRoot.Children.Add(image);
        pageRoot.Children.Add(overlay);
        _stage.Children.Add(pageRoot);

        // 侧栏条目（2026-11-16）：两行分别显示「设备名 + 真机型号」与「各自的无线调试地址」，
        // 单行塞不下 IP:端口，之前被裁掉过。● 已接入 / ○ 未接入。
        string devIp = dev is not null ? DeviceIpFor(dev) : target;
        string devTip = dev is not null
            ? $"{dev.Name} {dev.Model}\n无线调试地址 {devIp}\n已接入，点击切换查看"
            : $"已接入 {target}";
        string rowText = dev is not null
            ? string.IsNullOrEmpty(dev.Model)
                ? $"● {dev.Name}\n{devIp}"
                : $"● {dev.Name} {dev.Model}\n{devIp}"
            : $"● {id}\n{target}";
        var row = new FlatButton(rowText, BtnKind.Ghost, 40, 11);
        row.SetLeftAligned();
        row.Margin = new Thickness(0, 0, 0, 6);
        row.ToolTip = devTip;
        row.Clicked += () => SwitchTo(id, refitWindow: true);
        _deviceList.Children.Add(row);
        _deviceRows[id] = row;

        _pages[id] = new Page
        {
            Session = session,
            Root = pageRoot,
            Surface = surface,
            View = image,
            LockOverlay = overlay,
            Row = row,
            DeviceName = dev?.Name ?? "",
            DeviceIp = devIp
        };

        // 标题栏也放一枚同款入口（用户 22:16 要求）：侧栏收起时也能切。
        AddDeviceChip(id, dev?.Name, tip: devTip);
        // 启动前为这台摆的「未接入」占位要撤掉，否则两台都启用时标题栏会有 4 枚图标
        // （2 枚 pending + 2 枚真图标，2026-11-16 18:32 实测）。用户要的是两枚。
        RemovePendingDeviceUi(dev?.Name ?? "");

        SwitchTo(id);
        _deviceCount.Text = $"已连接 {_pages.Count}/{SessionManager.MaxDevices}";

        // 握手**必须串行**，不能与 CMD_STREAM 并发：
        // ReliableControlChannel 只有一个 _pending 槽，两条 SendAsync 同时跑时
        // 后发者覆盖前者的等待槽 → ACK 串台 → 其中一条必然超时失败。
        // 所以这里把握手任务保存下来，由 StartStreamWhenReadyAsync 等它完成后再发推流。
        session.HandshakeTask = session.ConnectAsync();
        _ = session.HandshakeTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                PostUi(() => SetStatus($"[{id}] 握手异常：{t.Exception?.GetBaseException().Message}"));
                return;
            }
            PostUi(() => SetStatus($"[{id}] 握手完成，可开始推流"));
            // 参考采集模式下禁止预热自有 uitest RPC：UitestRpcClient 的
            // pkill -f 'uitest start-daemon' 会把扩展守护进程一起杀掉导致立刻断流。
            // 输入改走启动期已建好的 ReferenceTouchChannel（参考项目 touch 通道），
            // 运行期只发持久 TCP，不再产生 hdc 调用；通道不可用时才回退 hdc shell。
            if (!UseRefCapture(dev))
            {
                // 后台预热注入通道（daemon 拉起 + tconn + fport 约 1~3s），
                // 否则这笔冷启动成本会砸在用户第一次点三键/画面的时候。
                _ = EnsureRpcAsync();
            }
        });
    }

    /// <summary>
    /// 在标题栏加一枚设备切换图标（用户 2026-11-13 22:16 要求）。
    /// 图标里不写文字：标题栏可用宽度只有约 380 DIP（右侧 145 留给系统窗口按钮），
    /// 菜单 + 帧率 + 两枚剪贴板箭头已经占了大半，再塞「手机」「平板」两个词必然挤到按钮上。
    /// 设备名放 tooltip，图形优先。
    /// </summary>
    private void AddDeviceChip(string id, string? name, bool muted = false,
        string? tip = null, Action? onClick = null)
    {
        if (_deviceChipRow is null)
        {
            // 标题栏没建（--menu off）时会走到这里。以前是静默 return，
            // 导致「图标不见了」完全查不到线索，这里至少留一条日志。
            AppLog.Warn($"[设备标签] {id} 跳过：标题栏未构建（menu={_menuEnabled}）");
            return;
        }
        if (_deviceChips.ContainsKey(id))
        {
            return;
        }
        var chip = new TitleIconButton(DeviceGlyphFor(name))
        {
            Margin = new Thickness(_deviceChips.Count == 0 ? 0 : 5, 0, 0, 0),
            ToolTip = tip ?? $"切到「{name ?? id}」（与左侧栏设备条目等效）"
        };
        if (muted)
        {
            chip.IsMuted = true;
        }
        // 2026-11-16：灰淡（未接入）不等于不可点。给了 onClick 就照样响应，
        // 否则用户点手机图标毫无反馈；没给 onClick 的在线项走默认切换。
        if (onClick is not null)
        {
            chip.Clicked += onClick;
        }
        else if (!muted)
        {
            chip.Clicked += () => SwitchTo(id, refitWindow: true);
        }
        // 标题栏默认整块是拖动区，逐个放开命中测试，否则点上去只是拖窗口。
        WindowChrome.SetIsHitTestVisibleInChrome(chip, true);
        _deviceChips[id] = chip;
        _deviceChipRow.Children.Add(chip);
        // 2026-11-14 改：原来「只有一台时整组隐藏」，用户直接把这两枚图标当成「设备已连接」的状态灯，
        // 只剩平板一台时看不到图标，反馈成「任务栏上方的平板和手机图标没了」。
        // 现在只要有设备就显示：一台时它是状态标识（点击无副作用），两台起才是切换器。
        _deviceChipRow.Visibility = Visibility.Visible;
        AppLog.Info($"[设备标签] 已添加 {id}（{name ?? "-"}）muted={muted}，当前共 {_deviceChips.Count} 枚");
        RefreshDeviceChips();
    }

    /// <summary>
    /// 「配置里写了、但当前没接入」的设备：侧栏条目 + 标题栏图标都摆出来，且都可点击。
    /// 2026-11-14 20:37 要求两枚图标都在；2026-11-16 要求分别显示手机/平板 IP、点击可切换。
    /// 未接入项点击后显示占位页说明原因，不假装投出了画面。按 key 去重，可重复调用，
    /// 启动流程结束后会再调一次，把「启用过但启动失败」的那台也补出来。
    /// </summary>
    private void AddPendingDeviceUi()
    {
        foreach (var d in _devices)
        {
            // 已接入的那台由 AddDevice 建真条目，避免重复。
            if (d.Id is { Length: > 0 } && _pages.ContainsKey(d.Id))
            {
                continue;
            }
            string key = "pending-" + d.Name;
            if (_deviceRows.ContainsKey(key))
            {
                continue;
            }
            string ip = DeviceIpFor(d);
            // 2026-11-16 18:23：以前一律写「当前未接入（配置值；端口可能变化）」，
            // 把「配置里被禁用」和「启用了却连不上」混成一团，用户会误以为是设备没开无线调试。
            // 按 enabled 分开说清楚，别再让用户去查设备。
            string reason = d.Enabled
                ? "已启用但本次启动未接入：无线调试端口变了 / 设备离线 / 采集桥失败（见 logs\\startup.log）"
                : "配置里 enabled=false，程序不会向这台发任何连接请求（与无线调试开关无关）；改成 true 后重启即可接入";
            string tip = $"{d.Name} {d.Model}\n无线调试地址 {ip}\n{reason}\n点击查看状态";
            var row = new FlatButton($"○ {d.Name} {d.Model}\n{ip}", BtnKind.Ghost, 40, 11);
            row.SetLeftAligned();
            row.Margin = new Thickness(0, 0, 0, 6);
            row.ToolTip = tip;
            row.Clicked += () => SwitchToOffline(d, key);
            _deviceList.Children.Add(row);
            _deviceRows[key] = row;
            AddDeviceChip(key, d.Name, muted: true, tip: tip,
                onClick: () => SwitchToOffline(d, key));
        }
    }

    /// <summary>
    /// 设备真正接入后撤掉它的「未接入」占位（侧栏行 + 标题栏图标）。
    /// 构造函数阶段设备还没连，AddPendingDeviceUi 先摆了灰淡项；接入成功后必须移除，
    /// 否则同一台设备在标题栏出现两枚。启动失败的设备不调用本方法，占位保留。
    /// </summary>
    private void RemovePendingDeviceUi(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = "pending-" + name;
        if (_deviceChips.TryGetValue(key, out var chip))
        {
            _deviceChipRow?.Children.Remove(chip);
            _deviceChips.Remove(key);
        }
        if (_deviceRows.TryGetValue(key, out var row))
        {
            _deviceList.Children.Remove(row);
            _deviceRows.Remove(key);
        }
        RefreshDeviceChips();
    }

    /// <summary>设备显示用地址：优先启动探测到的真实目标，其次配置值，最后裸 IP。</summary>
    private static string DeviceIpFor(DeviceEntry d)
    {
        if (!string.IsNullOrWhiteSpace(d.ResolvedTarget)) return d.ResolvedTarget!;
        if (!string.IsNullOrWhiteSpace(d.HdcTarget)) return d.HdcTarget!;
        return d.Host;
    }

    /// <summary>按真机型号反推设备名。只认本项目两台已知机型，认不出就保留配置名。</summary>
    private static string NameForModel(string model) =>
        model.Contains("MLR", StringComparison.OrdinalIgnoreCase) ? "平板" :
        model.Contains("HOP", StringComparison.OrdinalIgnoreCase) ? "手机" : "";

    /// <summary>
    /// 用启动探测到的真实型号校正设备显示（2026-11-16 根因修复）。
    /// name/model 只是 UI 标签，真连到哪台由 hdcTarget 决定；两者打架时以真机为准，
    /// 不再出现「显示手机、实连平板」。
    /// </summary>
    private void ApplyProbedModel(DeviceEntry d, string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        string probed = model.Trim();
        if (!string.Equals(d.Model, probed, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn($"[设备身份] 配置写的是「{d.Name} {d.Model}」，实测型号 {probed}" +
                        $"（{d.ResolvedTarget}），已按真机校正显示");
        }
        d.Model = probed;
        string name = NameForModel(probed);
        if (name.Length > 0 && !string.Equals(d.Name, name, StringComparison.Ordinal))
        {
            AppLog.Warn($"[设备身份] 显示名「{d.Name}」→「{name}」（按实测型号）");
            d.Name = name;
        }
    }

    /// <summary>设备名 → 图标。手机窄高、平板宽扁；认不出名字时按手机画。</summary>
    private static TitleGlyph DeviceGlyphFor(string? name) =>
        name is not null &&
        (name.Contains("平板", StringComparison.Ordinal) ||
         name.Contains("Pad", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Tablet", StringComparison.OrdinalIgnoreCase))
            ? TitleGlyph.Tablet
            : TitleGlyph.Phone;

    /// <summary>当前活动设备那枚图标高亮（强调色描边 + 强调色图形），其余保持灰。</summary>
    private void RefreshDeviceChips()
    {
        if (_deviceChips.Count == 0)
        {
            return;
        }
        // 选中态跟着「用户最后点的条目」走，而不是只认 _manager.ActiveId：
        // 未接入的设备没有会话，但它被点中时图标也该高亮（2026-11-16）。
        string active = _activeDeviceKey.Length > 0 ? _activeDeviceKey : _manager.ActiveId ?? "";
        foreach (var kv in _deviceChips)
        {
            kv.Value.IsActive = kv.Key == active;
        }
    }

    /// <param name="refitWindow">
    /// 用户主动点侧栏/标题栏切换设备时传 true：按新设备比例重算窗口尺寸。
    /// 启动期 AddDevice / 自动连接 / 自测默认 false —— 冻结基线里启动流程的尺寸贴合行为一点不动。
    /// </param>
    private void SwitchTo(string id, bool refitWindow = false)
    {
        if (!_pages.TryGetValue(id, out var page))
        {
            return;
        }
        _manager.Switch(id); // 只切呈现，不拆流
        _offlineView.Visibility = Visibility.Collapsed;
        _activeDeviceKey = id;
        RefreshDeviceChips(); // 标题栏的两枚设备图标跟着高亮
        // 剪贴板 / 敲字 / 亮度这些走 hdc 的动作只有一条通道，必须显式跟着活动设备换目标。
        SyncInjectorToActiveDevice();
        foreach (var r in _deviceRows.Values) r.IsActive = false;
        foreach (var kv in _pages)
        {
            bool active = kv.Key == id;
            kv.Value.Root.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            kv.Value.Row.IsActive = active;
        }
        _activeTitle.Text = _pages.TryGetValue(id, out var p) && p.DeviceName is { Length: > 0 }
            ? p.DeviceName
            : id;
        // 2026-11-16：副标题要显示设备自己的无线调试地址，而不是只写「本机采集桥」，
        // 否则切到手机/平板时看不出各自 IP。
        _activeSub.Text = page.DeviceIp.Length > 0
            ? $"{page.DeviceIp} · 本机采集桥 :{page.Session.LocalPort}"
            : page.Session.Host == "127.0.0.1"
                ? $"本机采集桥 · 收流 :{page.Session.LocalPort}"
                : $"{page.Session.Host}:{page.Session.Port}";
        UpdateStreamButton();
        Refresh();
        // 手机是 0.707 比、平板是 0.625 比。「铺满」会把画面拉变形，
        // 所以比例不一致的那台自动退回等比（两侧黑边但不失真），比例一致才铺满。
        // 先同步校一次，保证切换瞬间画面不拉伸。
        ApplyStretchForAspect(page);

        // 2026-11-16 18:46 用户报「从手机切到平板，窗口大小没跟着切，存在黑框」：
        // 窗口尺寸只在启动时按当时那台设备贴合过，切设备后仍留着上一台的比例，
        // 等比显示必然两侧留黑边。用户主动切换时按新设备比例重新贴合窗口。
        // ⚠ 2026-11-13 实测：在 SwitchTo 同步路径里直接调 FitWindowToDevice 会与
        // Visibility 重排互相触发（其内部 UpdateLayout），未处理异常直接终止进程。
        // 因此用 Loaded 优先级排队，等重排跑完再量 chrome —— 与 3786 行推流开始时
        // 同一个已真机验证过的模式；异常就地捕获写日志，绝不让 Dispatcher 回调杀进程。
        if (refitWindow && page.Session.Width > 0 && page.Session.Height > 0)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    // 期间用户可能又切到第三台：以回调执行时的活动设备为准，别贴错比例。
                    if (_manager.ActiveId != id) return;
                    FitWindowToDevice();
                    // 改了 Width/Height 后 stage 的 ActualWidth/Height 不会立刻刷新，
                    // 先强制布局再重判，否则拿旧比例算 closeEnough 会误判成 Uniform。
                    UpdateLayout();
                    ApplyStretchForAspect(page);   // 窗口尺寸变了，Fill/Uniform 要重判
                    AppLog.Info($"[切换贴合] {id} 设备 {page.Session.Width}×{page.Session.Height} " +
                                $"比例 {(double)page.Session.Width / page.Session.Height:F3} → " +
                                $"窗口 {ActualWidth:F0}×{ActualHeight:F0} 填充={page.View.Stretch}");
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[切换] {id} 按设备比例贴合窗口失败（已捕获，进程继续）：{ex}");
                }
            }));
        }
        // 分辨率尚未上报时不排队，由 session.ResolutionChanged（3405-3411）在比例到达后
        // 自行贴合，那里也只认当前活动设备，不会让两台来回抢窗口。
    }

    /// <summary>
    /// 选中一台当前没接入的设备（2026-11-16）：显示占位页、同步两处选中态、说清为什么没画面。
    /// 不建假会话 —— 那会让 _manager.Active() 指向一台不存在的设备，剪贴板/注入就会静默打错目标。
    /// </summary>
    private void SwitchToOffline(DeviceEntry d, string key)
    {
        string ip = DeviceIpFor(d);
        foreach (var p in _pages.Values)
        {
            p.Root.Visibility = Visibility.Collapsed;
            p.Row.IsActive = false;
        }
        foreach (var r in _deviceRows.Values) r.IsActive = false;
        if (_deviceRows.TryGetValue(key, out var row))
        {
            row.IsActive = true;
        }
        // 2026-11-16 18:23 事故：占位页把「无线调试未开 / 端口变 / 配置禁用」并列，
        // 实际是配置里 enabled=false，用户手机无线调试明明开着却被引导去查设备。
        // 现在按 enabled 给唯一确定的原因，不再让三种可能并列误导。
        string cause = d.Enabled
            ? "配置已启用，但本次启动没能接入。\n常见原因：无线调试端口变了（以设备屏幕显示为准）、设备离线，或采集桥启动失败。\n详见 logs\\startup.log。"
            : "这台设备在 harmoswin.json 里被禁用（enabled=false）。\n程序启动时不会向它发任何连接请求，与无线调试是否打开无关。\n把 devices 里这台的 enabled 改成 true 后重启本程序即可接入。";
        _offlineText.Text = $"{d.Name} {d.Model}\n{ip}\n\n{cause}";
        _offlineView.Visibility = Visibility.Visible;
        _activeDeviceKey = key;
        _activeTitle.Text = $"{d.Name}（未接入）";
        _activeSub.Text = ip;
        RefreshDeviceChips();
        UpdateStreamButton();
        SetStatus($"[{d.Name}] {ip} 未接入；当前投屏与注入仍作用于 {ActiveDeviceName()}");
        Refresh();
    }

    private async Task ToggleStreamAsync()
    {
        var s = _manager.Active();
        if (s is null)
        {
            SetStatus("先连接设备");
            return;
        }
        if (!_pages.TryGetValue(s.Id, out var page))
        {
            return;
        }
        if (page.Streaming)
        {
            await s.StopStreamAsync();
            page.Streaming = false;
            SetStatus($"[{s.Id}] 已停止推流（设备端采集暂停）");
        }
        else
        {
            bool ok = await s.StartStreamAsync();
            page.Streaming = ok;
            if (ok)
            {
                // 画面是主角：推流一开始就收起侧栏并按设备比例贴合窗口
                if (!_sidebarCollapsed)
                {
                    _sidebarCollapsed = true;
                    ApplyChrome();
                }
                // 用 Loaded 优先级：等侧栏 Collapsed 引起的布局真正跑完再量 chrome，
                // 否则读到的是带侧栏的旧 stage 宽，会先贴成宽窗口再跳回窄窗口
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitWindowToDevice));
            }
            SetStatus(ok ? $"[{s.Id}] 已下发 CMD_STREAM，等待画面（侧栏已收起，点 ‹ 展开）" : "CMD_STREAM 未获 ACK");
        }
        UpdateStreamButton();
    }

    /// <summary>
    /// 注入通道自测（--nav-test）：先走一遍 MainWindow 真实用的建链流程（含 daemon 自启），
    /// 再对 返回/主页 分别用 RPC 与 hdc 两条通道各打一次，结果写文件。
    /// 目的是验证「UI 里点三键」这条链路，而不只是 Core 里的裸客户端。
    /// </summary>
    private async Task NavSelfTestAsync(string path)
    {
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        var rpc = await EnsureRpcAsync().ConfigureAwait(false);
        lines.Add($"bootMs={sw.ElapsedMilliseconds} rpcReady={rpc is not null} connects={_rpc?.ConnectCount} " +
                  $"target={_injector.HdcTarget} agent={_rpc?.AgentStatus ?? "-"}");
        foreach (byte key in new byte[] { NavKey.Back, NavKey.Home })
        {
            if (rpc is not null)
            {
                var a = await rpc.PressNavAsync(key).ConfigureAwait(false);
                lines.Add($"rpc key={key} ok={a.Ok} ms={a.ElapsedMs:F2}");
            }
            var b = await _injector.NavAsync(key).ConfigureAwait(false);
            lines.Add($"hdc key={key} ok={b.Ok} ms={b.ElapsedMs:F2}");
        }
        await File.WriteAllLinesAsync(path, lines).ConfigureAwait(false);
        PostUi(() => SetStatus($"[nav-test] 已写入 {path}"));
    }

    /// <summary>
    /// 现场解析 hdc 无线调试目标并回写到注入器。
    /// 顺序：显式参数 → 在线列表（同 IP 优先）→ 配置 → 上次成功缓存。
    /// 每个候选都真连一次验证，所以「列表里挂着但已掉线」的陈旧目标不会被误用。
    /// 全过程写进 logs\startup.log，失败时状态栏给出日志路径。
    /// </summary>
    private async Task<string?> ResolveHdcTargetAsync()
    {
        // 单设备 / 首选目标用的缓存文件，与双路的 per-device 缓存（hdc-target-<ip>.txt）分开：
        // 共用一份会让「一台的地址」写进「另一台的缓存」，2026-11-13 已出过串台事故。
        string cacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarmOsWin", "hdc-target-primary.txt");
        string? configured = ReadConfiguredHdcTarget();
        // 2026-11-16：单设备分支也必须按 devices 清单里的 hdcTarget 选设备。
        // 以前只读顶层 hdcTarget，于是「清单写手机、顶层写平板」时解析到平板、名字却还是手机，
        // 侧栏出现「手机 HOP-AL10 192.168.1.100:38803」。顶层值降级为「没写 devices 时的兜底」。
        // HdcTargetResolver 会按这个 IP 丢弃跨设备候选，不会把手机解析到平板上。
        DeviceEntry? primary = _devices.FirstOrDefault(d => d.Enabled && !string.IsNullOrWhiteSpace(d.HdcTarget));
        if (primary?.HdcTarget is { Length: > 0 } pt)
        {
            configured = pt;
        }
        // 放后台线程：解析里全是同步的 hdc 进程调用（最坏要扫端口），不能卡住 UI 线程
        var res = await Task.Run(() => HdcTargetResolver.ResolveAsync(
            _injector.HdcPath, _hdcTargetOverride, configured, cacheFile)).ConfigureAwait(false);

        AppLog.Block($"hdc 目标解析 → {(res.Ok ? $"{res.Target}（来源 {res.Source}）" : "失败")}",
                     res.Trace);
        if (!res.Ok)
        {
            return null;
        }
        _injector.HdcTarget = res.Target!;
        _lastResolvedModel = res.Model;   // 真机型号，供上层校正 UI 显示
        return res.Target;
    }

    /// <summary>
    /// 定位 harmoswin.json。**不能只看 AppContext.BaseDirectory**：
    /// 2026-11-14 实测把发布 exe 放到 releases/ 后，那里没有配置文件 →
    /// LoadDeviceList 拿到空列表 → 退回单设备 → 手机压根没加载，
    /// 表现是「任务栏上方只剩一个设备、手机图标消失」。
    /// 查找顺序：exe 目录 → 当前工作目录 → 从 exe 目录向上最多 4 层
    ///           （覆盖 bin/Release/net8.0-windows 这种深埋产物）。
    /// </summary>
    private static string? ResolveConfigPath()
    {
        string name = "harmoswin.json";
        string direct = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(direct))
        {
            return direct;
        }
        string cwd = Path.Combine(Environment.CurrentDirectory, name);
        if (File.Exists(cwd))
        {
            return cwd;
        }
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir?.Parent is not null; i++)
            {
                dir = dir.Parent;
                string p = Path.Combine(dir.FullName, name);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }
        catch (Exception)
        {
            // 权限之类的问题不该影响启动
        }
        return null;
    }

    /// <summary>
    /// 读同目录 harmoswin.json 的 device 字段（形如 "192.168.1.100" 或 "192.168.1.100:38803"）。
    /// 目的是让发布后的 exe 支持「双击即连」——不用每次手敲 --auto 参数。
    /// 文件不存在或格式不对就返回 null，退回原有的手动连接流程。
    /// </summary>
    private static string? ReadConfiguredHost()
    {
        try
        {
            string? cfg = ResolveConfigPath();
            if (cfg is null)
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.TryGetProperty("device", out var dev))
            {
                string? v = dev.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
        }
        catch (Exception)
        {
            // 配置坏了不该影响启动，静默退回手动模式
        }
        return null;
    }

    /// <summary>
    /// 读 hdc 无线调试目标（必须带端口）。优先 hdcTarget 字段；
    /// 其次 device 字段里本身带端口的值；都没有则由 InputInjector 的默认值兜底。
    /// 无线调试端口会随设备重启变化，变了只需改 harmoswin.json。
    /// </summary>
    private static string? ReadConfiguredHdcTarget()
    {
        try
        {
            string? cfg = ResolveConfigPath();
            if (cfg is null)
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.TryGetProperty("hdcTarget", out var t))
            {
                string? v = t.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v!.Trim();
                }
            }
            if (doc.RootElement.TryGetProperty("device", out var d))
            {
                string? v = d.GetString();
                if (!string.IsNullOrWhiteSpace(v) && v!.Contains(':'))
                {
                    return v.Trim();
                }
            }
        }
        catch (Exception)
        {
            // 配置坏了不该影响启动
        }
        return null;
    }

    /// <summary>
    /// 读同目录 harmoswin.json 的 devices 数组（双路并行清单）。
    /// 没有该字段、文件缺失、或解析失败 → 列表为空，调用方退回单设备行为
    /// （只用 device / hdcTarget），保持旧配置可用。
    ///
    /// 每台设备分配一对本机 UDP 端口：第 1 台 9010/9020、第 2 台 9011/9021……
    /// 这是双路并行的硬性前提 —— 两台若都发往 9010，SessionManager 会把第二台的
    /// 流投进第一台的会话（按源 IP:端口派发），表现就是「切过去是另一台的静止画面」。
    /// </summary>
    private void LoadDeviceList(string[] argv)
    {
        _devices.Clear();
        try
        {
            string? cfg = ResolveConfigPath();
            if (cfg is null)
            {
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (!doc.RootElement.TryGetProperty("devices", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            int index = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (index >= SessionManager.MaxDevices)
                {
                    break;   // 上限就是 3，多写的忽略
                }
                string? target = Str(item, "hdcTarget");
                string? host = Str(item, "host");
                if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(host))
                {
                    continue;
                }
                bool enabled = !item.TryGetProperty("enabled", out var en)
                               || en.ValueKind != JsonValueKind.False;
                _devices.Add(new DeviceEntry
                {
                    Name = Str(item, "name") ?? $"设备{index + 1}",
                    Model = Str(item, "model"),
                    Host = host ?? HostOf(target!),
                    HdcTarget = target,
                    Enabled = enabled,
                    Note = Str(item, "note"),
                    Capture = Str(item, "capture") ?? "",
                    LocalPort = Hwp.LocalPort + index,
                    DevicePort = Hwp.DevicePort + index
                });
                index++;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"harmoswin.json 的 devices 解析失败，退回单设备：{ex.Message}");
            _devices.Clear();
        }

        if (_devices.Count == 0)
        {
            return;
        }
        // --single <n>：只用清单里第 n 台（1 起），用于隔离验证「两台同时投」里
        // 单台的链路是否健康。默认全部启用。
        string? singleArg = ArgValue(argv, "--single");
        if (int.TryParse(singleArg, out int only) && only >= 1)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                _devices[i].Enabled = i == only - 1;
            }
        }
        AppLog.Info($"设备清单 {_devices.Count} 台：" + string.Join(" | ", _devices.Select(d =>
            $"{d.Name} {d.HdcTarget ?? d.Host} → HWP :{d.LocalPort}/:{d.DevicePort}" +
            (d.Enabled ? "" : "（已禁用）"))));
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    private static string HostOf(string target)
    {
        int c = target.LastIndexOf(':');
        return c > 0 ? target[..c] : target;
    }

    /// <summary>
    /// 一次完整的「拉起采集 → 建会话 → 下发推流」。返回是否成功拿到 CMD_STREAM 回执。
    /// 抽出来是为了能整体重试：失败后清干净再走一遍，而不是让用户看到黑屏后自己去关掉重开。
    ///
    /// 单设备（配置里没写 devices）时走这条路；多设备清单存在时全部交给
    /// BringUpAllDevicesAsync —— 每台一条独立采集桥 + 独立会话，同时进行。
    /// </summary>
    /// <summary>
    /// 把「必须操作 WPF 控件」的动作切回 UI 线程。已在 UI 线程则直接执行。
    /// 2026-11-14：单设备分支在 await ...ConfigureAwait(false) 之后已经在线程池上，
    /// 直接调 AddDevice 会 new Image() → 抛「调用线程必须为 STA」，
    /// 被外层 try/catch 吞掉 → 一个设备卡片都不建（用户看到「平板和手机图标全没了」），
    /// 而且手机那一路因为走了单设备路径压根没被加载。双设备分支早就有这层保护，
    /// 单设备分支一直漏着，直到发布版落到没有配置文件的目录才暴露。
    /// </summary>
    private Task InvokeUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// 这台设备走不走参考采集（ref）。参考采集 = PC 经 hdc 拉 libscreen_casting 的码流、
    /// 在本机采集桥转 HWP 再回环给会话，所以会话连的是 127.0.0.1。
    /// hap = 设备自己的 AVScreenCapture 编码后直接按 HWP 推到 PC 的 LocalPort，
    /// 会话就得以设备真实 IP 建（PC 往它的 DevicePort 发控制包）。
    ///
    /// 为什么要按设备分：手机（HOP-AL10）自带的 /system/bin/uitest 和平板不是同一版本，
    /// 不调用扩展库的 UiTestExtension_OnInit，参考采集在手机上永远零帧（文档补记 66）。
    /// 所以手机只能走 hap，平板继续走 ref —— 同一进程里两路并存。
    /// </summary>
    private bool UseRefCapture(DeviceEntry? d)
    {
        if (!string.IsNullOrEmpty(d?.Capture))
        {
            return !string.Equals(d.Capture, "hap", StringComparison.OrdinalIgnoreCase);
        }
        return _useReferenceCapture;
    }

    /// <summary>
    /// 本机在通往该设备那块网卡上的 IPv4（HAP 采集要告诉设备「推给谁」）。
    /// 用 UDP connect 探路，不依赖网卡枚举顺序，也不会被 Hyper-V / VPN 的虚拟网卡带偏。
    /// </summary>
    private static string LocalIpFor(string deviceHost)
    {
        try
        {
            using var sock = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            sock.Connect(deviceHost, 9);
            return (sock.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "192.168.1.102";   // 历史兜底值；⚠ 102 是手机无线调试地址（用户 2026-11-16 最终裁定），勿当 PC 固定 IP
        }
    }

    /// <summary>
    /// 让手机上的自有 HAP 自己起采：`aa start ... --ps stream 1 --ps peer/peerport/localport`。
    /// 设备端 EntryAbility 解析后跳到会话页并自动开始 AVScreenCapture ——
    /// 但**系统录屏授权框仍要用户本人在手机上点一次「允许」**，这一步绕不过。
    /// 采集起来后设备按 HWP 把帧推到 PC 的 LocalPort（手机是 9011），控制包走 DevicePort（9021）。
    /// </summary>
    private async Task<bool> StartHapCaptureForAsync(DeviceEntry d)
    {
        string target = d.ResolvedTarget;
        string pcIp = LocalIpFor(d.Host);
        string cmd = "aa start -a EntryAbility -b com.harmos.mirror" +
                     $" --ps stream 1 --ps peer {pcIp}" +
                     $" --ps peerport {d.LocalPort} --ps localport {d.DevicePort}";
        var r = await _injector.ShellOnAsync(target, cmd).ConfigureAwait(false);
        AppLog.Info($"[{d.Name}] HAP 采集拉起（{pcIp} → :{d.LocalPort}，控制 :{d.DevicePort}）：" +
                    $"{(r.Ok ? "OK" : "失败")} {r.ElapsedMs:F0}ms");
        if (!r.Ok)
        {
            AppLog.Warn($"[{d.Name}] HAP 拉起输出：{FirstLine(r.Output)}");
        }
        // 拉起只是「请求」，出帧要等用户在手机上点授权，这里不阻塞等待。
        return true;
    }

    private async Task<bool> BringUpStreamAsync()
    {
        if (!_useReferenceCapture)
        {
            SetStatus("采集已就绪，正在建立画面连接…");
            await InvokeUiAsync(() => AddDevice(_autoHost!));
            await Task.Delay(1200);
            var s = _manager.Active();
            if (s is null)
            {
                AppLog.Error("没有活动会话，无法下发推流");
                return false;
            }
            return await StartSessionStreamAsync(s).ConfigureAwait(true);
        }

        // 2026-11-16：按「启用的设备数」而不是清单条数决定是否走双路。
        // 清单里放一台平板 + 一台未启用的手机（只为显示 IP / 可点切换）时，
        // 仍走已验证出画的单设备链路，不因为多写一条配置就改变启动行为。
        if (_devices.Count(d => d.Enabled) > 1)
        {
            return await BringUpAllDevicesAsync().ConfigureAwait(true);
        }

        // 单设备：沿用原有流程（一台一台来，行为与本轮改造前完全一致）。
        DeviceEntry? only = _devices.FirstOrDefault(d => d.Enabled) ?? _devices.FirstOrDefault();
        string? target = only?.ResolvedTarget is { Length: > 0 }
            ? only.ResolvedTarget
            : await ResolveHdcTargetAsync().ConfigureAwait(false);
        if (target is null)
        {
            only!.ResolvedTarget = "";
            SetStatus("未找到可用设备：请确认平板与电脑在同一 Wi-Fi，且平板的「无线调试」是开着的" +
                      $"（详见 {AppLog.LogPath}）");
            return false;
        }
        if (only is not null)
        {
            only.ResolvedTarget = target;
            only.Host = HostOf(target);
            ApplyProbedModel(only, _lastResolvedModel);   // 用真机型号校正 UI 标签
        }

        bool capOk;
        if (UseRefCapture(only))
        {
            capOk = await StartReferenceCaptureForAsync(only, target).ConfigureAwait(true);
        }
        else
        {
            SetStatus("正在拉起设备端采集（手机上需点一次「允许录屏」）…");
            capOk = only is not null && await StartHapCaptureForAsync(only).ConfigureAwait(true);
        }
        AppLog.Info($"采集服务启动: {(capOk ? "OK" : "失败")}");
        if (!capOk)
        {
            return false;
        }
        SetStatus("采集已就绪，正在建立画面连接…");
        // ref 设备仍传 _autoHost，AddDevice 内部会按 UseRefCapture 改写成 127.0.0.1；
        // hap 设备则原样用设备 IP 建会话。
        await InvokeUiAsync(() =>
            AddDevice(_autoHost!, only?.LocalPort ?? Hwp.LocalPort, only));
        await Task.Delay(1200);
        var sess = _manager.Active();
        if (sess is null)
        {
            AppLog.Error("没有活动会话，无法下发推流");
            return false;
        }
        return await StartSessionStreamAsync(sess).ConfigureAwait(true);
    }

    /// <summary>给一个已建好的会话下发 CMD_STREAM 并收尾（收起侧栏 / 贴合窗口）。</summary>
    private async Task<bool> StartSessionStreamAsync(DeviceSession s)
    {
        bool ok = await s.StartStreamAsync().ConfigureAwait(true);
        AppLog.Info($"CMD_STREAM: id={s.Id} host={s.Host} devicePort={s.Port} hwpLocal={s.LocalPort} ok={ok}");
        if (_pages.TryGetValue(s.Id, out var page))
        {
            page.Streaming = ok;
        }
        // 2026-11-16：ApplyChrome / UpdateStreamButton / FitWindowToDevice / SetStatus
        // 全都碰 WPF 控件，必须显式切回 UI 线程。原来只修 UpdateStreamButton 的话，
        // 异常会顺延到下一行 ApplyChrome 上，问题只是换个位置出现。
        await InvokeUiAsync(() =>
        {
            if (ok && !_sidebarCollapsed)
            {
                _sidebarCollapsed = true;
                ApplyChrome();
            }
            UpdateStreamButton();
            if (ok)
            {
                SetStatus("已下发推流，正在等待设备首帧…");
            }
        }).ConfigureAwait(true);
        if (ok)
        {
            await Task.Delay(2500);   // 等 CMD_RESOLUTION 到达后再按真实比例贴合
            await InvokeUiAsync(() => FitWindowToDevice()).ConfigureAwait(true);
        }
        return ok;
    }

    /// <summary>
    /// 双路并行：每台设备一条独立采集桥（独立 UDP 端口 + 独立 fport），
    /// 建各自的会话并同时下发推流。任一台上线即算成功；全失败才返回 false。
    /// 单台失败不影响另一台 —— 这正是用户要的「两台同时投，切换只看不动流」。
    /// </summary>
    private async Task<bool> BringUpAllDevicesAsync()
    {
        var enabled = _devices.Where(d => d.Enabled).ToList();
        if (enabled.Count == 0)
        {
            SetStatus("devices 里没有启用的设备");
            return false;
        }

        // 1) 并行探测每台的实际 hdc target（端口可能已变）。放到后台线程：
        //    HdcTargetResolver 内部全是同步 hdc 进程调用，不能占 UI 线程。
        await Task.Run(() =>
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HarmOsWin");
            foreach (var d in enabled)
            {
                try
                {
                    // 🔴 缓存必须按设备分开。以前两台共用一个 hdc-target.txt，
                    // 手机解析失败时会把平板的地址写进去，下次启动两台都读同一个文件，
                    // 直接把一台设备解析成另一台（2026-11-13 双路 2 秒互踢事故的源头之一）。
                    string cacheFile = Path.Combine(
                        cacheDir, $"hdc-target-{HostTag(d)}.txt");
                    var r = HdcTargetResolver.Resolve(
                        _injector.HdcPath, null, d.HdcTarget, cacheFile);
                    // 解析失败就置空，不能拿配置值兜底：那样会带一个确定连不上的目标去建桥，
                    // 而现在更危险 —— 回落到别的设备会把两条流喂到同一个 daemon 上。
                    d.ResolvedTarget = r.Ok ? r.Target! : "";
                    if (r.Ok)
                    {
                        ApplyProbedModel(d, r.Model);   // 双路同样以真机型号校正显示
                    }
                    AppLog.Info($"[设备] {d.Name} 目标解析 → " +
                                (r.Ok ? $"{r.Target}（来源 {r.Source}，型号 {r.Model}）" : "失败"));
                }
                catch (Exception ex)
                {
                    d.ResolvedTarget = "";
                    AppLog.Warn($"[设备] {d.Name} 目标解析异常：{ex.Message}");
                }
            }
        }).ConfigureAwait(false);

        // 2) 每台起采集桥（严格串行：hdc 并发调用会互踢，这是本机实测结论）。
        //    ⚠ 这段要 10~20 秒，必须留在后台线程（ConfigureAwait(false)）：
        //    占着 Dispatcher 会让窗口连重绘都做不到。状态栏用 PostUi 异步刷。
        foreach (var d in enabled)
        {
            if (string.IsNullOrWhiteSpace(d.ResolvedTarget))
            {
                d.Failed = true;
                AppLog.Warn($"[设备] {d.Name} 无可用 hdc 目标，跳过（检查无线调试端口）");
                PostUi(() => SetStatus($"[{d.Name}] 未找到设备：确认无线调试开着、与电脑同一 Wi-Fi"));
                continue;
            }
            PostUi(() => SetStatus($"[{d.Name}] 正在建立采集桥…"));
            bool ok;
            if (UseRefCapture(d))
            {
                ok = await StartReferenceCaptureForAsync(d, d.ResolvedTarget).ConfigureAwait(false);
            }
            else
            {
                // hap 设备不需要 PC 拉流：只要把它自己的采集拉起来，由设备推流到本机 LocalPort。
                PostUi(() => SetStatus($"[{d.Name}] 正在拉起设备端采集（手机需点一次「允许录屏」）…"));
                ok = await StartHapCaptureForAsync(d).ConfigureAwait(false);
            }
            if (!ok)
            {
                d.Failed = true;
                AppLog.Warn($"[设备] {d.Name} 采集桥启动失败，跳过");
            }
        }

        // 3) 会话必须在 UI 线程上建（要 new WPF 控件）。
        //    ⚠ 这里是关键：上面 await 后延续在线程池上，ConfigureAwait(true) 也救不了
        //    （它依赖当前 SynchronizationContext，后台 await 之后已经没有 UI 上下文）。
        //    所以显式切回 Dispatcher，否则 new Image() 会抛「调用线程必须为 STA」。
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(() => BringUpAllDevicesUiPartAsync(enabled))
                .Task.Unwrap().ConfigureAwait(true);
        }
        return await BringUpAllDevicesUiPartAsync(enabled).ConfigureAwait(true);
    }

    /// <summary>
    /// 双路并行的 UI 部分：建会话 + 建画面 + 下发推流。
    /// 必须在 UI 线程执行（会话里会 new Image / WriteableBitmap / Decoder）。
    /// </summary>
    private async Task<bool> BringUpAllDevicesUiPartAsync(List<DeviceEntry> enabled)
    {
        foreach (var d in enabled)
        {
            if (d.Failed)
            {
                continue;
            }
            // ref 设备：会话接本机采集桥（127.0.0.1）；hap 设备：设备自己推流，会话接设备真实 IP。
            string host = UseRefCapture(d) ? "127.0.0.1" : d.Host;
            AddDevice(host, d.LocalPort, d);
        }
        if (_pages.Count == 0)
        {
            SetStatus("没有设备可用：请确认手机与平板的无线调试都开着、与电脑同一 Wi-Fi" +
                      $"（详见 {AppLog.LogPath}）");
            return false;
        }

        // 4) 逐台等握手完成再发 CMD_STREAM。
        //    不能用「统一 delay 1200ms」：采集桥是串行起的，第二台比第一台晚好几秒，
        //    固定延时会让它还在 ConnectAsync 里就被要求 StartStream（实测 device-2
        //    ok=False，而同一份代码单独跑它是成功的）。
        var sessions = _pages.Values.Select(p => p.Session).ToList();
        var tasks = new List<Task<bool>>();
        foreach (var s in sessions)
        {
            tasks.Add(StartStreamWhenReadyAsync(s));
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(true);

        bool any = results.Any(r => r);
        if (!any)
        {
            AppLog.Warn("双路并行：所有设备都未拿到 CMD_STREAM 回执");
        }
        return any;
    }

    /// <summary>
    /// 等会话握手真正完成，再给它下发 CMD_STREAM（最多重试 3 次）。
    /// 双路并行下两台建链时间不同，固定延时会误判第二台「连不上」。
    /// </summary>
    private async Task<bool> StartStreamWhenReadyAsync(DeviceSession s)
    {
        // 关键：把握手任务**await 掉**，而不是靠延时猜。控制通道单槽，
        // 握手未结束就发 CMD_STREAM 会互相覆盖 ACK（双路实测 device-2 失败根因）。
        Task<bool>? hs = s.HandshakeTask;
        if (hs is not null && !hs.IsCompleted)
        {
            try
            {
                bool hsOk = await hs.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
                AppLog.Info($"[{s.Id}] 握手{(hsOk ? "成功" : "失败")}，继续下发推流");
            }
            catch (TimeoutException)
            {
                AppLog.Warn($"[{s.Id}] 握手 8s 未完成，仍尝试下发推流");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[{s.Id}] 握手异常（忽略）：{ex.GetBaseException().Message}");
            }
        }
        else if (!s.HandshakeDone)
        {
            AppLog.Warn($"[{s.Id}] 没有握手任务记录，直接下发推流");
        }

        for (int i = 1; i <= 3; i++)
        {
            bool ok = await StartSessionStreamAsync(s).ConfigureAwait(true);
            if (ok)
            {
                return true;
            }
            if (i < 3)
            {
                AppLog.Warn($"[{s.Id}] CMD_STREAM 第 {i} 次未获回执，重试");
                await Task.Delay(600).ConfigureAwait(true);
            }
        }
        AppLog.Warn($"[{s.Id}] CMD_STREAM 连续 3 次失败（设备可能锁屏/息屏）");
        return false;
    }

    /// <summary>停掉全部采集桥且不抛异常 —— 重试路径专用，失败原因下面会重新报。</summary>
    private async Task StopCaptureQuietlyAsync()
    {
        foreach (var kv in _referenceCaps.ToList())
        {
            try
            {
                await kv.Value.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"停止采集时异常（忽略）: {ex.Message}");
            }
            _referenceCaps.Remove(kv.Key);
        }
        foreach (var kv in _referenceTouches.ToList())
        {
            try
            {
                await kv.Value.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // 清理阶段不再抛
            }
            _referenceTouches.Remove(kv.Key);
        }
    }

    /// <summary>
    /// 给指定设备起一条采集桥（免录屏授权）+ 一条触控通道。
    /// 端口用该设备专属的一对（localPort / devicePort），互不干扰。
    /// 触控启动失败不阻断投屏：后续注入回退 hdc shell。
    /// </summary>
    private async Task<bool> StartReferenceCaptureForAsync(DeviceEntry? dev, string target)
    {
        int localPort = dev?.LocalPort ?? Hwp.LocalPort;
        int devicePort = dev?.DevicePort ?? Hwp.DevicePort;
        string tag = dev is null ? "" : $"[{dev.Name}] ";

        SetStatus($"{tag}正在自动查找设备（无线调试）…");
        string? so = ResolveScreenCastingSoPath();
        if (so is null)
        {
            // 2026-11-12：不静默回退到录屏模式。ref 连 127.0.0.1、hap 连设备真实 IP，
            // 回退要拆会话换地址重建，且 hap 必须由用户在平板上点一次录屏授权——
            // 静默切换只会从一种黑屏变成另一种黑屏。改为把出口讲清楚。
            SetStatus("[参考采集] 缺少投屏扩展库 screen_casting.so");
            AppLog.Error("[参考采集] 找不到 screen_casting.so（assets/ 或嵌入资源均缺失）。" +
                         "免授权通道不可用，可改用录屏模式：HarmOsWin.exe --capture hap" +
                         "（需在平板上点一次「允许」录屏）");
            return false;
        }
        // 用 BeginInvoke：窗口关闭时 StopAsync 仍会打日志，同步 Invoke 可能
        // 卡在已开始 shutdown 的 Dispatcher 上。
        // ⚠ 必须同时落盘：这条 log 覆盖采集/注入/自愈全过程（tconn、daemon 清理、
        // h2c 握手、探活判死、重连），只显示在底栏的话事后没有任何证据可查。
        // 2026-11-12 排查「kill daemon 后是否自愈」时就吃过这个亏：文件里只有
        // CMD_STREAM 之前的行，之后全静默，无法判断看门狗到底跑没跑。
        Action<string> log = msg =>
        {
            AppLog.Info($"{(dev is null ? "" : $"[{dev.Name}] ")}{msg}");
            // ⚠ BeginInvoke 不能无条件排队：hdc 的每步调用打 2-3 条日志，
            // 采集/触控两路并发时 UI 线程会被塞满；若 UI 线程此刻正被同步等待
            // （.GetResult() 之类）占住，队列只增不减，进一步拖慢一切。
            // 改为「队列已积压就跳过」——状态栏是给人看的，丢帧无害。
            try
            {
                if (Dispatcher.HasShutdownStarted)
                {
                    return;
                }
                // 只在没有积压时更新状态栏，避免 UI 线程成为瓶颈
                _ = Dispatcher.InvokeAsync(() => SetStatus(msg))
                    .Task.ContinueWith(t => { _ = t.Exception; });
            }
            catch
            {
                // 状态栏更新失败不影响功能
            }
        };

        log($"[设备] 已连接 {target}（本机 HWP :{localPort} / 控制 :{devicePort}）");
        log(_lowLatency
            ? $"[解码] MF 低延迟已启用：零补帧，真实帧直达（hold={_holdEnabled}）"
            : $"[解码] MF 低延迟不可用：保留补帧兜底（hold={_holdEnabled}）");

        // 参考项目 HosStreamBridge.start() 的顺序：touch.start() 先、video.start() 后。
        // 两个 singleness daemon 独立共存；视频侧 KillDaemonsAsync 只杀带
        // --extension-name 的那个，不会误伤这里拉起的系统 daemon。
        log("正在准备输入通道…");
        string? agent = ResolveAgentSoPath();
        if (agent is not null)
        {
            var touch = new ReferenceTouchChannel(_injector.HdcPath, target, agent, log);
            bool tOk = await touch.StartAsync().ConfigureAwait(false);
            if (tOk)
            {
                _referenceTouches[target] = touch;
            }
            else
            {
                log("[参考触控] 通道不可用，输入注入回退 hdc shell");
                await touch.StopAsync().ConfigureAwait(false);
            }
        }
        else
        {
            log("[参考触控] 缺少 agent.so，输入注入回退 hdc shell");
        }

        try
        {
            log("正在启动设备采集服务…");
            var cap = new ReferenceCapture(_injector.HdcPath, target, so, log,
                _holdEnabled, _refreshInterval, _frameRate, _bitRate, _devScale,
                localPort, devicePort);
            bool ok = await cap.StartAsync().ConfigureAwait(false);
            if (ok)
            {
                _referenceCaps[target] = cap;
                return true;
            }

            // 2026-11-12：失败原因分类，别只丢一句「启动失败」让人无从下手。
            // 系统升级是这条通道最可能失效的场景（so 实测过 6.5/6.9 通道就绪但永不出帧）。
            log("[参考采集] 免授权通道启动失败。排查顺序：" +
                "① 设备「无线调试」端口是否变了（设备重启必变）；" +
                "② 系统升级后 screen_casting.so 版本失配（本机验证可用版本 6.8-20260801）；" +
                "③ 设备端 --extension-name 守护进程是否被残留进程占住。" +
                "可改用录屏模式：HarmOsWin.exe --capture hap（需在平板上点一次「允许」）");
            AppLog.Error($"[参考采集] {tag}StartAsync 返回 false，免授权通道不可用，详见上方分类提示");
            await cap.StopAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            SetStatus($"[参考采集] {tag}启动异常：{ex.Message}");
            AppLog.Error($"[参考采集] {tag}启动抛异常：{ex.GetType().Name}: {ex.Message}。" +
                         "可改用录屏模式：HarmOsWin.exe --capture hap（需在平板上点一次「允许」）");
            return false;
        }
    }

    /// <summary>
    /// 定位投屏扩展库。顺序：① 输出目录 assets/screen_casting.so（开发态）；
    /// ② 嵌入资源（单文件发布唯一来源）→ 释放到 %LOCALAPPDATA%\HarmOsWin\。
    /// </summary>
    private static string? ResolveScreenCastingSoPath()
    {
        try
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "assets", "screen_casting.so");
            if (File.Exists(beside))
            {
                return beside;
            }
            using var stream = typeof(MainWindow).Assembly
                .GetManifestResourceStream("screen_casting.so");
            if (stream is null)
            {
                return null;
            }
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HarmOsWin");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "screen_casting.so");
            if (!File.Exists(target) || new FileInfo(target).Length != stream.Length)
            {
                using var fs = File.Create(target);
                stream.CopyTo(fs);
            }
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 定位随包分发的 agent.so。
    /// 顺序：① 输出目录旁的 assets/agent.so（开发/非单文件运行）；
    ///       ② 嵌入资源（单文件发布时唯一来源）→ 释放到 %LOCALAPPDATA%\HarmOsWin\agent.so。
    /// 释放是幂等的：文件已存在且长度一致就直接复用，避免每次启动都写盘。
    /// </summary>
    private static string? ResolveAgentSoPath()
    {
        try
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "assets", "agent.so");
            if (File.Exists(beside))
            {
                return beside;
            }

            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("agent.so");
            if (stream is null)
            {
                return null;   // 没有嵌入资源：交由 UitestRpcClient 走「跳过部署」分支
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarmOsWin");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "agent.so");

            if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
            {
                return target;
            }

            using (var fs = File.Create(target))
            {
                stream.CopyTo(fs);
            }
            return target;
        }
        catch (Exception)
        {
            return null;   // 任何失败都退化为「不自动部署」，不影响其它功能
        }
    }

    private void UpdateStreamButton()
    {
        // 2026-11-16：启动期这条会从线程池进来（StartSessionStreamAsync 在 await 之后
        // 已没有 UI 上下文），直接改 _streamBtn.Text 会抛「调用线程无法访问此对象」，
        // 异常被启动流程的 catch 吞掉 → 画面在投、按钮却永远停在「开始推流」。
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(UpdateStreamButton);
            return;
        }
        var s = _manager.Active();
        bool streaming = s is not null && _pages.TryGetValue(s.Id, out var p) && p.Streaming;
        _streamBtn.Text = streaming ? "停止推流" : "开始推流";
        _streamBtn.Enabled = s is not null;
        _lockBtn.Enabled = s is not null;
        _unlockBtn.Enabled = s is not null;
    }

    private void SendNav(byte key)
    {
        var s = _manager.Active();
        if (s is null)
        {
            NavHint("未连接设备，三键不可用");
            return;
        }
        // 点击瞬间就给视觉反馈（蓝色 = 已收到、正在下发），不要等下发结果回来才变色。
        // 之前只有回执/降级完成后才 Flash，用户在这段空窗里会以为没点上而重复点，主观延迟被放大。
        // Flash 内部按 brush 引用判断，后续的成功/失败色会覆盖蓝色且不会被这个定时器误清除。
        _navClickTicks = Environment.TickCount64;
        MarkEchoInput();
        if (_navButtons.TryGetValue(key, out var btn))
        {
            btn.Flash(Theme.Accent, 400);
        }
        _ = SendNavCoreAsync(s, key);
    }

    /// <summary>
    /// 三键下发：Home / Back 走 HWP CONTROL 主路，多任务直接走系统键码。
    ///
    /// 多任务为什么单独走：设备侧无障碍服务的实现是
    /// `AccessibilityElement.performAction('recentTask')`，而该 API 的三个重载在本机 SDK 里
    /// 全部标注 @deprecated since 12（api/application/AccessibilityExtensionContext.d.ts:351/368/384），
    /// 再加上它与侧栏同源、侧栏本身不可用 —— 系统真正的多任务入口是键码
    /// KEYCODE_VIRTUAL_MULTITASK(2210)，所以不再空等一条注定超时的 HWP 回执。
    ///
    /// 旧实现 `_ = s.SendNavAsync(key); _ = InjectNavAsync(key);` 会打两遍
    /// （按一次「返回」退两层），且两条 Task 都丢弃返回值、无人订阅 InjectResult。
    /// </summary>
    private async Task SendNavCoreAsync(DeviceSession s, byte key)
    {
        // 2026-11-13 延迟优化：默认**直连**已验证可用的注入通道，不再先发 HWP CONTROL 主路。
        //
        // 旧路径每次点击 = SendNavAsync(UDP) → 等 InjectResult 回执 700ms → 超时 → 降级注入。
        // 取证：startup.log 从启动至今「注入 ← 设备」回执 **0 条**（DeviceSession 收到必打日志），
        // 设备端无障碍扩展服务在本机 ROM 没有入口，这条主路对三键是注定超时、没有有效负载；
        // 而真正让设备动起来的是降级后的参考触控持久 TCP（SendGesturesAsync 写一次 <1ms）。
        // 也就是说 700ms 里约 99% 是白等。直连后连点也不再互相覆盖 _navPendingKey 单槽。
        // 保留 --nav hwp 逃生口：将来设备端服务可用时可切回「有明确回执」的主路。
        if (!_navUseHwpMain || key == NavKey.Recents)
        {
            await NavFallbackAsync(key).ConfigureAwait(true);
            return;
        }

        _navPendingKey = key;
        _navPendingSession = s.Id;
        _navPendingTicks = Environment.TickCount64;
        var btn = _navButtons.TryGetValue(key, out var b) ? b : null;

        bool sent;
        try
        {
            sent = await s.SendNavAsync(key).ConfigureAwait(true);
        }
        catch (Exception)
        {
            sent = false;
        }
        if (!sent)
        {
            // 链路本身发不出去（UDP CONTROL 不可用），立刻降级，不等超时。
            await NavFallbackAsync(key).ConfigureAwait(true);
            return;
        }
        // 主路已发出，等设备侧 InjectResult 回执；超时未回执则降级。
        await NavAckWaitAsync(key, btn).ConfigureAwait(true);
    }

    /// <summary>主路发出后等设备回执（P50 约 30ms）。超时视为设备端没接住，降级注入通道。</summary>
    private async Task NavAckWaitAsync(byte key, NavKeyButton? btn)
    {
        while (Environment.TickCount64 - _navPendingTicks < NavAckTimeoutMs)
        {
            // OnInjectResult 处理完会把 _navPendingKey 清零，说明回执已到、反馈已给。
            if (_navPendingKey != key)
            {
                return;
            }
            await Task.Delay(20).ConfigureAwait(true);
        }
        // 超时：设备端无障碍服务没跑起来时，HWP 包是发出去了但永远没人回执。
        _navPendingKey = 0;
        btn?.Flash(Theme.Warn, 240);
        await NavFallbackAsync(key).ConfigureAwait(true);
    }

    /// <summary>降级：走 uitest RPC / 按键 / hdc 注入通道（这条有 Ok 回执）。</summary>
    private async Task NavFallbackAsync(byte key)
    {
        NavKeyButton? btn = _navButtons.TryGetValue(key, out var b) ? b : null;
        try
        {
            bool ok = await InjectNavAsync(key).ConfigureAwait(true);
            btn?.Flash(ok ? Theme.Ok : Theme.Danger, ok ? 320 : 480);
            AppLog.Info($"[三键] {NavText(key)} 点击→注入完成 " +
                        $"{Environment.TickCount64 - _navClickTicks}ms ok={ok}");
            if (!ok)
            {
                NavHint($"三键未生效：{NavText(key)} 两条通道都没有确认");
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"[三键] {NavText(key)} 点击→异常 " +
                        $"{Environment.TickCount64 - _navClickTicks}ms {ex.Message}");
            SetStatus($"[三键] {NavText(key)} 下发异常：{ex.Message}");
            NavHint($"三键未生效：{ex.Message}");
            btn?.Flash(Theme.Danger, 480);
        }
    }

    /// <summary>
    /// 设备端注入回执（HWP CMD_INJECT_RESULT）。之前 PC 端只是把 ok/errCode 打进日志，
    /// 界面上三键点了像没点。这里把结果翻译成用户能看懂的一句话。
    /// </summary>
    private void OnInjectResult(string sessionId, InjectReport r)
    {
        if (_navPendingKey == 0 || sessionId != _navPendingSession)
        {
            return;
        }
        if (r.Stage != _navPendingKey)
        {
            return;
        }
        byte key = _navPendingKey;
        _navPendingKey = 0;
        NavKeyButton? btn = _navButtons.TryGetValue(key, out var b) ? b : null;
        string name = NavText(key);

        if (r.Ok)
        {
            btn?.Flash(Theme.Ok);
            SetStatus($"[三键] {name} 已执行 {r.ElapsedUs / 1000.0:F0}ms");
            return;
        }
        btn?.Flash(Theme.Danger, 480);
        // errCode = -2 是设备端 LowFreqBridge 的「250ms 内无响应」约定值
        // （见 tablet/.../LowFreqBridge.ets send() 的超时分支）。
        // 2026-11-16 更正：这条主路依赖的「无障碍扩展服务」在鸿蒙 7 上**根本不存在**
        // （用户 21:28 最终确认；取证 hidumper -s AccessibilityManagerService -a '-c' → client num: 0），
        // 所以 -2 不是「用户忘了开服务」，而是**必然超时**。只能提示去掉 --nav hwp 走直连注入，
        // 不要再把用户引向「设置 → 辅助功能 → 扩展服务」这个不存在的菜单。
        string why = r.ErrCode == -2
            ? "注入主路无回执（鸿蒙 7 无无障碍扩展服务）：去掉 --nav hwp 即走直连注入"
            : $"设备拒绝执行（code={r.ErrCode}）";
        SetStatus($"[三键] {name} 失败：{why}");
        NavHint($"三键未生效：{why}");
    }

    /// <summary>底部三键胶囊内的短提示：纯画面模式下状态栏隐藏，失败原因必须显示在画面里。</summary>
    private void NavHint(string text)
    {
        if (_navHint is null)
        {
            return;
        }
        _navHint.Text = text;
        _navHint.Visibility = Visibility.Visible;
        _navHintTimer?.Stop();
        _navHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _navHintTimer.Tick += (_, _) =>
        {
            _navHintTimer?.Stop();
            _navHintTimer = null;
            if (_navHint is not null)
            {
                _navHint.Visibility = Visibility.Collapsed;
            }
        };
        _navHintTimer.Start();
    }

    /// <summary>
    /// 三键真实注入。参考模式走 ReferenceTouchChannel（参考项目 touch 通道，运行期零 hdc）；
    /// 自有 HAP 模式走 UitestRpcClient。两路都不可用时回退 hdc shell。
    /// </summary>
    private async Task<bool> InjectNavAsync(byte key)
    {
        // 多任务：与抓流模式无关，只有「底部上滑悬停」这一条通道有效。
        // 实测（2026-11-11 00:29，MatePad Mini / HarmonyOS 7.0.0.111，手势导航）：
        //   uinput -K 2210 / 2717、uitest uiInput keyEvent 2210 全部零效果（系统不消费），
        //   而同一条 uinput 用 -m 上滑悬停稳定进最近任务卡片视图。
        if (key == NavKey.Recents)
        {
            var sess = _manager.Active();
            int pw = (int)((sess?.Width ?? 800) * _touchScale);
            int ph = (int)((sess?.Height ?? 1280) * _touchScale);
            var touchCh = ActiveTouch();
            bool gOk = touchCh is not null
                ? await touchCh.PressRecentsGestureAsync(pw, ph).ConfigureAwait(false)
                : (await _injector.ShellAsync(ReferenceTouchChannel.BuildRecentsGesture(pw, ph))
                    .ConfigureAwait(false)).Ok;
            PostUi(() => SetStatus(gOk
                ? $"[多任务] 上滑悬停手势已下发（{pw}x{ph}）"
                : $"[多任务] 手势下发失败（{pw}x{ph}）"));
            return gOk;
        }

        if (_useReferenceCapture)
        {
            // Back/Home 走参考触控通道的持久 TCP（运行期零 hdc）。
            var navTouch = ActiveTouch();
            if (navTouch is { IsHealthy: true } &&
                await navTouch.PressNavAsync(key).ConfigureAwait(false))
            {
                PostUi(() => SetStatus($"[注入 参考通道] {NavText(key)}"));
                return true;
            }
            var fallback = await _injector.NavAsync(key).ConfigureAwait(false);
            PostUi(() =>
                SetStatus($"[注入 hdc {fallback.ElapsedMs:F1}ms{(!fallback.Ok ? " 失败" : string.Empty)}] {NavText(key)}"));
            return fallback.Ok;
        }
        var rpc = await EnsureRpcAsync().ConfigureAwait(false);
        if (rpc is not null)
        {
            var r = await rpc.PressNavAsync(key).ConfigureAwait(false);
            if (r.Ok)
            {
                PostUi(() => SetStatus($"[注入 RPC {r.ElapsedMs:F1}ms] {NavText(key)}"));
                return true;
            }
        }
        var t = await _injector.NavAsync(key).ConfigureAwait(false);
        PostUi(() =>
            SetStatus($"[注入 hdc {t.ElapsedMs:F1}ms{(!t.Ok ? " 失败" : string.Empty)}] {NavText(key)}"));
        return t.Ok;
    }

    private static string NavText(byte key) => key switch
    {
        NavKey.Back => "返回",
        NavKey.Home => "主页",
        NavKey.Recents => "多任务",
        _ => key.ToString()
    };

    // ── 画面点击转发 ──────────────────────────────────────────────────

    private Page? ActivePage() =>
        _manager.Active() is { } s && _pages.TryGetValue(s.Id, out var p) ? p : null;

    /// <summary>当前活动页对应的设备清单项（按设备控制端口匹配）。单设备模式下为 null。</summary>
    private DeviceEntry? ActiveDevice()
    {
        var s = _manager.Active();
        if (s is null || _devices.Count == 0)
        {
            return null;
        }
        return _devices.FirstOrDefault(d => d.DevicePort == s.Port);
    }

    /// <summary>当前活动设备名，用于状态栏与日志（单设备模式回退「设备」）。</summary>
    private string ActiveDeviceName() => ActiveDevice()?.Name is { Length: > 0 } n ? n : "设备";

    /// <summary>按局域网 IP 反查设备名（剪贴板包带的是源 IP，状态栏要显示人能认的名字）。</summary>
    private string DeviceNameByIp(string ip)
    {
        var d = _devices.FirstOrDefault(x => string.Equals(x.Host, ip, StringComparison.Ordinal));
        return d?.Name is { Length: > 0 } n ? $"{n} " : "设备 ";
    }

    /// <summary>按设备分桶的键：多设备用会话 id，单设备统一空串（保持旧的单份缓存语义）。</summary>
    private string DeviceKey() => ActiveDevice()?.Id ?? "";

    /// <summary>
    /// 让所有 hdc 类动作跟着「当前活动设备」走。
    ///
    /// 为什么必须有这一步：三键与触控在参考采集模式下是每台各一条 ReferenceTouchChannel
    /// （按 target 建），天生分设备；而剪贴板上下行、敲字回退、亮度调节全部共用同一个
    /// InputInjector，它只认一个 HdcTarget —— 启动时钉在哪台就永远发往那台。
    /// 结果就是「切到手机了点 ↓，弹出的却是平板的剪贴板浮窗」。
    /// </summary>
    private void SyncInjectorToActiveDevice()
    {
        var d = ActiveDevice();
        if (d is null || string.IsNullOrWhiteSpace(d.ResolvedTarget))
        {
            return;   // 单设备模式 / 还没解析出目标：保持原有行为
        }
        if (string.Equals(_injector.HdcTarget, d.ResolvedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _injector.HdcTarget = d.ResolvedTarget;   // setter 内部会重建常驻 hdc 会话

        // 自有 HAP 模式的注入 RPC 是按 target 建的长连接，目标变了必须丢弃重建，
        // 否则会继续把键值发给上一台设备（现象是「切过去点返回，退的是另一台」）。
        var old = _rpc;
        _rpc = null;
        _rpcBoot = null;
        if (old is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await old.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 关闭旧连接失败不影响切换本身
                }
            });
        }
        AppLog.Info($"[设备] 剪贴板 / 注入目标 → {d.Name}（{d.ResolvedTarget}）");
    }

    private void CancelPendingTap()
    {
        _tapTimer?.Stop();
        _tapPendingX = -1;
        _tapPendingY = -1;
    }

    private void BeginTapCandidate(MouseButtonEventArgs e)
    {
        var page = ActivePage();
        if (page is null || !page.Streaming)
        {
            return;
        }
        CancelPendingTap();
        _tapDownOnImage = e.GetPosition(page.View);
        _tapDragging = false;
        _dragSession = null;

        // 这一下点击同时也是「我要在这里打字」的意图：把键盘焦点交给隐藏输入宿主，
        // 输入法才会激活（WPF 没有可编辑控件时 IME 根本不会起来，中文打不出来）。
        // 只在直通开启且已锚定时做，避免用户没打算输入时窗口悄悄抢走键盘。
        if (_kbdPassthrough)
        {
            _kbdSink?.Focus();
        }

        // 按下即下发 touchDown（不再等抬起）：设备侧的触摸在按下瞬间就成立，
        // 这样「按住—拖动—松开」的全程都是连续的，不会有中间断层。
        // 若最终判定是「原地单击」，抬起时补一个 touchUp 即可，等价于一次完整点击。
        if (TryMapToDevice(page, _tapDownOnImage, out int dx, out int dy))
        {
            // 记住这一下：PC→设备推送文本时，uitest inputText 的 x y 必须是
            // 「设备端最后一次触摸坐标」，否则整段被丢弃。这里存的就是那个值。
            // 按设备分开存：两台的坐标空间不同，混用会把字敲到另一台/错的点上。
            _lastTapByDevice[DeviceKey()] = (dx, dy);
            _dragSession = new DragSession { Page = page, LastX = dx, LastY = dy };
            MarkEchoInput();
            _ = InjectPointerAsync("touchDown", dx, dy, 0);
        }
    }

    /// <summary>
    /// 拖动实时转发。跨过容差阈值后进入拖动模式：
    /// 之后每个 MouseMove 都下发一条 touchMove（带坐标去重与合帧），
    /// 抬起时发 touchUp。实测 touchMove 往返 p50 9.6ms，够撑 60Hz 以上。
    /// </summary>
    private void TrackTapDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var page = ActivePage();
        if (page is null)
        {
            return;
        }
        var p = e.GetPosition(page.View);
        if (!_tapDragging)
        {
            if (Math.Abs(p.X - _tapDownOnImage.X) <= TapMoveTolerancePx &&
                Math.Abs(p.Y - _tapDownOnImage.Y) <= TapMoveTolerancePx)
            {
                return;
            }
            _tapDragging = true;   // 越过阈值 → 进入拖动
        }
        if (_dragSession is null)
        {
            return;
        }
        if (!TryMapToDevice(page, p, out int dx, out int dy))
        {
            return;   // 拖到黑边上，不转发
        }
        // 去重：同一设备像素不重复下发（鼠标抖动会产生大量同点事件）
        if (dx == _dragSession.LastX && dy == _dragSession.LastY)
        {
            return;
        }
        _dragSession.LastX = dx;
        _dragSession.LastY = dy;
        _dragSession.PendingX = dx;
        _dragSession.PendingY = dy;
        FlushDragMove(_dragSession);
    }

    /// <summary>
    /// 当前活动会话对应的采集桥（双路并行下必须按设备取，不能取"某一个"）。
    /// 多设备时按会话的本地端口反查设备条目，单设备时退回唯一实例。
    /// </summary>
    private ReferenceCapture? ActiveCapture()
    {
        var s = _manager.Active();
        if (s is null)
        {
            return null;
        }
        if (_devices.Count > 1)
        {
            var dev = _devices.FirstOrDefault(d => d.DevicePort == s.Port);
            return dev is not null && _referenceCaps.TryGetValue(dev.ResolvedTarget, out var c)
                ? c
                : null;
        }
        return _referenceCaps.Values.FirstOrDefault();
    }

    /// <summary>当前活动会话对应的触控通道。取值口径与 ActiveCapture 一致。</summary>
    private ReferenceTouchChannel? ActiveTouch()
    {
        var s = _manager.Active();
        if (s is null)
        {
            return null;
        }
        if (_devices.Count > 1)
        {
            var dev = _devices.FirstOrDefault(d => d.DevicePort == s.Port);
            return dev is not null && _referenceTouches.TryGetValue(dev.ResolvedTarget, out var t)
                ? t
                : null;
        }
        return _referenceTouches.Values.FirstOrDefault();
    }

    /// <summary>
    /// 把待发的 drag 位移刷出去。用「在途标记 + 完成后再补一帧」的方式合帧：
    /// 鼠标事件可能比网络往返快得多（120Hz+），逐条阻塞会把 UI 线程拖住，
    /// 所以同一时刻只允许一条 touchMove 在途，多余位移折叠成最新坐标。
    /// </summary>
    private void FlushDragMove(DragSession s)
    {
        if (s.InFlight)
        {
            s.Dirty = true;
            return;
        }
        s.InFlight = true;
        s.Dirty = false;
        _ = FlushDragLoopAsync(s);
    }

    private async Task FlushDragLoopAsync(DragSession s)
    {
        while (true)
        {
            int x = s.PendingX;
            int y = s.PendingY;
            await InjectPointerAsync("touchMove", x, y, 0).ConfigureAwait(false);
            if (!s.Dirty)
            {
                break;
            }
            s.Dirty = false;   // 期间又来了新坐标 → 再发一帧最新位置
        }
        s.InFlight = false;
    }

    private void EndTapCandidate(MouseButtonEventArgs e)
    {
        var page = ActivePage();
        var session = _dragSession;
        _dragSession = null;
        if (page is null || !page.Streaming || session is null)
        {
            return;
        }

        if (_tapDragging)
        {
            // 拖动收尾：抬起位置就是最后一次 move 的位置，补 touchUp 结束手势
            _ = InjectPointerAsync("touchUp", session.LastX, session.LastY, 0);
            _tapDragging = false;
            return;
        }

        // 原地点击：按下时已发 touchDown，这里补 touchUp 即完成一次点击。
        // 不再等 260ms 双击窗口 —— 那会把每次点击都拖慢 260ms，是最主要的「不跟手」来源。
        // 切换纯画面 / 控制条只走键盘 F11 / Esc 与工具栏按钮，不再占用任何鼠标手势。
        _ = InjectPointerAsync("touchUp", session.LastX, session.LastY, 0);
    }

    /// <summary>
    /// 下发一条指针手势（touchDown/touchMove/touchUp）。
    /// 参考模式走 ReferenceTouchChannel；自有 HAP 模式走 uitest RPC；两路失败都回退 hdc。
    /// </summary>
    private async Task InjectPointerAsync(string api, int x, int y, int holdMs)
    {
        if (_useReferenceCapture)
        {
            // 参考模式只发启动期建好的持久 TCP，绝不调用 EnsureRpcAsync（会杀扩展 daemon）。
            var ptrTouch = ActiveTouch();
            if (ptrTouch is { IsHealthy: true } &&
                await ptrTouch.TouchAsync(api, x, y, holdMs != 0 ? (int?)holdMs : null)
                    .ConfigureAwait(false))
            {
                if (api != "touchMove")
                {
                    PostUi(() => SetStatus($"[{api} 参考通道] ({x},{y})"));
                }
                return;
            }
            var refEv = new InputEvent
            {
                Type = EventKind.Touch,
                Action = api == "touchDown" ? EventAction.Down : EventAction.Up,
                X = (ushort)x,
                Y = (ushort)y
            };
            _ = await _injector.InjectAsync(refEv).ConfigureAwait(false);
            return;
        }
        var rpc = await EnsureRpcAsync().ConfigureAwait(false);
        if (rpc is not null)
        {
            var (ms, ok) = await rpc.PointerAsync(api, x, y, holdMs).ConfigureAwait(false);
            if (ok)
            {
                if (api != "touchMove")
                {
                    PostUi(() => SetStatus($"[{api} RPC {ms:F0}ms] ({x},{y})"));
                }
                return;
            }
        }
        var ev = new InputEvent
        {
            Type = EventKind.Touch,
            Action = api == "touchDown" ? EventAction.Down : EventAction.Up,
            X = (ushort)x,
            Y = (ushort)y
        };
        _ = await _injector.InjectAsync(ev).ConfigureAwait(false);
    }

    /// <summary>滚轮转发：参考模式按参考项目 gui/app.py 转 swipe，自有 HAP 模式转 Axis 手势。</summary>
    private void ForwardWheel(MouseWheelEventArgs e)
    {
        var page = ActivePage();
        if (page is null || !page.Streaming)
        {
            return;
        }
        if (!TryMapToDevice(page, e.GetPosition(page.View), out int dx, out int dy))
        {
            return;
        }
        MarkEchoInput();
        // 仅自有 HAP 模式用：设备侧 Axis 手势的 value 表示方向与格数。
        string api = e.Delta > 0 ? "AxisUp" : "AxisDown";
        // 参考项目 gui/app.py:476-484：滚轮转成一次 swipe（±80px, 0.15s），
        // 不用 AxisUp/AxisDown —— 参考项目的 Gestures 通道只有 Down/Move/Up。
        // dy 现在是物理屏幕坐标，位移与上限都必须用同一坐标系，
        // 否则滚轮会被夹在采集尺寸 1280 处（devscale 2 时只能滑上半屏）。
        int delta = (int)((e.Delta > 0 ? -80 : 80) * _touchScale);
        int sy = Math.Max(0, dy - delta);
        double touchH = page.Session.Height * _touchScale;
        int ey = (int)Math.Min(touchH - 1, dy + delta);
        _ = ForwardWheelAsync(api, dx, dy, sy, ey);
        e.Handled = true;
    }

    private async Task ForwardWheelAsync(string api, int x, int y, int sy, int ey)
    {
        if (_useReferenceCapture)
        {
            if (ActiveTouch() is { IsHealthy: true } swipeCh)
            {
                await swipeCh.SwipeAsync(x, sy, x, ey, 150).ConfigureAwait(false);
            }
            return;   // 参考模式下不预热自有 RPC（会杀掉扩展 daemon）
        }
        var rpc = await EnsureRpcAsync().ConfigureAwait(false);
        if (rpc is null)
        {
            return;
        }
        await rpc.PointerAsync(api, x, y, 1).ConfigureAwait(false);
    }

    /// <summary>
    /// 画面控件坐标 → 设备「物理屏幕」像素（注入坐标系）。
    /// 铺满（Stretch.Fill）：整块控件都是画面，直接线性映射，没有 letterbox 偏移；
    /// 等比（Stretch.Uniform）：控件内会留边，要按缩放比与居中偏移反算。
    /// 注意 s.Width/s.Height 是采集端 -scale 后的编码尺寸，不是物理屏幕尺寸，
    /// 末尾必须乘 _touchScale 换算回去，否则点击整体缩到左上角。
    /// </summary>
    private bool TryMapToDevice(Page page, Point p, out int devX, out int devY)
    {
        devX = 0;
        devY = 0;
        var s = page.Session;
        double cw = page.View.ActualWidth;
        double ch = page.View.ActualHeight;
        if (cw <= 1 || ch <= 1 || s.Width <= 0 || s.Height <= 0)
        {
            return false;
        }

        double nx, ny;
        if (_fillMode)
        {
            nx = p.X / cw;
            ny = p.Y / ch;
        }
        else
        {
            double scale = Math.Min(cw / s.Width, ch / s.Height);
            double contentW = s.Width * scale;
            double contentH = s.Height * scale;
            double offX = (cw - contentW) / 2;
            double offY = (ch - contentH) / 2;
            nx = (p.X - offX) / contentW;
            ny = (p.Y - offY) / contentH;
        }
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1)
        {
            return false;
        }
        // 用 (宽-1) 归一：保证最右/最下边沿正好落在最后一个像素上。
        // 注入通道（uitest Gestures / hdc）用的是设备物理屏幕坐标，与采集缩放无关：
        // devscale 2 时编码帧 800x1280，物理屏 1600x2560，必须乘回 2。
        double touchW = s.Width * _touchScale;
        double touchH = s.Height * _touchScale;
        devX = (int)Math.Round(nx * (touchW - 1));
        devY = (int)Math.Round(ny * (touchH - 1));
        return true;
    }

    /// <summary>一次真实点击：优先 uitest RPC（p50 ~75ms），不可用回退 hdc shell（~200ms+）。</summary>
    private async Task InjectTapAsync(int x, int y)
    {
        if (x < 0 || y < 0)
        {
            return;
        }
        long now = Environment.TickCount64;
        if (now - _lastTapInjectedMs < TapMinIntervalMs)
        {
            return; // 连点节流：兜住手抖造成的密集重复点击（双击现在也会正常注入两次）
        }
        _lastTapInjectedMs = now;

        if (_useReferenceCapture)
        {
            if (ActiveTouch() is { IsHealthy: true } &&
                await ActiveTouch()!.TapAsync(x, y, 50).ConfigureAwait(false))
            {
                PostUi(() => SetStatus($"[点击 参考通道] ({x},{y})"));
                return;
            }
            var refEv = new InputEvent
            {
                Type = EventKind.Touch,
                Action = EventAction.Up,
                X = (ushort)x,
                Y = (ushort)y
            };
            _ = await _injector.InjectAsync(refEv).ConfigureAwait(false);
            return;
        }

        var rpc = await EnsureRpcAsync().ConfigureAwait(false);
        if (rpc is not null)
        {
            var r = await rpc.TapAsync(x, y).ConfigureAwait(false);
            PostUi(() => SetStatus(
                $"[点击 RPC {r.ElapsedMs:F0}ms] ({x},{y}){(r.Ok ? string.Empty : " 失败")}"));
            if (r.Ok)
            {
                return;
            }
        }
        var ev = new InputEvent
        {
            Type = EventKind.Touch,
            Action = EventAction.Down,
            X = (ushort)x,
            Y = (ushort)y
        };
        var t = await _injector.InjectAsync(ev).ConfigureAwait(false);
        PostUi(() => SetStatus(
            $"[点击 hdc {t.ElapsedMs:F0}ms] ({x},{y}){(t.Ok ? string.Empty : " 失败")}"));
    }

    /// <summary>
    /// 点击映射自测：把归一化坐标当作用户点在画面上的位置，跑一遍与真实点击完全相同的
    /// 映射（TryMapToDevice）+ 注入（InjectTapAsync），把中间量落盘，便于核对是不是映射错了。
    /// 需要 --auto 先连上设备（否则拿不到设备分辨率与实际控件尺寸）。
    /// </summary>
    private async Task TapSelfTestAsync(string spec, string? outPath, int delayMs)
    {
        var lines = new List<string>();
        string[] parts = spec.Split(',');
        if (parts.Length != 2 || !double.TryParse(parts[0], out double nx) ||
            !double.TryParse(parts[1], out double ny))
        {
            lines.Add($"bad arg: {spec}（应为 0.5,0.5）");
            await WriteAsync(outPath, lines).ConfigureAwait(false);
            return;
        }
        await Task.Delay(delayMs).ConfigureAwait(false); // 等 CMD_RESOLUTION 与布局收敛（可用 --tap-delay 放宽）

        var page = ActivePage();
        if (page is null)
        {
            lines.Add("no active page");
            await WriteAsync(outPath, lines).ConfigureAwait(false);
            return;
        }
        var s = page.Session;
        double cw = page.View.ActualWidth;
        double ch = page.View.ActualHeight;
        lines.Add($"frame={s.Width}x{s.Height} touch={s.Width * _touchScale:F0}x{s.Height * _touchScale:F0} " +
                  $"touchScale={_touchScale:F2} ctrl={cw:F1}x{ch:F1} fillMode={_fillMode}");

        // 由归一化坐标反推控件坐标（与 TryMapToDevice 互为逆运算）
        Point pt;
        if (_fillMode)
        {
            pt = new Point(nx * cw, ny * ch);   // Fill：整块控件都是画面
        }
        else
        {
            double scale = Math.Min(cw / s.Width, ch / s.Height);
            double contentW = s.Width * scale;
            double contentH = s.Height * scale;
            double offX = (cw - contentW) / 2;
            double offY = (ch - contentH) / 2;
            pt = new Point(offX + nx * contentW, offY + ny * contentH);
        }

        bool mapped = TryMapToDevice(page, pt, out int dx, out int dy);
        lines.Add($"norm=({nx},{ny}) point=({pt.X:F1},{pt.Y:F1}) mapped={mapped} dev=({dx},{dy})");

        if (!mapped)
        {
            await WriteAsync(outPath, lines).ConfigureAwait(false);
            return;
        }
        var sw = Stopwatch.StartNew();
        await InjectTapAsync(dx, dy).ConfigureAwait(false);
        lines.Add($"injectMs={sw.ElapsedMilliseconds} rpcReady={_rpc?.IsConnected} connects={_rpc?.ConnectCount}");

        await WriteAsync(outPath, lines).ConfigureAwait(false);
        PostUi(() => SetStatus($"[tap-test] 已注入 ({dx},{dy})，明细见 {outPath ?? "控制台"}"));
    }

    /// <summary>
    /// 「取剪贴板」自测：走产品真实路径跑一次，输出端到端耗时与内容。
    /// 存在的意义是把用户主观感受（「动画太长」）变成可回归的数字，
    /// 免得以后改了别的地方又悄悄变慢却没人发现。
    /// </summary>
    /// <summary>
    /// 剪贴板上行自测（--clip-push &lt;png&gt;）：把指定 PNG 放进 Windows 剪贴板，
    /// 再调用与「↑ 上传」按钮完全相同的入口，验证图片下行整条链。
    /// 只用于自动化验证，不参与正常路径。
    /// </summary>
    private async Task ClipPushSelfTestAsync(string pngPath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(pngPath));
            bmp.EndInit();
            bmp.Freeze();
            Clipboard.SetImage(bmp);
            AppLog.Info($"[clip-push] Windows 剪贴板已放入 {Path.GetFileName(pngPath)} " +
                        $"{bmp.PixelWidth}x{bmp.PixelHeight}");
            await Task.Delay(400).ConfigureAwait(true);
            await SendClipboardToTabletAsync().ConfigureAwait(true);
            AppLog.Info("[clip-push] 上传流程执行完毕");
        }
        catch (Exception e)
        {
            AppLog.Info($"[clip-push] 失败：{FirstLine(e.Message)}");
        }
    }

    private async Task ClipPullSelfTestAsync(string? outPath)
    {
        var lines = new List<string>();
        _clipPullDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        Task pull = PullClipboardAsync(null);
        Task<string> got = _clipPullDone.Task;
        // 20s 而不是 12s：慢机（手机折叠屏）光等 READY 就要 5~6s，加点击与回传，
        // 12s 会把「其实成功了」判成超时（2026-11-13 实测：手机 12s 超时但 READY 已到）。
        Task done = await Task.WhenAny(got, Task.Delay(20000)).ConfigureAwait(true);
        await pull.ConfigureAwait(true);
        long ms = sw.ElapsedMilliseconds;
        lines.Add($"elapsed={ms}ms");
        // PullClipboardAsync 内部自己的分段计时（aa start / READY / click）也抄进来，
        // 这样一眼能看出慢在哪一段，不用去翻 startup.log。
        if (ReferenceEquals(done, got) && got.Status == TaskStatus.RanToCompletion)
        {
            string text = got.Result;
            lines.Add($"ok textLen={text.Length}");
            lines.Add($"text={text.Replace("\r", "").Replace("\n", "\\n")}");
            SetStatus($"[clip-test] 端到端 {ms}ms，收到 {text.Length} 字");
        }
        else
        {
            lines.Add("timeout: 20s 内没收到回传");
            SetStatus($"[clip-test] 12s 超时，未收到回传（详见 {AppLog.LogPath}）");
        }
        await WriteAsync(outPath, lines).ConfigureAwait(true);
    }

    private static async Task WriteAsync(string? path, List<string> lines)
    {
        if (path is null)
        {
            foreach (var l in lines)
            {
                Console.WriteLine(l);
            }
            return;
        }
        await File.WriteAllLinesAsync(path, lines).ConfigureAwait(false);
    }

    /// <summary>
    /// 惰性建链：设备侧先拉起 agent daemon（幂等，已在跑会立刻返回），再做 tconn → fport → TCP 长连接。
    /// 整个过程并发调用只跑一次；失败后下次调用会重试。
    /// </summary>
    private async Task<UitestRpcClient?> EnsureRpcAsync()
    {
        if (_rpcBoot is null)
        {
            _rpcBoot = BootRpcAsync();
        }
        bool ok = await _rpcBoot.ConfigureAwait(false);
        if (!ok)
        {
            _rpcBoot = null; // 允许下次重试
        }
        return ok ? _rpc : null;
    }

    private async Task<bool> BootRpcAsync()
    {
        try
        {
            _rpc ??= new UitestRpcClient(_injector.HdcPath, _injector.HdcTarget)
            {
                // agent.so 随 PC 端分发：开发时在输出目录 assets/，单文件发布时从嵌入资源释放
                AgentSoPath = ResolveAgentSoPath()
            };
            if (_rpc.IsConnected)
            {
                return true;
            }
            // 顺序由 UitestRpcClient 内部保证：先部署 agent.so，再拉 daemon，最后 fport。
            // 这里不再单独 `uitest start-daemon` —— 那会在 so 就位之前把空壳进程拉起来。
            bool ok = await _rpc.ConnectAsync().ConfigureAwait(false);
            string agent = _rpc.AgentStatus;
            PostUi(() => SetStatus(ok
                ? $"[注入通道] uitest RPC 就绪（agent.so {agent}）"
                : $"[注入通道] RPC 不可用，回退 hdc shell（agent.so {agent}）"));
            return ok;
        }
        catch (Exception ex)
        {
            PostUi(() => SetStatus($"[注入通道] RPC 启动异常：{ex.Message}"));
            return false;
        }
    }

    private void SetLocked(string id, bool locked)
    {
        if (!_pages.TryGetValue(id, out var p))
        {
            return;
        }
        p.LockOverlay.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(locked ? $"[{id}] 隐私遮罩：已遮挡本机画面（设备不受影响）" : $"[{id}] 遮罩已取消，恢复实时画面");
    }

    private static string? ArgValue(string[] argv, string name)
    {
        int i = Array.IndexOf(argv, name);
        return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
    }

    private long _mLastPres;
    private long _mLastTick;

    /// <summary>每秒一行 JSONL：PC 窗口真实上屏帧率与各阶段耗时，用于自动化判流畅度。</summary>
    private void DumpMetrics()
    {
        if (_metricsWriter is null)
        {
            return;
        }
        var session = _manager.Active();
        if (session is null || _pages.Count == 0)
        {
            return;
        }
        var page = _pages.Values.First();
        long pres = page.Surface.Presented;
        long tick = Environment.TickCount64;
        double fps = _mLastTick > 0 && tick > _mLastTick ? (pres - _mLastPres) * 1000.0 / (tick - _mLastTick) : 0;
        _mLastPres = pres;
        _mLastTick = tick;
        var st = session.Stats();
        string dec = page.Session.Decoder is MediaFoundationDecoder mf
            ? $"{mf.DecodedFrames}|{mf.Errors}|{mf.AvgConvertMs:F2}|{mf.AvgFeedMs:F2}"
            : "n/a";
        string crop = page.Session.Decoder is MediaFoundationDecoder mf2
            ? $"{mf2.CroppedFrames}|{mf2.OutWidth}x{mf2.OutHeight}"
            : "n/a";
        _metricsWriter.WriteLine(
            $"{{\"ms\":{tick},\"presentFps\":{fps:F1},\"presented\":{pres},\"presentDropped\":{page.Surface.Dropped}," +
            $"\"writeMs\":{page.Surface.AvgPresentMs:F2},\"frames\":{st.Frames},\"asmDropped\":{st.Dropped}," +
            $"\"packets\":{st.Packets},\"videoLost\":{st.VideoLost},\"rto\":{st.RtoMs}," +
            $"\"queueDropped\":{session.DecodeQueueDropped}," +
            $"\"latencyMs\":{session.AvgFrameLatencyMs:F1},\"dec\":\"{dec}\",\"crop\":\"{crop}\"}}");
    }

    /// <summary>窗口自快照（RenderTargetBitmap），无需外部截屏工具。</summary>
    private void CaptureWindow(string path)
    {
        try
        {
            var root = (FrameworkElement)Content;
            int w = (int)root.ActualWidth;
            int h = (int)root.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                AppLog.Warn($"[快照] 窗口尺寸无效 {w}x{h}，跳过");
                return;
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
            AppLog.Info($"[快照] 已写出 {path}（{w}x{h}）");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[快照] 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 只在静止态起探针：动态画面里下一帧本来就会到，测出来的只是帧间隔而不是设备响应延迟。
    /// 三键、画面按下、滚轮三个入口共用。
    /// </summary>
    private void MarkEchoInput()
    {
        var page = ActivePage();
        if (page is null || !page.Streaming)
        {
            return;
        }
        if (page.SlowScore < EchoIdleScore)
        {
            return;   // 画面还在动，本次不测
        }
        Interlocked.Exchange(ref _echoInputTicks, Environment.TickCount64);
    }

    /// <summary>WpfSurface 每上屏一帧调一次；有在途输入就落一条端到端耗时。</summary>
    private void OnFramePresented()
    {
        long at = Interlocked.Read(ref _echoInputTicks);
        if (at <= 0)
        {
            return;
        }
        long dt = Environment.TickCount64 - at;
        if (dt < EchoMinMs)
        {
            return;   // 静止期背景帧，继续等真正由输入引起的画面变化
        }
        Interlocked.Exchange(ref _echoInputTicks, 0);
        _echoMisses = 0;
        AppLog.Info($"[回显] 输入→首帧 {dt}ms");
    }

    private void Refresh()
    {
        var session = _manager.Active();
        if (session is null || !_pages.TryGetValue(session.Id, out var page))
        {
            _pill.Set("未连接", Theme.TextMuted);
            _fpsChip.SetUnit("");
            _fpsChip.SetValue("—");
            _fpsChip.SetTone(Theme.TitleTextMuted);
            UpdateNavEnabled();
            return;
        }
        UpdateNavEnabled();

        long pres = page.Surface.Presented;
        long tick = Environment.TickCount64;
        if (pres > page.LastPresented)
        {
            page.LastGrowMs = tick;
        }
        if (page.LastTickMs > 0 && tick > page.LastTickMs)
        {
            double instant = (pres - page.LastPresented) * 1000.0 / (tick - page.LastTickMs);
            // 编码端只在画面变化时出帧，瞬时值常在 0 和峰值之间跳；平滑一下更像「实际观感」
            page.Fps = page.Fps <= 0 ? instant : page.Fps * 0.6 + instant * 0.4;
        }
        page.LastPresented = pres;
        page.LastTickMs = tick;

        // 输入后超过 EchoNoFrameMs 仍无新帧：可能是点了空白处（正常），也可能链路停滞。
        // 只写日志 + 节流催一次 IDR，不重连、不改顶栏主状态 —— 静止是采集语义，
        // 按帧间隔判死会误杀健康流（参考项目 v1.0.3 每 ~25s 杀一次）。
        long echoAt = Interlocked.Read(ref _echoInputTicks);
        if (echoAt > 0 && tick - echoAt >= EchoNoFrameMs)
        {
            int misses = ++_echoMisses;
            long dt = tick - echoAt;
            if (tick - Interlocked.Read(ref _echoLastIdrTicks) > EchoIdrGapMs)
            {
                Interlocked.Exchange(ref _echoLastIdrTicks, tick);
                ActiveCapture()?.RequestKeyframeImmediate();
            }
            AppLog.Info($"[回显] 输入后 {dt}ms 无新帧（累计 {misses} 次），催 IDR 一次");
            if (misses >= 3)
            {
                SetStatus($"[回显] 连续 {misses} 次输入无新帧 · 已催帧（画面无变化或链路停滞）");
            }
            Interlocked.Exchange(ref _echoInputTicks, 0);
        }

        var st = session.Stats();
        var clip = session.Clipboard;
        bool locked = page.LockOverlay.Visibility == Visibility.Visible;

        // P0（2026-11-16）：「疑似平板息屏」原本只在 pres>0（曾经出过帧）时生效，
        // App 重启清空上屏缓存后 pres==0 永远走不到，用户会对着「等待画面」干等十几分钟。
        // 这里按设备独立计时，超过阈值给一条可操作提示；只改 UI，不动判死/唤屏/重连。
        if (pres == 0 && page.Streaming)
        {
            if (page.NoFrameSinceMs == 0)
            {
                page.NoFrameSinceMs = tick;
            }
        }
        else
        {
            page.NoFrameSinceMs = 0;
            if (page.ScreenOnHintShown && pres > 0)
            {
                SetStatus("投屏中");
            }
            page.ScreenOnHintShown = false;
        }
        bool screenOnHint = page.NoFrameSinceMs > 0
                            && tick - page.NoFrameSinceMs >= NoFrameHintMs;

        // 采集端「变化才出帧」：画面不动时只零星补帧（实测静止 0.7~6fps、滑动 28~36fps）。
        // 这不是性能问题，必须与断流/息屏区分开，否则用户看到冻住的画面 + 0fps 会以为程序挂了。
        // 判定用带迟滞的积分：低于 6fps 加分、高于则减分，累计到 6 分（约 3s）才算静止，恢复时约 2s 退出。
        long idleMs = page.LastGrowMs > 0 ? tick - page.LastGrowMs : 0;
        page.SlowScore = page.Fps < 6
            ? Math.Min(8, page.SlowScore + 1)
            : Math.Max(0, page.SlowScore - 2);
        bool idle = pres > 0 && page.Streaming && page.SlowScore >= EchoIdleScore;

        if (locked)
        {
            _pill.Set("已遮挡", Theme.Warn);
        }
        else if (!page.Streaming)
        {
            _pill.Set("已连接", Theme.Ok);
        }
        else if (pres == 0)
        {
            _pill.Set(screenOnHint ? "等待画面 · 请点亮设备屏幕" : "等待画面", Theme.Warn);
        }
        else if (idleMs >= 15000)
        {
            _pill.Set("画面静止 · 疑似平板息屏", Theme.TextSub);
        }
        else if (idle)
        {
            _pill.Set("画面静止", Theme.TextSub);
        }
        else
        {
            _pill.Set("投屏中", Theme.Ok);
        }

        _placeholder.Visibility = pres == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (screenOnHint)
        {
            string cur = _placeholder.Text ?? "";
            bool realError = cur.StartsWith("设备采集服务启动失败", StringComparison.Ordinal)
                          || cur.StartsWith("推流指令未获确认", StringComparison.Ordinal);
            if (!page.ScreenOnHintShown)
            {
                // Visibility 刚置好，SetStatus 同步写状态栏 + 画面中央（IsStageMessage 白名单）。
                page.ScreenOnHintShown = true;
                SetStatus(ScreenOnHint);
            }
            else if (!realError && cur != ScreenOnHint)
            {
                // 切换设备 / 重发推流会把占位页改回「正在等待设备首帧…」，下一轮写回提示；
                // 真错误文案保留，不被提示盖掉。
                _placeholder.Text = ScreenOnHint;
            }
        }
        // 上边框帧率胶囊常驻（无画面时显示 0 / 静止），底部三键导航常驻，连接前后都能点。

        // 静止时不再显示刺眼的 0：直接写「静止」并标灰，运动时恢复正常帧率显示。
        // 延迟 / 帧数 / 丢包 / 解码错误已从界面删除（用户 2026-11-10 22:23 要求「只显示帧率」），
        // 诊断数值仍保留在底栏 _detail 里，F11 呼出控制条即可查看，不占画面。
        if (idle)
        {
            _fpsChip.SetValue("静止");
            _fpsChip.SetUnit("");
            _fpsChip.SetTone(Theme.TitleTextMuted);
        }
        else
        {
            _fpsChip.SetUnit("fps");
            _fpsChip.SetValue(page.Fps > 0 ? page.Fps.ToString("F0") : "0");
            // 标题栏是浅底，状态色用压深一档的 TitleOk / TitleWarn，否则绿和琥珀会发飘。
            _fpsChip.SetTone(page.Fps >= 24
                ? Theme.TitleOk
                : (page.Fps > 0 ? Theme.TitleWarn : Theme.TitleText));
        }

        var mf = page.Session.Decoder as MediaFoundationDecoder;
        string decText = mf is not null ? $"{mf.DecodedFrames} 帧 / 错误 {mf.Errors}" : page.Session.Decoder.Name;

        _resolutionChip.Text = session.Width > 0
            ? (session.Folded == 1 ? $"{session.Width}×{session.Height} 折叠"
                : $"{session.Width}×{session.Height}")
            : "—";

        _detail.Text =
            $"包 {st.Packets:N0} · 帧 {st.Frames:N0} · 重组丢帧 {st.Dropped:N0} · " +
            $"排队丢 {session.DecodeQueueDropped:N0} · RTO {st.RtoMs}ms · " +
            $"写位图 {page.Surface.AvgPresentMs:F1}ms · 拥塞丢 {page.Surface.Dropped} · " +
            $"解码 {decText} · 剪贴板 ↑{clip?.SentText ?? 0}/↓{clip?.ReceivedText ?? 0}";
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // 日志必须最先初始化：后面任何一步失败都要有据可查（用户报「连不上」时全靠它）。
        AppLog.Init();
        AppLog.Info("进程启动");

        // 未捕获异常一律落盘。WPF 里这类异常默认静默关闭窗口，
        // 用户只会看到「双击闪一下就没了」，没有任何线索。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("AppDomain 未捕获异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 多为被超时放弃的异步 I/O（端口扫描、hdc 子进程），本身不影响功能，
            // 所以记 WARN；AppLog 内部对连续重复消息做了抑制，不会刷爆日志。
            AppLog.Warn($"未观察的任务异常（通常无害）: {e.Exception.GetType().Name}: {e.Exception.InnerException?.Message ?? e.Exception.Message}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            AppLog.Info("进程退出");
            // 兜底：窗口 Closed 没走到时（异常退出路径）也要收掉桥进程
            try { HarmOsWin.Core.HdcRunner.Shutdown(); } catch { }
        };

        // 必须在创建主窗口之前：打开新的就关掉旧的。
        // 理由见 InstanceGuard 注释 —— hdc 与设备端 daemon/fport 都是独占资源，
        // 两个实例并存会互相踢会话；而且覆盖 exe 不会让跑着的旧进程换成新代码。
        InstanceGuard.TakeOver();

        var app = new Application();
        // 界面线程异常：记下来再让程序按默认流程退出，不留「莫名其妙没了」的现场
        app.DispatcherUnhandledException += (_, e) =>
            AppLog.Error("UI 线程未捕获异常", e.Exception);

        try
        {
            app.Run(new MainWindow());
        }
        catch (Exception ex)
        {
            AppLog.Error("主循环异常退出", ex);
            throw;
        }
    }
}
