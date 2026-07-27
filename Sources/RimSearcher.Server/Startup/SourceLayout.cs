namespace RimSearcher.Server;

// 配置里的源路径落到磁盘上的实际情况：哪些在、哪些不在、哪些是我们自己建出来的。
public sealed record PreparedSources
{
    public required IReadOnlyList<string> ExistingCsharp { get; init; }
    public required IReadOnlyList<string> ExistingXml { get; init; }

    // 配了但磁盘上没有的，附来源名，直接可读
    public required IReadOnlyList<string> Missing { get; init; }

    public bool HasAnyExisting => ExistingCsharp.Count + ExistingXml.Count > 0;

    // 有路径缺失时整体不碰缓存：存下来的快照会缺一块，而指纹里看不出这件事，
    // 下次启动会把这份残缺快照当成完整的加载回来。
    public bool CacheIsTrustworthy => Missing.Count == 0;
}

public static class SourceLayout
{
    public static async Task<PreparedSources> PrepareAsync(ResolvedSources sources)
    {
        await EnsureDecompileTargetsExistAsync(sources);

        var existingCsharp = new List<string>();
        var existingXml = new List<string>();
        var missing = new List<string>();

        foreach (var entry in sources.Csharp)
        {
            if (Directory.Exists(entry.Path)) existingCsharp.Add(entry.Path);
            else missing.Add($"C# source '{entry.Name}': {entry.Path}");
        }

        foreach (var entry in sources.Xml)
        {
            if (Directory.Exists(entry.Path)) existingXml.Add(entry.Path);
            else missing.Add($"XML source '{entry.Name}': {entry.Path}");
        }

        if (missing.Count > 0)
        {
            await ServerLogger.Warning("Startup", "Some configured paths are unavailable",
                ("count", missing.Count), ("paths", string.Join("; ", missing)));
        }

        return new PreparedSources
        {
            ExistingCsharp = existingCsharp,
            ExistingXml = existingXml,
            Missing = missing
        };
    }

    // 可跟随源的 csharp[0] 就是反编译输出目标，首次 sync 前它本来就不存在——那是待办状态，
    // 不是配置错误。不先建出来的话它会落进 Missing，而 Missing 非空会整体禁掉索引缓存，
    // 于是「配好了但还没 sync」的用户每次启动都要重建一份 1 GB 索引。
    private static async Task EnsureDecompileTargetsExistAsync(ResolvedSources sources)
    {
        foreach (var entry in sources.Followable)
        {
            if (Directory.Exists(entry.Path)) continue;

            try
            {
                Directory.CreateDirectory(entry.Path);
                await ServerLogger.Info("Startup", "Created decompile output directory",
                    ("source", entry.Name), ("path", entry.Path));
            }
            catch (Exception ex)
            {
                // 建不出来（多见于装在 Program Files 下）就照常落进 Missing
                await ServerLogger.Warning("Startup", "Could not create decompile output directory",
                    ("source", entry.Name), ("path", entry.Path), ("reason", ex.Message));
            }
        }
    }
}
