using RimSearcher.Core;

namespace RimSearcher.Server;

// 查询与重建之间的读写协调。索引对象本身不替换（见 SourceIndexer.Clear），
// 所以这里不是为了保护引用切换，而是为了保证重建期间没有查询在读半成品索引。
//
// 刻意不做热替换：新旧两份索引并存会让内存翻倍（vanilla 单源就约 1 GB）。
// 宁可让重建期间的查询短暂挂起——实测全量重建约 4 秒。
//
// 刻意不用 ReaderWriterLockSlim：它要求获取与释放在同一线程，而读侧包住的是
// `await tool.ExecuteAsync(...)`——工具内部有真实挂起点（SearchRegexAsync 的
// Parallel.ForEachAsync、RoslynHelper 的 ReadToEndAsync 等），续体不保证回到原线程。
// 那样 ExitReadLock 会抛 SynchronizationLockException（被协议层吞成 -32603），
// 更糟的是原线程那份读锁计数永久泄漏，此后重建再也拿不到写锁，只能次次超时。
// 所以这里自己实现一个不绑线程的门：读侧异步等在 TaskCompletionSource 上，
// 写侧（Rebuild 必须保持同步签名，见 IndexRebuilder）阻塞等在 Monitor 上。
public static class IndexGate
{
    private static readonly object Sync = new();

    // 等着被放行的读者。只在重建窗口里非空，且上层并发闸把在途请求限到 10 个，故不会堆积。
    private static readonly Queue<ReadWaiter> ReadQueue = new();

    private static int _readers;
    private static bool _writerActive;

    // 有写者在等就不再放新读者进来。否则源源不断的查询能让重建永远等不到读者清零。
    private static int _writersWaiting;

    // 重建期间进来的查询等这么久。等不到就必须报错，不能放行：重建是原地 Clear + 重扫
    // （见 IndexRebuilder.Rebuild），此刻根本没有「旧的一份」可读，放进去的查询会拿到
    // 一个已冻结状态被重置的空壳，也就是一个看起来成功的错误答案。
    // 非 readonly 仅为让测试能在几十毫秒内走到超时分支，产品代码不应改它。
    internal static TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private static long _generation;

    // 每次重建自增。会话据此判断自己上次查询是否发生在更早的索引世代上。
    public static long Generation => Interlocked.Read(ref _generation);

    public static bool IsRebuilding { get; private set; }

    // 返回 null 表示等到超时仍未拿到读权（重建还没结束），调用方应回一个可重试的错误。
    // ct 取消则抛 OperationCanceledException：那是「这个请求不要了」，和「索引还没好」
    // 是两码事，协议层据此回 -32800 而不是那条重试提示。
    public static async Task<IDisposable?> TryEnterReadAsync(CancellationToken ct = default)
    {
        ReadWaiter waiter;

        lock (Sync)
        {
            if (!_writerActive && _writersWaiting == 0)
            {
                _readers++;
                return new ReadScope();
            }

            waiter = new ReadWaiter();
            ReadQueue.Enqueue(waiter);
        }

        try
        {
            await waiter.Completion.Task.WaitAsync(ReadTimeout, ct).ConfigureAwait(false);
            return new ReadScope();
        }
        catch (TimeoutException)
        {
            // 和放行赛跑输了：读者计数已经替我们加上，丢掉这次机会等于永久泄漏一份，
            // 不如就当拿到了——反正门此刻确实是开的。
            if (!TryAbandon(waiter)) return new ReadScope();
            return null;
        }
        catch (OperationCanceledException)
        {
            // 同一个赛跑，但调用方已经不要这次读了，必须把计数还回去，否则重建等不到清零
            if (!TryAbandon(waiter)) ExitRead();
            throw;
        }
    }

    // 返回 false 表示没抢到写锁（另一次重建正在进行，或读者迟迟不散），调用方应跳过本次重建。
    // 保持同步签名：SyncSourcesTool 经 IndexRebuilder.Rebuild 同步调用它。
    public static bool Rebuild(Action rebuild, TimeSpan timeout)
    {
        if (!EnterWrite(timeout)) return false;

        IsRebuilding = true;
        try
        {
            rebuild();
            Interlocked.Increment(ref _generation);
            return true;
        }
        finally
        {
            IsRebuilding = false;
            ExitWrite();
        }
    }

    private static bool EnterWrite(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var acquired = false;
        List<ReadWaiter>? granted = null;

        lock (Sync)
        {
            _writersWaiting++;
            try
            {
                // Monitor.Wait 会放开 Sync，期间新读者只会排队（_writersWaiting > 0 挡着），
                // 在场的读者退出到零时把我们叫醒，故不存在互相等死。
                while (_writerActive || _readers > 0)
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) break;
                    if (!Monitor.Wait(Sync, TimeSpan.FromMilliseconds(remaining))) break;
                }

                if (!_writerActive && _readers == 0)
                {
                    _writerActive = true;
                    acquired = true;
                }
            }
            finally
            {
                _writersWaiting--;

                // 我们放弃了等待，门却空着：排队的读者得有人放行，否则它们只能白等到超时
                if (!acquired && !_writerActive && _writersWaiting == 0) granted = GrantReaders();
            }
        }

        CompleteWaiters(granted);
        return acquired;
    }

    private static void ExitWrite()
    {
        List<ReadWaiter>? granted;

        lock (Sync)
        {
            _writerActive = false;

            // 先叫醒排队的写者；有写者在等就不放读者，让它接着重建，免得读写反复拉锯
            Monitor.PulseAll(Sync);
            granted = _writersWaiting == 0 ? GrantReaders() : null;
        }

        CompleteWaiters(granted);
    }

    private static void ExitRead()
    {
        lock (Sync)
        {
            _readers--;

            // 只有清零才可能让等着的写者动起来，中间的递减没人关心
            if (_readers == 0) Monitor.PulseAll(Sync);
        }
    }

    // 返回 false 表示放行已经发生（读者计数已加），调用方必须自己处置这份读权
    private static bool TryAbandon(ReadWaiter waiter)
    {
        lock (Sync)
        {
            if (waiter.Granted) return false;

            // 队列里的这一项留给 GrantReaders 顺手丢掉：Queue 不支持中途摘除，
            // 而重建结束时必然会走一遍排空。
            waiter.Abandoned = true;
            return true;
        }
    }

    // 必须持 Sync 调用。放行时就把读者计数加上，免得「已放行但续体还没跑起来」的窗口里
    // 写者误判读者已清零，一头闯进去清索引。
    private static List<ReadWaiter>? GrantReaders()
    {
        List<ReadWaiter>? granted = null;

        while (ReadQueue.Count > 0)
        {
            var waiter = ReadQueue.Dequeue();
            if (waiter.Abandoned) continue;

            waiter.Granted = true;
            _readers++;
            (granted ??= []).Add(waiter);
        }

        return granted;
    }

    private static void CompleteWaiters(List<ReadWaiter>? waiters)
    {
        if (waiters == null) return;
        foreach (var waiter in waiters) waiter.Completion.TrySetResult();
    }

    private sealed class ReadWaiter
    {
        // RunContinuationsAsynchronously：放行是在持 Sync 的路径上决定的，续体若同步跑
        // 就会把整个工具执行拖进锁里。
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 以下两个只在持 Sync 时读写
        public bool Granted;
        public bool Abandoned;
    }

    // 释放刻意不依赖线程身份：Dispose 跑在 await 之后的续体上，未必还是获取时那个线程
    private sealed class ReadScope : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            ExitRead();
        }
    }
}

// 拥有索引与源路径，负责就地重扫。Clear 之后显式 GC：不回收的话旧字典还在，
// 重建期间新旧并存，等于没省下热替换要付的那份内存。
public sealed class IndexRebuilder
{
    private readonly SourceIndexer _sourceIndexer;
    private readonly DefIndexer _defIndexer;
    private readonly ResolvedSources _sources;

    public IndexRebuilder(SourceIndexer sourceIndexer, DefIndexer defIndexer, ResolvedSources sources)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _sources = sources;
    }

    public RebuildResult Rebuild(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var csharpCount = 0;
        var xmlCount = 0;

        var acquired = IndexGate.Rebuild(() =>
        {
            _sourceIndexer.Clear();
            _defIndexer.Clear();

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
            GC.WaitForPendingFinalizers();

            foreach (var entry in _sources.Csharp)
            {
                if (!Directory.Exists(entry.Path)) continue;
                _sourceIndexer.Scan(entry.Path, _sources.Shadowed);
                csharpCount++;
            }

            foreach (var entry in _sources.Xml)
            {
                if (!Directory.Exists(entry.Path)) continue;
                _defIndexer.Scan(entry.Path, _sources.Shadowed);
                _sourceIndexer.Scan(entry.Path, _sources.Shadowed);
                xmlCount++;
            }

            _sourceIndexer.FreezeIndex();
            _defIndexer.FreezeIndex();
        }, timeout);

        stopwatch.Stop();

        return new RebuildResult
        {
            Succeeded = acquired,
            CsharpPaths = csharpCount,
            XmlPaths = xmlCount,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }
}

public sealed record RebuildResult
{
    public required bool Succeeded { get; init; }
    public int CsharpPaths { get; init; }
    public int XmlPaths { get; init; }
    public long ElapsedMs { get; init; }
}
