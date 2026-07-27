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

        if (csharp.Count == 0 && xml.Count == 0 && assemblies.Count == 0) return null;

        return new SourceDefinition
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? SourcePathEntry.InferNameFrom(csharp.FirstOrDefault() ?? xml.FirstOrDefault() ?? assemblies[0])
                : name,
            Csharp = csharp,
            Xml = xml,
            Assemblies = assemblies
        };
    }
}

// [[sources]] 摊平后的结果，下游只认这个
public sealed record ResolvedSources(List<SourcePathEntry> Csharp, List<SourcePathEntry> Xml)
{
    public bool HasAny => Csharp.Count > 0 || Xml.Count > 0;

    public IEnumerable<string> AllPaths => Csharp.Concat(Xml).Select(entry => entry.Path);

    public IEnumerable<(string Name, string Path)> AllSources =>
        Csharp.Concat(Xml).Select(entry => (entry.Name, entry.Path));

    public List<SourcePathEntry> Followable => Csharp.Where(entry => entry.CanFollow).ToList();
}
