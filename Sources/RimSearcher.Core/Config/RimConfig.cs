using RimTooling.IO;
using RimTooling.RimWorld;

namespace RimSearcher.Config;

public sealed class RimConfig
{
    public string? GameDir { get; init; }
    public string? SnapshotDir { get; init; }
    public string? ExportDir { get; init; }
    public string? DecompiledDir { get; init; }

    public string? ModsConfig { get; init; }

    public IReadOnlyList<string> ModRoots { get; init; } = [];
    public string? ActiveSnapshot { get; init; }

    public int SnapshotKeep { get; init; } = SnapshotKeepDefault;

    public const int SnapshotKeepDefault = 3;

    public IReadOnlyDictionary<string, string> Snapshots { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ScopeGroups { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public string Path { get; init; } = "";

    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".rimsearcher", "config.toml");

    public string ResolveSnapshotDir()
        => SnapshotDir ?? System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } d ? d
                : System.IO.Path.GetDirectoryName(DefaultPath)!,
            "snapshots");

    public string ResolveExportDir()
        => ExportDir ?? System.IO.Path.Combine(ResolveSnapshotDir(), "..", "exports");

    public string StatePath
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } d ? d
                : System.IO.Path.GetDirectoryName(DefaultPath)!,
            "state.toml");

    public static TomlTable LoadOptionalToml(string path)
    {
        try
        {
            return TomlDocument.TryLoad(path).Table;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TomlException(
                $"配置文件 {path} 存在但无法读取（{ex.Message}）。",
                innerException: ex);
        }
    }

    public static RimConfig Load(string? explicitPath = null)
    {
        var path = explicitPath ?? Environment.GetEnvironmentVariable("RIMSEARCHER_CONFIG") ?? DefaultPath;
        var root = LoadOptionalToml(path);

        var snapshots = ReadSnapshots(root, path);
        var groups = ReadScopeGroups(root, path);

        var stateDir = System.IO.Path.GetDirectoryName(path);
        var statePath = System.IO.Path.Combine(stateDir is { Length: > 0 } ? stateDir : ".", "state.toml");
        var state = LoadOptionalToml(statePath);

        return new RimConfig
        {
            Path = path,
            GameDir = ReadOptionalString(root, "game_dir", path),
            SnapshotDir = ReadOptionalString(root, "snapshot_dir", path),
            ExportDir = ReadOptionalString(root, "export_dir", path),
            DecompiledDir = ReadOptionalString(root, "decompiled_dir", path),
            ModsConfig = ReadOptionalString(root, "mods_config", path),
            ModRoots = ReadOptionalStringList(root, "mod_roots", path),
            ActiveSnapshot = ReadOptionalString(state, "active_snapshot", statePath)
                             ?? ReadOptionalString(root, "active_snapshot", path),
            SnapshotKeep = ReadKeep(root, path),
            Snapshots = snapshots,
            ScopeGroups = groups,
        };
    }

    private static Dictionary<string, string> ReadSnapshots(TomlTable root, string path)
    {
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetValue("snapshots", out var sectionRaw)) return snapshots;
        if (sectionRaw is not TomlTable table)
        {
            root.TryGetLine("snapshots", out var sectionLine);
            throw new TomlException("snapshots 须为 [snapshots] 表。", path, sectionLine);
        }

        foreach (var (k, v) in table)
        {
            table.TryGetLine(k, out var line);
            if (v is TomlTable nested)
            {
                var hint = SampleDottedKey(k, nested);
                throw new TomlException(
                    $"[snapshots] 中的 {k} 被解析为嵌套表，而非字符串路径（通常因别名含点且未加引号）。" +
                    $"给键加上引号，例如 {TomlDocument.FormatString(hint)} = \"...\"。",
                    path, line);
            }
            if (v is string s) snapshots[k] = s;
            else throw new TomlException($"[snapshots] 中的 {k} 须为字符串路径。", path, line);
        }

        return snapshots;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadScopeGroups(TomlTable root, string path)
    {
        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetValue("scope_groups", out var sectionRaw)) return groups;
        if (sectionRaw is not TomlTable table)
        {
            root.TryGetLine("scope_groups", out var sectionLine);
            throw new TomlException("scope_groups 须为 [scope_groups] 表。", path, sectionLine);
        }

        foreach (var (k, v) in table)
        {
            table.TryGetLine(k, out var line);
            if (v is TomlTable nested)
            {
                var hint = SampleDottedKey(k, nested);
                throw new TomlException(
                    $"[scope_groups] 中的 {k} 被解析为嵌套表，而非字符串数组（通常因组名含点且未加引号）。" +
                    $"给键加上引号，例如 {TomlDocument.FormatString(hint)} = [\"...\"]。",
                    path, line);
            }
            if (table.TryGetStringList(k, out var list)) groups[k] = list;
            else throw new TomlException($"[scope_groups] 中的 {k} 须为 packageId 字符串数组。", path, line);
        }

        return groups;
    }

    private static string SampleDottedKey(string first, TomlTable nested)
    {
        var parts = new List<string> { first };
        var cur = nested;
        for (var i = 0; i < 3 && cur.Count > 0; i++)
        {
            var next = cur.Keys.First();
            parts.Add(next);
            if (cur[next] is TomlTable deeper) cur = deeper;
            else break;
        }
        return string.Join('.', parts);
    }

    private static string? ReadOptionalString(TomlTable root, string key, string path)
    {
        if (!root.TryGetValue(key, out var raw)) return null;
        if (raw is string s) return s;
        root.TryGetLine(key, out var line);
        throw new TomlException($"{key} 须为字符串。", path, line);
    }

    private static IReadOnlyList<string> ReadOptionalStringList(TomlTable root, string key, string path)
    {
        if (!root.TryGetValue(key, out _)) return [];
        if (root.TryGetStringList(key, out var list)) return list;
        root.TryGetLine(key, out var line);
        throw new TomlException($"{key} 须为字符串数组。", path, line);
    }

    private static int ReadKeep(TomlTable root, string path)
    {
        if (!root.TryGetValue("snapshot_keep", out var raw)) return SnapshotKeepDefault;
        if (raw is not long n || n < 1)
        {
            root.TryGetLine("snapshot_keep", out var line);
            throw new TomlException(
                $"snapshot_keep 是一个快照名保留的世代数，计入正在写入的那一份，因此须为不小于 1 的整数，不接受 '{raw}'。" +
                "1 表示每次重新导出都直接覆盖。",
                path, line);
        }
        return (int)n;
    }

    public void SaveActiveSnapshot(string alias)
    {
        var dir = System.IO.Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        AtomicFile.Write(StatePath,
            "# 由 'rimsearcher snapshot use' 写入。手写的偏好放 config.toml，不要放这里。\n" +
            $"active_snapshot = {TomlDocument.FormatString(alias)}\n");
    }

    public string? ModsConfigPath()
        => ModsConfig is { Length: > 0 } p ? p : RimWorldModsConfig.TryGetDefaultConfigPath();
}
