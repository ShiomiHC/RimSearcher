using System.Diagnostics.CodeAnalysis;
using RimSearcher.Core;

namespace RimSearcher.Server;

// 查询与重建之间的读写协调。索引对象本身不替换（见 SourceIndexer.Clear），
// 所以这里不是为了保护引用切换，而是为了保证重建期间没有查询在读半成品索引。
//
// 刻意不做热替换：新旧两份索引并存会让内存翻倍（vanilla 单源就约 1 GB）。
// 宁可让重建期间的查询短暂挂起——实测全量重建约 4 秒。
public static class IndexGate
{
    private static readonly ReaderWriterLockSlim Lock = new(LockRecursionPolicy.NoRecursion);

    // 重建期间进来的查询等这么久。等不到就必须报错，不能放行：重建是原地 Clear + 重扫
    // （见 IndexRebuilder.Rebuild），此刻根本没有「旧的一份」可读，放进去的查询会拿到
    // 一个已冻结状态被重置的空壳，也就是一个看起来成功的错误答案。
    // 非 readonly 仅为让测试能在几十毫秒内走到超时分支，产品代码不应改它。
    internal static TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private static long _generation;

    // 每次重建自增。会话据此判断自己上次查询是否发生在更早的索引世代上。
    public static long Generation => Interlocked.Read(ref _generation);

    public static bool IsRebuilding { get; private set; }

    // 返回 false 表示等到超时仍未拿到读锁（重建还没结束），调用方应回一个可重试的错误
    public static bool TryEnterRead([NotNullWhen(true)] out IDisposable? scope)
    {
        if (!Lock.TryEnterReadLock(ReadTimeout))
        {
            scope = null;
            return false;
        }

        scope = new ReadScope();
        return true;
    }

    // 返回 false 表示没抢到写锁（另一次重建正在进行），调用方应跳过本次重建
    public static bool Rebuild(Action rebuild, TimeSpan timeout)
    {
        if (!Lock.TryEnterWriteLock(timeout)) return false;

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
            Lock.ExitWriteLock();
        }
    }

    private sealed class ReadScope : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            Lock.ExitReadLock();
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
