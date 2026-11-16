using System.Globalization;
using System.Text.RegularExpressions;

namespace HarmOsWin.Core;

/// <summary>
/// 设备亮度控制 —— 走「拉出控制中心 → 点击亮度条」的触摸注入路径。
///
/// 为什么不用系统 API：HarmonyOS 对第三方关闭了所有全局亮度入口，四条都实测判死
/// （见架构补记㊹）：@ohos.settings 写 SCREEN_BRIGHTNESS_STATUS 要 system_basic、
/// @system.brightness 全 deprecated、hidumper 只读、window.setWindowBrightness 退后台失效。
/// 唯一不需要任何受限权限的路，是用输入注入去点系统自己的控制中心。
///
/// 关键实测结论（2026-11-13，MatePad 1600×2560，HarmonyOS 7.0.0.111）：
///   ① 拉出控制中心：<c>uinput -T -m 1350 5 1350 2450</c>，约 2s 生效；
///   ② 拖动必须用 <c>uitest uiInput swipe</c>（uinput -T -m/-g 在本 ROM 拉不动 slider），
///      但 swipe 的**起点必须正好落在滑块上**，否则手势被整个丢弃 —— 起点难算，弃用；
///   ③ <c>uitest uiInput click x 730</c> 可以直接把亮度设到该位置，**不依赖起点**，
///      因此这里只用 click。亮度条 y=730，轨道 x∈[1180,1500]；
///   ④ 每次操作后控制中心自动收起，所以每次都要重新拉出（命令里已串好）。
/// </summary>
public sealed class DeviceBrightness
{
    private readonly InputInjector _injector;

    /// <summary>亮度条在控制中心里的纵向位置（设备物理坐标）。</summary>
    public const int PanelY = 730;

    /// <summary>控制中心拉出/收起用的手势端点（右侧空白区，避开图标）。</summary>
    private const int GestureX = 1350;

    public const int MinBrightness = 1;
    public const int MaxBrightness = 255;

    /// <summary>没有"调暗前记录"时的恢复值：系统 Default Brightness（hidumper 报 Default=102）。</summary>
    public const int FallbackBrightness = 102;

    // 实测标定表（click x → Brightness）。低端被系统压缩，不是线性的，必须查表插值：
    //   x=1180→1   1200→4   1260→28   1300→54   1350→97   1420→176   1460→230   1500→255
    // 2026-11-13 逐点实测，8 点全部单调且可复现。
    private static readonly int[] CalX = { 1180, 1200, 1260, 1300, 1350, 1420, 1460, 1500 };
    private static readonly int[] CalB = { 1, 4, 28, 54, 97, 176, 230, 255 };

    private static readonly Regex BrightnessRx =
        new(@"\bBrightness=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public DeviceBrightness(InputInjector injector) => _injector = injector;

    /// <summary>亮度值 → 亮度条上的点击位置（分段线性插值，越界钳到两端）。</summary>
    public static int BrightnessToX(int brightness)
    {
        if (brightness <= CalB[0])
        {
            return CalX[0];
        }
        if (brightness >= CalB[^1])
        {
            return CalX[^1];
        }
        for (int i = 0; i < CalB.Length - 1; i++)
        {
            if (brightness <= CalB[i + 1])
            {
                double t = (double)(brightness - CalB[i]) / (CalB[i + 1] - CalB[i]);
                return (int)Math.Round(CalX[i] + t * (CalX[i + 1] - CalX[i]));
            }
        }
        return CalX[^1];
    }

    /// <summary>亮度条位置 → 亮度值（反向标定，供调试与日志）。</summary>
    public static int XToBrightness(int x)
    {
        if (x <= CalX[0])
        {
            return CalB[0];
        }
        if (x >= CalX[^1])
        {
            return CalB[^1];
        }
        for (int i = 0; i < CalX.Length - 1; i++)
        {
            if (x <= CalX[i + 1])
            {
                double t = (double)(x - CalX[i]) / (CalX[i + 1] - CalX[i]);
                return (int)Math.Round(CalB[i] + t * (CalB[i + 1] - CalB[i]));
            }
        }
        return CalB[^1];
    }

    /// <summary>读当前亮度（0~255）。失败返回 null。</summary>
    public async Task<int?> ReadAsync()
    {
        var r = await _injector.ShellAsync("hidumper -s DisplayPowerManagerService").ConfigureAwait(false);
        return r.Ok ? Parse(r.Output) : null;
    }

    private static int? Parse(string output)
    {
        var m = BrightnessRx.Match(output);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                                        CultureInfo.InvariantCulture, out int v)
            ? v
            : null;
    }

    /// <summary>
    /// 把亮度设到 target（0~255 钳制）。一次 hdc 往返内完成「收起旧面板 → 拉出 → 点击 → 回读」。
    /// 返回值里带实测后的亮度，便于上层显示「已调到 3」而不是自己猜。
    /// </summary>
    public async Task<(bool Ok, int Value, string Output)> SetAsync(int target)
    {
        int clamped = Math.Clamp(target, MinBrightness, MaxBrightness);
        int x = BrightnessToX(clamped);

        // 收起的上滑是必要的：面板上一轮拖完已自动收起，但如果用户手动打开过控制中心，
        // 直接下拉会把它关掉，导致后面那一 click 点在全屏应用上。
        string cmd =
            $"uinput -T -m {GestureX} 2450 {GestureX} 5; sleep 0.6; " +
            $"uinput -T -m {GestureX} 5 {GestureX} 2450; sleep 1.8; " +
            $"uitest uiInput click {x} {PanelY}; sleep 0.6; " +
            "hidumper -s DisplayPowerManagerService";

        var r = await _injector.ShellAsync(cmd).ConfigureAwait(false);
        int? after = Parse(r.Output);

        // 回读与目标差太远说明那一下点空了（面板没拉出来、或换了分辨率/布局），重试一次。
        if (!r.Ok || after is null || Math.Abs(after.Value - clamped) > 12)
        {
            var retry = await _injector.ShellAsync(
                $"uinput -T -m {GestureX} 2450 {GestureX} 5; sleep 0.6; " +
                $"uinput -T -m {GestureX} 5 {GestureX} 2450; sleep 1.8; " +
                $"uitest uiInput click {x} {PanelY}; sleep 0.6; " +
                "hidumper -s DisplayPowerManagerService").ConfigureAwait(false);
            int? again = Parse(retry.Output);
            if (again is not null)
            {
                after = again;
                r = retry;
            }
        }

        return (r.Ok && after is not null, after ?? -1, r.Output);
    }
}
