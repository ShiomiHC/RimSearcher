using RimSearcher.Core;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace RimSearcher.Server;

// 索引侧看到的一条源路径。由 [[sources]] 展开而来——一个逻辑源的 csharp/xml 各条路径
// 在这里摊平成独立条目，靠 Name 相同重新归组。
public sealed class SourcePathEntry
{
    // 目录末段常是版本号或内容类型，拿它当源名毫无信息量；逐段回退到第一个有意义的段。
    private static readonly HashSet<string> UninformativeSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "patches", "assemblies", "languages", "textures", "sounds",
        "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "common", "data"
    };

    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;

    // 可选：Path 这份源码由哪些程序集目录反编译而来。配了才能跟随更新，
    // 留空即视为手工副本，同步流程会跳过它。
    public IReadOnlyList<string> AssemblyPaths { get; init; } = [];

    public bool CanFollow => AssemblyPaths.Count > 0;

    // SourceDefinition 未显式给 name 时按路径推断
    public static string InferNameFrom(string path)
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

    public const string ConfigFileName = "config.toml";

    public const string DefaultDecompileFolder = "Decompiled";

    // 一个 [[sources]] 块声明一个逻辑源的全部路径
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

    // 配了 assemblies 的源，在启动时顺带检查程序集有没有变过，并把结果附在工具返回里。
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

    // [[sources]] 摊平成下游唯一的事实来源。同名条目不去重：ScopeCatalog 本就按 name 把
    // 多个根归到同一个源，重复路径在索引侧也已按文件路径去重。
    //
    // baseDirectory 仅供测试注入，让这里不依赖 AppDomain 就能验证默认目录的推导。
    public ResolvedSources ResolveSources(string? baseDirectory = null)
    {
        var csharp = new List<SourcePathEntry>();
        var xml = new List<SourcePathEntry>();
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        var gameVersion = GameVersion ?? DetectGameVersion();

        foreach (var raw in Sources)
        {
            var definition = ExpandMods(raw, gameVersion, shadowed, notes);

            // 「什么路径都没写」的条目（config 里多打一个空 [[sources]] 就是这样）在 Parse 里
            // 已被丢掉，这里是给直接构造 AppConfig 的调用方兜底：漏进来会在下一行直接 NRE，
            // 而这一步在 TryLoad 的 catch 之外——整个进程会起不来
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

        return new ResolvedSources(csharp, xml)
        {
            Shadowed = shadowed,
            GameVersion = gameVersion,
            Notes = notes
        };
    }

    // mod 根 → 该游戏版本下真正生效的 Defs/Patches/Assemblies 目录。显式写的 xml/assemblies
    // 保留在前：手写的那几条是用户明确要的，展开结果只做追加。
    private static SourceDefinition? ExpandMods(
        SourceDefinition? definition,
        string? gameVersion,
        HashSet<string> shadowed,
        List<string> notes)
    {
        if (definition == null || definition.Mods.Count == 0) return definition;

        var xml = definition.Xml.ToList();
        var assemblies = definition.Assemblies.ToList();
        string? modName = null;

        foreach (var modRoot in definition.Mods)
        {
            var layout = ModLayoutResolver.Resolve(
                modRoot, gameVersion, definition.ActiveMods.Count > 0 ? definition.ActiveMods : null);
            if (layout == null)
            {
                // 路径不存在时不静默跳过：mod 退订/移库后目录就没了，而这条源会整个消失
                notes.Add($"{definition.Name}: mod root unavailable: {modRoot}");
                continue;
            }

            if (!layout.HasContent)
            {
                notes.Add($"{definition.Name}: no Defs/Patches/Assemblies under {layout.Root}");
                continue;
            }

            xml.AddRange(layout.XmlDirs);
            assemblies.AddRange(layout.AssemblyDirs);
            foreach (var file in layout.Shadowed) shadowed.Add(file);

            modName ??= layout.Name;
            foreach (var note in layout.Notes) notes.Add($"{definition.Name}: {note}");
        }

        return new SourceDefinition
        {
            // 用户没给 name 时，从数字 ID 目录猜出来的那个还不如 About.xml 里的正式名
            Name = definition.HasExplicitName || modName == null ? definition.Name : modName,
            HasExplicitName = definition.HasExplicitName,
            Csharp = definition.Csharp,
            Xml = xml,
            Assemblies = assemblies,
            Mods = definition.Mods,
            ActiveMods = definition.ActiveMods
        };
    }

    // Version.txt 首行形如 "1.6.4871 rev590"，取前两段作为 mod 版本目录的匹配键。
    // 种子取所有已配置的程序集目录与 mod 根：前者在游戏安装目录下（vanilla 那条源必然指到
    // 那里），后者在 workshop 下探不到，但只要有一条能探到就够了。
    private string? DetectGameVersion()
    {
        var seeds = Sources
            .Where(definition => definition != null)
            .SelectMany(definition => definition.Assemblies.Concat(definition.Mods));

        foreach (var seed in seeds)
        {
            var version = ReadVersionFileUpwards(seed);
            if (version != null) return version;
        }

        return null;
    }

    private static string? ReadVersionFileUpwards(string startPath)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(startPath);
        }
        catch
        {
            return null;
        }

        while (directory != null)
        {
            var versionFile = Path.Combine(directory.FullName, "Version.txt");
            if (File.Exists(versionFile))
            {
                try
                {
                    var first = File.ReadLines(versionFile).FirstOrDefault()?.Trim();
                    var parts = first?.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    if (parts is { Length: >= 2 }) return $"{parts[0]}.{parts[1]}";
                }
                catch
                {
                    // 读不出版本号是降级不是失败：GameVersion 留空，展开时退到 mod 里最高的版本目录
                }
            }

            directory = directory.Parent;
        }

        return null;
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

    public static AppConfig? Parse(string toml) => Parse(toml, out _);

    // TOML 文本 → AppConfig。语法错误返回 null，并把 Tomlyn 的诊断（带行列号）填进 error：
    // 手写配置漏个引号是常事，只报「解析失败」等于让人拿肉眼去扫整个文件。
    public static AppConfig? Parse(string toml, out string? error)
    {
        error = null;

        TomlTable? table;
        try
        {
            // 反序列化到 TomlTable（弱类型），字段绑定全在下面手写：config 的宽松性
            // （key 大小写/分隔符不敏感、单值与数组通吃）不是任何模型绑定器能表达的。
            table = TomlSerializer.Deserialize<TomlTable>(toml, (TomlSerializerOptions?)null);
        }
        catch (TomlException ex)
        {
            error = Describe(toml, ex);
            return null;
        }

        if (table is null)
        {
            error = "document did not parse to a table";
            return null;
        }

        return new AppConfig
        {
            Sources = ConfigToml.Items(ConfigToml.Find(table, "sources", "source"))
                .Select(SourceDefinition.FromToml)
                .OfType<SourceDefinition>()
                .ToList(),
            ScopeGroups = ReadScopeGroups(ConfigToml.Find(table, "scopeGroups", "groups")),
            DefaultScope = ConfigToml.String(ConfigToml.Find(table, "defaultScope")),
            GameVersion = ConfigToml.String(ConfigToml.Find(table, "gameVersion")),
            DecompileOutputRoot = ConfigToml.String(ConfigToml.Find(table, "decompileOutputRoot")),
            SkipPathSecurity = ConfigToml.Bool(ConfigToml.Find(table, "skipPathSecurity"), false),
            CheckUpdates = ConfigToml.Bool(ConfigToml.Find(table, "checkUpdates"), true),
            CheckSourceUpdates = ConfigToml.Bool(ConfigToml.Find(table, "checkSourceUpdates"), true),
            VerifySourceFreshness = ConfigToml.Bool(ConfigToml.Find(table, "verifySourceFreshness"), true),
            ShareIndexHost = ConfigToml.Bool(ConfigToml.Find(table, "shareIndexHost"), true),
            SourceHistoryDepth = ConfigToml.Int(ConfigToml.Find(table, "sourceHistoryDepth"), 0),
            IdleTimeoutMinutes = ConfigToml.Int(ConfigToml.Find(table, "idleTimeoutMinutes"), 0)
        };
    }

    // 反序列化是 fail-fast 的：抛出时 exception.Diagnostics 只有撞上的第一条。为了一次把问题
    // 列全（一份配置常常同一类笔误重复好几处），在已经失败的路径上再走一趟纯语法解析——
    // 它会继续往下扫完整个文档。正常启动走不到这里，多这一次解析不影响启动耗时。
    //
    // 只取前三条：全塞进日志会把第一现场淹掉，修完再解析一次即可。
    // DiagnosticMessage.ToString() 自带 "(行,列) : error : ..." 前缀。
    private static string Describe(string toml, TomlException exception)
    {
        var errors = SyntaxParser.Parse(toml).Diagnostics
            .Where(message => message.Kind == DiagnosticMessageKind.Error)
            .Select(message => message.ToString())
            .ToList();

        if (errors.Count == 0) return exception.Message;

        var head = string.Join(" | ", errors.Take(3));

        return errors.Count > 3 ? $"{head} (+{errors.Count - 3} more)" : head;
    }

    // [scope_groups] 下每个 key 是组名，值是源名列表。组名保持原样——它要跟用户在
    // scope 参数里写的名字对上，那侧的大小写宽容由 ScopeCatalog 负责。
    private static Dictionary<string, List<string>> ReadScopeGroups(object? value)
    {
        var groups = new Dictionary<string, List<string>>();
        if (value is not TomlTable table) return groups;

        foreach (var pair in table)
        {
            var members = ConfigToml.StringList(pair.Value);
            if (members.Count > 0) groups[pair.Key] = members;
        }

        return groups;
    }

    // Error 非空即「文件在、但没读成」，与「文件根本不存在」是两回事：
    // 前者是用户刚改坏了配置，值得把行号摆到脸上；后者只是还没配。
    public static (AppConfig Config, string Path, bool IsLoaded, string? Error) Load()
    {
        var envPath = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var resolvedEnvPath = ResolvePath(envPath);
            if (TryLoad(resolvedEnvPath, out var configFromEnv, out var envError))
                return (configFromEnv, resolvedEnvPath, true, null);

            return (new AppConfig(), resolvedEnvPath, false, envError);
        }

        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        if (TryLoad(path, out var config, out var error))
            return (config, path, true, null);

        return (new AppConfig(), path, false, error);
    }

    // internal 而非 private：Load() 本身要读环境变量和 AppDomain 目录，测不动；
    // 这一层拿路径就能测，而它正是「文件不存在 / 文件写错了」这个区分的落点。
    internal static bool TryLoad(string path, out AppConfig config, out string? error)
    {
        config = new AppConfig();
        error = null;

        try
        {
            if (!File.Exists(path)) return false;

            var loaded = Parse(File.ReadAllText(path), out error);
            if (loaded == null) return false;

            config = loaded;
            return true;
        }
        catch (Exception exception)
        {
            // 读不动文件（权限、占用、编码）也要说清楚是哪一步失败的
            error = exception.Message;
            return false;
        }
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
