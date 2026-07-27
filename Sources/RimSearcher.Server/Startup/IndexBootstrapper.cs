using System.Diagnostics;
using RimSearcher.Core;

namespace RimSearcher.Server;

// 把索引填起来：能从缓存加载就加载，否则扫盘重建并把结果存回去。
// 索引对象由调用方持有并传入——重建走的是原地 Clear + 重扫（见 IndexRebuilder），
// 全程不做引用替换，这里也一样。
public static class IndexBootstrapper
{
    public static async Task PopulateAsync(
        SourceIndexer indexer,
        DefIndexer defIndexer,
        PreparedSources prepared,
        string cacheDirectory,
        bool cacheDirectoryUsable,
        string cacheFingerprint)
    {
        if (!prepared.HasAnyExisting) return;

        var cacheUsable = cacheDirectoryUsable && prepared.CacheIsTrustworthy;

        if (cacheUsable && await TryLoadFromCacheAsync(indexer, defIndexer, cacheDirectory, cacheFingerprint))
            return;

        var stopwatch = Stopwatch.StartNew();

        foreach (var path in prepared.ExistingCsharp) indexer.Scan(path);

        // xml 两侧都要喂：DefIndexer 建 Def 索引，SourceIndexer 还要它做全文检索
        foreach (var path in prepared.ExistingXml)
        {
            defIndexer.Scan(path);
            indexer.Scan(path);
        }

        indexer.FreezeIndex();
        defIndexer.FreezeIndex();

        await ServerLogger.Info("Index", "Index build completed",
            ("csPaths", prepared.ExistingCsharp.Count),
            ("xmlPaths", prepared.ExistingXml.Count),
            ("durationMs", stopwatch.ElapsedMilliseconds));

        if (cacheUsable)
            await SaveToCacheAsync(indexer, defIndexer, cacheDirectory, cacheFingerprint, stopwatch.Elapsed);
    }

    private static async Task<bool> TryLoadFromCacheAsync(
        SourceIndexer indexer,
        DefIndexer defIndexer,
        string cacheDirectory,
        string cacheFingerprint)
    {
        var result = IndexCacheService.TryLoad(cacheDirectory, cacheFingerprint);
        if (!result.Success || result.Snapshot == null)
        {
            await ServerLogger.Info("Index", "Cache unavailable, rebuilding index", ("reason", result.Reason));
            return false;
        }

        indexer.ImportSnapshot(result.Snapshot.Source);
        defIndexer.ImportSnapshot(result.Snapshot.Def);
        indexer.FreezeIndex();
        defIndexer.FreezeIndex();

        await ServerLogger.Info("Index", "Index loaded from cache");
        return true;
    }

    private static async Task SaveToCacheAsync(
        SourceIndexer indexer,
        DefIndexer defIndexer,
        string cacheDirectory,
        string cacheFingerprint,
        TimeSpan buildDuration)
    {
        var snapshot = new IndexCacheSnapshot
        {
            Source = indexer.ExportSnapshot(),
            Def = defIndexer.ExportSnapshot()
        };

        var csharpFileCount = snapshot.Source.ProcessedFiles.Count(path =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        var result = IndexCacheService.Save(
            cacheDirectory,
            cacheFingerprint,
            snapshot,
            buildDuration,
            csharpFileCount,
            snapshot.Def.ProcessedFiles.Length);

        if (result.Success) await ServerLogger.Info("Index", "Index cache saved", ("path", cacheDirectory));
        else await ServerLogger.Warning("Index", "Failed to save index cache", ("reason", result.Reason));
    }
}
