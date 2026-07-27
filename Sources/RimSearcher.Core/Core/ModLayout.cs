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

    // 存在于某个生效文件夹里、但被更高优先级的同相对路径文件顶掉的文件（绝对路径）。
    // 游戏根本不会解析它们，索引也不该收。
    public required IReadOnlySet<string> Shadowed { get; init; }

    // 解析过程中的降级说明，交给 Server 侧记日志——Core 不依赖日志设施
    public required IReadOnlyList<string> Notes { get; init; }

    public bool HasContent => XmlDirs.Count > 0 || AssemblyDirs.Count > 0;
}

// 把一个 mod 根目录翻译成「这个游戏版本下 RimWorld 真正会加载的那些目录和文件」。
//
// 规则出自 ModContentPack.foldersToLoadDescendingOrder + DirectXmlLoader.XmlAssetsInModFolder：
// 游戏按优先级从高到低遍历各内容文件夹，用**相对于文件夹根的路径**做去重，同相对路径只取
// 优先级最高的那一份。注意这是文件级覆盖，不是 def 级合并——ModRoot/Defs/Traits.xml 只要
// 在 1.6/Defs/Traits.xml 里有同名文件，整份都不会被解析。
public static class ModLayoutResolver
{
    // 只收游戏真正解析成 Def / 加载成程序集的目录。Languages、Textures、Sounds 不进索引。
    private static readonly string[] XmlDirNames = ["Defs", "Patches"];
    private const string AssemblyDirName = "Assemblies";

    private const string LoadFoldersFileName = "loadFolders.xml";

    private static readonly Regex VersionDirName = new(@"^1\.\d+$", RegexOptions.Compiled);

    // gameVersion 为 null（Version.txt 读不到）时按目录里最高的版本走，并在 Notes 里说明。
    public static ModLayout? Resolve(string modRoot, string? gameVersion)
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

        List<string>? folders = null;
        List<string> xmlDirs = [];
        List<string> assemblyDirs = [];
        var chosen = chain[0];
        var matched = false;

        // 首选是 gameVersion 自身。它一份内容都产不出（mod 尚未适配该版本，或整个 mod 就没有
        // 版本目录）时才往下走——否则「手动指了这个 mod 却什么都搜不到」会被当成工具的 bug。
        foreach (var candidate in chain)
        {
            var candidateFolders = FoldersFor(root, candidate, notes);
            var (xml, assemblies) = ContentDirs(candidateFolders);

            // 一个候选都没成时（纯汉化包、纯贴图包）报的仍是首选那份布局，
            // 而不是链尾那次尝试的残留——后者会让日志显示一个该 mod 根本没走的版本
            folders ??= candidateFolders;

            if (xml.Count > 0 || assemblies.Count > 0)
            {
                folders = candidateFolders;
                xmlDirs = xml;
                assemblyDirs = assemblies;
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

        return new ModLayout
        {
            Root = root,
            Name = ReadModName(root),
            Version = chosen,
            Folders = folders ?? [],
            XmlDirs = xmlDirs,
            AssemblyDirs = assemblyDirs,
            Shadowed = ComputeShadowed(folders ?? []),
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
    private static List<string> FoldersFor(string root, string? version, List<string> notes)
    {
        var declared = ReadLoadFolders(root, version, notes);
        if (declared != null) return declared;

        // 默认布局：版本目录（若在）压过根目录
        var folders = new List<string>();
        if (version != null)
        {
            var versionDir = Path.Combine(root, version);
            if (Directory.Exists(versionDir)) folders.Add(versionDir);
        }

        folders.Add(root);
        return folders;
    }

    // loadFolders.xml 的 <v1.6> 节点。列表里越靠后优先级越高，故读出来要反转。
    // 返回 null 表示「没有这份声明」，由调用方走默认布局。
    private static List<string>? ReadLoadFolders(string root, string? version, List<string> notes)
    {
        if (version == null) return null;

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

        var node = document.Root?.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName.TrimStart('v', 'V'), version, StringComparison.OrdinalIgnoreCase));

        if (node == null) return null;

        var folders = new List<string>();
        var conditional = 0;

        foreach (var item in node.Elements("li"))
        {
            var value = item.Value.Trim();
            if (value.Length == 0) continue;

            // IfModActive / IfModNotActive 指向的补丁目录：手动指 mod 根时无从判断哪些 mod
            // 处于启用状态，全部收下。索引比游戏宽一点无害，漏索引才难查。
            if (item.Attributes().Any(attribute =>
                    attribute.Name.LocalName.StartsWith("IfMod", StringComparison.OrdinalIgnoreCase)))
                conditional++;

            var resolved = value is "/" or "."
                ? root
                : Path.GetFullPath(Path.Combine(root, value.Replace('/', Path.DirectorySeparatorChar)));

            if (Directory.Exists(resolved) && !folders.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                folders.Add(resolved);
        }

        if (folders.Count == 0) return null;

        if (conditional > 0)
            notes.Add($"{conditional} conditional folder(s) in {LoadFoldersFileName} included unconditionally");

        folders.Reverse();
        return folders;
    }

    private static (List<string> Xml, List<string> Assemblies) ContentDirs(IReadOnlyList<string> folders)
    {
        var xml = new List<string>();
        var assemblies = new List<string>();

        foreach (var folder in folders)
        {
            foreach (var name in XmlDirNames)
            {
                var directory = Path.Combine(folder, name);
                if (Directory.Exists(directory)) xml.Add(directory);
            }

            var assemblyDirectory = Path.Combine(folder, AssemblyDirName);
            if (Directory.Exists(assemblyDirectory)) assemblies.Add(assemblyDirectory);
        }

        return (xml, assemblies);
    }

    // folders 已是降序优先级，故先见到的即胜出者，后来的同相对路径文件全是死内容。
    private static HashSet<string> ComputeShadowed(IReadOnlyList<string> folders)
    {
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folders.Count < 2) return shadowed;

        var winners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var name in XmlDirNames.Append(AssemblyDirName))
            {
                var directory = Path.Combine(folder, name);
                if (!Directory.Exists(directory)) continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
                }
                catch { continue; }

                foreach (var file in files)
                {
                    // 相对于 mod 文件夹根，不是相对于 Defs——游戏比的就是这一层
                    var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    if (!winners.Add(relative)) shadowed.Add(Path.GetFullPath(file));
                }
            }
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
