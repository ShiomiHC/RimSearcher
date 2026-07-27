using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RimSearcher.Core;

// 两个索引器共用的入口筛：先扔掉被遮蔽的文件，再把本次真正要扫的挑出来。
// tryClaim 收绝对路径（两边的 processedFiles 都以它为键），返回 true 表示这个文件还没扫过。
internal static class ScanFilter
{
    public static List<string> SelectNew(
        IEnumerable<string> files,
        IReadOnlySet<string>? excluded,
        Func<string, bool> tryClaim)
    {
        var results = new List<string>();

        foreach (var file in files)
        {
            string full;
            try
            {
                full = Path.GetFullPath(file);
            }
            catch
            {
                continue;
            }

            if (excluded != null && excluded.Contains(full)) continue;
            if (tryClaim(full)) results.Add(file);
        }

        return results;
    }
}

// 一个 mod 根按 RimWorld 的加载规则解析出来的实际生效内容。
public sealed record ModLayout
{
    public required string Root { get; init; }

    // About.xml 里的 <name>；读不到则为 null（调用方回退到目录名，而 workshop 的目录名是数字 ID）
    public string? Name { get; init; }

    // 实际采用的版本目录键（"1.6"）。null = 该 mod 没有版本目录，内容全在根下。
    public string? Version { get; init; }

    // 游戏会加载的 mod 内容文件夹，优先级从高到低。同相对路径的文件里，靠前的那份赢。
    public required IReadOnlyList<string> Folders { get; init; }

    public required IReadOnlyList<string> XmlDirs { get; init; }
    public required IReadOnlyList<string> AssemblyDirs { get; init; }

    // 生效文件夹下的 Languages 目录，优先级同 Folders。不进任何搜索索引——只供译文查表，
    // 故与 XmlDirs 分开：混进去会让 search_regex 把翻译文件当源码搜出来。
    public required IReadOnlyList<string> LanguageDirs { get; init; }

    // 存在于某个生效文件夹里、但被更高优先级的同相对路径文件顶掉的文件（绝对路径）。
    // 游戏根本不会解析它们，索引也不该收。
    public required IReadOnlySet<string> Shadowed { get; init; }

    // 解析过程中的降级说明，交给 Server 侧记日志——Core 不依赖日志设施
    public required IReadOnlyList<string> Notes { get; init; }

    public bool HasContent => XmlDirs.Count > 0 || AssemblyDirs.Count > 0;

    // 纯汉化包（只有 Languages、没有 Defs/Patches/Assemblies）在 workshop 里是一大类，
    // 光这台机器的 249 个订阅里就有 65 个。它们对搜索无贡献，但正是别人 def 的译文出处。
    public bool HasLocalization => LanguageDirs.Count > 0;
}

// 把一个 mod 根目录翻译成「这个游戏版本下 RimWorld 真正会加载的那些目录和文件」。
//
// 规则逐条核对过 1.6 的 Verse.ModContentPack.InitLoadFolders / Verse.ModLoadFolders /
// Verse.LoadFolder.ShouldLoad / Verse.ModLister.AnyModActiveNoSuffix：
//   · loadFolders.xml 优先，节点选择顺序是 当前版本 → ≤当前版本的最高版本 → default
//   · 列表里越靠后优先级越高（AddFolders 是倒着遍历的）
//   · 没有 loadFolders.xml 时的默认布局是 [<版本目录>, Common, 根]
//   · 去重按**相对于 mod 文件夹根**的路径，是文件级覆盖而不是 def 级合并——
//     ModRoot/Defs/Traits.xml 只要在 1.6/Defs/Traits.xml 有同名文件，整份都不会被解析
public static class ModLayoutResolver
{
    // 只收游戏真正解析成 Def / 加载成程序集的目录。Textures、Sounds 不进索引。
    private static readonly string[] XmlDirNames = ["Defs", "Patches"];
    private const string AssemblyDirName = "Assemblies";

    // 不进搜索索引，只喂 LocalizationIndex（见 ModLayout.LanguageDirs）
    private const string LanguageDirName = "Languages";

    // ModContentPack.CommonFolderName：默认布局里排在版本目录之后、根目录之前
    private const string CommonFolderName = "Common";

    private const string LoadFoldersFileName = "loadFolders.xml";
    private const string DefaultVersionKey = "default";

    // ModMetaData.SteamModPostfix：同一个 mod 的 steam 版 packageId 带这个后缀，
    // 而条件判定走的是 *NoSuffix 系列，两边都要先脱掉它
    private const string SteamPostfix = "_steam";

    private static readonly Regex VersionDirName = new(@"^1\.\d+$", RegexOptions.Compiled);

    // 一个内容文件夹，以及 loadFolders.xml 给它挂的加载条件（无条件则为 null）
    private sealed record FolderEntry(string Path, LoadCondition? Condition);

    // LoadFolder.ShouldLoad 的三个来源：IfModActive 是「任一启用」，IfModActiveAll 是
    // 「全部启用」，IfModNotActive 是「任一启用即排除」。三者可以同时挂在一个 li 上，取合取。
    private sealed record LoadCondition(string[] AnyOf, string[] AllOf, string[] NotAnyOf)
    {
        // activeMods 为 null 表示「不知道谁启用着」——一律当满足，宁可多收
        public bool IsMet(IReadOnlySet<string>? activeMods)
        {
            if (activeMods == null) return true;

            if (AnyOf.Length > 0 && !AnyOf.Any(activeMods.Contains)) return false;
            if (AllOf.Length > 0 && !AllOf.All(activeMods.Contains)) return false;
            if (NotAnyOf.Length > 0 && NotAnyOf.Any(activeMods.Contains)) return false;

            return true;
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (AnyOf.Length > 0) parts.Add(string.Join("|", AnyOf));
            if (AllOf.Length > 0) parts.Add(string.Join("&", AllOf));
            if (NotAnyOf.Length > 0) parts.Add("!" + string.Join("|", NotAnyOf));

            return string.Join(" ", parts);
        }
    }

    // gameVersion 为 null（Version.txt 读不到）时按目录里最高的版本走，并在 Notes 里说明。
    //
    // activeMods 是「哪些 packageId 处于启用状态」的白名单，用来判定条件目录。传 null（默认）
    // 即不判定、条件目录全收——那是安全的一侧，但一个 mod 用两组互斥条件挂两套内容时
    // （RatkinGene 的 1.6 与 1.6_unofficial），全收会让优先级由 loadFolders 的书写顺序决定，
    // 而不是由哪个前置真的启用着决定。这种情形会记进 Notes 提示显式指定。
    public static ModLayout? Resolve(
        string modRoot,
        string? gameVersion,
        IReadOnlyCollection<string>? activeMods = null)
    {
        if (string.IsNullOrWhiteSpace(modRoot)) return null;

        string root;
        try
        {
            root = Path.GetFullPath(modRoot.Trim());
        }
        catch
        {
            return null;
        }

        if (!Directory.Exists(root)) return null;

        var notes = new List<string>();
        var chain = BuildVersionChain(root, gameVersion);
        var active = activeMods == null
            ? null
            : new HashSet<string>(activeMods.Select(NormalizePackageId), StringComparer.OrdinalIgnoreCase);

        List<FolderEntry>? folders = null;
        List<string> xmlDirs = [];
        List<string> assemblyDirs = [];
        List<string> languageDirs = [];
        var chosen = chain[0];
        var matched = false;

        // 首选是 gameVersion 自身。它一份内容都产不出（mod 尚未适配该版本，或整个 mod 就没有
        // 版本目录）时才往下走——游戏此时是什么都不加载，但用户手动指了这个 mod 就是想搜它，
        // 「配了却什么都搜不到」会被当成工具的 bug。回退了就记进 Notes。
        foreach (var candidate in chain)
        {
            var candidateFolders = FoldersFor(root, candidate, active, notes);
            var (xml, assemblies, languages) = ContentDirs(candidateFolders);

            // 一个候选都没成时（纯贴图包）报的仍是首选那份布局，
            // 而不是链尾那次尝试的残留——后者会让日志显示一个该 mod 根本没走的版本
            folders ??= candidateFolders;

            if (xml.Count > 0 || assemblies.Count > 0)
            {
                folders = candidateFolders;
                xmlDirs = xml;
                assemblyDirs = assemblies;
                languageDirs = languages;
                chosen = candidate;
                matched = true;
                break;
            }
        }

        // 白名单把内容全筛没了（该 mod 的前置一个都没启用，且没有无条件内容兜着）
        // → 与上面同一条思路：宁可多收也不让一个明确配了的 mod 变成空的
        if (!matched && active != null)
        {
            var relaxed = FoldersFor(root, chain[0], null, notes);
            var (xml, assemblies, languages) = ContentDirs(relaxed);

            if (xml.Count > 0 || assemblies.Count > 0)
            {
                notes.Add("active_mods matched no conditional folder, fell back to including all");
                folders = relaxed;
                xmlDirs = xml;
                assemblyDirs = assemblies;
                languageDirs = languages;
                chosen = chain[0];
                matched = true;
            }
        }

        // 纯汉化包：一个 Defs/Patches/Assemblies 都没有，全部内容就是 Languages。上面两轮都按
        // 「有没有可索引内容」判定，对它们必然落空，故这里单独再走一次版本链——否则一个只适配到
        // 1.5 的汉化包会被算成没有内容，连带它译的那些 def 全部显示不出译名。
        //
        // 单独一轮而不是并进上面那轮：普通 mod 的版本选择必须继续只看 Defs/Assemblies，
        // 让 Languages 参与判定会把「某版本目录下只放了翻译」的 mod 选到错的版本上。
        if (!matched)
        {
            foreach (var candidate in chain)
            {
                var candidateFolders = FoldersFor(root, candidate, active, notes);
                var (_, _, languages) = ContentDirs(candidateFolders);

                if (languages.Count == 0) continue;

                folders = candidateFolders;
                languageDirs = languages;
                chosen = candidate;
                matched = true;
                break;
            }
        }

        if (matched && chosen != gameVersion)
        {
            notes.Add(gameVersion == null
                ? $"game version unknown, using '{chosen ?? "<root>"}'"
                : $"no content for {gameVersion}, fell back to '{chosen ?? "<root>"}'");
        }

        var resolved = folders ?? [];
        var shadowed = ComputeShadowed(resolved, notes);

        return new ModLayout
        {
            Root = root,
            Name = ReadModName(root),
            Version = chosen,
            Folders = resolved.Select(entry => entry.Path).ToList(),
            XmlDirs = xmlDirs,
            AssemblyDirs = assemblyDirs,
            LanguageDirs = languageDirs,
            Shadowed = shadowed,
            // 版本链上试过几个候选就会走几遍 FoldersFor，同一条降级说明会被记多次
            Notes = notes.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    // 首选版本打头，其后是比它旧的版本目录（降序），最后是「不带版本目录」。
    // 比 gameVersion 新的目录一律不进：游戏自己也不会加载它们。
    private static List<string?> BuildVersionChain(string root, string? gameVersion)
    {
        var present = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (VersionDirName.IsMatch(name)) present.Add(name);
            }
        }
        catch { }

        present.Sort(static (a, b) => CompareVersions(b, a));

        var chain = new List<string?>();
        if (gameVersion != null) chain.Add(gameVersion);

        foreach (var version in present)
        {
            if (chain.Contains(version)) continue;
            if (gameVersion != null && CompareVersions(version, gameVersion) > 0) continue;
            chain.Add(version);
        }

        chain.Add(null);
        return chain;
    }

    // "1.10" 要大于 "1.6"：逐段按数值比，别落回字符串序
    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var parsedA) ? parsedA : 0;
            var y = i < b.Length && int.TryParse(b[i], out var parsedB) ? parsedB : 0;
            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    // 优先级从高到低的内容文件夹。loadFolders.xml 说了算，没有它才用默认布局。
    private static List<FolderEntry> FoldersFor(
        string root,
        string? version,
        IReadOnlySet<string>? activeMods,
        List<string> notes)
    {
        var declared = ReadLoadFolders(root, version, activeMods, notes);
        if (declared != null) return declared;

        // ModContentPack.InitLoadFolders 的默认布局：<版本目录>、Common、根，优先级依次递减
        var folders = new List<FolderEntry>();

        if (version != null)
        {
            var versionDir = Path.Combine(root, version);
            if (Directory.Exists(versionDir)) folders.Add(new FolderEntry(versionDir, null));
        }

        var commonDir = Path.Combine(root, CommonFolderName);
        if (Directory.Exists(commonDir)) folders.Add(new FolderEntry(commonDir, null));

        folders.Add(new FolderEntry(root, null));
        return folders;
    }

    // loadFolders.xml 的版本节点。节点选择顺序与 InitLoadFolders 一致：当前版本 →
    // ≤当前版本的最高版本 → default。列表里越靠后优先级越高，故读出来要反转。
    // 返回 null 表示「没有可用的声明」，由调用方走默认布局。
    private static List<FolderEntry>? ReadLoadFolders(
        string root,
        string? version,
        IReadOnlySet<string>? activeMods,
        List<string> notes)
    {
        var path = Path.Combine(root, LoadFoldersFileName);
        if (!File.Exists(path)) return null;

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            // 读坏了就当没有，走默认布局——比整个 mod 解析不出来强
            notes.Add($"{LoadFoldersFileName} unreadable ({ex.Message}), using default layout");
            return null;
        }

        var nodes = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Root?.Elements() ?? [])
        {
            var key = NormalizeVersionKey(element.Name.LocalName);
            if (key.Length > 0) nodes.TryAdd(key, element);
        }

        var node = SelectVersionNode(nodes, version);
        if (node == null) return null;

        var folders = new List<FolderEntry>();
        var included = 0;
        var skipped = 0;

        foreach (var item in node.Elements("li"))
        {
            var value = item.Value.Trim();
            if (value.Length == 0) continue;

            var condition = ReadCondition(item);
            if (condition != null)
            {
                if (!condition.IsMet(activeMods))
                {
                    skipped++;
                    continue;
                }

                included++;
            }

            // ModLoadFolders 只把 "/" 和 "\" 当作 mod 根
            var resolved = value is "/" or "\\"
                ? root
                : Path.GetFullPath(Path.Combine(root, value.Replace('/', Path.DirectorySeparatorChar)));

            if (Directory.Exists(resolved)
                && !folders.Any(entry => entry.Path.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
                folders.Add(new FolderEntry(resolved, condition));
        }

        if (folders.Count == 0) return null;

        // 没给 active_mods 时条件目录一律收下：索引比游戏宽一点无害，漏索引才难查
        if (included > 0 && activeMods == null)
            notes.Add($"{included} conditional folder(s) in {LoadFoldersFileName} included unconditionally");

        if (skipped > 0)
            notes.Add($"{skipped} conditional folder(s) skipped by active_mods");

        folders.Reverse();
        return folders;
    }

    // ModLoadFolders.LoadDataFromXmlCustom：节点名转小写后，打头的 v 去掉
    private static string NormalizeVersionKey(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return key.StartsWith('v') ? key[1..] : key;
    }

    private static XElement? SelectVersionNode(Dictionary<string, XElement> nodes, string? version)
    {
        if (version != null)
        {
            if (nodes.TryGetValue(version, out var exact)) return exact;

            // 声明的版本节点未必有同名目录，故这一步不能靠外层那条按目录名建的版本链
            var older = nodes.Keys
                .Where(key => key.Contains('.') && CompareVersions(key, version) <= 0)
                .OrderByDescending(key => key, Comparer<string>.Create(CompareVersions))
                .FirstOrDefault();

            if (older != null) return nodes[older];
        }

        return nodes.GetValueOrDefault(DefaultVersionKey);
    }

    // li 上的 IfModActive / IfModActiveAll / IfModNotActive，三者可以并存
    private static LoadCondition? ReadCondition(XElement item)
    {
        string[] anyOf = [];
        string[] allOf = [];
        string[] notAnyOf = [];

        foreach (var attribute in item.Attributes())
        {
            var name = attribute.Name.LocalName;

            if (name.Equals("IfModActive", StringComparison.OrdinalIgnoreCase))
                anyOf = SplitPackageIds(attribute.Value);
            else if (name.Equals("IfModActiveAll", StringComparison.OrdinalIgnoreCase))
                allOf = SplitPackageIds(attribute.Value);
            else if (name.Equals("IfModNotActive", StringComparison.OrdinalIgnoreCase))
                notAnyOf = SplitPackageIds(attribute.Value);
        }

        return anyOf.Length + allOf.Length + notAnyOf.Length == 0
            ? null
            : new LoadCondition(anyOf, allOf, notAnyOf);
    }

    // 脱掉 _steam 后缀后重复是常态：作者写 "CETeam.CombatExtended, CETeam.CombatExtended_steam"
    // 就是为了同时点到 CE 的两个发行版，而那两个的 NoSuffix 形式本就是同一个
    private static string[] SplitPackageIds(string value)
        => value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePackageId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizePackageId(string id)
    {
        var normalized = id.Trim().ToLowerInvariant();
        return normalized.EndsWith(SteamPostfix, StringComparison.Ordinal)
            ? normalized[..^SteamPostfix.Length]
            : normalized;
    }

    private static (List<string> Xml, List<string> Assemblies, List<string> Languages) ContentDirs(
        IReadOnlyList<FolderEntry> folders)
    {
        var xml = new List<string>();
        var assemblies = new List<string>();
        var languages = new List<string>();

        foreach (var folder in folders)
        {
            foreach (var name in XmlDirNames)
            {
                var directory = Path.Combine(folder.Path, name);
                if (Directory.Exists(directory)) xml.Add(directory);
            }

            var assemblyDirectory = Path.Combine(folder.Path, AssemblyDirName);
            if (Directory.Exists(assemblyDirectory)) assemblies.Add(assemblyDirectory);

            var languageDirectory = Path.Combine(folder.Path, LanguageDirName);
            if (Directory.Exists(languageDirectory)) languages.Add(languageDirectory);
        }

        return (xml, assemblies, languages);
    }

    // folders 已是降序优先级，故先见到的即胜出者，后来的同相对路径文件全是死内容。
    private static HashSet<string> ComputeShadowed(IReadOnlyList<FolderEntry> folders, List<string> notes)
    {
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folders.Count < 2) return shadowed;

        var winners = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);

        // 两个都带条件、条件又不同的文件夹互相遮蔽 = 一个 mod 挂了两套互斥内容（前置 A 装了
        // 用这套、装了 B 用那套）。此时谁赢由 loadFolders 的书写顺序决定，而不是由哪个前置真的
        // 启用着决定——正是 active_mods 该介入的地方。带条件的目录遮蔽无条件目录则是常规的
        // 「DLC 装了就替换基础定义」，不算冲突。
        var conflicts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            foreach (var name in XmlDirNames.Append(AssemblyDirName))
            {
                var directory = Path.Combine(folder.Path, name);
                if (!Directory.Exists(directory)) continue;

                IEnumerable<string> files;
                try
                {
                    // 只算会被索引的两类。Defs 目录里的 .gitkeep / 说明文本也会互相遮蔽，
                    // 但它们进不了索引，收进来只会让这个集合（进缓存指纹）随无关文件变动
                    files = Directory
                        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                        .Where(file => file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                       || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                }
                catch { continue; }

                foreach (var file in files)
                {
                    // 相对于 mod 文件夹根，不是相对于 Defs——游戏比的就是这一层
                    var relative = Path.GetRelativePath(folder.Path, file).Replace('\\', '/');

                    if (winners.TryGetValue(relative, out var winner))
                    {
                        shadowed.Add(Path.GetFullPath(file));

                        if (winner.Condition != null
                            && folder.Condition != null
                            && winner.Condition.Describe() != folder.Condition.Describe())
                        {
                            conflicts.Add(
                                $"{Path.GetFileName(winner.Path)} [{winner.Condition.Describe()}] vs " +
                                $"{Path.GetFileName(folder.Path)} [{folder.Condition.Describe()}]");
                        }
                    }
                    else
                    {
                        winners[relative] = folder;
                    }
                }
            }
        }

        foreach (var conflict in conflicts.Order(StringComparer.Ordinal))
        {
            notes.Add($"mutually exclusive conditional folders, both included: {conflict} " +
                      "— set active_mods to pick one");
        }

        return shadowed;
    }

    // About/About.xml 的 <name>。workshop 目录名是纯数字 ID，拿它当源名等于没有名字。
    private static string? ReadModName(string root)
    {
        var path = Path.Combine(root, "About", "About.xml");
        if (!File.Exists(path)) return null;

        try
        {
            var name = XDocument.Load(path).Root?
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase))?
                .Value.Trim();

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
