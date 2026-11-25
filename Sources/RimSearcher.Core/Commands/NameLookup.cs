using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

internal static class NameLookup
{
    internal enum Where
    {
        Def,
        DefOutsideScope,
        XmlNode,
        UnloadedTranslation,
        DefType,
        Class,
        DeclaredPath,
        CodeMember,
        Keyed,
        ModInSnapshot,
        ModNotInSnapshot,
        FieldValue,
        OtherSnapshot,
    }

    internal sealed record Sighting(Where Where, string Name, string Is, string In, string Next, bool Redirects = true);

    public const string Table = "found_as";
    public static readonly string[] Columns = ["name", "is", "in", "next"];
    public const string Caption = "名字另有所指：";

    public static readonly CliJsonKey JsonKey = new(Table,
        "所查名字在本快照里属于本命令所查范围之外时，每个名字一行：name、is（def、mod 范围外的 def、XML 节点、" +
        "Abstract 节点、无对应 def 的译文、def 类型、Class、已声明的字段路径、C# 字段、C# 属性、C# 方法、界面文本、" +
        "本快照的 mod、本快照外的 mod、字段值、其他快照中的 def、其他快照中的 XML 节点）、in（确切位置）、" +
        "next（能查到该名字的命令，可直接粘贴）。本命令找到该名字，或哪里都没有时，这张表为空。",
        Rows: true);

    public static readonly CliJsonKey CodeJsonKey = new(Table,
        "所查名字是本命令所查之外的东西时，每个名字一行：name、is（C# 字段、C# 类型、def、C# 属性、C# 方法、" +
        "C# 常量、C# 枚举成员、C# 命名空间）、in（声明该名字的类型或程序集，从源码读出时附 file:line 与声明；" +
        "或 def 类型与 mod）、next（能查到该名字的命令，可直接粘贴）。本命令找到该名字，或哪里都没有时，这张表为空。",
        Rows: true);

    public static void Say(CommandContext ctx, params IEnumerable<Sighting> sightings)
    {
        var snap = ctx.Args.Value(GlobalOptions.Snapshot);
        var picked = sightings.OrderBy(s => s.Where)
                              .DistinctBy(s => (s.Where, s.Name.ToUpperInvariant(), s.In))
                              .ToList();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var s in picked)
        {
            var row = new Dictionary<string, object?>
            {
                ["name"] = s.Name, ["is"] = s.Is, ["in"] = s.In,
                ["next"] = snap is { Length: > 0 } && !s.Next.Contains("--snapshot", StringComparison.Ordinal)
                    ? $"{s.Next} --snapshot {CommandContext.QuoteArg(snap)}"
                    : s.Next,
            };
            if (!s.Redirects) ctx.Report.StaysEmpty.Add(row);
            rows.Add(row);
        }
        if (rows.Count == 0) return;
        ctx.Report.AppendRows(Table, Columns, rows, Caption);
    }

    public static Sighting? AsClass(CommandContext ctx, string name, ScopeFilter scope)
    {
        var holders = ctx.Db.TypesHoldingClass(name, scope);
        if (holders.Count == 0) return null;
        var total = holders.Sum(h => h.Count);
        var where = string.Join("、", holders.Take(3).Select(h => h.DefType));
        return new Sighting(Where.Class, name, "Class",
                            Cjk.Join(Output.Tally.Complete(total).Render("def"), "归在", where,
                                     holders.Count > 3
                                         ? Cjk.Join("及另外", Output.Tally.Complete(holders.Count - 3).Render("def type"))
                                         : "",
                                     "下"),
                            CommandRegistry.ExeName + " list " + holders[0].DefType + " --class " + name);
    }

    public static Sighting UnloadedTranslation(string name, IReadOnlyList<TranslationRow> rows, string next)
    {
        var types = rows.Select(r => r.DefType).Where(t => t is { Length: > 0 }).Select(t => t!)
                        .Distinct(StringComparer.Ordinal).ToList();
        var files = rows.Select(r => r.SourceFile).Where(f => f is { Length: > 0 })
                        .Select(f => Path.GetFileName(f!)).Distinct(StringComparer.Ordinal).ToList();
        var keys = rows.Select(r => r.Path).Distinct(StringComparer.Ordinal).Count();
        return new Sighting(Where.UnloadedTranslation, name, "无对应 def 的译文",
                            (types.Count > 0 ? NameList.Render(types, 3) + "，" : "") +
                            Tally.Complete(keys).Render("DefInjected key") +
                            (files.Count > 0 ? "，见于 " + NameList.Render(files, 3) : ""),
                            next);
    }

    public static Sighting AsDef(string name, IReadOnlyList<DefRow> defs)
        => new(Where.Def, name, "def",
               string.Join("、", defs.Select(d => $"{d.DefType}（{d.SourceMod}）").Distinct(StringComparer.Ordinal)),
               $"{CommandRegistry.ExeName} get {name}");

    public static Sighting? Locate(CommandContext ctx, string name, ScopeFilter? scope = null)
    {
        if (name.Length == 0) return null;
        var exe = CommandRegistry.ExeName;

        var defs = ctx.Db.GetDefsNamed(name);
        if (defs.Count > 0 && scope is { IsAll: false })
        {
            var visible = defs.Where(d => scope.Includes(d.SourceMod)).ToList();
            if (visible.Count == 0)
            {
                var mods = defs.Select(d => d.SourceMod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new Sighting(Where.DefOutsideScope, name, "mod 范围外的 def",
                                    string.Join("、", mods), ctx.Without(ctx.ScopeOption));
            }
        }

        var node = ctx.Db.NodesNamed(name).FirstOrDefault();
        if (node is not null)
            return new Sighting(Where.XmlNode, name, node.Abstract ? "Abstract 节点" : "XML 节点",
                                $"{node.SourceMod}（{node.SourceFile}）", $"{exe} inherit {name}");

        if (defs.Count == 0 && ctx.Db.Translations(name) is { Count: > 0 } orphan)
            return UnloadedTranslation(name, orphan, $"{exe} get {name}");

        var unscoped = ctx.Unscoped();
        var type = ctx.Db.Types(unscoped)
                         .FirstOrDefault(t => string.Equals(t.Type, name, StringComparison.OrdinalIgnoreCase));
        if (type.Type is not null)
            return new Sighting(Where.DefType, name, "def 类型",
                                Output.Tally.Complete(type.Count).Render("def"), exe + " list " + type.Type);

        if (AsClass(ctx, name, unscoped) is { } asClass) return asClass;

        if (ctx.Db.KeyedCount() > 0)
        {
            const int probe = 25;
            var (keyedRows, keyedTotal, _, _) = ctx.Db.KeyedSearch(name, probe);
            if (keyedRows.Count > 0)
            {
                var distinct = keyedRows.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
                var oneKeyForSure = distinct.Count == 1 && keyedTotal <= probe;
                return new Sighting(Where.Keyed, name, "界面文本",
                                    $"{Output.Tally.Complete(keyedTotal).Render("keyed translation")}" +
                                    (oneKeyForSure ? $"，键 {distinct[0]}" : "，不止一个键"),
                                    $"{exe} keyed {QuoteArg(name)}");
            }
        }

        var installed = new Lazy<Dictionary<string, InstalledMod>?>(() => TryScanInstalled(ctx.Config));
        if (Mod(ctx, name, installed, fuzzy: false) is { } named) return named;

        var (holdingPaths, holdingTotal, _, _) = ctx.Db.PathsWithValue(name, unscoped, 3, ValueMatch.Identifier);
        if (holdingPaths.Count > 0)
        {
            var best = holdingPaths[0];
            var leaves = holdingPaths.Select(h => NoiseFilter.Leaf(h.Path)).Distinct(StringComparer.Ordinal).ToList();
            var next = EntryKeys.AsEntryKey(ctx, name, unscoped) is { } keyed
                ? $"{exe} where {EntryKeys.Quote(keyed.Path)}"
                : holdingTotal <= holdingPaths.Count && leaves.Count == 1
                    ? $"{exe} where {leaves[0]} {QuoteArg(name)}"
                    : $"{exe} where --value {QuoteArg(name)}";
            return new Sighting(Where.FieldValue, name, "字段值",
                                $"{best.Path} = {best.Sample}" +
                                (holdingTotal > 1 ? Cjk.Join("，共", Tally.Complete(holdingTotal).Render("path and def-type pair")) : ""),
                                next);
        }

        if (Mod(ctx, name, installed, fuzzy: true) is { } nicknamed) return nicknamed;

        return InOtherSnapshot(ctx, name);
    }

    internal enum Side { Data, Code }

    public static List<Sighting> Beyond(CommandContext ctx, string asked, Side side, string? onType = null,
                                        bool sameKind = false, Func<SourcePack.MemberHit, bool>? skip = null)
    {
        if (side == Side.Code && !asked.All(c => char.IsLetterOrDigit(c) || c == '_')) return [];
        var leaf = NoiseFilter.Leaf(asked);
        var dot = leaf.LastIndexOf('.');
        if (dot >= 0) leaf = leaf[(dot + 1)..];
        if (leaf.Length == 0 || !leaf.All(c => char.IsLetterOrDigit(c) || c == '_')) return [];
        var found = new List<Sighting>();
        if (side == Side.Data) found.AddRange(Declared(ctx, asked, leaf, onType));
        found.AddRange(InCode(ctx, asked, leaf, side, redirects: side == Side.Code && !sameKind, skip));
        return found;
    }

    private static IEnumerable<Sighting> Declared(CommandContext ctx, string asked, string leaf, string? onType)
    {
        var exe = CommandRegistry.ExeName;
        var unscoped = ctx.Unscoped();
        var shapes = ctx.Db.DeclaredPathsEndingIn(leaf)
            .Select(d => (Shape: PathSegments.Shape(d.Path), d.Types))
            .Select(d => (d.Shape, Types: onType is null ? d.Types : [.. d.Types.Where(t => t == onType)]))
            .Where(d => d.Types.Count > 0)
            .GroupBy(d => d.Shape, StringComparer.Ordinal)
            .Select(g => (Shape: g.Key, Types: g.SelectMany(x => x.Types).Distinct(StringComparer.Ordinal)
                                                 .Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(d => PathSegments.IsWholeSegment(d.Shape, asked))
            .ThenBy(d => d.Shape.Length).ThenBy(d => d.Shape, StringComparer.Ordinal)
            .ToList();
        var shown = shapes.Take(Limits.MaxSuggestions).ToList();
        foreach (var (shape, types) in shown)
        {
            var held = ctx.Db.FieldPathExists(new PathQuery(shape, Exact: true), unscoped);
            var more = shape == shown[^1].Shape && shapes.Count > shown.Count
                ? Cjk.Join("，另有", Tally.Complete(shapes.Count - shown.Count).Render("path shape")) : "";
            yield return new Sighting(Where.DeclaredPath, asked, "已声明的字段路径",
                Cjk.Join(NameList.Render(types, 3), "上的", shape) + (held ? "" : "，没有 def 在此有值") + more,
                held ? exe + " where " + shape : exe + " fields " + types[0] + " --path-contains " + leaf,
                Redirects: held);
        }
    }

    private static IEnumerable<Sighting> InCode(CommandContext ctx, string asked, string name, Side side, bool redirects,
                                                Func<SourcePack.MemberHit, bool>? skip)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        root = Path.GetFullPath(root);
        List<SourcePack.MemberHit> hits;
        try
        {
            List<SourcePack.MemberHit> In(int tier)
                => [.. SourcesShared.TreesOfTier(root, tier)
                                    .OrderBy(n => n is SourcePlanner.VanillaTree ? 0 : 1)
                                    .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                                    .SelectMany(t => SourcePack.FindMembers(root, t, name) ?? [])
                                    .Where(h => side == Side.Code || h.Member.Kind is "field" or "property")
                                    .Where(h => skip?.Invoke(h) != true)];
            hits = [];
            for (var tier = 0; hits.Count == 0 && tier < (side == Side.Code ? SourcePlanner.NameTiers : SourcePlanner.NameTiers - 1); tier++)
                hits = In(tier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        if (hits.Count == 0) return [];

        var load = hits.Any(h => !SourcePlanner.IsReserved(h.Tree)) ? LoadedCode.Open(ctx, root) : null;
        var include = LoadedCode.Include(ctx);
        bool Unloaded(SourcePack.MemberHit h)
            => load is not null && LoadedCode.Unloaded(load.Of(h.Tree, h.File.Replace('\\', '/')).State);
        hits = [.. hits.OrderBy(Unloaded)];

        var exe = CommandRegistry.ExeName;
        var rows = hits.Take(Limits.MaxSuggestions)
            .Select(h => CodeRow(asked, h, Unloaded(h) ? load!.Snapshot : null, include, "", redirects))
            .ToList();
        if (hits.Count > rows.Count)
            rows.Add(new Sighting(Where.CodeMember, asked, "C# 成员",
                                  $"反编译源码里另有 {Tally.Complete(hits.Count - rows.Count).Render("declaration")}",
                                  exe + " members --name " + name, Redirects: redirects));
        return rows;
    }

    internal static Sighting CodeRowFor(CommandContext ctx, string asked, SourcePack.MemberHit hit, string note)
    {
        var root = SourcesShared.Root(ctx);
        var load = SourcePlanner.IsReserved(hit.Tree) ? null : LoadedCode.Open(ctx, root);
        var off = load is not null && LoadedCode.Unloaded(load.Of(hit.Tree, hit.File.Replace('\\', '/')).State);
        return CodeRow(asked, hit, off ? load!.Snapshot : null, LoadedCode.Include(ctx), note, redirects: false);
    }

    private static Sighting CodeRow(string asked, SourcePack.MemberHit hit, string? notLoadedIn, bool include,
                                    string note, bool redirects)
        => new(Where.CodeMember, asked, "C# " + CsKind.Of(hit.Member.Kind),
               $"{hit.Member.Type}，{hit.Tree}/{hit.File}:{hit.Member.Line}：{hit.Member.Declaration}" + note +
               (notLoadedIn is not null ? $"（快照 '{notLoadedIn}' 未加载）" : ""),
               $"{CommandRegistry.ExeName} read {hit.Tree}/{hit.File} --member {hit.Member.Name}" +
               (notLoadedIn is not null && !include ? $" --{LoadedCode.IncludeOption.Name}" : ""),
               Redirects: redirects);

    private static string QuoteArg(string v)
        => v.Length > 0 && v.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'') ? v : "\"" + v.Replace("\"", "\\\"") + "\"";

    private static List<(string Alias, object What)> Fanout(CommandContext ctx, Func<SnapshotDb, object?> probe)
    {
        var here = Path.GetFullPath(ctx.Db.Path);
        var targets = SnapshotCatalog.Enumerate(ctx.Config)
            .Where(e => !RimTooling.IO.PathIdentity.Same(e.Path, here))
            .Where(e => !SnapshotRetention.IsGeneration(e.Alias))
            .ToList();

        var hits = new (string Alias, object What)?[targets.Count];
        Parallel.For(0, targets.Count, i =>
        {
            try
            {
                using var other = SnapshotDb.Open(targets[i].Path);
                if (probe(other) is { } what) hits[i] = (targets[i].Alias, what);
            }
            catch
            {
            }
        });

        return [.. hits.Where(h => h is not null).Select(h => h!.Value)];
    }

    private static Sighting? Mod(
        CommandContext ctx, string name, Lazy<Dictionary<string, InstalledMod>?> installed, bool fuzzy)
    {
        var exe = CommandRegistry.ExeName;
        var inSnapshot = ctx.Db.Mods.FirstOrDefault(m => SameMod(m.PackageId, m.Name, name, fuzzy));
        if (inSnapshot is not null)
            return new Sighting(Where.ModInSnapshot, name, "本快照的 mod", inSnapshot.PackageId,
                                exe + " list --scope " + inSnapshot.PackageId);

        var offSnapshot = installed.Value?.Values.FirstOrDefault(m => SameMod(m.PackageId, m.Name, name, fuzzy));
        if (offSnapshot is null) return null;

        return new Sighting(Where.ModNotInSnapshot, name, "本快照外的 mod",
                            $"{offSnapshot.PackageId}，已安装", $"{exe} snapshot status");
    }

    public static string? Elsewhere(CommandContext ctx, Func<SnapshotDb, int> probe, string noun)
    {
        var found = Fanout(ctx, db => probe(db) is var n && n > 0 ? (object)n : null)
            .Select(f => (f.Alias, Count: (int)f.What)).ToList();

        if (found.Count == 0) return null;

        return "其他已登记的快照中有结果：" +
               string.Join("、", found.Select(f => $"'{f.Alias}' 有 {Tally.Complete(f.Count).Render(noun)}")) +
               $"。加 '--snapshot {found[0].Alias}' 可在那里查询；这项检查覆盖其他快照的全部 mod。";
    }

    private static Sighting? InOtherSnapshot(CommandContext ctx, string name)
    {
        var found = Fanout(ctx, other =>
        {
            var defs = other.GetDefsNamed(name);
            if (defs.Count > 0) return (Is: "其他快照中的 def", What: $"{defs[0].DefType}（{defs[0].SourceMod}）");
            return other.NodesNamed(name).Count > 0 ? (Is: "其他快照中的 XML 节点", What: "") : null;
        }).Select(f => (f.Alias, Hit: ((string Is, string What))f.What)).ToList();

        if (found.Count == 0) return null;

        var groups = found.GroupBy(f => f.Hit.What, StringComparer.Ordinal)
                          .Select(g => string.Join("、", g.Select(f => $"'{f.Alias}'")) + (g.Key.Length > 0 ? $"：{g.Key}" : ""));
        return new Sighting(Where.OtherSnapshot, name, found[0].Hit.Is, string.Join("；", groups),
                            $"{ctx.Without(GlobalOptions.Snapshot)} --snapshot {found[0].Alias}");
    }

    private const int NicknameMinLength = 4;

    private static string Spell(string typed, string packageId)
        => string.Equals(typed, packageId, StringComparison.OrdinalIgnoreCase) ? "" : $"（{packageId}）";

    private static bool SameMod(string packageId, string? displayName, string typed, bool fuzzy)
    {
        if (string.Equals(packageId, typed, StringComparison.OrdinalIgnoreCase)) return true;
        var tail = packageId.Contains('.') ? packageId[(packageId.LastIndexOf('.') + 1)..] : packageId;
        if (string.Equals(tail, typed, StringComparison.OrdinalIgnoreCase)) return true;
        if (displayName is { Length: > 0 } && string.Equals(displayName, typed, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!fuzzy || typed.Length < NicknameMinLength) return false;
        return tail.Contains(typed, StringComparison.OrdinalIgnoreCase) ||
               (displayName is { Length: > 0 } && displayName.Contains(typed, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, InstalledMod>? TryScanInstalled(RimConfig config)
    {
        try { return InstalledMods.Scan(config); }
        catch { return null; }
    }
}
