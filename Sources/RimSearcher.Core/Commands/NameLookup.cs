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
/// 顺序即结论强度:先说确定的(它在这儿,只是被你的过滤器挡住了),再说换个命令能拿到的,
/// 最后才是换环境。每条都只在**当场算得出来**时才出现 —— 算不出来就一个字都不说,
/// 免得变成免责声明。只出现在嵌套 <c>&lt;li Class="…"&gt;</c> 里的类不进快照,
/// 九档全落空之后交回调用方,由它说出这条边界。
///
/// 每一档是 <c>found_as</c> 表的一行(name / is / in / next),不再是一句散文(Docs/25 乙2):
/// 「它是什么」是封闭词表,「在哪」是算出来的落点,「下一步」是填好的命令。每档为什么
/// 落不到 def 面(抽象父永远不成 def、keyed 不属于任何 def、补丁打不到代码造的 def)
/// 是机制,住 <see cref="Help"/>,各命令的 Remarks 引它。
/// </summary>
internal static class NameLookup
{
    /// <summary>一个名字可能的落点。顺序即结论强度,<see cref="Locate"/> 按此依次判定。</summary>
    internal enum Where
    {
        /// <summary>是本快照里的 def。<see cref="Locate"/> 不产这一档 —— 它的调用方本来就在查 def;
        /// 查别的东西的命令(keyed / where 的字段格)撞上 def 名时用 <see cref="AsDef"/>。</summary>
        Def,
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

    /// <summary>一行:问的名字、它是什么(封闭词表)、在哪、够得着它的命令。</summary>
    internal sealed record Sighting(Where Where, string Name, string Is, string In, string Next);

    public const string Table = "found_as";
    public static readonly string[] Columns = ["name", "is", "in", "next"];
    public const string Caption = "Where the name does turn up:";

    public static readonly JsonKeySpec JsonKey = new()
    {
        Key = Table,
        Rows = true,
        What = "one row per name asked for that turns up as something other than what this command " +
               "looks up: name, is (def / def outside --scope / xml node / abstract xml node / def type / " +
               "class / interface text / mod in this snapshot / mod not in this snapshot / field value / " +
               "def in another snapshot / xml node in another snapshot), in (where exactly), next (a " +
               "command that reaches it, ready to paste). Empty when the name was found here, or turns up nowhere.",
    };

    /// <summary>
    /// 各档为什么落不到这条命令的答案上 —— 机制,住 help。每条会印 <c>found_as</c> 的命令
    /// 把它接在自己的 Remarks 后面。
    /// </summary>
    public const string Help =
        "When a name asked for is not what this command looks up, a found_as table says what it is instead " +
        "and gives the command that reaches it. An xml node is an inheritance-layer entry (Name=, ParentName= " +
        "or Abstract=) and never becomes a def, so only 'inherit' reaches it; interface text lives in keyed " +
        "translations that belong to no def, so only 'keyed' finds it; a field value is what some defs set a " +
        "field to (comps[N].compClass and the like), so 'where --value' lists them; a class is the runtime " +
        "type some defs are built as — the game files them under the nearest def type with a database of " +
        "its own, so 'list <DefType> --class' reaches them; a def outside --scope is " +
        "in this snapshot but excluded by the --scope given; a mod is a --scope, not a def. A def turns up " +
        "when the name is a def but this command reads something narrower: 'inherit' reads only nodes that " +
        "declare Name=, ParentName= or Abstract=, 'keyed' reads interface text, 'economy' reads priced " +
        "things; 'get' reaches the def itself.";

    /// <summary>把一行(或几行)印进 <c>found_as</c>;同一次输出里只有一张,后来的往里加行。</summary>
    /// <summary>next 是另一条命令;调用方点了 --snapshot 的话跟着走(同 index_gap),「在别的快照里」那档自己带着。</summary>
    public static void Say(CommandContext ctx, params Sighting[] sightings)
    {
        var snap = ctx.Args.Value("snapshot");
        var rows = sightings.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["name"] = s.Name, ["is"] = s.Is, ["in"] = s.In,
            ["next"] = snap is { Length: > 0 } && !s.Next.Contains("--snapshot", StringComparison.Ordinal)
                ? $"{s.Next} --snapshot {CommandContext.QuoteArg(snap)}"
                : s.Next,
        }).ToList();
        if (rows.Count == 0) return;
        ctx.Report.AppendRows(Table, Columns, rows, Caption);
    }

    /// <summary>
    /// 名字是某些 def 的运行时 class(<c>Class=</c>),不是 def 类型:get --type / list 拿到一个
    /// 不是分桶键的名字时也走这一档,出路是 <c>list &lt;桶&gt; --class</c>。
    /// </summary>
    public static Sighting? AsClass(CommandContext ctx, string name, ScopeFilter scope)
    {
        var holders = ctx.Db.TypesHoldingClass(name, scope);
        if (holders.Count == 0) return null;
        var total = holders.Sum(h => h.Count);
        var where = string.Join(", ", holders.Take(3).Select(h => h.DefType));
        return new Sighting(Where.Class, name, "class",
                            $"{Output.Tally.Complete(total).Render("def")} under {where}" +
                            (holders.Count > 3
                                ? $" and {Output.Tally.Complete(holders.Count - 3).Render("def type")} more"
                                : ""),
                            CommandRegistry.ExeName + " list " + holders[0].DefType + " --class " + name);
    }

    /// <summary>这条命令查的不是 def,而名字是个 def:keyed / where 的字段格撞上 def 名时用。</summary>
    public static Sighting AsDef(string name, IReadOnlyList<DefRow> defs)
        => new(Where.Def, name, "def",
               string.Join(", ", defs.Select(d => $"{d.DefType} in {d.SourceMod}").Distinct(StringComparer.Ordinal)),
               $"{CommandRegistry.ExeName} get {name}");

    /// <summary>
    /// 名字在哪儿。返回 null 表示**当场算不出来**,那时调用方照旧说自己的话。
    ///
    /// <paramref name="scope"/> 给出这次查询用的过滤器,只用来判第一条(被自己的过滤器
    /// 挡住)。不传就跳过那一条。
    /// </summary>
    public static Sighting? Locate(CommandContext ctx, string name, ScopeFilter? scope = null)
    {
        if (name.Length == 0) return null;
        var exe = CommandRegistry.ExeName;

        // (1) 它就在这份快照里,只是被 --scope 挡住了。放第一位:把「过滤掉了」说成
        //     「没有」是最贵的那种错。
        var defs = ctx.Db.GetDefsNamed(name);
        if (defs.Count > 0 && scope is { IsAll: false })
        {
            var visible = defs.Where(d => scope.Includes(d.SourceMod)).ToList();
            if (visible.Count == 0)
            {
                var mods = defs.Select(d => d.SourceMod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new Sighting(Where.DefOutsideScope, name, "def outside --scope",
                                    string.Join(", ", mods), ctx.Without("scope"));
            }
        }

        // (2) 继承层。抽象父节点从头到尾没有 Def 实例,search / get / where 三条路都够不着,
        //     只有 inherit 走的那张表里有。
        var node = ctx.Db.NodesNamed(name).FirstOrDefault();
        if (node is not null)
            return new Sighting(Where.XmlNode, name, node.Abstract ? "abstract xml node" : "xml node",
                                $"{node.SourceMod} ({node.SourceFile})", $"{exe} inherit {name}");

        // (3) def 类型(存储桶)。名字长得跟 def 名一模一样,而问法完全不同。
        var unscoped = ctx.Unscoped();
        var type = ctx.Db.Types(unscoped)
                         .FirstOrDefault(t => string.Equals(t.Type, name, StringComparison.OrdinalIgnoreCase));
        if (type.Type is not null)
            return new Sighting(Where.DefType, name, "def type",
                                Output.Tally.Complete(type.Count).Render("def"), exe + " list " + type.Type);

        // (4) 运行时 class。这一条要在 mod 之前:类名与 mod 名撞车的可能性远小于反过来。
        if (AsClass(ctx, name, unscoped) is { } asClass) return asClass;

        // (5) 界面文案。上面每一档判的是「这个**名字**是什么」,这一档判的是「这句**话**
        //     是什么」—— keyed 那一层与 def 无关。search 只索引 def 的 label / description /
        //     注入译文,把屏幕上看到的一句话打进 search,零结果的样子与「游戏里没有这句话」
        //     逐字同形。
        //
        //     排在 mod 与字段值之前:那两档只对标识符形态的查询成立,而标识符形态的查询
        //     也几乎不会 FTS 命中界面文案,两边抢不到彼此的活。
        if (ctx.Db.KeyedCount() > 0)
        {
            // 取一批而不是一条:同一句界面文案由**几个 key 各自承载**是常态(「转至事件
            // 发生地点」同时是 JumpToLocation 与 ClickToJumpToProblem),只取一条就得挑一个
            // 证不了的赢家。取满 25 条,distinct 在总数 ≤ 25 时是准数;超了只知下界,
            // 那时不点名 key —— FTS 是分词匹配,命中的几行未必整句相等。
            const int probe = 25;
            var (keyedRows, keyedTotal, _) = ctx.Db.KeyedSearch(name, probe);
            if (keyedRows.Count > 0)
            {
                var distinct = keyedRows.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
                var oneKeyForSure = distinct.Count == 1 && keyedTotal <= probe;
                return new Sighting(Where.Keyed, name, "interface text",
                                    $"{Output.Tally.Complete(keyedTotal).Render("keyed translation")}" +
                                    (oneKeyForSure ? $", key {distinct[0]}" : ", more than one key"),
                                    $"{exe} keyed {QuoteArg(name)}");
            }
        }

        // (6) mod,而且是**报全名报对了**的那种。这一档分成两半夹住下一条:整名
        //     (packageId / 末段 / 显示名)相等排在字段值**之前** —— `ludeon.rimworld`
        //     在 `linkedMod` 里有一个整值命中,可把 packageId 打进 search 的人要的是
        //     `--scope`,不是那个偏僻字段;外号子串排在字段值**之后**,猜的没资格抢一条
        //     算得出来的。本机 mod 目录只扫一次:扫盘是这条落空路径上最贵的一步。
        var installed = new Lazy<Dictionary<string, InstalledMod>?>(() => TryScanInstalled(ctx.Config));
        if (Mod(ctx, name, installed, fuzzy: false) is { } named) return named;

        // (7) 字段值。最常见的形态就是类名:`CompShield` 不是 def、不是 def 的 class,
        //     而是某些 def 的 comps[N].compClass **取值**。
        //
        //     只认整值与限定形态(ValueMatch.Identifier):子串会把更强的解释挤掉 ——
        //     `ludeon.rimworld` 会被 `showIfModsLoaded[0]` 装的 `ludeon.rimworld.royalty`
        //     抢答,而正确答案是下一档的「它是本快照覆盖的 mod」。
        var (holdingPaths, holdingTotal, _, _) = ctx.Db.PathsWithValue(name, unscoped, 3, ValueMatch.Identifier);
        if (holdingPaths.Count > 0)
        {
            var best = holdingPaths[0];
            // 不在这里报 def 数:PathsWithValue 按 (path, def_type) 分组,而 comps[2] 与
            // comps[5] 是两组,报出来的「1 def」会被读成「全快照只有一个」。计数交给 where,
            // 它数的是对的那个东西。
            //
            // 下一步:拿到的三条路径都以同一段收尾、而且三条就是全部时,给 `where <末段> <名字>` ——
            // 它直接列 def 名,而 `where --value` 列的是路径,拿名字还得再敲一条(r19b:B 臂常见档
            // 3/3 多走这一跳,弱档 2/3)。末段不齐或路径不止三条时仍给 `--value`,覆盖到哪条由它
            // 自己按当次数据说,推荐侧不替它担保。
            var leaves = holdingPaths.Select(h => NoiseFilter.Leaf(h.Path)).Distinct(StringComparer.Ordinal).ToList();
            var next = holdingTotal <= holdingPaths.Count && leaves.Count == 1
                ? $"{exe} where {leaves[0]} {QuoteArg(name)}"
                : $"{exe} where --value {QuoteArg(name)}";
            return new Sighting(Where.FieldValue, name, "field value",
                                $"{best.Path} = {best.Sample}" +
                                (holdingTotal > 1 ? $", {holdingTotal} path and def-type pairs in all" : ""),
                                next);
        }

        // (8) mod,报的是外号(输入 `Milira`,packageId 是 Ancot.MiliraRace)。
        if (Mod(ctx, name, installed, fuzzy: true) is { } nicknamed) return nicknamed;

        // (9) 别的快照。
        return InOtherSnapshot(ctx, name);
    }

    private static string QuoteArg(string v)
        => v.Length > 0 && v.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'') ? v : "\"" + v.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// 「别的快照里有没有」这类补充信息的公共遍历面。<paramref name="probe"/> 回 null 表示
    /// 那份库里没有,非 null 的原样带回,次序按登记处而不是按谁先跑完。
    ///
    /// **两条收窄,都只作用于这里,不影响显式寻址**(<c>--snapshot baseline.prev</c> 照常可用):
    ///
    /// 一是**不探旧代**。<c>{name}.prev</c> 是同一份快照的上一次导出,「那边有没有」几乎
    /// 必然与它的当代同答案 —— 探完只会让同一句话把 'baseline' 和 'baseline.prev' 并排念
    /// 一遍。本机 19 份库里 12 份是旧代,而每份都要开库、跑同一条谓词。
    ///
    /// 二是**并行**。每份库是各自的文件、各自的只读连接,彼此不共享任何东西;
    /// 这条路径上唯一的成本就是各库那一次查询。
    ///
    /// 打不开的快照一律咽掉:补充信息不该让一句「没找到」变成一次崩溃。
    /// </summary>
    private static List<(string Alias, object What)> Fanout(CommandContext ctx, Func<SnapshotDb, object?> probe)
    {
        var here = Path.GetFullPath(ctx.Db.Path);
        var targets = SnapshotCatalog.Enumerate(ctx.Config)
            .Where(e => !string.Equals(Path.GetFullPath(e.Path), here, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 这个名字是不是一个 mod。快照里有它 → 那是个 <c>--scope</c>;本机装着但快照没覆盖
    /// → 那是要重新导出。两句话都要点名 packageId,因为 <c>--scope</c> 只认它。
    /// </summary>
    private static Sighting? Mod(
        CommandContext ctx, string name, Lazy<Dictionary<string, InstalledMod>?> installed, bool fuzzy)
    {
        var exe = CommandRegistry.ExeName;
        var inSnapshot = ctx.Db.Mods.FirstOrDefault(m => SameMod(m.PackageId, m.Name, name, fuzzy));
        if (inSnapshot is not null)
            return new Sighting(Where.ModInSnapshot, name, "mod in this snapshot", inSnapshot.PackageId,
                                exe + " list --scope " + inSnapshot.PackageId);

        var offSnapshot = installed.Value?.Values.FirstOrDefault(m => SameMod(m.PackageId, m.Name, name, fuzzy));
        if (offSnapshot is null) return null;

        return new Sighting(Where.ModNotInSnapshot, name, "mod not in this snapshot",
                            $"{offSnapshot.PackageId}, installed", $"{exe} snapshot status");
    }

    /// <summary>
    /// 零结果时问一遍别的已注册快照:同一个问题在那边有没有答案。
    ///
    /// **叠加不替换**:本快照那句成因分流仍然要说完,这条只在后面补一句「别处有」。
    /// 只数不取行;而且**一律不带 scope** —— 别的快照装的 mod 不一样,把这里的
    /// `--scope` 搬过去,只会把「那边有」错报成「那边也没有」。
    /// </summary>
    public static string? Elsewhere(CommandContext ctx, Func<SnapshotDb, int> probe, string noun)
    {
        var found = Fanout(ctx, db => probe(db) is var n && n > 0 ? (object)n : null)
            .Select(f => (f.Alias, Count: (int)f.What)).ToList();

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
        var found = Fanout(ctx, other =>
        {
            var defs = other.GetDefsNamed(name);
            if (defs.Count > 0) return (Is: "def in another snapshot", What: $"{defs[0].DefType} from {defs[0].SourceMod}");
            return other.NodesNamed(name).Count > 0 ? (Is: "xml node in another snapshot", What: "") : null;
        }).Select(f => (f.Alias, Hit: ((string Is, string What))f.What)).ToList();

        if (found.Count == 0) return null;

        // 同一件东西在八份库里各一份是常态(官方 def 在每份快照里都是同一个 mod 的同一类型):
        // 按「是什么」分组,别名并排,不把同一句重复八遍。
        var groups = found.GroupBy(f => f.Hit.What, StringComparer.Ordinal)
                          .Select(g => string.Join(", ", g.Select(f => $"'{f.Alias}'")) + (g.Key.Length > 0 ? $": {g.Key}" : ""));
        return new Sighting(Where.OtherSnapshot, name, found[0].Hit.Is, string.Join("; ", groups),
                            $"{ctx.Without("snapshot")} --snapshot {found[0].Alias}");
    }

    /// <summary>
    /// packageId 全名、它的末段、mod 的显示名 —— 三者任一相等都算把名字报对了。
    ///
    /// 模糊档再往下放到包含关系,因为人记得的是外号,而外号不在任何数据里:输入
    /// <c>Milira</c> 时 packageId 是 <c>Ancot.MiliraRace</c>、显示名是 <c>Milira Race</c>,
    /// 三者互不相等,只认相等这条判定就永远不触发。四字符下限挡住 <c>Core</c> 这类短词。
    /// </summary>
    private const int NicknameMinLength = 4;

    /// <summary>
    /// 外号命中时补一个 packageId,原样命中时什么都不补 —— 「'ludeon.rimworld' is a mod
    /// this snapshot covers (ludeon.rimworld)」这种把输入原样念一遍的括号是纯噪音。
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
