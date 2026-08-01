using System.Xml.Linq;
using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 游戏原生的 <c>.rml</c> 模组列表,不发明新格式。
///
/// 结构三块:meta 存档头(仅告警用)/ **有序 ids(唯一效力载体)** / names(展示糖)。
/// 合法生产者三个:游戏界面、<c>modlist save</c>、**手写(含 LLM)**。
///
/// **宽读严写**:读只要求 ids,手写门槛就是一列 packageId;写时补全 names 与 meta 头,
/// 保证游戏自己的载入对话框认得。
/// </summary>
public sealed record ModListFile(string Name, string Path, IReadOnlyList<string> Ids, IReadOnlyList<string> Names, string? GameVersion);

/// <summary>
/// 目录里的一个 <c>.rml</c>,读通了与没读通都算数。
///
/// 没读通的也进枚举结果,是因为吞掉它之后「你没存过这个列表」与「存了、文件坏了」
/// 在输出上一个字都不差 —— 而这两件事读者的下一步完全不同(重存一遍 / 去改那个文件)。
/// </summary>
/// <param name="List">读通了才有;为 null 时 <paramref name="Problem"/> 是 <c>Read</c> 的原话。</param>
public sealed record ModListEntry(string Name, string Path, ModListFile? List, string? Problem);

public static class ModListIo
{
    public const string Extension = ".rml";

    public static string DefaultDirectory()
        => System.IO.Path.GetFullPath(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "..", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "ModLists"));

    public static IReadOnlyList<string> Directories(RimConfig config)
    {
        var dirs = new List<string> { DefaultDirectory() };
        var local = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(config.Path) is { Length: > 0 } d ? d : ".", "modlists");
        dirs.Add(local);
        return dirs;
    }

    public static IReadOnlyList<ModListEntry> Enumerate(RimConfig config)
    {
        var result = new List<ModListEntry>();
        foreach (var dir in Directories(config))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*" + Extension))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                // 坏文件不让整个列表失败,但也不消失 —— 缘由留在条目里,由调用方说破。
                try { result.Add(new ModListEntry(name, file, Read(file), null)); }
                catch (CliUsageException ex) { result.Add(new ModListEntry(name, file, null, ex.Message)); }
            }
        }
        return result;
    }

    public static ModListFile Resolve(RimConfig config, string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return Read(nameOrPath);
        foreach (var dir in Directories(config))
        {
            var candidate = System.IO.Path.Combine(dir, nameOrPath + Extension);
            if (File.Exists(candidate)) return Read(candidate);
        }
        // 读不通的也进 Known lists:它的名字**是**一条能走的路 —— 上面那圈 File.Exists
        // 会命中它并让 Read 抛出具体哪里坏了。把它从这里摘掉,才是把人引向死路。
        var known = Enumerate(config).Select(m => m.Name).ToList();
        throw new CliUsageException(
            $"No mod list named '{nameOrPath}'." +
            (known.Count > 0
                ? $" Known lists: {string.Join(", ", known)}."
                : $" Save one from the game's mod screen, or write a {Extension} file by hand — " +
                  "a list of packageId entries is enough."));
    }

    /// <summary>宽读:只要文档里有一串 li 的 ids(或 meta 的 modIds),就认。</summary>
    public static ModListFile Read(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception ex)
        {
            throw new CliUsageException($"'{System.IO.Path.GetFileName(path)}' is not readable XML: {ex.Message}");
        }

        var root = doc.Root ?? throw new CliUsageException($"'{System.IO.Path.GetFileName(path)}' is empty.");

        var ids = Items(root, "ids") ?? Items(root, "modIds");
        if (ids is null || ids.Count == 0)
            throw new CliUsageException(
                $"'{System.IO.Path.GetFileName(path)}' has no <ids> list, which is the only part that decides " +
                "which mods load. The minimum a hand-written file needs is:\n" +
                "  <savedModList><modList><ids><li>ludeon.rimworld</li></ids></modList></savedModList>");

        var names = Items(root, "names") ?? Items(root, "modNames") ?? [];
        var version = root.Descendants("gameVersion").FirstOrDefault()?.Value.Trim();

        return new ModListFile(System.IO.Path.GetFileNameWithoutExtension(path), path, ids, names, version);
    }

    private static List<string>? Items(XElement root, string name)
    {
        var el = root.Name.LocalName == name ? root : root.Descendants(name).FirstOrDefault();
        if (el is null) return null;
        var items = el.Elements("li").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList();
        return items.Count == 0 ? null : items;
    }

    /// <summary>严写:补全 names 与 meta 头,产物游戏自己认得。</summary>
    public static void Write(string path, IReadOnlyList<string> ids, IReadOnlyList<string> names, string? gameVersion)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("savedModList",
                new XElement("meta",
                    new XElement("gameVersion", gameVersion ?? "unknown"),
                    new XElement("modIds", ids.Select(i => new XElement("li", i))),
                    new XElement("modNames", names.Select(n => new XElement("li", n)))),
                new XElement("modList",
                    new XElement("ids", ids.Select(i => new XElement("li", i))),
                    new XElement("names", names.Select(n => new XElement("li", n))))));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        doc.Save(path);
    }
}

public sealed class ModListListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "modlist list",
        Summary = "List the mod lists available on this machine.",
        Remarks = "Mod lists are the game's own .rml files. Anything that produces one — the game's mod screen, " +
                  "'modlist save', or a text editor — is equally valid input.",
        Options = [],
        Examples = ["rimsearcher modlist list"],
        JsonKeys = [new() { Key = "modlists", Rows = true, What = "one row per saved mod list: name, mods, game_version, path. " +
                                                                 "'mods' is text, not a number — a file that does not parse still gets its row, with 'unreadable' there." }],
    };

    public override int Run(CommandContext ctx)
    {
        var lists = ModListIo.Enumerate(ctx.Config);
        if (lists.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                "No mod lists found. Save one from the game's mod screen, or write a .rml file by hand: " +
                "a <savedModList><modList><ids> block listing packageId entries in load order is enough.");
            return 1;
        }

        // 说破在表**前**:读者碰到那格 'unreadable' 之前就得知道它是个什么东西。
        var bad = lists.Count(e => e.List is null);
        if (bad > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(bad).Render("mod list")} below could not be read; the mods column says " +
                "'unreadable' there. Each such file exists and merely fails to parse — that is not a list with " +
                $"no mods in it. '{CommandRegistry.ExeName} modlist show <name>' says what is wrong with one.");

        ctx.Report.Table("modlists", ["name", "mods", "game_version", "path"],
            lists.Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                // 整列是文本,不是数字 —— 读不出的那格要能说出「读不出」,而这一列多一个
                // 「0」的读法就是把坏文件报成空列表。快照那张表同理,两处口径一致。
                ["mods"] = m.List is { } l ? l.Ids.Count.ToString() : "unreadable",
                ["game_version"] = m.List?.GameVersion,
                ["path"] = m.Path,
            }).ToList());
        return 0;
    }
}

public sealed class ModListShowCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "modlist show",
        Summary = "Show the mods in one list, in load order.",
        Positionals = [new PositionalSpec { Name = "name", Required = false, Help = "A name from 'modlist list', or a path to a .rml file. Omit it with --find to search every list." }],
        Options =
        [
            new OptionSpec
            {
                Name = "find",
                Aliases = ["filter", "grep", "search", "match"],
                Placeholder = "<text>",
                Help = "Only rows whose id or name contains this. Without a list name, every list is searched.",
            },
        ],
        Examples =
        [
            "rimsearcher modlist show vanilla",
            "rimsearcher modlist show --find milira",
        ],
        // 「装没装」不是一列,是表旁边的一句话 —— 声明里写成列名的话,按列去读的人
        // 会拿到 null 并把它读成「没装」。
        JsonKeys = [new() { Key = "mods", Rows = true, What = "one row per mod in the list, in load order: order, package_id, name. Whether they are installed here is a note beside the table, not a column." }],
    };

    public override int Run(CommandContext ctx)
    {
        var filter = ctx.Args.Value("find");
        var which = ctx.Args.Positional(0);

        if (which is null)
        {
            if (filter is null)
                throw new CliUsageException(
                    "'modlist show' needs a list name, or --find <text> to search every list.");
            return SearchAll(ctx, filter);
        }

        var list = ModListIo.Resolve(ctx.Config, which);
        var rows = list.Ids.Select((id, i) => (Order: i, Id: id, Name: i < list.Names.Count ? list.Names[i] : null))
                           .Where(r => filter is null || Matches(r.Id, r.Name, filter))
                           .ToList();

        ctx.Report.CountNotice(
            filter is null ? Tally.Complete(rows.Count) : Tally.Of(rows.Count, list.Ids.Count),
            "mod", "drop --find to see the whole list.");

        ctx.Report.Table("mods", ["order", "package_id", "name"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["order"] = r.Order,
                ["package_id"] = r.Id,
                ["name"] = r.Name,
            }).ToList());

        if (rows.Count == 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No mod in '{which}' matches '{filter}'. Drop the list name to search every list at once.");
        else if (list.Names.Count == 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "This list carries no display names, which is normal for a hand-written file. " +
                "Load order comes from the ids alone, so nothing is missing.");
        return rows.Count == 0 ? 1 : 0;
    }

    private static bool Matches(string id, string? name, string filter)
        => id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
           (name is not null && name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static int SearchAll(CommandContext ctx, string filter)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var searched = 0;
        var skipped = new List<string>();

        foreach (var entry in ModListIo.Enumerate(ctx.Config))
        {
            if (entry.List is not { } list) { skipped.Add(entry.Name); continue; }
            searched++;
            for (var i = 0; i < list.Ids.Count; i++)
            {
                var name = i < list.Names.Count ? list.Names[i] : null;
                if (!Matches(list.Ids[i], name, filter)) continue;
                rows.Add(new Dictionary<string, object?>
                {
                    ["modlist"] = entry.Name,
                    ["order"] = i,
                    ["package_id"] = list.Ids[i],
                    ["name"] = name,
                });
            }
        }

        // 「一份都没点它的名」是个完整性断言,而它只覆盖打得开的那些。跳过了几份就得
        // 当场说破,否则那句话把一批没看过的文件算进了「看过了」。
        void NoteSkipped()
        {
            if (skipped.Count == 0) return;
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(skipped.Count).Render("mod list")} could not be read and so was not searched " +
                $"({NameList.Render(skipped, 5)}). A match could be sitting in there.");
        }

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No mod matching '{filter}' appears in any of the {searched} lists on this machine. " +
                "That says nothing about whether it is installed — only that no saved list names it.");
            NoteSkipped();
            return 1;
        }

        // hint 不说 "all":打不开的那几份也在这台机器上,而这里数的是打得开的。
        // (它只在截断态才印,而这个 Tally 恒完整 —— 但一句不印的话说错了,照样是错的。)
        ctx.Report.CountNotice(Tally.Complete(rows.Count), "mod",
            $"searched {Tally.Complete(searched).Render("mod list")}.");
        NoteSkipped();
        ctx.Report.Table("mods", ["modlist", "order", "package_id", "name"], rows);
        return 0;
    }
}

public sealed class ModListSaveCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "modlist save",
        Summary = "Capture the mods currently enabled in the game as a named list.",
        Remarks = "Also works as a tidy-up pass on a hand-written file: read it back with --from and it is " +
                  "rewritten with display names and a meta header, which is what the game's own load dialog expects.",
        Positionals = [new PositionalSpec { Name = "name", Help = "Name for the new list." }],
        Options =
        [
            new OptionSpec
            {
                Name = "from",
                Aliases = ["source", "input"],
                Placeholder = "<name|path>",
                Help = "Read the ids from this list instead of from the game's current configuration.",
            },
        ],
        Examples = ["rimsearcher modlist save current", "rimsearcher modlist save tidy --from scratch.rml"],
        JsonKeys = [new() { Key = "saved", What = "an object: the list that was written, and where." }],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;
        IReadOnlyList<string> ids;
        string? gameVersion;

        if (ctx.Args.Value("from") is { Length: > 0 } from)
        {
            var src = ModListIo.Resolve(ctx.Config, from);
            ids = src.Ids;
            gameVersion = src.GameVersion;
        }
        else
        {
            var env = Snapshot.SnapshotCatalog.ReadActiveMods(ctx.Config);
            if (env.ActiveMods.Count == 0)
                throw new CliUsageException(
                    "The game's ModsConfig.xml could not be read, so there is nothing to capture. " +
                    "Pass --from to build the list out of another file instead.");
            ids = env.ActiveMods;
            gameVersion = env.GameVersion;
        }

        var installed = InstalledMods.Scan(ctx.Config);
        var names = ids.Select(id => installed.TryGetValue(id, out var m) ? m.Name : id).ToList();

        var path = Path.Combine(ModListIo.Directories(ctx.Config)[0], name + ModListIo.Extension);
        ModListIo.Write(path, ids, names, gameVersion);

        ctx.Report.Detail("saved", [new("name", name), new("path", path), new("mods", ids.Count)]);

        var missing = ids.Where(id => !installed.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(missing.Count).Render("mod")} in the list are not installed on this machine " +
                $"({NameList.Render(missing, 5)}). " +
                "The file is written as asked; 'export' will refuse to start the game until they are present.");
        return 0;
    }
}

public sealed record InstalledMod(string PackageId, string Name, string Directory)
{
    /// <summary>
    /// About.xml 里 <c>modDependencies</c> 声明的硬依赖(含 <c>modDependenciesByVersion</c>)。
    ///
    /// 只取这一节:<c>loadAfter</c> / <c>loadBefore</c> 是排序提示,<c>incompatibleWith</c>
    /// 是反向关系,三者的元素形状与依赖一模一样,全收会把「不兼容」当成「必需」。
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

public static class InstalledMods
{
    /// <summary>扫本机三处 mod 目录,建 packageId → 目录 的表。启动前验证靠它。</summary>
    public static Dictionary<string, InstalledMod> Scan(RimConfig config)
    {
        var result = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots(config))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var about = Path.Combine(dir, "About", "About.xml");
                if (!File.Exists(about)) continue;
                try
                {
                    var doc = XDocument.Load(about);
                    var id = Find(doc, "packageId");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    result.TryAdd(id.Trim(), new InstalledMod(id.Trim(), Find(doc, "name") ?? Path.GetFileName(dir), dir)
                    {
                        Dependencies = ReadDependencies(doc),
                    });
                }
                catch { /* 坏 About.xml 跳过 */ }
            }
        }
        return result;
    }

    public static IReadOnlyList<string> Roots(RimConfig config)
    {
        if (config.ModRoots.Count > 0) return config.ModRoots;
        if (config.GameDir is { Length: > 0 } g)
            return [Path.Combine(g, "Data"), Path.Combine(g, "Mods")];
        return [];
    }

    /// <summary>
    /// <c>modDependencies</c> 与 <c>modDependenciesByVersion</c> 下的每个 <c>&lt;packageId&gt;</c>。
    /// 后者按游戏版本分组(<c>&lt;v1.6&gt;</c>),这里不挑版本全收 —— 少报一个依赖的代价是
    /// 一次几十秒的空转加载,多报一个的代价只是列表里多一个本来就装着的 mod。
    /// </summary>
    private static List<string> ReadDependencies(XDocument doc)
    {
        var ids = new List<string>();
        foreach (var section in doc.Root?.Elements() ?? [])
        {
            var n = section.Name.LocalName;
            if (!n.Equals("modDependencies", StringComparison.OrdinalIgnoreCase) &&
                !n.Equals("modDependenciesByVersion", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var el in section.Descendants())
                if (el.Name.LocalName.Equals("packageId", StringComparison.OrdinalIgnoreCase) &&
                    el.Value.Trim() is { Length: > 0 } id)
                    ids.Add(id.Trim());
        }
        return ids;
    }

    private static string? Find(XDocument doc, string element)
        => doc.Root?.Elements().FirstOrDefault(e =>
               string.Equals(e.Name.LocalName, element, StringComparison.OrdinalIgnoreCase))?.Value;
}
