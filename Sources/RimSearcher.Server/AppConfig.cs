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

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var property = reader.GetString();
                if (!reader.Read()) break;

                if (reader.TokenType != JsonTokenType.String)
                {
                    reader.Skip();
                    continue;
                }

                if (string.Equals(property, "name", StringComparison.OrdinalIgnoreCase)) name = reader.GetString();
                else if (string.Equals(property, "path", StringComparison.OrdinalIgnoreCase)) path = reader.GetString();
            }

            path ??= string.Empty;
            return new SourcePathEntry
            {
                Name = string.IsNullOrWhiteSpace(name) ? InferName(path) : name!.Trim(),
                Path = path
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
        writer.WriteEndObject();
    }

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

    public List<SourcePathEntry> CsharpSourcePaths { get; init; } = new();
    public List<SourcePathEntry> XmlSourcePaths { get; init; } = new();

    // 组名 → 源名列表。一个源可同属多组；组内顺序即同分时的排序优先级。
    public Dictionary<string, List<string>> ScopeGroups { get; init; } = new();

    // 未显式传 scope 时使用的表达式（组名 / 源名 / 逗号并列 / '-' 排除）。留空即全域。
    public string? DefaultScope { get; init; }

    public bool SkipPathSecurity { get; init; } = false;
    public bool CheckUpdates { get; init; } = true;

    // 启动时把源文件的大小/修改时间摘要纳入缓存指纹，让 Steam 更新过的 mod 自动触发重建。
    // 代价是每次启动多几百毫秒的元数据枚举；源全是手工副本、从不变动时可关掉。
    public bool VerifySourceFreshness { get; init; } = true;

    // 0 = 不启用。父进程守护恒开，故这只是额外的兜底闸。
    public int IdleTimeoutMinutes { get; init; } = 0;

    // 多个 client 各起一个进程时，索引会被复制 N 份（每份约 1 GB）。开启后首个实例成为
    // 索引宿主，后续实例只做 stdio↔管道转发，全机只保留一份索引。
    public bool ShareIndexHost { get; init; } = true;

    public IEnumerable<string> AllPaths => CsharpSourcePaths.Concat(XmlSourcePaths).Select(entry => entry.Path);

    public IEnumerable<(string Name, string Path)> AllSources =>
        CsharpSourcePaths.Concat(XmlSourcePaths).Select(entry => (entry.Name, entry.Path));

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

    private static string ResolvePath(string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, expanded));
    }
}
