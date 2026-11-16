using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HarmOsWin.App;

internal enum BtnKind
{
    /// <summary>主操作：实心蓝底白字。</summary>
    Primary,
    /// <summary>次操作：深灰卡片底。</summary>
    Ghost,
    /// <summary>危险/停止类：深灰底 + 红字。</summary>
    Danger,
    /// <summary>浅色标题栏胶囊：浅灰底 + 深字。深色 Ghost 放在 #F3F3F3 上像一块膏药。</summary>
    TitleBar
}

/// <summary>
/// 圆角扁平按钮。WPF 原生 Button 的 chrome 无法直接改圆角与内边距，
/// 所以这里用 Border 自绘（含 hover / pressed / active / disabled 四态）。
/// </summary>
internal sealed class FlatButton : Border
{
    private readonly TextBlock _label;
    private readonly BtnKind _kind;
    private bool _hover;
    private bool _pressed;
    private bool _active;
    private bool _enabled = true;

    public event Action? Clicked;

    public FlatButton(string text, BtnKind kind = BtnKind.Ghost, double height = 34, double fontSize = 13)
    {
        _kind = kind;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Height = height;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        _label = new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        Child = _label;

        Padding = new Thickness(14, 0, 14, 0);

        MouseEnter += (_, _) => { _hover = true; Apply(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Apply(); };
        MouseLeftButtonDown += (_, e) => { _pressed = true; Apply(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            bool wasPressed = _pressed;
            _pressed = false;
            Apply();
            if (wasPressed && _enabled && IsMouseOver)
            {
                Clicked?.Invoke();
            }
            e.Handled = true;
        };

        Apply();
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    /// <summary>切换型按钮的选中态（如当前设备项）。</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Apply();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Cursor = value ? Cursors.Hand : Cursors.Arrow;
            Apply();
        }
    }

    /// <summary>左对齐 + 图标位（设备列表项用）。</summary>
    public void SetLeftAligned()
    {
        _label.HorizontalAlignment = HorizontalAlignment.Left;
        _label.TextAlignment = TextAlignment.Left;
        Padding = new Thickness(12, 0, 12, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void Apply()
    {
        double alpha = _enabled ? 1.0 : 0.4;
        Opacity = alpha;

        if (_active)
        {
            Background = Theme.AccentSoft;
            BorderBrush = Theme.Accent;
            _label.Foreground = Theme.Accent;
            return;
        }

        switch (_kind)
        {
            case BtnKind.Primary:
                Background = _hover ? Theme.AccentHover : Theme.Accent;
                BorderBrush = _hover ? Theme.AccentHover : Theme.Accent;
                _label.Foreground = Brushes.White;
                break;
            case BtnKind.Danger:
                Background = _hover ? Theme.CardHover : Theme.Card;
                BorderBrush = Theme.BorderSoft;
                _label.Foreground = Theme.Danger;
                break;
            case BtnKind.TitleBar:
                Background = _hover ? Theme.TitleChipHover : Theme.TitleChip;
                BorderBrush = Theme.TitleChipBorder;
                _label.Foreground = Theme.TitleText;
                break;
            default:
                Background = _hover ? Theme.CardHover : Theme.Card;
                BorderBrush = Theme.BorderSoft;
                _label.Foreground = Theme.TextSub;
                break;
        }

        if (_pressed)
        {
            Background = _kind == BtnKind.TitleBar ? Theme.TitleChipHover : Theme.CardHover;
        }
    }
}

/// <summary>
/// 指标磁贴：小标题 + 大数字 + 单位。数值走粗体大字，
/// 比原来一行 200 字的长字符串可读得多。
/// </summary>
internal sealed class StatTile : Border
{
    private readonly TextBlock _value;
    private readonly TextBlock _unit;

    public StatTile(string caption, string unit, string initial = "—")
    {
        Background = Theme.Card;
        BorderBrush = Theme.BorderSoft;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(14, 10, 14, 10);
        Margin = new Thickness(0, 0, 10, 0);
        MinWidth = 132;

        _value = new TextBlock
        {
            Text = initial,
            FontFamily = Theme.UiFont,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            Margin = new Thickness(0, 2, 0, 0)
        };
        _unit = new TextBlock
        {
            Text = unit,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(5, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Bottom
        };

        var captionBlock = new TextBlock
        {
            Text = caption,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextSub
        };

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
        valueRow.Children.Add(_value);
        valueRow.Children.Add(_unit);

        var stack = new StackPanel();
        stack.Children.Add(captionBlock);
        stack.Children.Add(valueRow);
        Child = stack;
    }

    public void SetValue(string value)
    {
        _value.Text = value;
    }

    public void SetUnit(string unit)
    {
        _unit.Text = unit;
    }

    public void SetTone(SolidColorBrush brush)
    {
        _value.Foreground = brush;
    }
}

/// <summary>
/// 紧凑指标项（画面 HUD 用）：一行「标签 数值 单位」，高度约 22 DIP。
/// 原来的 StatTile 是「小标题 + 22 号大字」的两行磁贴，占 60+ 高度，
/// 钉在画面下方会把画面挤小；改成 HUD 浮层后不再占用布局高度。
/// </summary>
internal sealed class MiniStat : StackPanel
{
    private readonly TextBlock _value;
    private readonly TextBlock _unit;

    public MiniStat(string caption, string unit)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        var label = new TextBlock
        {
            Text = caption,
            FontFamily = Theme.UiFont,
            FontSize = 10.5,
            Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        _value = new TextBlock
        {
            Text = "—",
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var unitBlock = _unit = new TextBlock
        {
            Text = unit,
            FontFamily = Theme.UiFont,
            FontSize = 10,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Children.Add(label);
        Children.Add(_value);
        Children.Add(unitBlock);
    }

    public void SetValue(string value) => _value.Text = value;

    /// <summary>值已自带语义（如「静止」）时把单位清掉，避免出现「静止 fps」这种读起来别扭的组合。</summary>
    public void SetUnit(string unit) => _unit.Text = unit;

    public void SetTone(SolidColorBrush brush) => _value.Foreground = brush;
}

/// <summary>
/// 标题栏浅色指标胶囊：只显示「数值 + 单位」（如 <c>60 fps</c> / <c>静止</c>），高度 22。
/// 画面 HUD 用的 <see cref="MiniStat"/> 带一个「帧率」标签且走深色主题色，
/// 放到 #F3F3F3 标题栏上文字几乎看不见 —— 所以这里单开浅色版本，并且省掉标签。
/// </summary>
internal sealed class TitleChip : Border
{
    private readonly TextBlock _value;
    private readonly TextBlock _unit;

    public TitleChip(string unit)
    {
        Background = Theme.TitleChip;
        BorderBrush = Theme.TitleChipBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(11);
        Height = 22;
        Padding = new Thickness(9, 0, 9, 0);
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;

        _value = new TextBlock
        {
            Text = "—",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TitleText,
            VerticalAlignment = VerticalAlignment.Center
        };
        _unit = new TextBlock
        {
            Text = unit,
            FontFamily = Theme.UiFont,
            FontSize = 9.5,
            Foreground = Theme.TitleTextMuted,
            Margin = new Thickness(3, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_value);
        row.Children.Add(_unit);
        Child = row;
    }

    public void SetValue(string value) => _value.Text = value;

    /// <summary>值已自带语义（如「静止」）时把单位清空，避免出现「静止 fps」。</summary>
    public void SetUnit(string unit) => _unit.Text = unit;

    public void SetTone(SolidColorBrush brush) => _value.Foreground = brush;
}

/// <summary>三键导航的图标类型，与设备「设置 → 三键导航」的 □ ○ ◁ 组合一一对应。</summary>
internal enum NavGlyph
{
    /// <summary>多任务：圆角方框。</summary>
    Recents,
    /// <summary>主页：圆环。</summary>
    Home,
    /// <summary>返回：左向三角。</summary>
    Back
}

/// <summary>
/// 底部三键导航按钮：自绘图标（+ 可选文字）。
/// 原来是直接写字符「□ 多任务 / ○ 主页 / ◁ 返回」——□ ○ ◁ 是三个不同字族的几何字符，
/// 字体给它们的字号、线宽、基线各不相同（□ 小得像个点，◁ 又偏大），三个键摆一起完全不成套。
/// 改成同一套画布、同一线宽的自绘图形，视觉重量才一致。
///
/// 2026-11-11 00:45 按用户要求新增「纯图标 + 全透明」形态（iconOnly=true）：
/// 去掉文字与胶囊底色，只留白色自绘图标浮在画面上；图标带柔和投影，
/// 避免叠在浅色画面（桌面壁纸 / 白底文档）上糊掉看不见。
/// </summary>
internal sealed class NavKeyButton : Border
{
    private readonly TextBlock _label;
    private readonly Shape _glyph;
    private readonly bool _iconOnly;
    private bool _hover;
    private bool _pressed;
    private bool _enabled = true;
    private SolidColorBrush? _flashBrush;

    public event Action? Clicked;

    /// <summary>
    /// 未连接设备时置 false：外观压暗、不响应点击。
    /// 之前三键点了没反应且没有任何提示，用户分不清「没连上」和「连上了但设备没执行」。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }
            _enabled = value;
            Cursor = value ? Cursors.Hand : Cursors.Arrow;
            Opacity = value ? 1.0 : 0.38;
            Apply();
        }
    }

    /// <summary>
    /// 点按结果反馈：下发中/成功/失败时图标与文字短暂变色。
    /// 纯画面模式下状态栏是隐藏的，用户只能靠按钮自身判断「这一下有没有生效」。
    /// </summary>
    public void Flash(SolidColorBrush brush, int holdMs = 320)
    {
        _flashBrush = brush;
        Apply();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_flashBrush, brush))
            {
                _flashBrush = null;
                Apply();
            }
        };
        timer.Start();
    }

    /// <param name="glyphScale">
    /// 纯图标模式下图标的放大倍数（默认 2.2 → 22px 图形、56×46 热区）。
    /// 2026-11-13 用户要求三键放进画面右下角一个小框里且不许超出，传 1.45 得到约 14.5px 图形、37×30 热区。
    /// 热区按同比例缩，保证点击手感跟着视觉一起收，不会「看着小、点着大」误触到画面。
    /// </param>
    public NavKeyButton(NavGlyph glyph, string text, double width = 92, bool iconOnly = false,
                        double glyphScale = 2.2)
    {
        _iconOnly = iconOnly;
        // 热区跟着图形成比例缩放（基准 56×46 对应 2.2 倍图形）
        double s = glyphScale / 2.2;
        Width = iconOnly ? 56 * s : width;
        Height = iconOnly ? 46 * s : 30;
        CornerRadius = new CornerRadius(12 * s);
        BorderThickness = new Thickness(iconOnly ? 0 : 1);
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        Padding = new Thickness(0, 0, 0, 0);
        HorizontalAlignment = HorizontalAlignment.Center;
        // 全透明也必须给 Brushes.Transparent（不能是 null），否则 WPF 不参与命中测试，点击会穿透到画面。
        Background = Brushes.Transparent;

        // 纯图标模式没有文字，图标就是唯一内容，默认放大 2.2 倍（10 → 22），线宽 1.4 → 2.0。
        // 16 太细太小，铺在画面上像三个小点，看着抠。
        double k = iconOnly ? glyphScale : 1.0;
        double box = 10 * k;
        // 线宽也跟着缩，否则小图标会被 2.2px 的粗描边糊成一团
        double stroke = iconOnly ? Math.Max(1.3, 2.2 * s) : 1.4;

        _glyph = glyph switch
        {
            NavGlyph.Recents => new Rectangle
            {
                Width = box,
                Height = box,
                RadiusX = 2 * k,
                RadiusY = 2 * k,
                StrokeThickness = stroke
            },
            NavGlyph.Home => new Ellipse
            {
                Width = box,
                Height = box,
                StrokeThickness = stroke
            },
            _ => new Polygon
            {
                Points = new PointCollection
                {
                    new Point(10.6 * k, 1.4 * k),
                    new Point(10.6 * k, 11.6 * k),
                    new Point(2.2 * k, 6.5 * k)
                },
                StrokeThickness = stroke,
                StrokeLineJoin = PenLineJoin.Round
            }
        };
        if (iconOnly)
        {
            // 全透明会直接叠在画面 / 壁纸上：白色图标遇到浅色区域会糊，加一层零位移的柔和投影保证可读。
            _glyph.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8 * s,
                ShadowDepth = 0,
                Opacity = 0.95
            };
        }
        _glyph.Stroke = iconOnly ? Brushes.White : Theme.TextSub;
        _glyph.Fill = Brushes.Transparent;

        var canvas = new Canvas
        {
            Width = 13 * k,
            Height = 13 * k,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_glyph is not Polygon)
        {
            Canvas.SetLeft(_glyph, 1.5 * k);
            Canvas.SetTop(_glyph, 1.5 * k);
        }
        canvas.Children.Add(_glyph);

        _label = new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.TextSub,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(canvas);
        if (!iconOnly)
        {
            row.Children.Add(new Border { Width = 7 });
            row.Children.Add(_label);
        }
        Child = row;

        MouseEnter += (_, _) => { _hover = true; Apply(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Apply(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_enabled)
            {
                e.Handled = true;
                return;
            }
            _pressed = true;
            Apply();
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            bool wasPressed = _pressed;
            _pressed = false;
            Apply();
            if (_enabled && wasPressed && IsMouseOver)
            {
                Clicked?.Invoke();
            }
            e.Handled = true;
        };

        Apply();
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    private void Apply()
    {
        bool hot = _hover || _pressed;
        if (_iconOnly)
        {
            // 全透明：常态完全无底无框，只剩图标。hover / 按下时给一层极淡的白色，
            // 让人知道「这里能点」，否则透明按钮点完毫无反馈像没反应。
            Background = hot ? Theme.Alpha(0x2E, 0xFF, 0xFF, 0xFF) : Brushes.Transparent;
            BorderBrush = Brushes.Transparent;
            SolidColorBrush ink = _flashBrush ?? Brushes.White;
            _label.Foreground = ink;
            _glyph.Stroke = ink;
            return;
        }
        Background = hot ? Theme.CardHover : Theme.Card;
        BorderBrush = _hover ? Theme.Border : Theme.BorderSoft;
        var normalInk = _flashBrush ?? (hot ? Theme.Text : Theme.TextSub);
        _label.Foreground = normalInk;
        _glyph.Stroke = normalInk;
    }
}

/// <summary>状态胶囊：圆点 + 文字（未连接 / 等待画面 / 投屏中 / 已锁屏）。</summary>
internal sealed class StatusPill : Border
{
    private readonly Ellipse _dot;
    private readonly TextBlock _text;

    public StatusPill()
    {
        Background = Theme.Card;
        BorderBrush = Theme.BorderSoft;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12, 6, 14, 6);
        VerticalAlignment = VerticalAlignment.Center;

        _dot = new Ellipse { Width = 7, Height = 7, Fill = Theme.TextMuted };
        _text = new TextBlock
        {
            Text = "未连接",
            FontFamily = Theme.UiFont,
            FontSize = 12,
            Foreground = Theme.TextSub,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_text);
        Child = row;
    }

    public void Set(string text, SolidColorBrush tone)
    {
        _text.Text = text;
        _dot.Fill = tone;
        _text.Foreground = ReferenceEquals(tone, Theme.TextMuted) ? Theme.TextSub : Theme.Text;
    }
}

/// <summary>统一的小标题（侧栏分组名）。</summary>
internal static class Caption
{
    public static TextBlock Make(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 11,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(2, 0, 0, 8)
        };
    }
}

/// <summary>1px 分割线。</summary>
internal static class Divider
{
    public static Border Make(double top = 14, double bottom = 14)
    {
        return new Border
        {
            Height = 1,
            Background = Theme.BorderSoft,
            Margin = new Thickness(0, top, 0, bottom)
        };
    }
}

/// <summary>自绘标题栏按钮的三种形态。</summary>
internal enum CaptionKind
{
    Minimize,
    Maximize,
    Close
}

/// <summary>
/// 自绘窗口按钮（最小化 / 最大化 / 关闭）。
///
/// 为什么要自绘：本工程把系统标题栏交给 WindowChrome 自绘（菜单 + 帧率挂在栏上），
/// 系统原生 caption 按钮在这种浅灰底上既没有 hover 反馈、也跟自绘胶囊风格割裂，
/// 用户 2026-11-11 00:59 明确要求「右上角放一个叉号，点击退出整个应用」。
/// 于是关掉 Aero 原生按钮，三种窗口按钮一起自己画，风格统一且行为可控。
///
/// 图标不用字符（☰ 曾被渲染成「三」的教训），全部用 10×10 矢量线画：
/// 最小化一条横线、最大化一个方框（最大化后换成还原用的双层方框）、关闭一个叉。
/// 尺寸对齐 Windows 11 原生标题栏：46×30，hover 中灰；关闭按钮 hover 砖红底白叉。
/// </summary>
internal sealed class CaptionButton : Border
{
    private const double GlyphSize = 10;

    private readonly CaptionKind _kind;
    private readonly Path _glyph;
    private bool _hover;
    private bool _pressed;
    private bool _maximized;

    public event Action? Clicked;

    public CaptionButton(CaptionKind kind)
    {
        _kind = kind;
        Width = 46;
        Height = 30;
        // 必须给 Transparent 而不是 null：null 背景不参与命中测试，点击会漏到标题栏去拖动窗口。
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        SnapsToDevicePixels = true;
        Cursor = Cursors.Arrow;

        _glyph = new Path
        {
            Stroke = Theme.TitleText,
            StrokeThickness = 1.0,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            SnapsToDevicePixels = true
        };
        RenderGlyph();

        Child = new Canvas
        {
            Width = GlyphSize,
            Height = GlyphSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _glyph }
        };

        MouseEnter += (_, _) => { _hover = true; Apply(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Apply(); };
        MouseLeftButtonDown += (_, e) =>
        {
            _pressed = true;
            Apply();
            // 必须在标题栏区把事件吃掉，否则 WindowChrome 会当成拖动窗口。
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed)
            {
                _pressed = false;
                Apply();
                Clicked?.Invoke();
            }
            e.Handled = true;
        };

        Apply();
    }

    /// <summary>最大化状态变化时换图标：最大化后应该显示「还原」而不是继续显示方框。</summary>
    public void SetMaximized(bool maximized)
    {
        if (_maximized == maximized)
        {
            return;
        }
        _maximized = maximized;
        RenderGlyph();
    }

    public void SetEnabledLook(bool enabled)
    {
        Opacity = enabled ? 1.0 : 0.4;
        IsHitTestVisible = enabled;
    }

    private void RenderGlyph()
    {
        switch (_kind)
        {
            case CaptionKind.Minimize:
                // 一条居中横线
                _glyph.Data = Geometry.Parse("M 0,5 L 10,5");
                _glyph.StrokeThickness = 1.0;
                break;
            case CaptionKind.Maximize when !_maximized:
                // 单层方框，0.5 偏移让 1px 线落在像素中心，不发虚
                _glyph.Data = Geometry.Parse("M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");
                _glyph.StrokeThickness = 1.0;
                break;
            case CaptionKind.Maximize:
                // 还原：后面一个方框 + 前面一个方框
                _glyph.Data = Geometry.Parse(
                    "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 Z");
                _glyph.StrokeThickness = 1.0;
                break;
            default:
                // 叉号：1.1 线宽比其它两个略粗，视觉重量才一致
                _glyph.Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10");
                _glyph.StrokeThickness = 1.1;
                break;
        }
    }

    private void Apply()
    {
        bool closer = _kind == CaptionKind.Close;

        if (closer && (_hover || _pressed))
        {
            Background = _pressed ? Theme.CaptionClosePress : Theme.CaptionCloseHover;
            _glyph.Stroke = Brushes.White;
            return;
        }

        if (!closer && (_hover || _pressed))
        {
            Background = _pressed ? Theme.CaptionPress : Theme.CaptionHover;
        }
        else
        {
            Background = Brushes.Transparent;
        }

        _glyph.Stroke = _hover && !closer ? Theme.TitleText : Theme.TitleTextMuted;
    }
}

/// <summary>标题栏图标按钮的图形种类。</summary>
internal enum TitleGlyph
{
    /// <summary>剪贴板（外框 + 夹子）。保留给旧调用点。</summary>
    Clipboard,
    /// <summary>上传箭头：PC → 平板。</summary>
    Upload,
    /// <summary>下载箭头：平板 → PC。</summary>
    Download,
    /// <summary>手机（窄高矩形 + 底部横线）。2026-11-13 用于标题栏的设备切换。</summary>
    Phone,
    /// <summary>平板（宽矩形 + 顶部横线）。与手机靠「长宽比」区分，一眼能认。</summary>
    Tablet
}

/// <summary>
/// 标题栏里的纯图标按钮：外观与帧率胶囊（TitleChip）完全同款，
/// 只是内容从「数值 + 单位」换成一个矢量图标。
///
/// 2026-11-11 02:20 按用户要求把「取剪贴板」从底部三键旁移到顶部窗口栏、与菜单 / 帧率并排：
/// 它本质是 PC 侧动作，跟设备三键不是一类东西，混在底部容易被当成第四个导航键。
/// 注意 WindowChrome 的标题栏默认整块是拖动区，必须由调用方对该控件放开命中测试。
/// </summary>
internal sealed class TitleIconButton : Border
{
    private readonly Path _glyph;
    private bool _hover;
    private bool _active;
    private bool _muted;
    private SolidColorBrush? _flash;

    public event Action? Clicked;

    public TitleIconButton(TitleGlyph icon = TitleGlyph.Clipboard)
    {
        Height = 22;
        Padding = new Thickness(7, 0, 7, 0);
        CornerRadius = new CornerRadius(11);
        BorderThickness = new Thickness(1);
        Background = Theme.TitleChip;
        BorderBrush = Theme.TitleChipBorder;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        // 三个图标共用同一画布（13.6 x 13.4）与同一线宽 1.4，摆一起才成套。
        // 箭头刻意只画「一个竖杆 + 一个 V 形头」，不加尾横线 —— 22 高的胶囊里塞两条线会更糊。
        string data = icon switch
        {
            // 上传（PC → 平板）：杆从下往上，箭头在顶端
            TitleGlyph.Upload => "M6.8,12.6 V2.2 M2.7,6.3 L6.8,2.1 L10.9,6.3",
            // 下载（平板 → PC）：杆从上往下，箭头在底端
            TitleGlyph.Download => "M6.8,1.4 V11.8 M2.7,7.7 L6.8,11.9 L10.9,7.7",
            // 剪贴板：外框 + 顶部夹子，两段子路径一次描边。坐标系 0..10，整体 k=1.2 后落在 13.5 的画布里。
            // 手机：窄高矩形（4.4 × 11.0）+ 底部横线当 Home 条；平板：宽扁矩形（10.4 × 8.8）+ 顶部横线当摄像头。
            // 两者只差长宽比与横线的上下位置，14px 下也分得开 —— 平板/手机的分辨率比例其实同向（都是竖屏采集），
            // 靠画面比例判断不可靠，这里直接画成两种体量。
            TitleGlyph.Phone => "M4.6,1.2 H9.0 V12.2 H4.6 Z M6.0,10.6 H7.6",
            TitleGlyph.Tablet => "M1.6,2.4 H12.0 V11.0 H1.6 Z M6.2,3.7 H7.4",
            _ => "M1.44,3.24 H10.56 V13.2 H1.44 Z M4.08,3.24 V1.32 H7.92 V3.24"
        };
        _glyph = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = Theme.TitleText,
            Stretch = Stretch.None
        };
        var canvas = new Canvas
        {
            Width = 13.6,
            Height = 13.4,
            VerticalAlignment = VerticalAlignment.Center
        };
        Canvas.SetLeft(_glyph, 0.1);
        Canvas.SetTop(_glyph, 0.1);
        canvas.Children.Add(_glyph);
        Child = canvas;

        MouseEnter += (_, _) => { _hover = true; Apply(); };
        MouseLeave += (_, _) => { _hover = false; Apply(); };
        MouseLeftButtonUp += (_, e) =>
        {
            // 2026-11-16：灰淡（未接入）也要能点击切换。旧行为「muted 就吞掉点击」
            // 会让用户点手机图标毫无反馈；是否响应改由订阅方决定（挂不挂 Clicked）。
            Clicked?.Invoke();
            e.Handled = true;
        };
        Apply();
    }

    /// <summary>是否是「当前正在查看的那台」。活动态用强调色描边 + 强调色图标，
    /// 与另一台的灰色区分开 —— 标题栏空间小，不能靠文字说明。</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }
            _active = value;
            Apply();
        }
    }

    /// <summary>成功 / 失败时的短暂变色，让用户知道这一下有没有生效（状态栏在纯画面模式下是隐藏的）。</summary>
    public void Flash(SolidColorBrush brush, int holdMs = 600)
    {
        _flash = brush;
        Apply();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_flash, brush))
            {
                _flash = null;
                Apply();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 「配置里有这台、但这台当前没接进来」（例如手机被禁用 / 采集不通）。
    /// 2026-11-14 要求两枚图标始终都在；2026-11-16 进一步要求**灰淡态也能点击切换**，
    /// 所以这里只表示视觉上的「未接入」，不再是「不可点」。
    /// </summary>
    public bool IsMuted
    {
        get => _muted;
        set
        {
            if (_muted == value)
            {
                return;
            }
            _muted = value;
            Cursor = Cursors.Hand;   // 未接入也可点选，别给「不可点」的箭头光标
            Opacity = value ? 0.38 : 1.0;
            Apply();
        }
    }

    private void Apply()
    {
        if (_muted)
        {
            // 灰淡只表示「未接入」，不是「不可点」。选中时仍给强调色描边，
            // 否则用户点了手机图标看不到任何反馈（2026-11-16）。
            Background = Theme.TitleChip;
            BorderBrush = _active ? Theme.Accent : Theme.TitleChipBorder;
            _glyph.Stroke = Theme.TitleText;
            return;
        }
        Background = _hover ? Theme.TitleChipHover : Theme.TitleChip;
        if (_active)
        {
            BorderBrush = Theme.Accent;
            _glyph.Stroke = _flash ?? Theme.Accent;
        }
        else
        {
            BorderBrush = Theme.TitleChipBorder;
            _glyph.Stroke = _flash ?? Theme.TitleText;
        }
    }
}
