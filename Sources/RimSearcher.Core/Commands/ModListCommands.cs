using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimTooling.Cli;
using RimTooling.IO;
using RimTooling.RimWorld;

namespace RimSearcher.Commands;

public sealed record ModListFile(string Name, string Path, IReadOnlyList<string> Ids, string? GameVersion);

public sealed record ModListEntry(string Name, string Path, ModListFile? List, string? Problem);

public static class ModListIo
{
    public const string Extension = RimWorldModList.Extension;

    private static readonly ModListLoadOptions LoadOptions = new()
    {
        AllowedExtensionSections = new HashSet<string>(["rimrunner"], StringComparer.Ordinal),
        ExcludedFileSuffixes = [".prefs.toml", ".newgame.toml"],
    };

    public static string? DefaultDirectory(RimConfig config)
        => config.ModsConfigPath() is { Length: > 0 } p && System.IO.Path.GetDirectoryName(p) is { Length: > 0 } dir
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "ModLists"))
            : null;

    public static string LocalDirectory(RimConfig config)
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(config.Path) is { Length: > 0 } d ? d : ".", "modlists");

    public static string LocalPath(RimConfig config, string name)
        => System.IO.Path.Combine(LocalDirectory(config), name + Extension);

    public static IReadOnlyList<string> Directories(RimConfig config)
    {
        var dirs = new List<string> { LocalDirectory(config) };
        if (DefaultDirectory(config) is { Length: > 0 } fallback) dirs.Add(fallback);
        return dirs.Distinct(PathComparison.DefaultComparer).ToList();
    }

    public static IReadOnlyList<ModListEntry> Enumerate(RimConfig config)
    {
        try
        {
            return RimWorldModList.Enumerate(Directories(config), LoadOptions)
                .Select(MapEntry)
                .ToList();
        }
        catch (ModListException ex)
        {
            throw new CliUsageException(ex.Message);
        }
    }

    public static ModListFile Resolve(RimConfig config, string nameOrPath)
    {
        try
        {
            return MapFile(RimWorldModList.Resolve(Directories(config), nameOrPath, LoadOptions));
        }
        catch (ModListException ex)
        {
            throw new CliUsageException(Describe(config, ex, nameOrPath));
        }
    }

    private static string Describe(RimConfig config, ModListException ex, string nameOrPath)
    {
        static string Known(IReadOnlyList<string> names) =>
            names.Count > 0 ? $"已知名单：{string.Join("、", names)}。" : "";

        switch (ex.Kind)
        {
            case ModListErrorKind.Syntax when ex.InnerException is TomlException toml:
                return $"{System.IO.Path.GetFileName(ex.FilePath ?? nameOrPath)} 不是合法的 TOML" +
                       (toml.Line > 0 ? $"（第 {toml.Line} 行）" : "") +
                       $"：{toml.Reason}";

            case ModListErrorKind.UnknownList:
                return $"没有名为 '{nameOrPath}' 的 mod 名单。" +
                       (ex.AvailableNames.Count > 0
                           ? Known(ex.AvailableNames)
                           : $"'{CommandRegistry.ExeName} modlist save <名称>' 可从当前 ModsConfig 保存一份；" +
                             $"也可手写一个 {Extension} 文件，写 mods = [\"ludeon.rimworld\"] 即可。");

            case ModListErrorKind.FileMissing:
                IReadOnlyList<string> known;
                try { known = Enumerate(config).Select(m => m.Name).ToList(); }
                catch (CliUsageException) { known = []; }
                return $"未找到 mod 名单文件：{ex.FilePath}。" + Known(known);

            case ModListErrorKind.ParentMissing:
                return $"mod 名单 {ex.ReferencedBy ?? nameOrPath} 经 mods_from 引用的上级 mod 名单 {ex.ModListName} 不存在" +
                       $"（第 {ex.Line} 行；预期位置 {ex.FilePath}）。";

            default:
                return ex.Message;
        }
    }

    public static void Write(string path, IReadOnlyList<string> ids, string? gameVersion)
    {
        try
        {
            var version = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion;
            RimWorldModList.Write(path, ids, modsFrom: null, gameVersion: version);
        }
        catch (ModListException ex)
        {
            throw new CliUsageException(ex.Message);
        }
    }

    private static ModListFile MapFile(RimWorldModList list)
        => new(list.Name, list.Path, list.Mods, list.GameVersion);

    private static ModListEntry MapEntry(RimTooling.RimWorld.ModListEntry entry)
        => new(entry.Name, entry.FilePath, entry.ModList is { } m ? MapFile(m) : null, entry.Error?.Message);
}

public sealed class ModListListCommand : Command
{
    public override CliOperation Operation { get; } = new()
    {
        Name = "modlist list",
        Verb = "list",
        Summary = "列出本机可用的 mod 名单。",
        Remarks = "mod 名单是 .toml 文件（mods = [...]，mods_from 与 game_version 可选）。",
        Options = [],
        Examples = ["rimsearcher modlist list"],
        JsonKeys = [new("modlists",
            "每份已保存的 mod 名单一行：name、mods、game_version、path。" +
            "mods 为文本，无法解析的文件该格为「无法读取」。",
            Rows: true)],
    };

    public override int Run(CommandContext ctx)
    {
        var lists = ModListIo.Enumerate(ctx.Config);
        if (lists.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                "未找到 mod 名单。'rimsearcher modlist save <名称>' 可从当前 ModsConfig 保存一份；" +
                "也可手写一个 .toml 文件，写 mods = [\"ludeon.rimworld\"] 即可。");
            return 1;
        }

        var bad = lists.Count(e => e.List is null);
        if (bad > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("下表中有", Tally.Complete(bad).Render("mod list"), "（mod 数为「无法读取」）无法解析：") +
                $"具体问题见 '{CommandRegistry.ExeName} modlist show <名称>'。");

        ctx.Report.Table("modlists", ["name", "mods", "game_version", "path"],
            lists.Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["mods"] = m.List is { } l ? l.Ids.Count.ToString() : "无法读取",
                ["game_version"] = m.List?.GameVersion,
                ["path"] = m.Path,
            }).ToList());
        return 0;
    }
}

public sealed class ModListShowCommand : Command
{
    private static readonly CliOption Find = CliOption.Text("find") with
    {
        Aliases = ["filter", "grep", "search", "match"],
        Placeholder = "<文本>",
        Help = "只列 packageId（或已安装 mod 的显示名，能查到时）含这段文本的行。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "modlist show",
        Verb = "show",
        Summary = "按加载顺序列出一份名单里的 mod。",
        Positionals = [new CliPositional { Name = "name", Required = false, Help = "'modlist list' 列出的名称，或一个 .toml 文件的路径。配合 --find 时可省略，以搜索全部名单。" }],
        Options =
        [
            Find,
        ],
        Examples =
        [
            "rimsearcher modlist show vanilla",
            "rimsearcher modlist show --find milira",
        ],
        JsonKeys = [new("mods",
            "名单里每个 mod 一行，按加载顺序：modlist、order、package_id、name。" +
            "name 按本机已安装的 mod 查得，该 mod 未在本机安装时为 null（文本输出中留空）。" +
            "指定名单与搜索全部名单两种用法，每一行都带 modlist；" +
            "文本表只在搜索全部名单时输出该列。",
            Rows: true)],
    };

    public override int Run(CommandContext ctx)
    {
        var filter = ctx.Args.Value(Find);
        var which = ctx.Args.Positional(0);

        if (which is null)
        {
            if (filter is null)
                throw new CliUsageException(
                    "缺少名单名称：'modlist show' 须指定一份名单，或用 --find <文本> 搜索全部名单。");
            return SearchAll(ctx, filter);
        }

        var list = ModListIo.Resolve(ctx.Config, which);
        var installed = InstalledMods.Scan(ctx.Config);
        var rows = list.Ids
            .Select((id, i) => (Order: i, Id: id, Name: installed.TryGetValue(id, out var mod) ? mod.Name : null))
            .Where(r => filter is null || Matches(r.Id, r.Name, filter))
            .ToList();

        ctx.Report.CountNotice(
            filter is null ? Tally.Complete(rows.Count) : Tally.Of(rows.Count, list.Ids.Count),
            "mod");

        ctx.Report.Table("mods", ["order", "package_id", "name"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["modlist"] = list.Name,
                ["order"] = r.Order,
                ["package_id"] = r.Id,
                ["name"] = r.Name,
            }).ToList());

        if (rows.Count == 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                Cjk.Join(which, "中没有 mod 匹配", $"'{filter}'") + "。去掉名单名称即可一次搜索全部名单。");
        return rows.Count == 0 ? 1 : 0;
    }

    private static bool Matches(string id, string? name, string filter)
        => id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
           (name is not null && name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static int SearchAll(CommandContext ctx, string filter)
    {
        var installed = InstalledMods.Scan(ctx.Config);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var searched = 0;
        var skipped = new List<string>();

        foreach (var entry in ModListIo.Enumerate(ctx.Config))
        {
            if (entry.List is not { } list) { skipped.Add(entry.Name); continue; }
            searched++;
            for (var i = 0; i < list.Ids.Count; i++)
            {
                var id = list.Ids[i];
                var name = installed.TryGetValue(id, out var mod) ? mod.Name : null;
                if (!Matches(id, name, filter)) continue;
                rows.Add(new Dictionary<string, object?>
                {
                    ["modlist"] = entry.Name,
                    ["order"] = i,
                    ["package_id"] = id,
                    ["name"] = name,
                });
            }
        }

        void NoteSkipped()
        {
            if (skipped.Count == 0) return;
            ctx.Report.Notice(NoticeKind.Boundary,
                Tally.Complete(skipped.Count).Render("mod list") +
                $"（{NameList.Render(skipped, 5)}）无法读取，因此未搜索。");
        }

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                Cjk.Join("本机的", Tally.Complete(searched).Render("saved mod list"), "中都没有匹配", $"'{filter}'", "的 mod") + "。");
            NoteSkipped();
            return 1;
        }

        ctx.Report.Notice(NoticeKind.Count,
            Cjk.Join("已搜索", Tally.Complete(searched).Render("mod list"), "，其中有", Tally.Complete(rows.Count).Render("mod"), "匹配") + "。");
        NoteSkipped();
        ctx.Report.Table("mods", ["modlist", "order", "package_id", "name"], rows);
        return 0;
    }
}

public sealed class ModListSaveCommand : Command
{
    private static readonly CliOption From = CliOption.Text("from") with
    {
        Aliases = ["input"],
        Placeholder = "<名称|路径>",
        Help = "从这份名单读取 packageId，而不读取游戏当前的配置。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "modlist save",
        Verb = "save",
        Summary = "把游戏当前启用的 mod 保存为一份命名的 mod 名单。",
        Remarks = "在配置文件旁写入一份 .toml mod 名单。配合 --from 时也可用于整理另一份名单：" +
                  "展开其中的 mods_from 继承，并规范化输出。",
        Positionals = [new CliPositional { Name = "name", Help = "新名单的名称。" }],
        Options =
        [
            From,
        ],
        Examples = ["rimsearcher modlist save mylist", "rimsearcher modlist save tidy --from scratch.toml"],
        JsonKeys = [new("saved",
            "一个对象：所写入的名单及其位置。")],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;
        IReadOnlyList<string> ids;
        string? gameVersion;

        if (ctx.Args.Value(From) is { Length: > 0 } from)
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
                    "无法读取游戏的 ModsConfig.xml，因此没有可保存的内容。改用 --from 从另一个文件生成名单。");
            ids = env.ActiveMods;
            gameVersion = env.GameVersion;
        }

        var path = ModListIo.LocalPath(ctx.Config, name);
        ModListIo.Write(path, ids, gameVersion);

        ctx.Report.Detail("saved", [new("name", name), new("path", path), new("mods", ids.Count)]);

        var installed = InstalledMods.Scan(ctx.Config);
        var missing = ids.Where(id => !installed.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("名单中有", Tally.Complete(missing.Count).Render("mod"), "未在本机安装") +
                $"（{NameList.Render(missing, 5)}）；这些 mod 安装之前，'export' 拒绝启动游戏。");
        return 0;
    }
}

public sealed record InstalledMod(string PackageId, string Name, string Directory)
{
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

public static class InstalledMods
{
    public static Dictionary<string, InstalledMod> Scan(RimConfig config)
    {
        var result = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots(config))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            IReadOnlyList<ModFolderEntry> entries;
            try { entries = ModDirectoryScanner.Scan(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (entry.About?.PackageId is not { Length: > 0 } id) continue;
                var name = entry.About.Name ?? Path.GetFileName(entry.Path);
                result.TryAdd(id, new InstalledMod(id, name, entry.Path)
                {
                    Dependencies = entry.About.Dependencies,
                });
            }
        }
        return result;
    }

    public static IReadOnlyList<string> Roots(RimConfig config)
    {
        if (config.ModRoots.Count > 0)
            return config.ModRoots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (config.GameDir is { Length: > 0 } g)
            return [Path.Combine(g, "Data"), Path.Combine(g, "Mods")];
        return [];
    }
}
