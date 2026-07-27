using RimSearcher.Server;

namespace RimSearcher.Tests;

// IndexGate 是进程级静态状态，这些用例会真的把写锁攥在手里，故禁掉与其他类的并行
[Collection("IndexGate")]
public class IndexGateTests
{
    [Fact]
    public void TryEnterRead_WithoutRebuild_Succeeds()
    {
        Assert.True(IndexGate.TryEnterRead(out var scope));
        Assert.NotNull(scope);
        scope!.Dispose();
    }

    [Fact]
    public void TryEnterRead_IsReentrantAcrossThreads()
    {
        // 读锁之间不互斥：并发查询不该互相排队
        Assert.True(IndexGate.TryEnterRead(out var first));

        var secondAcquired = false;
        var other = new Thread(() =>
        {
            secondAcquired = IndexGate.TryEnterRead(out var second);
            second?.Dispose();
        });
        other.Start();
        other.Join();

        first!.Dispose();
        Assert.True(secondAcquired);
    }

    // 这是 #6 的回归：旧实现超时返回 NoopScope，调用方分不出「拿到了」和「没拿到」，
    // 于是在索引被清空的窗口里照常查询，拿到空结果却报成功。
    [Fact]
    public async Task TryEnterRead_WhileRebuilding_TimesOutInsteadOfLettingTheQueryThrough()
    {
        var original = IndexGate.ReadTimeout;
        IndexGate.ReadTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            using var rebuildStarted = new ManualResetEventSlim(false);
            using var releaseRebuild = new ManualResetEventSlim(false);

            var rebuild = Task.Run(() => IndexGate.Rebuild(() =>
            {
                rebuildStarted.Set();
                releaseRebuild.Wait();
            }, TimeSpan.FromSeconds(5)));

            Assert.True(rebuildStarted.Wait(TimeSpan.FromSeconds(5)));

            Assert.False(IndexGate.TryEnterRead(out var scope));
            Assert.Null(scope);
            Assert.True(IndexGate.IsRebuilding);

            releaseRebuild.Set();
            Assert.True(await rebuild.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            IndexGate.ReadTimeout = original;
        }

        // 重建结束后立刻恢复放行
        Assert.True(IndexGate.TryEnterRead(out var after));
        after!.Dispose();
    }

    [Fact]
    public void Rebuild_BumpsGeneration_OnlyOnSuccess()
    {
        var before = IndexGate.Generation;
        Assert.True(IndexGate.Rebuild(() => { }, TimeSpan.FromSeconds(5)));
        Assert.Equal(before + 1, IndexGate.Generation);
    }

    // 第二次重建拿不到写锁时必须跳过，而不是排队再跑一遍
    [Fact]
    public async Task Rebuild_WhileAnotherIsRunning_IsSkipped()
    {
        using var firstStarted = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);

        var first = Task.Run(() => IndexGate.Rebuild(() =>
        {
            firstStarted.Set();
            releaseFirst.Wait();
        }, TimeSpan.FromSeconds(5)));

        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        var secondRan = false;
        var second = IndexGate.Rebuild(() => secondRan = true, TimeSpan.FromMilliseconds(50));

        Assert.False(second);
        Assert.False(secondRan);

        releaseFirst.Set();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
