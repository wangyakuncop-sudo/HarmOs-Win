namespace HarmOsWin.Core;

/// <summary>
/// ASCII → HarmonyOS 键码映射，用于把一串字符压成**一条** `uinput -K` 命令。
///
/// 为什么值得单独做一张表：以前每个字符都要一次 `uitest uiInput text`（556~604ms/条），
/// 打 10 个字就是 6 秒。`uinput -K` 支持在一条命令里串联多段 `-d/-u`，
/// 所以整串只花一次通道往返（ConPTY 常驻会话下实测 250ms 量级）。
///
/// 键码来源：hnscrcpy 在 MRT-AL10 上用 `scripts/spike-keymap.sh` 真机标定过的全表
/// （不是查文档得来的）。我们已复测其中 2017='a'、2055=退格 有效，
/// 并在平板 MLR-AL00 上复测「一条命令串 2017/2018/2019」三个键全部执行。
/// 注意空格是 **2050**（文档写的 2052 是错的）。
///
/// 不可映射的字符（中文、emoji、其它 Unicode）返回 null，
/// 由调用方回退到 `uitest uiInput text`（那条路必须经 IME，但能处理任意文本）。
/// </summary>
public static class KeyCharMap
{
    public const ushort Shift = 2047;
    public const ushort Enter = 2054;
    public const ushort Delete = 2055;   // 退格（KEYCODE_DEL）
    public const ushort Tab = 2049;

    /// <summary>单条命令里最多串多少个字符。命令过长会顶到命令行长度上限，超过就走 text 通道。</summary>
    public const int MaxCharsPerCommand = 120;

    /// <summary>
    /// 把文本编译成 `uinput -K` 的参数片段（形如 `-d 2017 -u 2017 -d 2054 -u 2054`）。
    /// 任一字符无法映射时返回 null —— **不做部分转换**，否则会出现「半个串进设备、剩下丢了」。
    /// </summary>
    public static string? BuildKeyScript(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxCharsPerCommand)
        {
            return null;
        }
        var sb = new System.Text.StringBuilder(text.Length * 16);
        foreach (char c in text)
        {
            if (!AppendKey(sb, c))
            {
                return null;
            }
        }
        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    /// <summary>把一个字符追加成「按下 / 抬起」段。不可映射返回 false。</summary>
    private static bool AppendKey(System.Text.StringBuilder sb, char c)
    {
        if (!TryMap(c, out ushort code, out bool needShift))
        {
            return false;
        }
        if (needShift)
        {
            sb.Append("-d ").Append(Shift).Append(' ');
        }
        sb.Append("-d ").Append(code).Append(" -u ").Append(code).Append(' ');
        if (needShift)
        {
            sb.Append("-u ").Append(Shift).Append(' ');
        }
        return true;
    }

    /// <summary>字符 → (键码, 是否需要 Shift)。</summary>
    private static bool TryMap(char c, out ushort code, out bool shift)
    {
        shift = false;
        if (c >= 'a' && c <= 'z')
        {
            code = (ushort)(2017 + (c - 'a'));
            return true;
        }
        if (c >= 'A' && c <= 'Z')
        {
            code = (ushort)(2017 + (c - 'A'));
            shift = true;
            return true;
        }
        if (c >= '0' && c <= '9')
        {
            code = (ushort)(2000 + (c - '0'));
            return true;
        }
        ushort? direct = c switch
        {
            ' ' => 2050,
            ',' => 2043,
            '.' => 2044,
            '`' => 2056,
            '-' => 2057,
            '=' => 2058,
            '[' => 2059,
            ']' => 2060,
            '\\' => 2061,
            ';' => 2062,
            '\'' => 2063,
            '/' => 2064,
            '\n' => Enter,
            '\r' => Enter,
            '\t' => Tab,
            _ => null
        };
        if (direct is ushort d)
        {
            code = d;
            return true;
        }

        // 移位符号：同一个物理键 + Shift
        ushort? shifted = c switch
        {
            '!' => 2001, '@' => 2002, '#' => 2003, '$' => 2004, '%' => 2005,
            '^' => 2006, '&' => 2007, '*' => 2008, '(' => 2009, ')' => 2000,
            '_' => 2057, '+' => 2058,
            '{' => 2059, '}' => 2060, '|' => 2061,
            ':' => 2062, '"' => 2063, '?' => 2064,
            '~' => 2056,
            '<' => 2043, '>' => 2044,
            _ => null
        };
        if (shifted is ushort s)
        {
            code = s;
            shift = true;
            return true;
        }

        code = 0;
        return false;
    }
}
