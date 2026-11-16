using System.Windows.Media;

namespace HarmOsWin.App;

/// <summary>
/// PC 端深色配色基准，与平板端 common/Theme.ets 同一套语义
/// （主色蓝、状态色红/琥珀/绿、中性灰），只是把浅色底换成深色底。
/// </summary>
internal static class Theme
{
    public static readonly SolidColorBrush Bg = Freeze(0x0E, 0x10, 0x15);
    public static readonly SolidColorBrush Panel = Freeze(0x15, 0x18, 0x1F);
    public static readonly SolidColorBrush Card = Freeze(0x1A, 0x1E, 0x26);
    public static readonly SolidColorBrush CardHover = Freeze(0x22, 0x28, 0x34);
    public static readonly SolidColorBrush Surface = Freeze(0x0A, 0x0B, 0x0E);
    public static readonly SolidColorBrush Border = Freeze(0x26, 0x2C, 0x38);
    public static readonly SolidColorBrush BorderSoft = Freeze(0x1F, 0x24, 0x2E);

    public static readonly Color TextColor = Color.FromRgb(0xE8, 0xEC, 0xF3);
    public static readonly SolidColorBrush Text = Freeze(0xE8, 0xEC, 0xF3);
    public static readonly SolidColorBrush TextSub = Freeze(0x9A, 0xA4, 0xB5);
    public static readonly SolidColorBrush TextMuted = Freeze(0x6B, 0x74, 0x86);

    public static readonly Color AccentColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly SolidColorBrush Accent = Freeze(0x0A, 0x84, 0xFF);
    public static readonly SolidColorBrush AccentHover = Freeze(0x2E, 0x96, 0xFF);
    public static readonly SolidColorBrush AccentSoft = Freeze(0x14, 0x27, 0x3E);

    public static readonly SolidColorBrush Ok = Freeze(0x12, 0xB7, 0x6A);
    public static readonly SolidColorBrush Warn = Freeze(0xF0, 0xA4, 0x00);
    public static readonly SolidColorBrush Danger = Freeze(0xFF, 0x5A, 0x4E);

    // ── 浅色标题栏专用色板 ───────────────────────────────────────────
    // 上面那套是画面区深色底用的。同一批胶囊搬到 #F3F3F3 系统标题栏上时，
    // 深底 + 浅字会变成一块突兀的深色膏药，所以标题栏单开一套浅底深字。
    public static readonly SolidColorBrush TitleBar = Freeze(0xF3, 0xF3, 0xF3);
    public static readonly SolidColorBrush TitleBarBorder = Freeze(0xD8, 0xDC, 0xE2);
    public static readonly SolidColorBrush TitleChip = Freeze(0xE8, 0xEB, 0xF1);
    public static readonly SolidColorBrush TitleChipHover = Freeze(0xDA, 0xE0, 0xE8);
    public static readonly SolidColorBrush TitleChipBorder = Freeze(0xD2, 0xD8, 0xE1);
    public static readonly SolidColorBrush TitleText = Freeze(0x2B, 0x30, 0x38);
    public static readonly SolidColorBrush TitleTextMuted = Freeze(0x66, 0x6E, 0x7C);
    /// <summary>浅底上的状态色：比画面区的绿 / 琥珀压深一档，避免在白底上发飘。</summary>
    public static readonly SolidColorBrush TitleOk = Freeze(0x0E, 0x9F, 0x5F);
    public static readonly SolidColorBrush TitleWarn = Freeze(0xB0, 0x6A, 0x00);

    // ── 自绘窗口按钮（最小化 / 最大化 / 关闭）────────────────────────
    // 取值对齐 Windows 11 原生标题栏：普通按钮 hover 是中灰，关闭按钮 hover 是
    // 那抹标志性的砖红 + 白字。自绘就必须自己还这笔账，否则一眼假。
    public static readonly SolidColorBrush CaptionHover = Freeze(0xE5, 0xE5, 0xE5);
    public static readonly SolidColorBrush CaptionPress = Freeze(0xD5, 0xD5, 0xD5);
    public static readonly SolidColorBrush CaptionCloseHover = Freeze(0xC4, 0x2B, 0x1C);
    public static readonly SolidColorBrush CaptionClosePress = Freeze(0xB0, 0x25, 0x17);

    public static readonly FontFamily UiFont =
        new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    public static readonly FontFamily MonoFont = new("Consolas");

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>带透明度的描边画刷（分割线用）。</summary>
    public static SolidColorBrush Alpha(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
