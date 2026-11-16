using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace HarmOsWin.App;

/// <summary>
/// 单实例接管（用户 2026-11-11 00:49 要求：「当我打开新的，会自动关闭旧的」）。
///
/// 为什么必须做：本工程的运行期资源是**独占**的 ——
///   · hdc 通道严格串行，两个进程同时打 hdc 会互相把对方的会话踢掉（表现为「点了没反应」）；
///   · 参考采集要占设备端扩展 daemon + fport 规则，两套实例会抢同一批端口；
///   · 单文件 exe 覆盖后旧进程仍跑内存里的旧映像（Windows 不会热替换），
///     用户容易「以为已经是新版」，实际在旧代码上报问题。
///
/// 策略是**接管**而不是「激活旧窗口」：新进程把旧进程关掉，自己成为唯一实例。
/// 关闭优先走 CloseMainWindow（WM_CLOSE），让旧进程的 Closed 处理器跑完同步清理
/// （hdc fport rm + 杀设备端扩展 daemon），超时才 Kill 整棵进程树。
///
/// 锁用命名 Mutex 串行化竞态：两个新进程同时启动时只有一个能进，
/// 另一个先关掉前者、再拿锁 —— 不会出现 A 杀 B 的同时 B 杀 A、双双阵亡。
/// </summary>
internal static class InstanceGuard
{
    private const string MutexName = @"Local\HarmOsWin.InstanceGuard.Single";

    /// <summary>旧进程优雅退出的最长等待（清理含若干次 hdc 调用，实测 1~3s）。</summary>
    private const int GracefulCloseMs = 8000;

    /// <summary>优雅退出失败后强杀，并等它真正消失的上限。</summary>
    private const int KillWaitMs = 2000;

    /// <summary>整个接管流程的总预算；超时也必须放行，不能把用户卡在启动上。</summary>
    private const int OverallBudgetMs = 15000;

    // 必须静态持有：Mutex 被 GC 回收就等于释放锁。
    private static Mutex? _held;

    /// <summary>本进程是否已拿到实例锁（诊断用；同时保证 _held 不会被判为「只赋值不使用」）。</summary>
    public static bool IsHeld => _held is not null;

    /// <summary>
    /// 在创建主窗口之前调用。返回时保证：同名旧进程已退出，且本进程持有实例锁（正常路径）。
    /// 永不抛异常 —— 接管失败也要让程序能起来。
    /// </summary>
    public static void TakeOver()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                if (TryAcquire())
                {
                    // 拿到锁不代表没有残留：崩溃退出、或旧版本（无本守卫）的进程不持有这把锁。
                    CloseForeignInstances(sw);
                    return;
                }

                // 还有实例持锁 → 关掉它，然后重抢。
                CloseForeignInstances(sw);

                if (sw.ElapsedMilliseconds >= OverallBudgetMs)
                {
                    // 预算耗尽仍抢不到：直接放行，宁可双开也不要卡死在启动阶段。
                    return;
                }

                Thread.Sleep(120);
            }
        }
        catch
        {
            // 守卫自身故障不得阻断启动。
        }
    }

    private static bool TryAcquire()
    {
        Mutex m;
        try
        {
            m = new Mutex(initiallyOwned: false, MutexName);
        }
        catch
        {
            // 极端情况（命名对象被禁用）下退化为无保护模式，仍然继续启动。
            return true;
        }

        try
        {
            if (m.WaitOne(0))
            {
                _held = m;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者被强杀，锁已归我们。
            _held = m;
            return true;
        }

        m.Dispose();
        return false;
    }

    /// <summary>关掉所有同名的其它进程（不含自己），优雅优先、必要时强杀。</summary>
    private static void CloseForeignInstances(Stopwatch sw)
    {
        int self = Environment.ProcessId;
        string name = Path.GetFileNameWithoutExtension(
            Environment.ProcessPath ?? "HarmOsWin");

        Process[] all;
        try
        {
            all = Process.GetProcessesByName(name);
        }
        catch
        {
            return;
        }

        var victims = new System.Collections.Generic.List<Process>();
        foreach (var p in all)
        {
            try
            {
                if (p.Id == self)
                {
                    p.Dispose();
                    continue;
                }
                victims.Add(p);
            }
            catch
            {
                // 进程已消失或无权访问
            }
        }

        if (victims.Count == 0)
        {
            return;
        }

        // 第一步：礼貌敲门，让旧进程跑完它的 Closed 清理。
        // 只有「确实有主窗口、WM_CLOSE 已送达」的进程才值得等；其余直接进强杀，
        // 否则旧进程还停在启动阶段（主窗口尚未创建）时会白等满 8 秒。
        var polite = new System.Collections.Generic.HashSet<int>();
        foreach (var p in victims)
        {
            try
            {
                if (p.CloseMainWindow())
                {
                    polite.Add(p.Id);
                }
            }
            catch
            {
            }
        }

        // 第二步：等它自己退出。给足时间，因为清理里有 hdc 往返。
        long remainBudget = OverallBudgetMs - sw.ElapsedMilliseconds;
        long waitMs = System.Math.Clamp(remainBudget, 500, GracefulCloseMs);
        var deadline = System.Environment.TickCount64 + waitMs;

        foreach (var p in victims)
        {
            if (!polite.Contains(p.Id))
            {
                continue;
            }
            try
            {
                while (System.Environment.TickCount64 < deadline)
                {
                    if (p.HasExited)
                    {
                        break;
                    }
                    Thread.Sleep(80);
                }
            }
            catch
            {
            }
        }

        // 第三步：还没走就强杀整棵进程树（连它派生的 hdc 子进程一起收掉）。
        foreach (var p in victims)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(KillWaitMs);
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        // 让 Windows 释放被映射的 exe 映像与套接字句柄，再让新实例去连设备。
        Thread.Sleep(200);
    }
}
