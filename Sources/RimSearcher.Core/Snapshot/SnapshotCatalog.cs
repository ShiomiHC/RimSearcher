using System.Xml.Linq;
using RimSearcher.Config;
using RimSearcher.Storage;

namespace RimSearcher.Snapshot;

public sealed record SnapshotEntry(string Alias, string Path);

public enum SelectionSource { ExplicitDb, ExplicitAlias, Pinned, AutoDetected, OnlyOne }

public sealed record SnapshotSelection(string Path, string? Alias, SelectionSource Source);

public enum EnvironmentMatch
{
    /// <summary>快照与当前 ModsConfig.xml 完全一致。</summary>
    Same,
    /// <summary>同一套 mod、同一顺序,但版本或游戏 build 变了 —— 02-4 的过期。</summary>
    VersionDrift,
    /// <summary>启用的 mod 清单或顺序不同。</summary>
    DifferentModlist,
    /// <summary>读不到 ModsConfig.xml,无从比对。</summary>
    Unknown,
}

public sealed record EnvironmentReport(EnvironmentMatch Match, IReadOnlyList<string> ActiveMods, string? GameVersion)
{
    public int Added { get; init; }
    public int Removed { get; init; }
}

/// <summary>
/// 快照寻址 —— **显式恒胜自动**(用户裁决:当前游戏启用的不一定是正在查询的目标环境)。
///
///   1. 本次调用显式指定:<c>--db &lt;path&gt;</c> 或 <c>--snapshot &lt;别名&gt;</c>
///   2. <c>snapshot use</c> 固定的活动快照
///   3. 都没有才走自动检测(读 ModsConfig.xml ↔ 各快照 meta 指纹比对)
///
/// 但无论选择来自哪一层,每次输出都要报告「所用快照 ↔ 当前 ModsConfig」的比对结果:
/// 寻址与过期自证是同一次比对的两个产出。**不一致只声明,不静默切换。**
/// </summary>
public static class SnapshotCatalog
{
    public static IReadOnlyList<SnapshotEntry> Enumerate(RimConfig config)
    {
        var result = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        var dir = config.ResolveSnapshotDir();

        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.db").OrderBy(f => f, StringComparer.Ordinal))
                result[Path.GetFileNameWithoutExtension(file)] =
                    new SnapshotEntry(Path.GetFileNameWithoutExtension(file), file);

        foreach (var (alias, target) in config.Snapshots)
        {
            var path = Path.IsPathRooted(target) ? target : Path.Combine(dir, target);
            result[alias] = new SnapshotEntry(alias, path);
        }

        return result.Values.OrderBy(e => e.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static SnapshotSelection Resolve(RimConfig config, string? explicitDb, string? explicitAlias)
    {
        if (explicitDb is { Length: > 0 })
            return new SnapshotSelection(explicitDb, null, SelectionSource.ExplicitDb);

        var entries = Enumerate(config);

        if (explicitAlias is { Length: > 0 })
        {
            var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, explicitAlias, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                throw new SnapshotFormatError(
                    $"No snapshot named '{explicitAlias}'. " +
                    (entries.Count == 0
                        ? "No snapshots are registered yet; run 'rimsearcher export' to make one."
                        : $"Registered: {string.Join(", ", entries.Select(e => e.Alias))}."));
            return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.ExplicitAlias);
        }

        if (config.ActiveSnapshot is { Length: > 0 } pinned)
        {
            var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, pinned, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.Pinned);
        }

        if (entries.Count == 0)
            throw new SnapshotFormatError(
                "No snapshot is available. A snapshot is produced inside the game: run 'rimsearcher export --modlist <name>' " +
                "to drive it, or press the export button in the mod's settings page and then " +
                "'rimsearcher snapshot import <file>'.");

        // 第 3 层:自动检测。只在前两层都没说话时才轮到它。
        var env = ReadActiveMods(config);
        if (env.ActiveMods.Count > 0)
        {
            foreach (var entry in entries)
            {
                try
                {
                    using var db = SnapshotDb.Open(entry.Path);
                    if (db.Meta.ModlistFingerprint == ExportMeta.ComputeModlistFingerprint(env.ActiveMods))
                        return new SnapshotSelection(entry.Path, entry.Alias, SelectionSource.AutoDetected);
                }
                catch (SnapshotFormatError) { /* 坏库不该让寻址整个失败 */ }
            }
        }

        if (entries.Count == 1)
            return new SnapshotSelection(entries[0].Path, entries[0].Alias, SelectionSource.OnlyOne);

        throw new SnapshotFormatError(
            "More than one snapshot is registered and none matches the mods currently enabled in the game, " +
            $"so there is no safe default. Pick one with --snapshot: {string.Join(", ", entries.Select(e => e.Alias))}. " +
            "'rimsearcher snapshot use <name>' makes the choice stick.");
    }

    /// <summary>读 ModsConfig.xml,拿有序 activeMods 与游戏版本。</summary>
    public static EnvironmentReport ReadActiveMods(RimConfig config)
    {
        var path = config.ModsConfigPath();
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) return new EnvironmentReport(EnvironmentMatch.Unknown, [], null);
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return new EnvironmentReport(EnvironmentMatch.Unknown, [], null);
            var version = root.Element("version")?.Value?.Trim();
            var mods = root.Element("activeMods")?.Elements("li")
                           .Select(e => e.Value.Trim())
                           .Where(s => s.Length > 0).ToList() ?? [];
            return new EnvironmentReport(EnvironmentMatch.Unknown, mods, version);
        }
        catch
        {
            return new EnvironmentReport(EnvironmentMatch.Unknown, [], null);
        }
    }

    private static List<string> WithoutExporter(IEnumerable<string> ids)
        => ids.Where(id => !string.Equals(id, Contract.IntermediateFormat.ExporterPackageId,
                                          StringComparison.OrdinalIgnoreCase))
              .ToList();

    /// <summary>寻址与过期自证的共同产出:所用快照 ↔ 当前 ModsConfig 的比对。</summary>
    public static EnvironmentReport Compare(SnapshotDb db, RimConfig config)
    {
        var env = ReadActiveMods(config);
        if (env.ActiveMods.Count == 0) return env with { Match = EnvironmentMatch.Unknown };

        // 导出器两侧都不算数。它是导出时临时塞进去的工具,快照里有、玩家的 ModsConfig 里
        // 通常没有 —— 拿它参与比对,每一次导出都会给自己造出一条「有 1 个 mod 不再启用」的
        // 假过期。声明必须描述**内容**的差异,不能把工具自己的脚印算进去。
        var snapshotIds = WithoutExporter(db.Mods.Select(m => m.PackageId));
        var activeIds = WithoutExporter(env.ActiveMods);

        var sameList = ExportMeta.ComputeModlistFingerprint(snapshotIds) ==
                       ExportMeta.ComputeModlistFingerprint(activeIds);

        if (!sameList)
        {
            var snapSet = snapshotIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var envSet = activeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return env with
            {
                Match = EnvironmentMatch.DifferentModlist,
                Added = envSet.Except(snapSet, StringComparer.OrdinalIgnoreCase).Count(),
                Removed = snapSet.Except(envSet, StringComparer.OrdinalIgnoreCase).Count(),
            };
        }

        if (env.GameVersion is { Length: > 0 } gv &&
            !db.Meta.GameVersion.StartsWith(gv, StringComparison.OrdinalIgnoreCase) &&
            !gv.StartsWith(db.Meta.GameVersion, StringComparison.OrdinalIgnoreCase))
            return env with { Match = EnvironmentMatch.VersionDrift };

        return env with { Match = EnvironmentMatch.Same };
    }
}
