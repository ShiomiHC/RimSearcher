using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimSearcher.Sources;

/// <summary>一棵树里的一个来源程序集。路径相对 mod 根目录 —— 库搬家不该让每棵树都变红。</summary>
public sealed record SourceAssembly
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
}

/// <summary>
/// 一棵反编译树的清单。三件事一个文件:
///
///   1. **归属标记** —— 有它才说明这个目录是本工具产的,才敢覆盖。没有它而目录非空,
///      就是别人的源码副本,一次配置笔误不该抹掉它。
///   2. **变更判据** —— 来源 dll 的哈希,对得上就不必重跑。
///   3. **产地记录** —— 它跟着源码一起进 git,历史里每次源码变化旁边都写着
///      「是哪几个 dll 的哪个版本产生的」。
///
/// 刻意**不记时间戳**:那是 git 提交时间的活儿,写进来会让每次同步都无端改一行。
/// </summary>
public sealed record SourceTreeState
{
    /// <summary>清单文件名。它在树的根上,一眼可见。</summary>
    public const string FileName = ".rimsearcher-source.json";

    /// <summary>旧世系留下的归属标记。只用来认领它建的树,本工具不再写。</summary>
    public const string LegacyMarker = ".rimsearcher-decompiled";

    [JsonPropertyName("package_id")] public required string PackageId { get; init; }
    [JsonPropertyName("game_version")] public required string GameVersion { get; init; }
    [JsonPropertyName("root")] public required string Root { get; init; }
    [JsonPropertyName("assemblies")] public required IReadOnlyList<SourceAssembly> Assemblies { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 反斜杠与非 ASCII 不转义:这份文件是给人读的,而 mod 名里中日文很常见。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SourceTreeState? Read(string treeDir)
    {
        var path = System.IO.Path.Combine(treeDir, FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SourceTreeState>(File.ReadAllText(path), Options); }
        catch { return null; }
    }

    public void Write(string treeDir)
        => File.WriteAllText(System.IO.Path.Combine(treeDir, FileName),
                             JsonSerializer.Serialize(this, Options) + "\n");

    /// <summary>
    /// 这个目录是本工具管的吗。空目录算是(还没建过),有清单或旧标记算是,其余不算。
    /// </summary>
    public static bool IsOurs(string treeDir)
    {
        if (!Directory.Exists(treeDir)) return true;
        if (File.Exists(System.IO.Path.Combine(treeDir, FileName))) return true;
        if (File.Exists(System.IO.Path.Combine(treeDir, LegacyMarker))) return true;
        try { return !Directory.EnumerateFileSystemEntries(treeDir).Any(); }
        catch { return false; }
    }

    /// <summary>来源与上次记录的完全一致吗。<c>null</c>(没有记录)一律算不一致。</summary>
    public bool SameSources(SourceTreeState other)
        => GameVersion == other.GameVersion
        && Assemblies.Count == other.Assemblies.Count
        && Assemblies.Zip(other.Assemblies).All(p => p.First.Path == p.Second.Path
                                                  && p.First.Sha256 == p.Second.Sha256);
}
