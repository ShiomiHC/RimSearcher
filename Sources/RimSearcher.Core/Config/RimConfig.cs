namespace RimSearcher.Config;

/// <summary>
/// config.toml —— 机器事实与偏好的产地。
///
/// **不放**:指纹事实(产地在 db 的 meta 表)、任何声明文本(产地在代码)。
/// 这条边界是 06 定的:配置里复制一份指纹,就等于给「这份 db 是什么」造了第二个产地。
/// </summary>
public sealed class RimConfig
{
    public string? GameDir { get; init; }
    public string? SnapshotDir { get; init; }
    public string? ExportDir { get; init; }
    public string? DecompiledDir { get; init; }

    /// <summary>
    /// 导出器 mod 的**构建产物**目录(含 About/ 与 Assemblies/)。配了它,导出才会在游戏的
    /// Mods 目录下临时接一个目录联接过去,导完断开 —— 于是平常的游戏里没有这个 mod。
    /// 不配就是老样子:导出器得自己常驻装在 Mods 目录下。
    /// </summary>
    public string? DataModDir { get; init; }

    /// <summary>
    /// ModsConfig.xml 的位置。不配就是 Windows 上那条固定路径。
    ///
    /// 可配的头号理由是**测试到得了**:写死在代码里的话,「快照与当前游戏一致吗」这条分支
    /// 在测试里根本到不了,而一道到不了的分支上挂的那句话红不了。
    /// 顺带解掉一个真限制:非 Steam 安装与非 Windows 上这个文件不在那儿。
    /// </summary>
    public string? ModsConfig { get; init; }

    public IReadOnlyList<string> ModRoots { get; init; } = [];
    public string? ActiveSnapshot { get; init; }

    /// <summary>别名 → 快照文件名(相对 SnapshotDir)或绝对路径。</summary>
    public IReadOnlyDictionary<string, string> Snapshots { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>scope 组:组名 → packageId 列表。<c>--scope</c> 的组定义在这里。</summary>
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

    /// <summary>state 文件(activeSnapshot 这类会被命令改写的值)与 config 分家,免得改写用户的注释。</summary>
    public string StatePath
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } d ? d
                : System.IO.Path.GetDirectoryName(DefaultPath)!,
            "state.toml");

    public static RimConfig Load(string? explicitPath = null)
    {
        var path = explicitPath ?? Environment.GetEnvironmentVariable("RIMSEARCHER_CONFIG") ?? DefaultPath;
        var root = Toml.Load(path);

        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in root.Sub("snapshots").Values)
            if (v is string s) snapshots[k] = s;

        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in root.Sub("scope_groups").Values)
            if (v is List<string> l) groups[k] = l;

        var stateDir = System.IO.Path.GetDirectoryName(path);
        var state = Toml.Load(System.IO.Path.Combine(stateDir is { Length: > 0 } ? stateDir : ".", "state.toml"));

        return new RimConfig
        {
            Path = path,
            GameDir = root.String("game_dir"),
            SnapshotDir = root.String("snapshot_dir"),
            ExportDir = root.String("export_dir"),
            DecompiledDir = root.String("decompiled_dir"),
            DataModDir = root.String("datamod_dir"),
            ModsConfig = root.String("mods_config"),
            ModRoots = root.Strings("mod_roots"),
            ActiveSnapshot = state.String("active_snapshot") ?? root.String("active_snapshot"),
            Snapshots = snapshots,
            ScopeGroups = groups,
        };
    }

    public void SaveActiveSnapshot(string alias)
    {
        var dir = System.IO.Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StatePath,
            "# 由 `rimsearcher snapshot use` 写入。手写的偏好放 config.toml,不要放这里。\n" +
            $"active_snapshot = {Toml.Quote(alias)}\n");
    }

    /// <summary>ModsConfig.xml 的位置。自动检测(快照选择第三层)读它。</summary>
    public string ModsConfigPath()
        => ModsConfig is { Length: > 0 } p ? p : System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "..", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config", "ModsConfig.xml");
}
