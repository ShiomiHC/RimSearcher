using Tomlyn.Model;

namespace RimSearcher.Server;

// 一个逻辑源的完整声明，对应 config 里的一个 [[sources]] 块。每类路径都允许有多个——
// DLC 的 Core/Royalty/Ideology 各有 Defs 目录，mod 也常是 1.6/Defs + Common/Defs。
public sealed class SourceDefinition
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Csharp { get; init; } = [];
    public IReadOnlyList<string> Xml { get; init; } = [];
    public IReadOnlyList<string> Assemblies { get; init; } = [];

    // mod 展开出来的 Languages 目录，只喂译文查表。用户不直接写它——写了也没用，
    // 手写 xml 路径下的语言目录由 ResolveSources 自己探（见 DiscoverLanguageDirs）。
    public IReadOnlyList<string> Languages { get; init; } = [];

    // mod 根目录。写了它就不必再手写 xml/assemblies：ResolveSources 会按 loadFolders.xml
    // 与当前游戏版本展开成游戏真正加载的那些目录，旧版本目录一律不进。
    public IReadOnlyList<string> Mods { get; init; } = [];

    // 判定 loadFolders.xml 条件目录用的 packageId 白名单。留空即条件目录全收；
    // 配了就是「只有这些前置算启用」，其余条件目录一概不收。
    public IReadOnlyList<string> ActiveMods { get; init; } = [];

    // name 是用户写的还是从路径猜的。猜来的那个在 mod 展开时会被 About.xml 里的名字顶掉——
    // workshop 的目录名是纯数字 ID，拿它当源名等于没有名字。
    public bool HasExplicitName { get; init; }

    // 反编译产物写到第一个 csharp 路径；其余视为附加只读源码目录（手工副本、官方 Source 等）
    public string? DecompileTarget => Csharp.Count > 0 ? Csharp[0] : null;

    public bool CanFollow => Assemblies.Count > 0 && DecompileTarget != null;

    // 三类路径一个都没写就返回 null——config 里多打一个空的 [[sources]] 会走到这里，
    // 造一个空定义出来只会在下游变成一条无来源的记录，不如当它不存在。
    public static SourceDefinition? FromToml(object? value)
    {
        if (value is not TomlTable table) return null;

        var name = ConfigToml.String(ConfigToml.Find(table, "name"));
        var csharp = ConfigToml.StringList(
            ConfigToml.Find(table, "csharp", "cs", "csharpPath", "csharpPaths", "source", "sources"));
        var xml = ConfigToml.StringList(
            ConfigToml.Find(table, "xml", "xmlPath", "xmlPaths", "defs"));
        var assemblies = ConfigToml.StringList(
            ConfigToml.Find(table, "assemblies", "assembly", "assemblyPath", "assemblyPaths", "dll", "dlls"));
        var mods = ConfigToml.StringList(
            ConfigToml.Find(table, "mod", "mods", "modRoot", "modRoots", "modFolder", "modFolders"));
        var activeMods = ConfigToml.StringList(
            ConfigToml.Find(table, "activeMods", "activeMod", "requires", "withMods"));

        if (csharp.Count == 0 && xml.Count == 0 && assemblies.Count == 0 && mods.Count == 0) return null;

        return new SourceDefinition
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? SourcePathEntry.InferNameFrom(
                    csharp.FirstOrDefault() ?? xml.FirstOrDefault() ?? assemblies.FirstOrDefault() ?? mods[0])
                : name,
            HasExplicitName = !string.IsNullOrWhiteSpace(name),
            Csharp = csharp,
            Xml = xml,
            Assemblies = assemblies,
            Mods = mods,
            ActiveMods = activeMods
        };
    }
}

// 一个 Languages 目录，连同它在覆盖顺序里的位置。
//
// SourceRank 是该源在 config 里的书写序，FolderRank 是它在该源自己的目录优先级里的位次。
// RimWorld 真正的规则是「后加载的 mod 覆盖先加载的」，而加载顺序在 ModsConfig.xml 里、不在
// 我们的配置里，故以 config 的书写顺序代之——这一点要写进 README，否则译文谁覆盖谁没法解释。
public sealed record LanguageDirEntry(string Name, string Path, int SourceRank, int FolderRank);

// [[sources]] 摊平后的结果，下游只认这个
public sealed record ResolvedSources(List<SourcePathEntry> Csharp, List<SourcePathEntry> Xml)
{
    // 只喂 LocalizationIndex。刻意不进 Csharp/Xml：那两个列表是 ScopeCatalog 的词表来源，
    // 纯汉化包混进去会在 scope 里多出几十个「搜什么都是空」的源名。
    public IReadOnlyList<LanguageDirEntry> Languages { get; init; } = [];

    // mod 展开时被高优先级同名文件顶掉的文件（绝对路径）。索引侧照此跳过——
    // 游戏不解析它们，搜到了只会把人带去一份运行时根本不生效的旧定义。
    public IReadOnlySet<string> Shadowed { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 展开 mod 时实际采用的游戏版本（config 显式给出，或从 Version.txt 探得）
    public string? GameVersion { get; init; }

    // 展开过程中的降级说明，由启动流程记进日志
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool HasAny => Csharp.Count > 0 || Xml.Count > 0;

    public IEnumerable<string> AllPaths => Csharp.Concat(Xml).Select(entry => entry.Path);

    public IEnumerable<(string Name, string Path)> AllSources =>
        Csharp.Concat(Xml).Select(entry => (entry.Name, entry.Path));

    public List<SourcePathEntry> Followable => Csharp.Where(entry => entry.CanFollow).ToList();
}
