using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 「这个名字到底在哪儿」—— 零结果的成因分流,产地唯一。
///
/// 三轮 R8(四种误诊)与 R10 fatal(三个场景)是同一个形状:落空的时候,**答案就在
/// 工具自己手里,它没去算**。逐条对照:
///
///   · <c>search BuildingNaturalBase</c> 说「That looks like a class」并指向 find /
///     code-search 两条必然空手的路 —— 而 <c>inherit BuildingNaturalBase</c> 当场给出
///     5 个子节点。它是继承层里的抽象 <c>Name=</c>,这一层就在同一个库里。
///   · <c>search Milira</c> 走另一条分支,被指向 <c>types</c> —— 而 <c>types</c> 列的是
///     def 类型,与「某个 mod 在不在」毫无关系。真因是这个 **mod** 不在快照覆盖里,
///     该指的是 <c>mods</c> / <c>snapshot status</c>。三轮把它单列为 R10 的一半。
///   · <c>search SubSoundDef</c>(三轮评为整轮最贵的那次)两条分支都答不对:它既不是
///     def、也不是 def 类型,而是只出现在嵌套 <c>&lt;li Class="…"&gt;</c> 里的类 ——
///     那一层不进快照(R9 的边界)。这里六档全落空之后交回调用方,由它说出这条边界。
///   · R10:工具知道本快照的 mod 列表,也读得到别的已注册快照,「它在 modded 那份里」
///     这句话是**可算出来的**,它从来没说过。
///
/// 于是这里把「名字的落点」做成一次有序判定,谁落空都问它。顺序即结论强度:先说
/// 确定的(它在这儿,只是被你的过滤器挡住了),再说换个命令能拿到的,最后才是换环境。
/// 每条都只在**当场算得出来**时才出现 —— 算不出来就一个字都不说,免得又变成免责声明。
/// </summary>
internal static class NameLookup
{
    /// <summary>一个名字可能的落点。顺序即结论强度,<see cref="Locate"/> 按此依次判定。</summary>
    internal enum Where
    {
        /// <summary>是本快照里的 def,只是被 --scope 挡住了。</summary>
        DefOutsideScope,
        /// <summary>是继承层里的 XML 节点(多半是抽象父),永远不会成为 def。</summary>
        XmlNode,
        /// <summary>是一个 def 类型(存储桶),不是一个 def。</summary>
        DefType,
        /// <summary>是某些 def 的运行时 class。</summary>
        Class,
        /// <summary>是界面文案(keyed 译文),与 def 无关 —— 别的每一档都判不到它。</summary>
        Keyed,
        /// <summary>是本快照覆盖的一个 mod。名字报全了的走在字段值之前,报外号的走在之后。</summary>
        ModInSnapshot,
        /// <summary>是本机装着、但这份快照没覆盖的 mod。同上,分两档夹住字段值。</summary>
        ModNotInSnapshot,
        /// <summary>是某些 def 的字段**取值**(comps[N].compClass 那一类)。</summary>
        FieldValue,
        /// <summary>别的已注册快照里有这个名字。</summary>
        OtherSnapshot,
    }

    internal sealed record Sighting(Where Where, string Sentence);

    /// <summary>
    /// 名字在哪儿。返回 null 表示**当场算不出来**,那时调用方照旧说自己的话。
    ///
    /// <paramref name="scope"/> 给出这次查询用的过滤器,只用来判第一条(被自己的过滤器
    /// 挡住)。不传就跳过那一条。
    /// </summary>
    public static Sighting? Locate(CommandContext ctx, string name, ScopeFilter? scope = null)
    {
        if (name.Length == 0) return null;

        // (1) 它就在这份快照里,只是被 --scope 挡住了。这是最强的一条:数据在手边,
        //     缺的只是一个参数。放第一位,因为把「过滤掉了」说成「没有」是最贵的那种错。
        var defs = ctx.Db.GetDefsNamed(name);
        if (defs.Count > 0 && scope is { IsAll: false })
        {
            var visible = defs.Where(d => scope.Includes(d.SourceMod)).ToList();
            if (visible.Count == 0)
            {
                var mods = defs.Select(d => d.SourceMod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new Sighting(Where.DefOutsideScope,
                    $"'{name}' is in this snapshot after all — it comes from {string.Join(", ", mods)}, " +
                    $"which --scope {scope.Expression} excludes. Drop --scope, or name that mod in it.");
            }
        }

        // (2) 继承层。抽象父节点从头到尾没有 Def 实例,所以 search / get / find 三条路
        //     全都找不到它 —— 而它就在同一个库的另一张表里,inherit 一问就有。
        var node = ctx.Db.NodesNamed(name).FirstOrDefault();
        if (node is not null)
            return new Sighting(Where.XmlNode,
                $"'{name}' is an XML node in {node.SourceMod} ({node.SourceFile}) but never becomes a def" +
                (node.Abstract ? " — it is Abstract=\"True\"" : "") +
                $". 'rimsearcher inherit {name}' shows what it inherits from, what inherits from it, " +
                "and which concrete child carries the merged values.");

        // (3) def 类型(存储桶)。名字长得跟 def 名一模一样,而问法完全不同。
        var unscoped = ctx.Unscoped();
        var type = ctx.Db.Types(unscoped)
                         .FirstOrDefault(t => string.Equals(t.Type, name, StringComparison.OrdinalIgnoreCase));
        if (type.Type is not null)
            return new Sighting(Where.DefType,
                $"'{type.Type}' is a def type in this snapshot, not a def: it holds " +
                $"{Output.Tally.Complete(type.Count).Render("def")}. " +
                $"'rimsearcher list {type.Type}' lists them and 'rimsearcher fields {type.Type}' " +
                "shows what fields they can have.");

        // (4) 运行时 class。这一条要在 mod 之前:类名与 mod 名撞车的可能性远小于反过来。
        var holders = ctx.Db.TypesHoldingClass(name, unscoped);
        if (holders.Count > 0)
        {
            var total = holders.Sum(h => h.Count);
            var where = string.Join(", ", holders.Take(3).Select(h => h.DefType));
            // 计数放句尾的从句里,主句不带随数变形的动词,末句也不带回指代词 ——
            // 「1 def … lists them」同样是数一致性(R6 同一课:名词有登记处,动词与代词都没有)。
            return new Sighting(Where.Class,
                $"'{name}' is a class rather than a def name. This snapshot holds " +
                $"{Output.Tally.Complete(total).Render("def")} of that class, filed under {where}" +
                (holders.Count > 3
                    ? $" and {Output.Tally.Complete(holders.Count - 3).Render("def type")} more"
                    : "") +
                $"; the query is 'rimsearcher list {holders[0].DefType} --class {name}'.");
        }

        // (5) 界面文案。上面每一档判的都是「这个**名字**是什么」,而这一档判的是
        //     「这句**话**是什么」—— keyed 那一层与 def 无关,所以前四档原理上都碰不到它。
        //     它恰恰是 search 落空最常见的真实成因之一:把屏幕上看到的一句话打进 search,
        //     而 search 只索引 def 的 label / description / 注入译文,零结果出来的样子与
        //     「游戏里没有这句话」逐字同形。R4 把这条记成「索引口径的洞」,当时降级成纯文档
        //     处理;这一档是它算得出来的那一半。
        //
        //     排在 mod 与字段值之前:那两档都只对标识符形态的查询成立,而进到这里的
        //     phrase 形态查询它们一个都判不到 —— 反过来,标识符形态的查询也几乎不会
        //     FTS 命中界面文案,两边抢不到彼此的活。
        if (ctx.Db.KeyedCount() > 0)
        {
            var (keyedRows, keyedTotal) = ctx.Db.KeyedSearch(name, 1);
            if (keyedRows.Count > 0)
            {
                // 主语固定单数(this snapshot),计数进宾语,末句不带回指代词 ——
                // 名词有登记处,主谓一致与「them / one」这类回指都没有(R6 同一课;
                // 这一句第一版写的就是「shows them」,而计数是 1)。
                var first = keyedRows[0];
                return new Sighting(Where.Keyed,
                    $"'{name}' is interface text rather than a def name: this snapshot holds " +
                    $"{Output.Tally.Complete(keyedTotal).Render("keyed translation")} matching it, the closest " +
                    $"under the key '{first.Key}'. Keyed translations belong to no def at all, which is why " +
                    $"a def search reaches none of them. The query is 'rimsearcher keyed {name}', and " +
                    $"'rimsearcher code-search \"\\\"{first.Key}\\\"\"' finds the code that prints that key.");
            }
        }

        // (6) mod,而且是**报全名报对了**的那种。R8 的第二种误诊死在这:`search Milira`
        //     被指向 `types`,而 types 列的是 def 类型,与「某个 mod 在不在」毫无关系。
        //
        //     它分成两半夹住下一条:整名(packageId / 末段 / 显示名)相等排在字段值**之前**
        //     —— 实测 `search ludeon.rimworld` 在 `linkedMod` 里有一个整值命中,可把
        //     packageId 打进 search 的人要的是 `--scope`,不是那个偏僻字段;而外号子串排在
        //     字段值**之后**,它只是猜得比较准,没资格抢一条算得出来的。
        //     本机 mod 目录只扫一次:这一档与模糊那一档都要用它,而扫盘是这条落空路径上
        //     最贵的一步。
        var installed = new Lazy<Dictionary<string, InstalledMod>?>(() => TryScanInstalled(ctx.Config));
        if (Mod(ctx, name, installed, fuzzy: false) is { } named) return named;

        // (7) 字段值。最常见的形态就是类名:`CompShield` 不是 def、不是 def 的 class,
        //     而是某些 def 的 comps[N].compClass **取值**。
        //
        //     这一条是写这次修复时补回来的:原先那句「像类名 → 去跑 find compClass X」
        //     虽然是猜的,可它猜对的那一半是真能用的(语料里 find compClass CompShield
        //     命中 2 个 def)。第一版把猜话换成了「no class」—— 把一句有用的猜测换成了
        //     一句错误的断言,正是本条修复要修的那类错本身。于是把它变成可算的:
        //     直接问哪些字段路径装着这个值,连路径一起端出来。
        //     只认整值与限定形态(ValueMatch.Identifier):子串在这里会把更强的解释挤掉 ——
        //     实测 `search ludeon.rimworld` 被 `showIfModsLoaded[0]` 装的
        //     `ludeon.rimworld.royalty` 抢答,而正确答案是下一档的「它是本快照覆盖的 mod」。
        var (holdingPaths, holdingTotal, _) = ctx.Db.PathsWithValue(name, unscoped, 3, ValueMatch.Identifier);
        if (holdingPaths.Count > 0)
        {
            var best = holdingPaths[0];
            var tail = best.Path.Contains('.') ? best.Path[(best.Path.LastIndexOf('.') + 1)..] : best.Path;
            return new Sighting(Where.FieldValue,
                // 不在这里报 def 数:PathsWithValue 按 (path, def_type) 分组,而 comps[2] 与
                // comps[5] 是两组,报出来的「1 def」会被读成「全快照只有一个」。计数交给 find,
                // 它数的是对的那个东西。
                $"'{name}' is not a def name, but it appears as a field value: '{best.Path}' holds " +
                $"'{best.Sample}'" +
                (holdingTotal > 1
                    ? $", and it turns up under {holdingTotal} path and def-type combinations in all"
                    : "") +
                $". 'rimsearcher find {tail} {name}' lists the defs that use it, and " +
                $"'rimsearcher find --value {name}' covers every path at once.");
        }

        // (8) mod,报的是外号。实测那次输入就是 `Milira`,而 packageId 是 Ancot.MiliraRace。
        if (Mod(ctx, name, installed, fuzzy: true) is { } nicknamed) return nicknamed;

        // (9) 别的快照。R10 的核心:这句话一直是可算出来的。
        return InOtherSnapshot(ctx, name);
    }

    /// <summary>
    /// 这个名字是不是一个 mod。快照里有它 → 那是个 <c>--scope</c>;本机装着但快照没覆盖
    /// → 那是要重新导出。两句话都要点名 packageId,因为 <c>--scope</c> 只认它。
    /// </summary>
    private static Sighting? Mod(
        CommandContext ctx, string name, Lazy<Dictionary<string, InstalledMod>?> installed, bool fuzzy)
    {
        var inSnapshot = ctx.Db.Mods.FirstOrDefault(m => SameMod(m.PackageId, m.Name, name, fuzzy));
        if (inSnapshot is not null)
            return new Sighting(Where.ModInSnapshot,
                $"'{name}' is a mod this snapshot covers{Spell(name, inSnapshot.PackageId)}, not a def. " +
                $"'--scope {inSnapshot.PackageId}' restricts any query to it; 'rimsearcher mods' lists them all.");

        var offSnapshot = installed.Value?.Values.FirstOrDefault(m => SameMod(m.PackageId, m.Name, name, fuzzy));
        if (offSnapshot is null) return null;

        return new Sighting(Where.ModNotInSnapshot,
            $"'{name}' is a mod installed on this machine{Spell(name, offSnapshot.PackageId)} that this snapshot " +
            "does not cover, so nothing from it can be found here. 'rimsearcher snapshot status' compares " +
            "the two, and re-exporting with that mod enabled is what brings it in.");
    }

    /// <summary>
    /// 零结果时问一遍别的已注册快照:同一个问题在那边有没有答案。
    ///
    /// R10 原先只覆盖「按名字取一个 def」那条路。第五轮实测里 `find` 落空、而
    /// races 那份快照里明明有 6 条 —— 同一句「本快照没有」在这里说出来,读的人只能
    /// 读成「这东西不存在」。
    ///
    /// **叠加不替换**:本快照那句成因分流仍然要说完,这条只在后面补一句「别处有」。
    /// 只数不取行;而且**一律不带 scope** —— 别的快照装的 mod 不一样,把这里的
    /// `--scope` 搬过去,只会把「那边有」错报成「那边也没有」。
    /// </summary>
    public static string? Elsewhere(CommandContext ctx, Func<SnapshotDb, int> probe, string noun)
    {
        var here = ctx.Db.Path;
        var found = new List<(string Alias, int Count)>();

        foreach (var entry in SnapshotCatalog.Enumerate(ctx.Config))
        {
            if (string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(here), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var other = SnapshotDb.Open(entry.Path);
                var n = probe(other);
                if (n > 0) found.Add((entry.Alias, n));
            }
            catch
            {
                // 打不开的快照不该让一句「没找到」变成一次崩溃 —— 它本来就只是补充信息。
            }
        }

        if (found.Count == 0) return null;

        // 句中不出现随计数变形的动词:别名是固定的单数,数目一律走登记处。
        return "Another registered snapshot does have it — " +
               string.Join(", ", found.Select(f => $"'{f.Alias}': {Tally.Complete(f.Count).Render(noun)}")) +
               $". Add '--snapshot {found[0].Alias}' to ask there; that check ignored --scope.";
    }

    /// <summary>
    /// 别的已注册快照里有没有这个名字。只开库、只查一条精确名,不做模糊 ——
    /// 落空路径上的额外开销要小到可以无条件付。
    /// </summary>
    private static Sighting? InOtherSnapshot(CommandContext ctx, string name)
    {
        var here = ctx.Db.Path;
        var found = new List<(string Alias, string What)>();

        foreach (var entry in SnapshotCatalog.Enumerate(ctx.Config))
        {
            if (string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(here), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var other = SnapshotDb.Open(entry.Path);
                var defs = other.GetDefsNamed(name);
                if (defs.Count > 0)
                {
                    found.Add((entry.Alias, $"a {defs[0].DefType} from {defs[0].SourceMod}"));
                    continue;
                }
                if (other.NodesNamed(name).Count > 0)
                    found.Add((entry.Alias, "an XML node in its inheritance layer"));
            }
            catch
            {
                // 打不开的快照不该让一句「没找到」变成一次崩溃。它本来就只是补充信息。
            }
        }

        if (found.Count == 0) return null;

        return new Sighting(Where.OtherSnapshot,
            $"'{name}' is not in the snapshot this query used, but it is in " +
            string.Join(", ", found.Select(f => $"'{f.Alias}' ({f.What})")) +
            $". Add '--snapshot {found[0].Alias}' to ask there instead.");
    }

    /// <summary>
    /// packageId 全名、它的末段、mod 的显示名 —— 三者任一相等都算把名字报对了。
    ///
    /// 模糊档再往下放到包含关系,因为人记得的是外号:三轮实测的那一次输入是 <c>Milira</c>,
    /// 而 packageId 是 <c>Ancot.MiliraRace</c>、显示名是 <c>Milira Race</c>,三者互不相等,
    /// 只认相等等于这条判定永远不触发(<c>--source HAR</c> 那条注释里写过同一件事:
    /// 外号不在任何数据里)。四字符下限挡住 <c>Core</c> 这类会乱撞的短词。
    /// </summary>
    private const int NicknameMinLength = 4;

    /// <summary>
    /// 外号命中时补一个 packageId,原样命中时什么都不补 —— 「'ludeon.rimworld' is a mod
    /// this snapshot covers (ludeon.rimworld)」这种把输入原样念一遍的括号是纯噪音,
    /// 而调用方的上下文预算是这轮所有取舍的第一约束。
    /// </summary>
    private static string Spell(string typed, string packageId)
        => string.Equals(typed, packageId, StringComparison.OrdinalIgnoreCase) ? "" : $" ({packageId})";

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
