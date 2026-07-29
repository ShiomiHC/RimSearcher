using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// IndexGate 是进程级静态状态，这些用例会真的把写锁攥在手里，故禁掉与其他类的并行
[Collection("IndexGate")]
public class IndexGateTests
{
    [Fact]
    public async Task TryEnterReadAsync_WithoutRebuild_Succeeds()
    {
        var scope = await IndexGate.TryEnterReadAsync();
        Assert.NotNull(scope);
        scope.Dispose();
    }

    [Fact]
    public async Task TryEnterReadAsync_DoesNotBlockOtherReaders()
    {
        // 读权之间不互斥：并发查询不该互相排队
        var first = await IndexGate.TryEnterReadAsync();
        Assert.NotNull(first);

        var second = await Task.Run(() => IndexGate.TryEnterReadAsync());
        Assert.NotNull(second);

        second.Dispose();
        first.Dispose();
    }

    // 这是「异步工具跨 await 持有线程绑定读锁」的回归：读权曾用 ReaderWriterLockSlim，
    // 它要求获取与释放在同一线程，而调用方是 `await tool.ExecuteAsync(...)` 之后的续体。
    // 换线程释放会抛 SynchronizationLockException，且原线程那份计数永久泄漏。
    [Fact]
    public async Task ReadScope_CanBeReleasedOnAnotherThread()
    {
        var scope = await IndexGate.TryEnterReadAsync();
        Assert.NotNull(scope);

        Exception? failure = null;
        var releaser = new Thread(() =>
        {
            try { scope.Dispose(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };

        releaser.Start();
        Assert.True(releaser.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);

        // 计数真的还回去了才拿得到写锁；泄漏的话这里只能等到超时
        Assert.True(IndexGate.Rebuild(() => { }, TimeSpan.FromSeconds(1)));
    }

    // 这是 #6 的回归：旧实现超时返回 NoopScope，调用方分不出「拿到了」和「没拿到」，
    // 于是在索引被清空的窗口里照常查询，拿到空结果却报成功。
    [Fact]
    public async Task TryEnterReadAsync_WhileRebuilding_TimesOutInsteadOfLettingTheQueryThrough()
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

            Assert.Null(await IndexGate.TryEnterReadAsync());
            Assert.True(IndexGate.IsRebuilding);

            releaseRebuild.Set();
            Assert.True(await rebuild.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            IndexGate.ReadTimeout = original;
        }

        // 重建结束后立刻恢复放行
        var after = await IndexGate.TryEnterReadAsync();
        Assert.NotNull(after);
        after.Dispose();
    }

    // 排队等读权期间必须观察 ct：重建窗口可能长达数秒，客户端撤单了还干等满 ReadTimeout
    // 就等于占着并发闸的一个名额空转。
    [Fact]
    public async Task TryEnterReadAsync_WhileRebuilding_ObservesCancellationWithoutWaitingOutTheTimeout()
    {
        var original = IndexGate.ReadTimeout;
        IndexGate.ReadTimeout = TimeSpan.FromSeconds(30);

        try
        {
            using var rebuildStarted = new ManualResetEventSlim(false);
            using var releaseRebuild = new ManualResetEventSlim(false);

            var rebuild = Task.Run(() => IndexGate.Rebuild(() =>
            {
                rebuildStarted.Set();
                releaseRebuild.Wait();
            }, TimeSpan.FromSeconds(10)));

            Assert.True(rebuildStarted.Wait(TimeSpan.FromSeconds(5)));

            using var cts = new CancellationTokenSource();
            var pending = IndexGate.TryEnterReadAsync(cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            elapsed.Stop();

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"取消后等了 {elapsed.ElapsedMilliseconds} ms，说明是靠 ReadTimeout 兜底而非观察 ct");

            releaseRebuild.Set();
            Assert.True(await rebuild.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            IndexGate.ReadTimeout = original;
        }

        // 取消掉的那个等待者不能把读者计数留在门里
        Assert.True(IndexGate.Rebuild(() => { }, TimeSpan.FromSeconds(1)));
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

    // 重建必须等到读者全散。中途拿到写锁就意味着有查询正在读被 Clear 掉一半的索引。
    [Fact]
    public async Task Rebuild_WaitsForAllReadersToDrain_ThenBumpsGenerationOnce()
    {
        var before = IndexGate.Generation;

        var scopes = new List<IDisposable>();
        for (var i = 0; i < 4; i++)
        {
            var scope = await IndexGate.TryEnterReadAsync();
            Assert.NotNull(scope);
            scopes.Add(scope);
        }

        var rebuild = Task.Run(() => IndexGate.Rebuild(() => { }, TimeSpan.FromSeconds(10)));

        // 让重建先真的排到等读者清零那一步，否则下面的交错断言测不到东西
        await Task.Delay(100);

        foreach (var scope in scopes)
        {
            Assert.False(rebuild.IsCompleted, "还有读者在场，重建就已经拿到写锁了");
            scope.Dispose();
        }

        Assert.True(await rebuild.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(before + 1, IndexGate.Generation);
    }

    // 「一定会挂起、且一定在别的线程上恢复」的等待点。
    //
    // 自己实现 awaiter 而不是用 TaskCompletionSource，是因为 TCS 那种写法是**有竞态的**：
    // `await` 先看 `IsCompleted`，任务已完成就压根不挂起、续体就地跑完——于是「换线程」这件事
    // 取决于后台线程能不能慢过主线程走到 await，而那是调度器说了算。原先靠 `Thread.Sleep(50)`
    // 押这一头，主线程被挤走超过 50ms 时 `EnterThread == ExitThread`，用例的前提断言当场翻。
    // 实测过：把 SetResult 提到 await 之前，红的正是那一条（`Assert.NotEqual() Failure`）。
    //
    // 这里 `IsCompleted` 恒 false，故 await 一定走 `OnCompleted`；续体交给一条新线程跑，
    // 故一定换线程。两件事都不再与时序有关。
    //
    // 也不捕获同步上下文（`INotifyCompletion` 由我们自己实现，续体去哪儿由 OnCompleted 说了算），
    // 故不需要、也不能再挂 `ConfigureAwait(false)`。
    private readonly struct HopsToAnotherThread : INotifyCompletion
    {
        public HopsToAnotherThread GetAwaiter() => this;

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
            => new Thread(() => continuation()) { IsBackground = true }.Start();

        public void GetResult() { }
    }

    // 真的会在 await 之后换线程的工具。线上的挂起点是 Parallel.ForEachAsync / ReadToEndAsync。
    private sealed class ThreadHoppingTool : ITool
    {
        public int EnterThread;
        public int ExitThread;

        public string Name => "hop";
        public string Description => "test tool";
        public object JsonSchema => new { type = "object" };

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            EnterThread = Environment.CurrentManagedThreadId;

            await new HopsToAnotherThread();

            ExitThread = Environment.CurrentManagedThreadId;
            return new ToolResult("hopped");
        }
    }

    // 关键回归，走完整协议路径（参 ProtocolTests）：工具在 await 之后换了线程，
    // 读权的释放不能因此失败。旧实现在这里 ExitReadLock 抛 SynchronizationLockException，
    // 被 DispatchLineAsync 吞成 -32603，同时把一份读者计数永久留在门里。
    [Fact]
    public async Task ToolsCall_WithToolThatSuspendsAcrossThreads_SucceedsAndLeaksNoReader()
    {
        var tool = new ThreadHoppingTool();
        var output = new StringWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);
        server.RegisterTool(tool);

        await server.RunAsync(new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"hop","arguments":{}}}"""));

        var responses = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line.Trim()).RootElement.Clone())
            .Where(element => element.TryGetProperty("id", out _))
            .ToList();

        var response = Assert.Single(responses);

        Assert.False(response.TryGetProperty("error", out _),
            "工具换线程后释放读权失败，被吞成了 -32603");

        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("hopped", result.GetProperty("content")[0].GetProperty("text").GetString());

        // 用例本身有效的前提：续体确实换了线程
        Assert.NotEqual(tool.EnterThread, tool.ExitThread);

        // 计数没泄漏才拿得到写锁
        Assert.True(IndexGate.Rebuild(() => { }, TimeSpan.FromSeconds(1)));
    }
}
