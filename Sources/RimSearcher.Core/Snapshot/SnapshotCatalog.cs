using System.Globalization;
using RimSearcher.Config;
using RimSearcher.Sources;
using RimSearcher.Storage;
using RimTooling.RimWorld;

namespace RimSearcher.Snapshot;

public sealed record SnapshotEntry(string Alias, string Path);

public enum SelectionSource { ExplicitDb, ExplicitAlias, Pinned, AutoDetected, OnlyOne }

public sealed record SnapshotSelection(string Path, string? Alias, SelectionSource Source);

public enum EnvironmentMatch
{
    Same,
    VersionDrift,
    ContentDrift,
    Unknown,
}

public enum EnvironmentVersionSource
{
    None,
    ModsConfig,
    Assembly,
}

public sealed record EnvironmentReport(EnvironmentMatch Match, IReadOnlyList<string> ActiveMods, string? GameVersion)
{
    public IReadOnlyList<string> AddedMods { get; init; } = [];

    public IReadOnlyList<string> RemovedMods { get; init; } = [];

    public int Added => AddedMods.Count;

    public int Removed => RemovedMods.Count;

    public bool Reordered { get; init; }

    public EnvironmentVersionSource VersionSource { get; init; } = EnvironmentVersionSource.None;

    public ContentComparison? Content { get; init; }
}

public static class SnapshotCatalog
{
    public static string DatabasePath(RimConfig config, string name)
        => Path.Combine(config.ResolveSnapshotDir(), name + ".db");

    public static string ExportPath(RimConfig config, string name)
        => Path.GetFullPath(Path.Combine(config.ResolveExportDir(), name + Contract.IntermediateFormat.FileExtension));

    public static IReadOnlyList<SnapshotEntry> Enumerate(RimConfig config) => Enumerate(config, out _);

    public static IReadOnlyList<SnapshotEntry> Enumerate(RimConfig config, out string? dirUnreadable)
    {
        var result = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        var dir = config.ResolveSnapshotDir();

        foreach (var file in DbFilesIn(dir, out dirUnreadable))
            result[Path.GetFileNameWithoutExtension(file)] =
                new SnapshotEntry(Path.GetFileNameWithoutExtension(file), file);

        foreach (var (alias, target) in config.Snapshots)
        {
            var path = Path.IsPathRooted(target) ? target : Path.Combine(dir, target);
            result[alias] = new SnapshotEntry(alias, path);
        }

        return result.Values.OrderBy(e => e.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> DbFilesIn(string dir, out string? unreadable)
    {
        unreadable = null;
        try
        {
            return Directory.EnumerateFiles(dir, "*.db").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var shape = File.Exists(dir) ? "该路径是文件而非目录。" : "";
            unreadable =
                $"快照目录 '{dir}' 存在但无法读取：{ex.Message} {shape}".TrimEnd();
            return [];
        }
    }

    public static void ValidateExplicit(RimConfig config, string? explicitDb, string? explicitAlias)
    {
        if (explicitDb is { Length: > 0 })
        {
            if (!File.Exists(explicitDb)) throw new SnapshotFormatError(SnapshotDb.NoDatabaseAt(explicitDb));
            return;
        }

        if (explicitAlias is { Length: > 0 }) _ = Resolve(config, null, explicitAlias);
    }

    public static SnapshotSelection Resolve(RimConfig config, string? explicitDb, string? explicitAlias)
    {
        if (explicitDb is { Length: > 0 })
            return new SnapshotSelection(explicitDb, null, SelectionSource.ExplicitDb);

        var entries = Enumerate(config, out var dirUnreadable);

        if (explicitAlias is { Length: > 0 })
        {
            var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, explicitAlias, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                throw new SnapshotFormatError(
                    $"没有名为 '{explicitAlias}' 的快照。" +
                    (entries.Count > 0
                        ? $"已登记：{string.Join("、", entries.Select(e => e.Alias))}。"
                        : dirUnreadable ?? "尚未登记任何快照；'rimsearcher export --modlist <名称>' 可生成一份。"));
            return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.ExplicitAlias);
        }

        if (config.ActiveSnapshot is { Length: > 0 } pinned)
        {
            var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, pinned, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.Pinned);
        }

        if (entries.Count == 0)
            throw new SnapshotFormatError(
                dirUnreadable ??
                "没有可用的快照。'rimsearcher export --modlist <名称>' 运行游戏生成一份。");

        var env = ReadActiveMods(config);
        if (env.ActiveMods.Count > 0)
        {
            var envFp = ExportMeta.ComputeModlistFingerprint(WithoutExporter(env.ActiveMods));
            var matches = new List<(SnapshotEntry Entry, ExportMeta Meta)>();
            foreach (var entry in entries)
            {
                try
                {
                    using var db = SnapshotDb.Open(entry.Path);
                    var dbFp = ExportMeta.ComputeModlistFingerprint(WithoutExporter(db.Mods.Select(m => m.PackageId)));
                    if (dbFp == envFp)
                        matches.Add((entry, db.Meta));
                }
                catch (SnapshotFormatError) {  }
            }

            if (matches.Count > 0)
            {
                var best = matches
                    .OrderByDescending(m => VersionMatchTier(env, m.Meta.GameVersion))
                    .ThenBy(m => SnapshotRetention.IsGeneration(m.Entry.Alias) ? 1 : 0)
                    .ThenBy(m => SnapshotRetention.GenerationOf(m.Entry.Alias).Generation)
                    .ThenByDescending(m => GameLine(m.Meta.GameVersion))
                    .ThenByDescending(m => ParseExportTime(m.Meta.ExportedAtUtc))
                    .ThenBy(m => m.Entry.Alias, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new SnapshotSelection(best.Entry.Path, best.Entry.Alias, SelectionSource.AutoDetected);
            }
        }

        if (entries.Count == 1)
            return new SnapshotSelection(entries[0].Path, entries[0].Alias, SelectionSource.OnlyOne);

        throw new SnapshotFormatError(
            "登记的快照不止一份，" +
            (env.ActiveMods.Count > 0 ? "且没有一份与游戏当前启用的 mod 匹配，" : "而无法读取游戏当前启用的 mod，") + "因此无法自动选择。" +
            $"用 --snapshot 指定一份：{string.Join("、", entries.Select(e => e.Alias))}。" +
            "'rimsearcher snapshot use <名称>' 可固定这一选择。");
    }

    public static EnvironmentReport ReadActiveMods(RimConfig config)
    {
        var installed = GameBuild.Installed(config.GameDir);

        var path = config.ModsConfigPath();
        if (string.IsNullOrWhiteSpace(path)) return Empty(installed);
        try
        {
            var snapshot = RimWorldModsConfig.Read(path);
            return new EnvironmentReport(EnvironmentMatch.Unknown, snapshot.ActiveModIds, installed ?? snapshot.Version)
            {
                VersionSource = installed is not null ? EnvironmentVersionSource.Assembly
                              : snapshot.Version is { Length: > 0 } ? EnvironmentVersionSource.ModsConfig
                              : EnvironmentVersionSource.None,
            };
        }
        catch
        {
            return Empty(installed);
        }
    }

    private static EnvironmentReport Empty(string? installedVersion)
        => new(EnvironmentMatch.Unknown, [], installedVersion)
        {
            VersionSource = installedVersion is null ? EnvironmentVersionSource.None : EnvironmentVersionSource.Assembly,
        };

    private static List<string> WithoutExporter(IEnumerable<string> ids)
        => ids.Where(id => !Contract.IntermediateFormat.IsExporterPackageId(id))
              .ToList();

    public static EnvironmentReport Compare(SnapshotDb db, RimConfig config)
    {
        var env = ReadActiveMods(config);
        if (env.ActiveMods.Count == 0) return env with { Match = EnvironmentMatch.Unknown };

        var snapshotIds = WithoutExporter(db.Mods.Select(m => m.PackageId));
        var activeIds = WithoutExporter(env.ActiveMods);

        if (ExportMeta.ComputeModlistFingerprint(snapshotIds) !=
            ExportMeta.ComputeModlistFingerprint(activeIds))
        {
            var snapSet = snapshotIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var envSet = activeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            env = env with
            {
                AddedMods = [.. activeIds.Where(id => !snapSet.Contains(id))],
                RemovedMods = [.. snapshotIds.Where(id => !envSet.Contains(id))],
                Reordered = !IsSubsequence([.. snapshotIds.Where(envSet.Contains)], activeIds),
            };
        }

        if (VersionDrifted(env, db.Meta.GameVersion))
            return env with { Match = EnvironmentMatch.VersionDrift };

        var content = CompareContent(db, config, env);
        if (content is { Drifted: true })
            return env with { Match = EnvironmentMatch.ContentDrift, Content = content };

        return env with { Match = EnvironmentMatch.Same, Content = content };
    }

    private static bool IsSubsequence(IReadOnlyList<string> inner, IReadOnlyList<string> outer)
    {
        var i = 0;
        foreach (var id in outer)
        {
            if (i == inner.Count) break;
            if (string.Equals(inner[i], id, StringComparison.OrdinalIgnoreCase)) i++;
        }
        return i == inner.Count;
    }

    internal static Version? GameLine(string? game)
    {
        if (string.IsNullOrWhiteSpace(game)) return null;
        var parts = game.Split('.', ' ');
        return parts.Length >= 2 && int.TryParse(parts[0], out var ma) && int.TryParse(parts[1], out var mi)
            ? new Version(ma, mi) : null;
    }

    private static int VersionMatchTier(EnvironmentReport env, string snapshotVersion)
    {
        if (env.GameVersion is not { Length: > 0 } gv) return 0;
        if (!VersionDrifted(env, snapshotVersion)) return 2;
        return GameLine(gv) is { } envLine && GameLine(snapshotVersion) is { } snapLine && envLine == snapLine
            ? 1 : 0;
    }

    private static DateTimeOffset ParseExportTime(string? utc)
        => DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : DateTimeOffset.MinValue;

    private static bool VersionDrifted(EnvironmentReport env, string snapshotVersion)
    {
        if (env.GameVersion is not { Length: > 0 } gv) return false;
        return env.VersionSource == EnvironmentVersionSource.Assembly
            ? !string.Equals(gv, snapshotVersion, StringComparison.OrdinalIgnoreCase)
            : !snapshotVersion.StartsWith(gv, StringComparison.OrdinalIgnoreCase) &&
              !gv.StartsWith(snapshotVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static ContentComparison? CompareContent(SnapshotDb db, RimConfig config, EnvironmentReport env)
    {
        if (db.Content is not { } recorded) return null;
        var version = env.GameVersion is { Length: > 0 } gv ? gv : db.Meta.GameVersion;
        var current = ContentFingerprint.Rescan(config, recorded, db.Mods.Select(m => m.PackageId), version);
        return current is null ? null : ContentFingerprint.Compare(recorded, current);
    }

    public static IReadOnlyList<string>? CompareLanguages(SnapshotDb db, RimConfig config, EnvironmentReport env)
    {
        if (db.Content is not { } recorded) return null;
        if (db.Meta.Language is not { Length: > 0 } language) return null;
        var version = env.GameVersion is { Length: > 0 } gv ? gv : db.Meta.GameVersion;
        var current = ContentFingerprint.RescanLanguages(config, recorded, db.Mods.Select(m => m.PackageId),
                                                         version, language);
        return current is null ? null : ContentFingerprint.CompareLanguages(recorded, current);
    }
}
