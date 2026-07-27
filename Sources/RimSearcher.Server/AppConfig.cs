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

    // 查 def 时附带哪种语言的译名。"auto"（默认）= 读游戏 Prefs.xml 里的 langFolderName；
    // "off" = 不做本地化；也可以直接写语言名（"ChineseSimplified"，带不带原生名后缀都认）。
    public string? Localization { get; init; } = LocalizationAuto;

    // 译文描述只在 inspect 里显示，且默认关：它比 label 长一到两个数量级，
    // 默认打开会让每次 inspect 都多吐几百字，而多数时候只想知道这个 def 叫什么。
    public bool LocalizationDescription { get; init; } = false;

    public const string LocalizationAuto = "auto";
    public const string LocalizationOff = "off";

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
        var languages = new List<LanguageDirEntry>();
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        var gameVersion = GameVersion ?? DetectGameVersion();
        var sourceRank = -1;

        foreach (var raw in Sources)
        {
            sourceRank++;

            // FolderRank 在源内从 0 起算：跨源的先后由 SourceRank 定，同源内才轮到它。
            var folderRank = 0;

            // 用户手写的 xml 路径（vanilla 那条指的是 Data，各 DLC 平铺在下面）不走 mod 布局解析，
            // 语言目录只能自己找。mod 展开出来的 xml 目录不在这里探——那些的语言目录由布局给出。
            if (raw != null)
            {
                foreach (var path in raw.Xml)
                {
                    foreach (var found in DiscoverLanguageDirs(path))
                    {
                        languages.Add(new LanguageDirEntry(raw.Name, found, sourceRank, folderRank++));
                    }
                }
            }

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

            // mod 布局给出的语言目录已按优先级降序，直接接在手写路径探出来的那些后面
            foreach (var path in definition.Languages)
            {
                languages.Add(new LanguageDirEntry(definition.Name, path, sourceRank, folderRank++));
            }
        }

        return new ResolvedSources(csharp, xml)
        {
            Languages = languages,
            Shadowed = shadowed,
            GameVersion = gameVersion,
            Notes = notes
        };
    }

    // 从一条手写的 xml 路径往下找 Languages 目录。深度 2 覆盖了实际会出现的两种写法：
    // 直接指到内容根（<root>\Languages），以及指到 Data 这种把各 DLC 平铺在下面的父目录
    // （Data\Core\Languages）。再深就会扫进 mod 的 Defs 子树，代价与误报都不划算。
    private static IEnumerable<string> DiscoverLanguageDirs(string path)
    {
        const string languageDirName = "Languages";

        if (string.IsNullOrWhiteSpace(path)) yield break;

        string root;
        try
        {
            root = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(root)) yield break;
        }
        catch
        {
            yield break;
        }

        var direct = Path.Combine(root, languageDirName);
        if (Directory.Exists(direct)) yield return direct;

        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (Path.GetFileName(child).Equals(languageDirName, StringComparison.OrdinalIgnoreCase)) continue;

            var nested = Path.Combine(child, languageDirName);
            if (Directory.Exists(nested)) yield return nested;
        }
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
        var languages = new List<string>();
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

            // 纯汉化包只有 Languages，可索引内容为零。以前这里直接跳过整个 mod，于是它译的那些
            // def 一个译名都拿不到；现在放它过去收语言目录，而 Csharp/Xml 仍是空——ScopeCatalog
            // 的词表按那两个列表建，所以它照旧不会在 scope 里冒出一个搜不出东西的源名。
            if (!layout.HasContent && !layout.HasLocalization)
            {
                notes.Add($"{definition.Name}: no Defs/Patches/Assemblies under {layout.Root}");
                continue;
            }

            xml.AddRange(layout.XmlDirs);
            assemblies.AddRange(layout.AssemblyDirs);
            languages.AddRange(layout.LanguageDirs);
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
            Languages = languages,
            Mods = definition.Mods,
            ActiveMods = definition.ActiveMods
        };
    }

    // 最终要用的语言名。null = 不做本地化。
    //
    // "auto" 读游戏自己的 Prefs.xml——那是唯一权威的「用户在玩哪个语言」，比按目录存在与否
    // 猜可靠得多。读不到就返回 null 关掉整个特性：猜一个语言出来，用户看到的会是一堆自己
    // 根本没在用的译名，比不显示更糟。
    public string? ResolveLanguage()
    {
        var configured = Localization?.Trim();

        if (string.IsNullOrEmpty(configured)) return null;
        if (string.Equals(configured, LocalizationOff, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(configured, LocalizationAuto, StringComparison.OrdinalIgnoreCase)) return configured;

        return ReadGameLanguagePreference();
    }

    // %USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\Prefs.xml
    // 里的 <langFolderName>，形如 "ChineseSimplified (简体中文)"——正是语言目录/tar 的名字。
    private static string? ReadGameLanguagePreference()
    {
        foreach (var path in PrefsCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;

                var value = System.Xml.Linq.XDocument.Load(path).Root?
                    .Elements()
                    .FirstOrDefault(element =>
                        element.Name.LocalName.Equals("langFolderName", StringComparison.OrdinalIgnoreCase))?
                    .Value.Trim();

                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch
            {
                // 读不动就试下一个候选，全不成即关闭本地化
            }
        }

        return null;
    }

    private static IEnumerable<string> PrefsCandidates()
    {
        const string relative = @"Ludeon Studios\RimWorld by Ludeon Studios\Config\Prefs.xml";

        if (OperatingSystem.IsWindows())
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                yield return Path.Combine(profile, "AppData", "LocalLow", relative);
        }
        else
        {
            // Unity 在 Linux/macOS 上把 LocalLow 那套落到 ~/.config 与 ~/Library
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) yield break;

            var unix = relative.Replace('\\', Path.DirectorySeparatorChar);
            yield return Path.Combine(home, ".config", "unity3d", unix);
            yield return Path.Combine(home, "Library", "Application Support", unix);
        }
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
            Localization = ConfigToml.String(ConfigToml.Find(table, "localization", "language", "lang"))
                ?? LocalizationAuto,
            LocalizationDescription = ConfigToml.Bool(
                ConfigToml.Find(table, "localizationDescription", "localizedDescription"), false),
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
