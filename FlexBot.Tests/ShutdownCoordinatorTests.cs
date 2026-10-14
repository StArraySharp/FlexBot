using System.Threading;
using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// 优雅停机的状态机（② 拆分后从 Program 的局部函数变成可测类）。
/// exit 注入空动作：测试里绝不能真的退出进程。
/// </summary>
public class ShutdownCoordinatorTests
{
    [Fact]
    public async Task 并发收尾只执行一次()
    {
        var c = new ShutdownCoordinator(_ => { });
        var count = 0;
        Func<Task> cleanup = () => { Interlocked.Increment(ref count); return Task.Delay(50); };
        var tasks = Enumerable.Range(0, 8).Select(_ => c.CleanupAsync(cleanup)).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(1, count);                       // 8 个并发调用，只真正收尾一次
    }

    [Fact]
    public async Task 触发一次后等待信号完成且收尾只跑一次()
    {
        var c = new ShutdownCoordinator(_ => { });
        var count = 0;
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> cleanup = () => { Interlocked.Increment(ref count); ran.TrySetResult(true); return Task.CompletedTask; };

        c.RequestShutdown("测试-A", cleanup);
        c.RequestShutdown("测试-B", cleanup);          // 重复触发应被忽略
        c.RequestShutdown("测试-C", cleanup);

        await Task.WhenAny(ran.Task, Task.Delay(2000));
        Assert.True(ran.Task.IsCompleted, "收尾没有在 2 秒内开始");
        Assert.True(c.WaitAsync().IsCompleted);        // 停机信号已置位
        await Task.Delay(120);
        Assert.Equal(1, count);                        // 三次触发，一次收尾
        Assert.True(c.ShuttingDown);
    }

    [Fact]
    public async Task 已置停机位后不会重复收尾()
    {
        var c = new ShutdownCoordinator(_ => { });
        c.MarkShuttingDown();
        Assert.True(c.ShuttingDown);

        var count = 0;
        c.RequestShutdown("测试", () => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        await Task.Delay(120);
        Assert.Equal(0, count);                        // 已经停机过，不再触发第二次收尾
    }

    [Fact]
    public async Task 收尾抛异常不影响等待信号置位()
    {
        var c = new ShutdownCoordinator(_ => { });
        await Assert.ThrowsAnyAsync<Exception>(() => c.CleanupAsync(() => throw new InvalidOperationException("boom")));
        Assert.False(c.WaitAsync().IsCompleted);       // 异常不静默吞掉，也不误置停机位
    }
}
