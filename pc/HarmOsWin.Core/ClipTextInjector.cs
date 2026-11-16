namespace HarmOsWin.Core;

/// <summary>
/// 把一段纯文本翻译成「设备侧模拟键入」的 shell 命令序列 —— PC → 平板方向的剪贴板推送。
///
/// 为什么不是「写设备剪贴板」：鸿蒙不给第三方应用静默写系统剪贴板的能力
/// （<c>SystemPasteboard.setData()</c> 虽无 @permission 声明，但后台应用会被
/// 27787278 Replication is prohibited 拒）。
///
/// 2026-11-14 更正：此前这里写着「API 26 的 Action 枚举根本不存在 SET_TEXT」，**是错的**。
/// 直接核对 SDK 的 api/@ohos.accessibility.d.ts:87，type Action 里确实含 <c>'setText'</c>
/// （注释还明确写了 "You need to set the setText parameter"）。
/// 但 setText 仍不是本文件的首选路径：它要求先由用户在系统设置里开启无障碍扩展服务，
/// 而实测的 HarmonyOS 7 平板「辅助功能」页里**已经没有「扩展服务」入口**（第三方服务无从开启），
/// 所以键盘与文本注入统一走 uitest（与参考项目 hnscrcpy 同一路线）。
/// 唯一现成可用的通道是系统自带的 <c>uitest uiInput inputText &lt;x&gt; &lt;y&gt; '&lt;text&gt;'</c>，
/// 它走设备 IME，所以中文正常，本质是「替你敲字」而不是「替你粘贴」。
///
/// 它的硬约束（2026-11-11 读 hnscrcpy TextInjector 得到的代码级结论）：
/// x y 取的是**设备端最后一次触摸坐标**，从没触摸过就直接丢弃（日志里是 no anchor）。
/// 所以调用方必须传入「用户最后一次点击投屏画面的设备坐标」，并且要求那一下点的就是输入框。
///
/// 与参考项目的差异：我们不用它的「80ms 攒批」（它要合并高频鼠标事件，我们是一次性推送一段文本），
/// 但保留它的分片思路 —— 单条 shell 命令太长会被设备侧截断。
/// </summary>
public static class ClipTextInjector
{
    /// <summary>回车键码，@ohos.multimodalInput.keyCode.d.ts:555 KEYCODE_ENTER = 2054。</summary>
    public const ushort KeyEnter = 2054;

    /// <summary>
    /// 单条 inputText 的字符上限。中文在 UTF-8 下 3 字节/字，180 字 ≈ 540 字节，
    /// 离 hdc shell 与 uitest 的参数长度上限都还有很大余量。
    /// 注意：这只约束**降级路径**（命令行直传）。首选路径走文件，不受此限。
    /// </summary>
    public const int MaxChunkChars = 180;

    /// <summary>
    /// 首选路径：文本落到设备上的固定文件，由设备端 shell 展开后交给 uitest。
    ///
    /// 为什么优于命令行直传（2026-11-11 真机实测）：
    ///   · **emoji 保真** —— Windows → hdc 的命令行是 ANSI 编码，😀 会被替换成 '??'；
    ///     文件走字节流，设备端 `$(cat …)` 展开完好。
    ///   · **不需要任何转义** —— 文件内容不经过 shell 词法解析；
    ///     `"$(cat f)"` 的展开结果不会再被分词或去引号，所以文本里的 ' " $ ` 全是字面量。
    ///   · **不需要分片** —— 215 字 3 行一次注入成功（cmd 条数 1 对 N）。
    /// 代价：每条要一次 `hdc file send`（实测 26ms / 613B），文件名固定、每次覆盖，不留垃圾。
    /// </summary>
    public const string RemotePath = "/data/local/tmp/harmos-clip.txt";

    /// <summary>设备端展开文件内容的注入命令（整个参数用双引号包住，交给设备 sh 展开一次）。</summary>
    public static string BuildFileCommand(int x, int y) =>
        $"uitest uiInput inputText {x} {y} \"$(cat {RemotePath})\"";

    // ── 2026-11-14 实测：焦点注入比坐标注入快一倍以上 ──
    //
    // 同一条 hdc 通道下的端到端耗时（平板 MLR-AL00，各测 3 次）：
    //   uitest uiInput inputText <x> <y> <text>   1222 / 1430 / 1458 ms   ← 内部会先模拟一次点击
    //   uitest uiInput text <text>                 533 / 732 / 752 ms     ← 直接写当前焦点
    //   uinput -T -c <x> <y>                       537 / 567 ms
    //   hdc shell true（纯通道基线）               249 / 329 / 383 ms
    //
    // 所以键盘直通一律走 `text`：省 ~700ms，且**不再需要坐标**——
    // 坐标来自「设备端最后一次触摸」，猜错就会把字敲进别的应用，这风险直接消失。
    // 约束变成「设备当前必须有焦点输入框」，比坐标安全得多。
    //
    // 中文仍比 ASCII 慢（1574 / 1630ms，IME 逐字上屏），emoji 内联会退化成 '?'，
    // 这两条决定了攒批窗口要放宽到「停手才发」，见 MainWindow.ScheduleKbdFlush。

    /// <summary>焦点注入（命令行直传）。仅用于纯 BMP 文本。</summary>
    public static string BuildInlineTextCommand(string text) =>
        $"uitest uiInput text {ShellQuote(text)}";

    /// <summary>焦点注入（文件通道）。emoji 与长文本必须走这条，否则命令行按 ANSI 截断。</summary>
    public static string BuildFileTextCommand() =>
        $"uitest uiInput text \"$(cat {RemotePath})\"";

    /// <summary>
    /// 是否需要文件通道：含 BMP 外字符（emoji 代理对）或超长。
    /// 阈值取 180 字，与 <see cref="MaxChunkChars"/> 一致 —— 中文 180 字约 540 字节，
    /// 离 hdc shell 与 uitest 的参数上限都还有余量。
    /// </summary>
    public static bool NeedsFileChannel(string text)
    {
        if (text.Length > MaxChunkChars)
        {
            return true;
        }
        foreach (char c in text)
        {
            if (char.IsSurrogate(c))
            {
                return true;
            }
        }
        return false;
    }

    // ── 首选：真·写平板系统剪贴板（2026-11-11 21:45，用户选定「真写平板剪贴板」）──
    //
    // 机制：PC 用 aa start 拉起设备端 ClipSetAbility（无窗口 ability），
    // 它在 onCreate 里把 base64 文本解码后调 pasteboard.setData()，写完立刻退出。
    // 与「敲字」相比：不需要先点输入框、不动用户的光标、多行与 emoji 原样保留。
    //
    // 为什么文本要 base64：want 参数按位置成对解析，明文里的空格 / 换行 / 引号会被切断；
    // base64 只有 A-Za-z0-9+/= 五种字符，无空格无引号，塞进 want 最省事。
    public const string ClipSetAbility = "ClipSetAbility";
    public const string BundleName = "com.harmos.mirror";

    /// <summary>
    /// base64 报文长度上限。2026-11-11 真机实测：3600 字符（900 汉字）与 10000 字符（2500 汉字）
    /// 都能完整写入（hilog 打印的字数与源串一致，无截断），故取 16000 留一倍余量。
    /// 16000 base64 ≈ 12000 字节 UTF-8 ≈ 4000 个汉字；超过此值由调用方回退到「文件 + 敲字」路径。
    /// </summary>
    public const int MaxBase64Chars = 16000;

    /// <summary>
    /// 生成「把文本写进平板剪贴板」的启动命令；文本过长返回 null（调用方应改走敲字）。
    /// 单引号包裹 base64 —— base64 字符集里没有单引号，不需要再转义。
    /// </summary>
    public static string? BuildSetClipboardCommand(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        if (b64.Length > MaxBase64Chars)
        {
            return null;
        }
        return $"aa start -a {ClipSetAbility} -b {BundleName} --ps cliptext '{b64}'";
    }

    // ── 剪贴板粘贴通道（2026-11-14 真机验证，比 uitest inputText 快 40~55%）──
    //
    // 起因：补记 64 查明 `uitest uiInput text` 的内部实现就是「SetPasteData 写剪贴板 + 粘贴」。
    // 既然本质相同，就用我们自己的 ClipSetAbility 写（免起 uitest CLI 框架），再手动触发粘贴。
    //
    // 真机实测（平板 MLR-AL00，无线，直连 hdc，各 2~3 次）：
    //   分两条命令（写剪贴板 + Ctrl+V）      951 / 1029 / 1107 ms
    //   合并一条命令（aa start; sleep; uinput）1028 / 1051 / 1289 ms
    //   长文本 140 字中文（合并命令）          1064 / 1189 ms
    //   对照：uitest uiInput text              1500 / 2422 / 2457 ms
    //
    // 键码：KEYCODE_CTRL_LEFT=2072、KEYCODE_V=2038（@ohos.multimodalInput.keyCode.d.ts:681/:443）。
    // 组合键用「单命令多段 -d/-u」写法，与 Shift 组合同理。
    // 注意 KEYCODE_PASTE=2622 实测**无效**（系统不消费该键），必须用 Ctrl+V。

    /// <summary>Ctrl 左键码（KEYCODE_CTRL_LEFT）。</summary>
    public const ushort KeyCtrlLeft = 2072;

    /// <summary>V 键码（KEYCODE_V）。</summary>
    public const ushort KeyV = 2038;

    /// <summary>
    /// `aa start` 拉起 ClipSetAbility 到剪贴板真正可粘贴之间的等待（毫秒）。
    ///
    /// 为什么不能省：ClipSetAbility 是**异步**的 —— `aa start` 返回只代表 ability 被拉起，
    /// `pasteboard.setData()` 还在它的 onCreate 里跑。等太短会粘到**旧内容**。
    /// 实测 400ms 稳定（更长文本 140 字同样 400ms 够），故取 400 并留 100ms 余量到 500。
    /// </summary>
    public const int ClipSettleMs = 500;

    /// <summary>
    /// 构造「写剪贴板 → 等落盘 → Ctrl+V 粘贴」的单条 shell 命令。
    /// 文本过长（base64 超 <see cref="MaxBase64Chars"/>）返回 null，调用方回退敲字通道。
    ///
    /// 为什么合成一条命令而不是两次往返：省一次 hdc 进程往返（实测省 100~200ms），
    /// 且保证「写完再粘」的顺序不会被打断。
    /// </summary>
    public static string? BuildPasteCommand(string text)
    {
        string? setCmd = BuildSetClipboardCommand(text);
        if (setCmd is null)
        {
            return null;
        }
        // 用 sh 的 sleep 而不是 await：整个动作必须在同一条 shell 里完成，
        // 否则两次 hdc 调用之间可能被别的注入插队（顺序错乱）。
        return $"{setCmd}; sleep {ClipSettleMs / 1000.0:0.###}; " +
               $"uinput -K -d {KeyCtrlLeft} -d {KeyV} -u {KeyV} -u {KeyCtrlLeft}";
    }

    /// <summary>
    /// 写本地临时文件供 `hdc file send` 使用。
    /// 必须是**无 BOM** 的 UTF-8：带 BOM 时文件开头的 U+FEFF 会被一起注入到输入框里。
    /// </summary>
    public static string WriteTempFile(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "harmos-clip.txt");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// 生成完整命令序列：逐行 inputText，行与行之间补一个 ENTER 键事件。
    /// 空行也要补 ENTER（否则用户复制的段落结构会塌成一段）。
    /// </summary>
    public static List<string> BuildCommands(int x, int y, string text)
    {
        var cmds = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return cmds;
        }
        // 统一换行符后再切分：Windows 剪贴板几乎都是 \r\n
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                cmds.Add($"uitest uiInput keyEvent {KeyEnter}");
            }
            foreach (string chunk in SplitByChars(lines[i], MaxChunkChars))
            {
                cmds.Add($"uitest uiInput inputText {x} {y} {ShellQuote(chunk)}");
            }
        }
        return cmds;
    }

    /// <summary>
    /// 按「字符」切分，但不切断代理对（emoji 由两个 char 组成，切中间会出乱码或让
    /// 设备 IME 收到孤立代理项后整段丢弃）。
    /// </summary>
    public static IEnumerable<string> SplitByChars(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || max <= 0)
        {
            yield break;
        }
        int start = 0;
        while (start < s.Length)
        {
            int len = Math.Min(max, s.Length - start);
            int end = start + len;
            // 落在低代理位上 → 回退一格，把整个代理对留给下一片
            if (end < s.Length && char.IsLowSurrogate(s[end]))
            {
                end--;
            }
            if (end <= start)
            {
                end = start + len;   // 极端情况（max==1 且是代理对）不强拆，直接整对放行
                if (end < s.Length && char.IsLowSurrogate(s[end]))
                {
                    end++;
                }
            }
            yield return s.Substring(start, end - start);
            start = end;
        }
    }

    /// <summary>
    /// 单引号包裹并转义，供**设备侧 sh** 解析（整条命令作为 hdc shell 的单个参数传下去，
    /// PC 侧不会再过一层 shell，所以只需扛住设备端这一次展开）。
    ///
    /// 单引号是 sh 里唯一「字面量安全」的引法：$ ` \ 在单引号内都不展开。
    /// 文本里若自带上单引号，用标准的 '\'' 断开-转义-续接写法。
    /// 制表符转空格：uitest 的参数里 \t 会被吞成空白，交给 IME 也不一定能输入制表符。
    /// </summary>
    public static string ShellQuote(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        sb.Append('\'');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\'':
                    sb.Append("'\\''");
                    break;
                case '\t':
                    sb.Append(' ');
                    break;
                // NUL 与其它 C0 控制字符会让 sh / uitest 的参数解析出错，直接丢
                case '\0' or '\u0001' or '\u0002' or '\u0003' or '\u0004' or '\u0005' or '\u0006' or '\u0007'
                    or '\b' or '\v' or '\f' or '\u000E' or '\u000F':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
