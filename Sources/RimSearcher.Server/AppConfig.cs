using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimSearcher.Server;

// config 里的一条源路径。旧格式的裸字符串仍可用（名字从路径推断），
// 新格式 {"name":"HAR","path":"..."} 才能让同一个逻辑源跨 C#/XML 两侧归为一组。
[JsonConverter(typeof(SourcePathEntryConverter))]
public sealed class SourcePathEntry
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;

    // 可选：Path 这份源码由哪些程序集目录反编译而来。配了才能跟随更新，
    // 留空即视为手工副本（现状），同步流程会跳过它。
    public IReadOnlyList<string> AssemblyPaths { get; init; } = [];

    public bool CanFollow => AssemblyPaths.Count > 0;
}

public sealed class SourcePathEntryConverter : JsonConverter<SourcePathEntry>
{
    // 目录末段常是版本号或内容类型，拿它当源名毫无信息量；逐段回退到第一个有意义的段。
    private static readonly HashSet<string> UninformativeSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "patches", "assemblies", "languages", "textures", "sounds",
        "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "common", "data"
    };

    public override SourcePathEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var path = reader.GetString() ?? string.Empty;
            return new SourcePathEntry { Name = InferName(path), Path = path };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            string? path = null;
            var assemblyPaths = new List<string>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var property = reader.GetString();
                if (!reader.Read()) break;

                // assemblyPath(s) 单数写字符串、复数写数组都要认，故不能在这里一刀切掉非字符串
                switch (ConfigJson.NormalizeKey(property ?? string.Empty))
                {
                    case "assemblypath" or "assemblypaths" or "assemblies":
                        ConfigJson.ReadStringOrArray(ref reader, assemblyPaths);
                        break;
                    case "name":
                        if (reader.TokenType == JsonTokenType.String) name = reader.GetString();
                        else reader.Skip();
                        break;
                    case "path":
                        if (reader.TokenType == JsonTokenType.String) path = reader.GetString();
                        else reader.Skip();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            path ??= string.Empty;
            return new SourcePathEntry
            {
                Name = string.IsNullOrWhiteSpace(name) ? InferName(path) : name!.Trim(),
                Path = path,
                AssemblyPaths = assemblyPaths
            };
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, SourcePathEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("path", value.Path);
        if (value.AssemblyPaths.Count > 0)
        {
            writer.WriteStartArray("assemblyPaths");
            foreach (var assemblyPath in value.AssemblyPaths) writer.WriteStringValue(assemblyPath);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    // SourceDefinition 未显式给 name 时复用同一套推断规则
    public static string InferNameFrom(string path) => InferName(path);

    private static string InferName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "unnamed";

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i].Trim().Trim('[', ']');
            if (segment.Length == 0) continue;
            if (UninformativeSegments.Contains(segment)) continue;
            if (segment.EndsWith(':')) continue;
            return segment;
        }

        return segments.Length > 0 ? segments[^1] : "unnamed";
    }
}

public record AppConfig
{
    public const string ConfigPathEnvVar = "RIMSEARCHER_CONFIG";

    public const string DefaultDecompileFolder = "Decompiled";

    public List<SourcePathEntry> CsharpSourcePaths { get; init; } = new();
    public List<SourcePathEntry> XmlSourcePaths { get; init; } = new();

    // 新格式：一行声明一个逻辑源的全部路径。与上面两个旧列表并存，最终由 ResolveSources() 合并。
    public List<SourceDefinition> Sources { get; init; } = new();

    // 组名 → 源名列表。一个源可同属多组；组内顺序即同分时的排序优先级。
    public Dictionary<string, List<string>> ScopeGroups { get; init; } = new();

    // 未显式传 scope 时使用的表达式（组名 / 源名 / 逗号并列 / '-' 排除）。留空即全域。
    public string? DefaultScope { get; init; }

    public bool SkipPathSecurity { get; init; } = false;
    public bool CheckUpdates { get; init; } = true;

    // mod 的多版本布局（ModRoot/1.6/Assemblies）里只有当前版本那份会被游戏加载，
    // 其余是历史死代码。留空则从任一 assemblyPath 上溯找 Version.txt 自动判定。
    public string? GameVersion { get; init; }

    // 配了 assemblyPath 的源，在启动时顺带检查程序集有没有变过，并把结果附在工具返回里。
    // 只做检查不做反编译——反编译由 sync_sources 工具显式触发。
    public bool CheckSourceUpdates { get; init; } = true;

    // 保留几代反编译历史用于 diff。0 = 不保留。每代只存被覆盖掉的旧文件（反向增量），
    // 一次游戏更新通常只动 5–20% 的文件，故占用远小于同等份数的完整副本。
    public int SourceHistoryDepth { get; init; } = 0;

    // 只配了 assemblies、没配 csharp 的源，产物落到 <这个根>/<源名>。
    // 留空则用 <exe目录>/Decompiled——与 .cache/index 同处一地，可写性假定一致，
    // 且与目标同卷，暂存区转正走的是原子 rename 而非跨盘复制。
    public string? DecompileOutputRoot { get; init; }

    // 启动时把源文件的大小/修改时间摘要纳入缓存指纹，让 Steam 更新过的 mod 自动触发重建。
    // 代价是每次启动多几百毫秒的元数据枚举；源全是手工副本、从不变动时可关掉。
    public bool VerifySourceFreshness { get; init; } = true;

    // 0 = 不启用。父进程守护恒开，故这只是额外的兜底闸。
    public int IdleTimeoutMinutes { get; init; } = 0;

    // 多个 client 各起一个进程时，索引会被复制 N 份（每份约 1 GB）。开启后首个实例成为
    // 索引宿主，后续实例只做 stdio↔管道转发，全机只保留一份索引。
    public bool ShareIndexHost { get; init; } = true;

    // 新旧两种格式合并成下游唯一的事实来源。同名条目不去重：ScopeCatalog 本就按 name 把
    // 多个根归到同一个源，重复路径在索引侧也已按文件路径去重。
    //
    // baseDirectory 仅供测试注入，让这里不依赖 AppDomain 就能验证默认目录的推导。
    public ResolvedSources ResolveSources(string? baseDirectory = null)
    {
        var csharp = new List<SourcePathEntry>(CsharpSourcePaths);
        var xml = new List<SourcePathEntry>(XmlSourcePaths);

        foreach (var definition in Sources)
        {
            // 转换器对「什么路径都没写」的条目返回 null（config 里多打一个 {} 就是这样），
            // 不滤掉会在下一行直接 NRE，而这里在 TryLoad 的 catch 之外——整个进程会起不来
            if (definition == null) continue;

            // 只写 assemblies、没写 csharp 时补一个默认输出目录。不补的话这个源在下面的
            // 循环里一条路径都不产出，既不会被索引也不会被反编译——静默消失，最难查的那种。
            var paths = definition.Csharp.Count > 0
                ? definition.Csharp
                : definition.Assemblies.Count > 0
                    ? [DefaultDecompileTarget(definition.Name, baseDirectory)]
                    : definition.Csharp;

            for (var i = 0; i < paths.Count; i++)
            {
                csharp.Add(new SourcePathEntry
                {
                    Name = definition.Name,
                    Path = paths[i],
                    // 只有反编译目标那条挂 assemblies，否则同一批程序集会被多条源码路径重复扫描
                    AssemblyPaths = i == 0 ? definition.Assemblies : []
                });
            }

            foreach (var path in definition.Xml)
            {
                xml.Add(new SourcePathEntry { Name = definition.Name, Path = path });
            }
        }

        return new ResolvedSources(csharp, xml);
    }

    // 源名直接进路径，故必须先洗掉分隔符和非法字符：name 可由用户显式给出，
    // 也可能是从路径末段推断来的，两者都不保证是合法的单层目录名。
    private string DefaultDecompileTarget(string sourceName, string? baseDirectory)
    {
        var root = string.IsNullOrWhiteSpace(DecompileOutputRoot)
            ? Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, DefaultDecompileFolder)
            : ResolvePath(DecompileOutputRoot, baseDirectory);

        return Path.Combine(root, SanitizeFolderName(sourceName));
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');

        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    public static (AppConfig Config, string Path, bool IsLoaded) Load()
    {
        var envPath = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var resolvedEnvPath = ResolvePath(envPath);
            if (TryLoad(resolvedEnvPath, out var configFromEnv))
                return (configFromEnv, resolvedEnvPath, true);

            return (new AppConfig(), resolvedEnvPath, false);
        }

        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        if (TryLoad(path, out var config))
            return (config, path, true);

        return (new AppConfig(), path, false);
    }

    private static bool TryLoad(string path, out AppConfig config)
    {
        config = new AppConfig();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, options);
                if (loaded != null)
                {
                    config = loaded;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static string ResolvePath(string rawPath, string? baseDirectory = null)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);

        var baseDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, expanded));
    }
}
