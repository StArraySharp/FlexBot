namespace FlexBot;

/// <summary>
/// ② 拆分：优雅停机的状态机（原先散在 Program 的 4 个局部变量 + 2 个局部函数里）。
/// 与宿主资源**解耦** —— 真正的收尾动作（卸插件 / 断通道 / 清理子进程）由调用方以 Func&lt;Task&gt; 传入，
/// 因此它可以被单测：并发收尾只执行一次、重复触发只生效一次、等待只完成一次。
/// </summary>
internal sealed class ShutdownCoordinator
{
    private readonly Action<int> _exit;

    /// <summary>exit 可注入：默认真的退出进程，测试里换成空动作 —— 否则"重复触发只生效一次"根本没法验证。</summary>
    public ShutdownCoordinator(Action<int>? exit = null) => _exit = exit ?? (code => Environment.Exit(code));

    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task? _cleanupTask;
    private int _shuttingDown;

    /// <summary>是否已经开始停机（读取用，幂等）</summary>
    public bool ShuttingDown => Volatile.Read(ref _shuttingDown) != 0;

    /// <summary>等停机信号（主流程 await 它）</summary>
    public Task WaitAsync() => _tcs.Task;

    /// <summary>只置位，不触发收尾（主流程正常走到出口时用）</summary>
    public void MarkShuttingDown() => Interlocked.Exchange(ref _shuttingDown, 1);

    /// <summary>幂等收尾：并发调用只会真正执行一次 cleanup</summary>
    public Task CleanupAsync(Func<Task> cleanup)
    {
        lock (_gate) _cleanupTask ??= cleanup();
        return _cleanupTask;
    }

    /// <summary>
    /// 首次触发才生效（重复信号直接返回）。随后在后台收尾并强制退出 ——
    /// 主流程可能被阻塞（stdin 重定向、窗口模式等），所以不能只依赖它自己走完。
    /// </summary>
    public void RequestShutdown(string reason, Func<Task> cleanup)
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        Console.WriteLine($"[shutdown] 收到 {reason}，开始优雅停机…");
        _tcs.TrySetResult(true);
        _ = Task.Run(async () =>
        {
            try
            {
                var task = CleanupAsync(cleanup);
                var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
                if (!ReferenceEquals(done, task)) Console.WriteLine("[shutdown] 收尾超时，直接退出");
            }
            catch (Exception ex) { Console.WriteLine($"[shutdown] 收尾异常: {ex.Message}"); }
            Console.WriteLine("[+] Closed.");
            _exit(0);
        });
    }
}
