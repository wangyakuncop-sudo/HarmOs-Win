namespace HarmOsWin.Core;

/// <summary>
/// 全进程 hdc 串行闸门。
///
/// 为什么必须有：hdc 客户端进程之间共享一个 hdcd server（本机固定 8710），
/// 并发调用必定互踢（INV-02，单设备时代就已经踩过）。双路并行后问题升级成
/// **fport 子命令根本不看 `-t`**：
///
///   `hdc fport ls` 实测（2026-11-13 23:47）44 条转发规则**全部挂在同一台设备**
///   （192.168.1.100）上，另一台一条都没有。也就是说 `hdc -t A fport ...` 的
///   目标参数被忽略，规则一律落到 server 的「当前默认目标」——哪个设备最后一次
///   被 tconn / 带 -t 的命令访问过，就是它。
///
/// 后果：两路各自重连时，A 路建的转发规则落到 B 设备上，B 路再来一次又落回去，
/// 双方轮流覆盖 → gRPC 流每 2.3 秒被重置一次，画面看起来在闪断。
///
/// 修法两条，本类只负责第一条：
///   ① 所有 hdc 进程调用串行，任意时刻只有一条在跑；
///   ② fport 段必须「tconn → rm → add」在**同一个锁**里连续完成（见 HdcBatchAsync）。
/// </summary>
public static class HdcSerial
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>当前等待串行的调用数（仅诊断）。</summary>
    public static int Waiting => Gate.CurrentCount == 1 ? 0 : 1;

    /// <summary>
    /// 获取全局串行锁。
    ///
    /// 2026-11-15 加超时：之前无超时，一旦某条调用忘记释放（或持锁线程异常退出），
    /// 后续所有 hdc 调用会永久等待，表现为「启动流程卡在某条命令且无任何日志」。
    /// 现在 15s 内拿不到就抛 TimeoutException，让上层能看到明确原因。
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        CancellationToken ct = default, int timeoutMs = 15_000)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        try
        {
            await Gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"hdc 串行锁 {timeoutMs}ms 未获取到（可能上一条调用未释放）");
        }
        return new Leaser();
    }

    /// <summary>
    /// 同步版，给那些改不成 async 的启动期探测代码用（HdcTargetResolver）。
    /// 超时返回 null —— 调用方按「加锁失败」降级，绝不能因为抢不到锁把启动流程挂住。
    /// </summary>
    public static IDisposable? TryAcquire(int timeoutMs = 10_000)
    {
        return Gate.Wait(timeoutMs) ? new Leaser() : null;
    }

    private sealed class Leaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }
}
