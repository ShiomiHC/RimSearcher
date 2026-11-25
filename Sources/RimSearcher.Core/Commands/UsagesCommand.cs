using RimTooling.Cli;
using System.Reflection.Metadata.Ecma335;
using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimSearcher.Sources;
using System.Text.RegularExpressions;

namespace RimSearcher.Commands;

public sealed class UsagesCommand : Command
{
    private static readonly CliOption Writes = CliOption.Flag("writes") with
    {
        Aliases = ["write", "writes-only", "assigned", "setters"],
        Help = "只列存入字段（写入）、取其地址（取地址）或调用属性 setter 的行。",
    };

    private static readonly CliOption Source = CliOption.Repeat("source") with
    {
        Aliases = ["tree", "from-tree", "mod", "mods"],
        Placeholder = "<源码树>",
        Help = "只计该源码树中的使用处；多棵用 --source 重复指定。使用方方法及其所用的成员仍从每棵源码树中识别。" +
               $"不指定时搜索除 {SourcePlanner.Listed(SourcePlanner.LastResortTrees, describe: true)}以外的每棵源码树。",
        Narrows = true,
    };

    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只计出自该程序集的使用处。成员本身在每棵源码树中查找。");

    private static readonly CliOption LimitOption = CommonOptions.Limit("使用处");

    public override CliOperation Operation { get; } = new()
    {
        Name = "usages",
        Aliases = ["refs", "references", "find-usages"],
        Summary = "查找读取、写入、调用或指名某个字段、属性、方法或类型的方法。",
        Remarks =
            "字段在指令加载它（读取）、存入它（写入）或取其地址（取地址：以 'ref' 传递，Scribe_Values.Look 即以此保存）处计入。" +
            "向字段所持的列表添加元素，或设置其字典的键，都会加载该字段，属于读取：--writes 不列这类行。" +
            "属性计其访问器调用；自动属性另计访问器之外对其后备字段的存入。" +
            "类型在指令指名它的位置计入（类型检查对应 'is' / 'as'，另有强制转换、typeof，类型实参对应 List<T> 或 " +
            "GetComponent<T>()，其余为类型操作数），并计入该类型自身声明的成员的使用处，编译时内联的常量与枚举值除外。" +
            "只出现在签名中的类型（字段类型、参数、返回值）不计入；" +
            "派生关系由 'rimsearcher types <type> --derived' 回答。\n\n" +
            "经由派生类型访问的成员计在声明它的类型上，因此查询继承来的字段时，答案针对其声明类型，「目标类型」列注明是哪个类型。" +
            "运行时按名称的访问（反射、Harmony patch）同样不计入。",
        Positionals =
        [
            new CliPositional
            {
                Name = "symbol",
                Variadic = true,
                Help = "字段、属性、方法或类型：'Verse.Map.components'、'Verse.Pawn.Dead'、'Verse.Pawn.Kill'、" +
                       "'Verse.MapComponent'。多个名称进入同一张表，由「目标类型」与「目标成员」列区分各行的归属。",
            },
        ],
        Options =
        [
            LoadedCode.IncludeOption,
            Writes,
            Source,
            AssemblyOption,
            LimitOption,
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher usages Verse.Map.components --writes",
            "rimsearcher usages Verse.Pawn.Dead",
            "rimsearcher usages Verse.MapComponent --source vanilla",
        ],
        JsonKeys =
        [
            new("usages",
                "每个使用方方法、目标与访问方式的组合一行：tree、from_assembly、from_type、from_member、to_type、" +
                "to_member（指名类型本身时为空）、access（调用 / 读取 / 写入 / 取地址 / 类型检查 / 强制转换 / typeof / " +
                "类型实参 / 类型操作数）、sites、loaded。loaded 说明游戏在快照中是否加载使用方方法所在的程序集：" +
                "已加载、mod 不在快照、程序集未加载（这两种只在指定 --include-unloaded 或本次调用指定该源码树或程序集时出现）或未知。",
                Rows: true),
            new("absent",
                "本次搜索未覆盖的每棵源码树一行：layer（'call_graph:' + 树名）、state 未建、next（建表的 sync 命令）；" +
                "搜索的每棵源码树都有表时为空。另外，因源码树未留存副本而改为读取已安装的程序集，也各占一行" +
                "（'assembly_copy:' + 树/程序集）。",
                Rows: true),
            NameLookup.CodeJsonKey,
        ],
    };

    private static readonly string[] Columns =
        ["tree", "from_assembly", "from_type", "from_member", "to_type", "to_member", "access", "sites"];

    private static readonly IEqualityComparer<(string, int)> Key = new KeyComparer();

    private sealed class Targets
    {
        public readonly HashSet<(string Assembly, int Token)> Calls = new(Key);
        public readonly HashSet<(string Assembly, int Token)> Accesses = new(Key);

        public readonly Dictionary<(string Assembly, int Token), HashSet<string>> Copies = new(Key);

        public readonly Dictionary<(string Assembly, int Token), HashSet<string>> Setters = new(Key);

        public readonly HashSet<(string, int, string, int)> SelfAccess = [];

        public readonly Dictionary<(string Assembly, int Token), int> Asked = new(Key);
        public int Current;

        public void Call(string tree, (string, int) t) { Calls.Add(t); In(Copies, tree, t); Asked.TryAdd(t, Current); }
        public void Access(string tree, (string, int) t) { Accesses.Add(t); In(Copies, tree, t); Asked.TryAdd(t, Current); }
        public void Setter(string tree, (string, int) t) => In(Setters, tree, t);

        public static bool Has(Dictionary<(string, int), HashSet<string>> where, string? copy, (string, int) t)
            => copy is not null && where.TryGetValue(t, out var c) && c.Contains(copy);

        private static void In(Dictionary<(string, int), HashSet<string>> where, string tree, (string, int) t)
        {
            if (!where.TryGetValue(t, out var c)) where[t] = c = new(StringComparer.OrdinalIgnoreCase);
            c.Add(tree);
        }

        public readonly List<NameLookup.Sighting> Constants = [];

        public bool OnlyMethods = true;
        public bool Any => Calls.Count > 0 || Accesses.Count > 0;
    }

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;
        if (asked.Count == 0)
            throw new CliUsageException("缺少字段、属性、方法或类型，如 'Verse.Map.components'。");

        ctx.Report.Promises("usages");

        using var lookup = CodeShared.Open(ctx, out var root, out var selection, everyTree: true);

        var targets = new Targets();
        for (var i = 0; i < asked.Count; i++)
        {
            targets.Current = i;
            Resolve(ctx, lookup, SymbolRef.Parse(asked[i]), targets);
        }

        NameLookup.Say(ctx, selection.Assemblies is null ? targets.Constants
            : [.. targets.Constants.Select(c => c with
              {
                  Next = (c.Next.IndexOf(" --scope ", StringComparison.Ordinal) is var at and >= 0 ? c.Next[..at] : c.Next) +
                         string.Concat(ctx.Args.Values(AssemblyOption).Where(v => v.Length > 0)
                                          .Select(v => v.Contains('*') ? $" --assembly \"{v}\"" : $" --assembly {v}")),
              })]);

        if (!targets.Any)
        {
            ctx.Report.Table("usages", Columns, []);
            return 1;
        }

        var writes = ctx.Args.Flag(Writes);
        if (writes && targets.OnlyMethods)
            throw new CliUsageException(
                $"--writes 只保留对字段的存入与属性 setter 的调用，而 {NameList.Render([.. asked], 4)} " +
                "只指名方法。");

        var only = selection.Trees;
        var graphs = CallGraphSet.Load(root, only.Count > 0 ? only : null, withAccesses: true);

        if (graphs.Graphs.Count == 0)
        {
            var blind = graphs.Without.Count > 0 ? graphs.Without
                      : only;
            ctx.Report.Absent(DataLayers.CallGraphRows(blind));
            ctx.Report.Table("usages", Columns, []);
            return 1;
        }

        ctx.Report.Absent(DataLayers.CallGraphRows(graphs.Without));

        var stale = graphs.Graphs.Where(g => CallersCommand.IsStale(root, g)).Select(g => g.Tree).ToList();
        if (stale.Count > 0)
            ctx.Report.Notice(NoticeKind.Staleness,
                Cjk.Join(Tally.Complete(stale.Count).Render("source tree"), "的调用图早于程序集的当前版本：") +
                $"{NameList.Render(stale, 6)}。");

        var load = LoadedCode.Open(ctx, root);
        var scope = LoadedCode.ReadScope.Of(ctx, CodeShared.Trees(ctx), selection.Exact);

        lookup.PinReferences(graphs.References);
        var trust = new GraphTrust(graphs, lookup);
        trust.Say(ctx,
                  [.. targets.Calls.Concat(targets.Accesses).Select(t => t.Assembly).Distinct(StringComparer.OrdinalIgnoreCase)],
                  [.. graphs.Graphs.Select(g => g.Tree)],
                  (tree, from) => load is null || !LoadedCode.Unloaded(load.OfAssembly(tree, from).State) || scope.Covers(tree, from));

        var rows = new Dictionary<(string, int, string, int, string, string), int>();
        void Add(string tree, string fa, int ft, string ta, int tt, string access)
        {
            if (!selection.Keeps(fa)) return;
            var k = (fa, ft, ta, tt, access, tree);
            rows[k] = rows.GetValueOrDefault(k) + 1;
        }

        foreach (var site in graphs.CallsTo(targets.Calls))
        {
            var e = site.Edge;
            var copy = lookup.CopyFor(site.Tree, e.ToAssembly)?.Tree;
            if (!Targets.Has(targets.Copies, copy, (e.ToAssembly, e.ToToken))) continue;
            if (!trust.Current(site.Tree, e.FromAssembly, e.ToAssembly)) continue;
            if (writes && !Targets.Has(targets.Setters, copy, (e.ToAssembly, e.ToToken))) continue;
            Add(site.Tree, e.FromAssembly, e.FromToken, e.ToAssembly, e.ToToken, "call");
        }

        foreach (var site in graphs.AccessesTo(targets.Accesses))
        {
            var e = site.Edge;
            if (!Targets.Has(targets.Copies, lookup.CopyFor(site.Tree, e.ToAssembly)?.Tree, (e.ToAssembly, e.ToToken)))
                continue;
            if (!trust.Current(site.Tree, e.FromAssembly, e.ToAssembly)) continue;
            if (writes && !AccessKindText.IsWrite(e.Kind)) continue;
            if (targets.SelfAccess.Contains((e.FromAssembly, e.FromToken, e.ToAssembly, e.ToToken))) continue;
            Add(site.Tree, e.FromAssembly, e.FromToken, e.ToAssembly, e.ToToken, AccessKindText.Render(e.Kind));
        }

        (CodeLoad State, string? Assembly) LoadOf((string Fa, int Ft, string Ta, int Tt, string Access, string Tree) k)
            => load?.OfAssembly(k.Tree, k.Fa) ?? (CodeLoad.Unknown, null);
        var leftOut = new List<(string Tree, CodeLoad State, string? Assembly)>();
        if (!scope.All)
            foreach (var k in rows.Keys.ToList())
                if (LoadOf(k) is var state && LoadedCode.Unloaded(state.State) && !scope.Covers(k.Item6, k.Item1))
                {
                    leftOut.Add((k.Item6, state.State, state.Assembly));
                    rows.Remove(k);
                }
        var leftOutSentence = load is null ? null : LoadedCode.LeftOut(load, leftOut, "usage");

        lookup.TreesInAnswer.UnionWith(rows.Keys.Select(k => k.Item6));
        var homeTrees = targets.Copies.Values.SelectMany(t => t).ToList();

        if (rows.Count == 0)
        {
            CodeShared.SayOwnTreeUnsearched(ctx, homeTrees, only, "使用处");
            lookup.TreesSearched.UnionWith(graphs.Graphs.Select(g => g.Tree));
            if (leftOutSentence is not null)
            {
                ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
                ctx.Report.Table("usages", Columns, []);
                return 1;
            }
            var searched = asked.Where((_, i) => targets.Asked.ContainsValue(i)).ToList();
            if (searched.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    Cjk.Join(selection.Assemblies is null
                                 ? Cjk.Join("所搜索的", Tally.Complete(graphs.Graphs.Count).Render("source tree"), "中没有")
                                 : Cjk.Join(selection.Phrase, "中没有"),
                             writes ? "写入" : "使用", NameList.Render(searched, 4), "的方法。"));
            ctx.Report.Table("usages", Columns, []);
            return 1;
        }

        var limit = ctx.Limit(LimitOption);
        var ordered = rows.OrderBy(r => targets.Asked.GetValueOrDefault((r.Key.Item3, r.Key.Item4)))
                          .ThenByDescending(r => r.Value)
                          .ThenBy(r => r.Key.Item1, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(r => r.Key.Item2)
                          .ThenBy(r => r.Key.Item4)
                          .ThenBy(r => r.Key.Item5, StringComparer.Ordinal)
                          .ThenBy(r => r.Key.Item6, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var notLoaded = ordered.Select(r => (r.Key.Item6, Load: LoadOf(r.Key)))
                               .Where(n => n.Load.State != CodeLoad.Loaded)
                               .Select(n => (n.Item1, n.Load.State, n.Load.Assembly))
                               .ToList();

        var printed = ordered.Take(limit.Effective).Select(r => Row(lookup, r.Key, r.Value, LoadOf(r.Key).State)).ToList();

        var tally = ordered.Count > limit.Effective
            ? Tally.Of(limit.Effective, ordered.Count)
            : Tally.Complete(ordered.Count);
        ctx.Report.CountNotice(tally, "usage");

        CodeShared.SayOwnTreeUnsearched(ctx, homeTrees, only, "使用处");
        if (leftOutSentence is not null) ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
        if (load is not null)
            LoadedCode.SayNotLoaded(ctx, load, notLoaded, ordered.Count, "usage", null, "这些使用处",
                                    "快照未记录其程序集");

        ctx.Report.Table("usages", LoadedCode.WithColumn(Columns, printed, load), printed);
        return 0;
    }

    private static void Resolve(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol, Targets targets)
    {
        lookup.Ask([symbol.TypeName, symbol.AsWholeType().TypeName],
                   symbol.HasMember ? null : () => lookup.FindMembersAnywhere(symbol.TypeName, 1).Count > 0);
        if (!symbol.HasMember)
        {
            var asType = lookup.FindTypes(symbol.TypeName);
            if (asType.Count > 0)
            {
                SayIfSeveral(ctx, symbol.TypeName, asType);
                foreach (var t in asType) AddType(lookup, t, targets);
                return;
            }

            var holders = lookup.FindMembersAnywhere(symbol.TypeName, Limits.MaxSuggestions * 4)
                                .Select(r => r.Type).DistinctBy(t => (t.Assembly.Name, t.FullName)).ToList();
            var hit = false;
            foreach (var t in holders) hit |= AddMember(lookup, t, symbol.TypeName, targets);
            if (hit) return;

            SayNothing(ctx, lookup, symbol);
            return;
        }

        var types = lookup.FindTypes(symbol.TypeName);
        if (types.Count > 0)
        {
            var found = false;
            foreach (var t in types)
            {
                if (AddMember(lookup, t, symbol.MemberName!, targets)) { found = true; continue; }

                using var quiet = lookup.Unnoted();
                foreach (var b in new Hierarchy(lookup).BaseChain(t.FullName))
                    if (lookup.FindTypes(b).FirstOrDefault() is { } bt && AddMember(lookup, bt, symbol.MemberName!, targets))
                    {
                        found = true;
                        break;
                    }
            }
            if (found)
            {
                SayIfSeveral(ctx, symbol.TypeName, types);
                return;
            }
        }

        var whole = lookup.FindTypes(symbol.AsWholeType().TypeName);
        if (whole.Count > 0)
        {
            SayIfSeveral(ctx, symbol.AsWholeType().TypeName, whole);
            foreach (var t in whole) AddType(lookup, t, targets);
            return;
        }

        if (types.Count > 0)
        {
            CodeShared.SayNoMember(ctx, lookup, types[0], symbol.MemberName!);
            return;
        }

        SayNothing(ctx, lookup, symbol);
    }

    private static bool AddMember(MetadataLookup lookup, TypeHit type, string member, Targets targets)
    {
        var asm = type.Assembly.Name;
        var found = false;

        var accessors = new HashSet<int>();
        foreach (var p in lookup.FindProperties(type, member))
        {
            found = true;
            targets.OnlyMethods = false;
            foreach (var a in p.Accessors)
            {
                var tok = MetadataTokens.GetToken(a);
                accessors.Add(tok);
                targets.Call(type.Assembly.Tree, (asm, tok));
            }
            var setter = p.Accessors.Select(a => MetadataTokens.GetToken(a))
                                    .Where(t => lookup.ResolveTarget(type.Assembly.Tree, asm, t)?.Member?.StartsWith("set_") == true);
            foreach (var s in setter) targets.Setter(type.Assembly.Tree, (asm, s));

            if (!p.Backing.IsNil)
            {
                var backing = MetadataTokens.GetToken(p.Backing);
                targets.Access(type.Assembly.Tree, (asm, backing));
                foreach (var a in p.Accessors) targets.SelfAccess.Add((asm, MetadataTokens.GetToken(a), asm, backing));
            }
        }

        foreach (var f in lookup.FindFields(type, member))
        {
            found = true;
            targets.OnlyMethods = false;
            if (lookup.IsConstant(type, f.Handle, out var inEnum))
            {
                targets.Constants.Add(Constant(type, f.Name, inEnum));
                continue;
            }
            targets.Access(type.Assembly.Tree, (asm, MetadataTokens.GetToken(f.Handle)));
        }

        foreach (var m in lookup.FindMethods(type, member))
        {
            var tok = MetadataTokens.GetToken(m.Handle);
            if (accessors.Contains(tok)) continue;
            found = true;
            targets.Call(type.Assembly.Tree, (asm, tok));
        }

        if (found) lookup.TreesInAnswer.Add(type.Assembly.Tree);
        return found;
    }

    private static NameLookup.Sighting Constant(TypeHit type, string name, bool inEnum)
    {
        var pattern = inEnum ? $@"\b{Regex.Escape(type.Name)}\.{name}\b" : $@"\b{name}\b";
        var tree = type.Assembly.Tree;
        return new NameLookup.Sighting(
            NameLookup.Where.CodeMember, $"{type.FullName}.{name}", inEnum ? "C# 枚举成员" : "C# 常量",
            $"{type.FullName}（{tree}/{type.Assembly.Name}）",
            $"{CommandRegistry.ExeName} code-search \"{pattern}\"" +
            (SourcePlanner.IsSearchedOnlyWhenNamed(tree)
                ? " --scope " + string.Join(",", SourcePlanner.GameDirTrees
                      .Where(t => !SourcePlanner.IsLastResort(t) || t.Equals(tree, StringComparison.OrdinalIgnoreCase))
                      .Prepend("all")) : ""));
    }

    private static void AddType(MetadataLookup lookup, TypeHit type, Targets targets)
    {
        var asm = type.Assembly.Name;
        lookup.TreesInAnswer.Add(type.Assembly.Tree);
        targets.OnlyMethods = false;
        var tree = type.Assembly.Tree;
        targets.Access(tree, (asm, MetadataTokens.GetToken(type.Handle)));
        var (methods, fields, setters) = lookup.DeclaredTokens(type);
        foreach (var m in methods) targets.Call(tree, (asm, m));
        foreach (var f in fields) targets.Access(tree, (asm, f));
        foreach (var s in setters) targets.Setter(tree, (asm, s));
    }

    private static void SayIfSeveral(CommandContext ctx, string name, IReadOnlyList<TypeHit> types)
    {
        if (types.Count <= 1) return;
        ctx.Report.Notice(NoticeKind.Filter,
            $"'{name}' 在本次读取的程序集中对应 {Tally.Complete(types.Count).Render("type")}：" +
            $"{NameList.Render([.. types.Select(t => t.FullName)], 6)}。已全部列出；" +
            "写出命名空间可选定其中一个。");
    }

    private static void SayNothing(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol)
    {
        var name = symbol.HasMember ? symbol.MemberName! : symbol.TypeName;
        IReadOnlyList<DefRow> defs = [];
        if (!symbol.HasMember)
        {
            try { defs = ctx.Db.GetDefsNamed(name); }
            catch (Exception ex) when (ex is SnapshotFormatError or CliUsageException or IOException) { }
        }

        if (defs.Count > 0)
        {
            NameLookup.Say(ctx, new NameLookup.Sighting(
                NameLookup.Where.Def, symbol.Raw, "def",
                string.Join("、", defs.Select(d => $"{d.DefType}（{d.SourceMod}）").Distinct(StringComparer.Ordinal)),
                $"{CommandRegistry.ExeName} where --value {name}"));
            return;
        }

        CodeShared.SayNoType(ctx, lookup, symbol);
    }

    private static IReadOnlyDictionary<string, object?> Row(
        MetadataLookup lookup, (string Fa, int Ft, string Ta, int Tt, string Access, string Tree) k, int count, CodeLoad load)
    {
        var from = lookup.ResolveToken(k.Tree, k.Fa, k.Ft);
        var to = lookup.ResolveTarget(k.Tree, k.Ta, k.Tt);

        return new Dictionary<string, object?>
        {
            ["tree"] = k.Tree,
            ["from_assembly"] = k.Fa,
            ["from_type"] = from?.Type.FullName,
            ["from_member"] = from?.Name,
            ["to_type"] = to?.Type.FullName,
            ["to_member"] = to?.Member,
            ["access"] = AccessKindText.Shown(k.Access),
            ["sites"] = count,
            ["loaded"] = LoadedCode.Value(load),
        };
    }

    private sealed class KeyComparer : IEqualityComparer<(string, int)>
    {
        public bool Equals((string, int) a, (string, int) b)
            => a.Item2 == b.Item2 && string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, int) k)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), k.Item2);
    }
}
