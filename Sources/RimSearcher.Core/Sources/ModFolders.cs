using System.Xml.Linq;

namespace RimSearcher.Sources;

/// <summary>
/// 一个 mod 里游戏**真会加载**的那些程序集。
///
/// 这不是「找出所有 dll」——那件事很容易,也很容易错。一个装了多年的 mod 目录里,
/// 属于旧游戏版本的 dll 通常比在用的多(HAR 实测:20 个 dll,在用的 2 个),
/// 而互斥分支(RatkinGene 的 <c>1.6</c> 与 <c>1.6_unofficial</c>)更是两套只该进一套。
/// 反编译进错的那一套,后果是 <c>code-search</c> 找出根本没在跑的代码 ——
/// 而它长得跟真答案一模一样。
///
/// 所以这里**复刻游戏自己的算法**,而不是另立一套启发式:
/// <see cref="LoadFolders"/> 对应 <c>ModContentPack.InitLoadFolders()</c>,
/// <see cref="Assemblies"/> 对应 <c>GetAllFilesForModPreserveOrder(mod, "Assemblies/")</c> 的去重。
/// 判据不是「看起来对」,而是「与那两个方法读同一份 loadFolders.xml 得出同一个答案」。
///
/// 复刻得起来的前提是**知道哪些 mod 是启用的** —— <c>IfModActive</c> 这类条件靠它才判得动。
/// 而那件事快照里已经记着(游戏亲自答的),不必再手写一份会漂的列表。
/// </summary>
public static class ModFolders
{
    /// <summary>Steam 订阅副本在 packageId 后面挂的后缀。游戏比对时忽略它(<c>ignorePostfix</c>)。</summary>
    private const string SteamPostfix = "_steam";

    private sealed record Entry(string Folder, List<string>? IfAny, List<string>? IfAll, List<string>? IfNot);

    /// <summary>
    /// 该 mod 的加载目录,**优先级由高到低**。同名相对路径的文件,靠前的那个赢。
    /// </summary>
    /// <param name="gameVersion">
    /// 游戏的 <c>CurrentVersionString</c>,即 <c>major.minor.build</c>(如 <c>1.6.4871</c>)。
    /// loadFolders.xml 的键几乎总是 <c>major.minor</c>,故它一般走「小于等于当前的最高一个」那条分支 ——
    /// 这与游戏的顺序一致,不是巧合:那两级回退就是照抄的。
    /// </param>
    public static List<string> LoadFolders(string rootDir, string gameVersion, IReadOnlySet<string> activeIds)
    {
        var declared = ReadLoadFolders(rootDir);
        if (declared.Count > 0)
        {
            var picked = PickVersion(declared, gameVersion);
            if (picked is not null)
            {
                // 游戏是**倒着**加进去的(AddFolders 从末尾往前走),即 xml 里写在后面的优先级更高。
                var result = new List<string>();
                for (var i = picked.Count - 1; i >= 0; i--)
                {
                    var e = picked[i];
                    if (!ShouldLoad(e, activeIds)) continue;
                    result.Add(e.Folder.Length == 0 ? rootDir : Path.Combine(rootDir, e.Folder));
                }
                // 声明了本版本的 folder 列表就**只**用它:根目录、Common、版本目录一概不再自动补。
                // 这一条最容易想当然地补上,而补了就等于让 IfModActive 关掉的那套又漏进来。
                if (result.Count > 0) return result;
            }
        }

        var folders = new List<string>();

        var withoutBuild = MajorMinor(gameVersion);
        var exact = withoutBuild is null ? null : Path.Combine(rootDir, withoutBuild);
        if (exact is not null && Directory.Exists(exact))
        {
            folders.Add(exact);
        }
        else if (BestVersionDir(rootDir, gameVersion) is { } best)
        {
            folders.Add(best);
        }

        var common = Path.Combine(rootDir, "Common");
        if (Directory.Exists(common)) folders.Add(common);

        folders.Add(rootDir);
        return folders;
    }

    /// <summary>
    /// 该 mod 里游戏会加载的 dll,绝对路径。按 <c>Assemblies/</c> 下的相对路径去重,高优先级目录赢。
    ///
    /// 实测 HAR:根 <c>Assemblies/</c> 与 <c>1.6/Assemblies/</c> 各有一份 AlienRace.dll 与 0Harmony.dll,
    /// 游戏用的是 1.6 那份。少了这一步,反编译出来的会是好几年前的代码。
    /// </summary>
    public static List<string> Assemblies(string rootDir, string gameVersion, IReadOnlySet<string> activeIds)
    {
        var byRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in LoadFolders(rootDir, gameVersion, activeIds))
        {
            var dir = Path.Combine(folder, "Assemblies");
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                // 键是 Assemblies/ 起算的相对路径 —— 游戏用的正是这个粒度,而不是文件名:
                // Assemblies/a/x.dll 与 Assemblies/b/x.dll 在游戏看来是两个文件,都会加载。
                var key = Path.GetRelativePath(folder, file).Replace('\\', '/');
                // 先到先得:LoadFolders 已经按优先级从高到低排好了。
                byRelative.TryAdd(key, Path.GetFullPath(file));
            }
        }

        var result = byRelative.Values.Where(p => !AssemblyFilter.IsRuntimeAssembly(p)).ToList();
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    // ---- loadFolders.xml ----

    private static Dictionary<string, List<Entry>> ReadLoadFolders(string rootDir)
    {
        var result = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

        // 游戏用 ResolveCaseInsensitiveFilePath 找它。Windows 上文件系统本来不分大小写,
        // 但真实 mod 里 LoadFolders.xml / loadFolders.xml 两种写法都有,列目录来找最省心。
        string? path;
        try
        {
            path = Directory.EnumerateFiles(rootDir, "*.xml", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(f => Path.GetFileName(f)
                                .Equals("loadFolders.xml", StringComparison.OrdinalIgnoreCase));
        }
        catch { return result; }
        if (path is null) return result;

        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch { return result; }   // 坏 xml:退回默认布局,与游戏一致(ItemFromXmlFile 失败返回 null)

        foreach (var versionNode in doc.Root?.Elements() ?? [])
        {
            // 游戏把节点名小写、再剥掉开头的 'v'。<v1.6> 与 <V1.6> 与 <1.6> 是同一个键。
            var key = versionNode.Name.LocalName.ToLowerInvariant();
            if (key.StartsWith('v')) key = key[1..];

            if (!result.TryGetValue(key, out var list)) result[key] = list = [];

            foreach (var li in versionNode.Elements())
            {
                var folder = li.Value.Trim();
                if (folder is "/" or "\\") folder = "";
                list.Add(new Entry(
                    folder.Replace('/', Path.DirectorySeparatorChar),
                    Split(li.Attribute("IfModActive")?.Value),
                    Split(li.Attribute("IfModActiveAll")?.Value),
                    Split(li.Attribute("IfModNotActive")?.Value)));
            }
        }

        return result;
    }

    private static List<string>? Split(string? raw)
        => raw is null ? null : raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>
    /// 版本键的两级回退,与 <c>InitLoadFolders</c> 一致:先精确匹配完整版本号,
    /// 再取「小于等于当前版本的最高一个」,最后 <c>default</c>。
    ///
    /// 第二级不是可选的花活:游戏的完整版本号是 <c>1.6.4871</c>,而 mod 写的键是 <c>1.6</c> ——
    /// 只做精确匹配的话,**每一个** loadFolders.xml 都会被判成不适用。
    /// </summary>
    private static List<Entry>? PickVersion(Dictionary<string, List<Entry>> declared, string gameVersion)
    {
        if (declared.TryGetValue(gameVersion, out var exact) && exact.Count > 0) return exact;

        var current = ParseVersion(gameVersion);
        if (current is not null)
        {
            var best = declared.Keys
                .Where(k => !k.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(k => (Key: k, Version: ParseVersion(k)))
                .Where(t => t.Version is not null && t.Version <= current)
                .OrderByDescending(t => t.Version)
                .Select(t => t.Key)
                .FirstOrDefault();
            if (best is not null) return declared[best];
        }

        return declared.TryGetValue("default", out var fallback) ? fallback : null;
    }

    private static bool ShouldLoad(Entry e, IReadOnlySet<string> activeIds)
    {
        if (e.IfAny is { Count: > 0 } && !e.IfAny.Any(id => IsActive(id, activeIds))) return false;
        if (e.IfAll is { Count: > 0 } && !e.IfAll.All(id => IsActive(id, activeIds))) return false;
        if (e.IfNot is { Count: > 0 } && e.IfNot.Any(id => IsActive(id, activeIds))) return false;
        return true;
    }

    /// <summary>后缀不算。Steam 订阅那份 id 尾巴上挂着 <c>_steam</c>,而 loadFolders 里写的是裸 id。</summary>
    private static bool IsActive(string id, IReadOnlySet<string> activeIds)
        => activeIds.Contains(StripPostfix(id));

    internal static string StripPostfix(string id)
    {
        var trimmed = id.Trim();
        return trimmed.EndsWith(SteamPostfix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^SteamPostfix.Length]
            : trimmed;
    }

    /// <summary>启用集合的规范形态:小写、剥掉 <c>_steam</c>。比对两侧都过这一道。</summary>
    public static HashSet<string> NormalizeActive(IEnumerable<string> packageIds)
        => packageIds.Select(id => StripPostfix(id).ToLowerInvariant())
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? MajorMinor(string version)
    {
        var v = ParseVersion(version);
        return v is null ? null : $"{v.Major}.{v.Minor}";
    }

    /// <summary>
    /// 版本目录名 → 版本。<c>1.6_unofficial</c> **解析不出来**,于是它永远不会被当成版本目录 ——
    /// 这正是要的:那是一条靠 <c>IfModActive</c> 开关的互斥分支,不是「1.6 的另一种写法」。
    /// </summary>
    private static Version? ParseVersion(string raw)
    {
        var parts = raw.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return null;
        return parts.Length switch
        {
            2 => new Version(nums[0], nums[1]),
            3 => new Version(nums[0], nums[1], nums[2]),
            _ => new Version(nums[0], nums[1], nums[2], nums[3]),
        };
    }

    private static string? BestVersionDir(string rootDir, string gameVersion)
    {
        var current = ParseVersion(gameVersion);
        if (current is null) return null;

        string[] dirs;
        try { dirs = Directory.GetDirectories(rootDir); }
        catch { return null; }

        return dirs
            .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
            .Where(t => t.Version is not null && t.Version <= current)
            .OrderByDescending(t => t.Version)
            .Select(t => t.Dir)
            .FirstOrDefault();
    }
}
