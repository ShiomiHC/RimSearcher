using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 界面文案那一层 —— <c>"SomeKey".Translate()</c> 里那个 SomeKey 显示成什么,以及反过来:
/// 屏幕上那句话是哪个 key。
///
/// **这一层与 def 无关**,而这正是它非有不可的理由:玩家看见的字里超过一半不来自任何 def。
/// 缺了它,「界面上这句提示从哪来」这条路整个不通 —— 而且它不通的样子与「查不到」
/// 逐字同形(中文进 <c>search</c>,零结果出来,而 <c>search</c> 只索引 def 的 label 与译文)。
/// R4 把这条记成「索引口径的洞」时降级成了纯文档处理,连那条线也没落地。
///
/// 数据来自两处,分层写在每一行的 origin 上,不合并:
/// 运行时 <c>keyedReplacements</c>(游戏最终用的那一句 —— 覆盖冲突的赢家,唯一)
/// 与 import 时从 mod 磁盘收割的(只说「存在」,不说「生效」)。
/// </summary>
public sealed class KeyedCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "keyed",
        Aliases = ["ui", "ui-text", "strings", "key", "translation"],
        Summary = "Look up the UI text behind a translation key, or find the key behind a piece of UI text.",
        Remarks =
            "This is the layer defs do not cover. A def's label and description are translated through " +
            "DefInjected and belong to 'get' and 'search'; everything else on screen — button captions, " +
            "alerts, tooltips, failure reasons — is a keyed translation, and only this command reads those.\n\n" +
            "It works in both directions. Given a key it shows what the game displays for it; given a phrase " +
            "in either language it shows which keys carry that text, which is how you get from a line on " +
            "screen to the code that prints it: take the key from here and run " +
            "'rimsearcher code-search \"\\\"TheKey\\\"\"'.\n\n" +
            "Rows are marked 'in effect' or 'on disk'. Only 'in effect' is what the game displays: keyed " +
            "translations override each other by mod load order and the snapshot keeps the winner, so an " +
            "'on disk' row is a translation that exists in some mod's language files without necessarily " +
            "being the one that wins.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "query",
                Required = false,
                Help = "A translation key, or a phrase from the interface in any language the snapshot has. " +
                       "Leave it out to list the layer itself — every keyed translation, or with " +
                       "--placeholders only the untranslated ones.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("keys"),
            CommonOptions.Offset("keys"),
            new OptionSpec
            {
                Name = "placeholders",
                Arity = Arity.Flag,
                Aliases = ["untranslated", "todo"],
                Help = "List only keys whose translation is still a placeholder — the language file has the " +
                       "key but not a translation, so the game falls back to English. This is what a " +
                       "translation-coverage question wants, and it needs no query: on its own it filters " +
                       "the whole layer.",
                // 这个开关一给,计数就只在它划的那道线之内完整。不念回去的话,
                // 「2 keyed translations.」会被读成「这份快照一共两条界面文案」,而真值是 2105。
                Narrows = true,
            },
        ],
        Examples =
        [
            "rimsearcher keyed CannotUseNoPower",
            "rimsearcher keyed 没有电力",
            "rimsearcher keyed Command --limit all",
            "rimsearcher keyed --placeholders --limit all",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "keys",
                Rows = true,
                What = "one row per keyed translation — key, translated, original, origin ('in effect' or " +
                       "'on disk'), placeholder, mod, source. Always an array, including when a single key " +
                       "matched exactly, so the shape does not change with the kind of match.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var query = ctx.Args.Positional(0);
        var limit = ctx.Limit();
        var offset = ctx.Args.Int("offset", 0);
        var placeholdersOnly = ctx.Args.Flag("placeholders");

        // 这一层整个是空的,与「这个 key 不在里面」是两件事,而它们的输出会长成一样。
        // 先问一次,好让下面每一条落空的话都能带上正确的成因。
        var total = ctx.Db.KeyedCount();
        if (total == 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                "This snapshot has no keyed translations at all, so nothing here can be looked up — that is a " +
                "property of the snapshot, " +
                (query is null ? "not an answer about what this layer holds. " : $"not an answer about '{query}'. ") +
                "Two exports look like this and " +
                "this line cannot tell them apart: one written before this layer was measured at all, and one " +
                "written from a game whose language data was not loaded. The fix is the same either way — export " +
                "again; 'rimsearcher snapshot status' names the snapshot in use.");
            return 1;
        }

        // 不给查询词就是整层枚举。原先位置参数是必填的,于是「把还没译的全列出来」这条意图
        // 一种可表达的形式都没有 —— 而 `--placeholders` 本来就是**整层的过滤器**,不是搜索
        // 结果上的过滤器,它单独出现是这个开关最自然的用法。实测里一个调用方为此烧了八次
        // 调用(空串 / `*` / `.` / 空格轮着试,全被同一句 Missing required argument 挡回),
        // 最后改去猜实词("Command" / "the"),而那答的是另一个问题、且答得像答对了。
        // 判据与 `list` 同源:分模式看**给没给位置参数**,不看开关。
        if (query is null)
            return RunAll(ctx, limit, offset, placeholdersOnly);

        // 精确 key 命中优先。key 与界面文案不会同形,所以这一步不会抢走「按文案搜」的意图。
        var exact = ctx.Db.KeyedByKey(query);
        var rows = exact;
        var matchedOn = "key";
        // 分页三件事按它算。--placeholders 下推之后它是过滤后的数。
        var ftsTotal = exact.Count;
        // 「N 条命中里一条占位都没有」要的是过滤**之前**的数。两个数混用一个,
        // 那句最强否定句就会拿着自己筛剩的零去否定全体。
        var matchedTotal = exact.Count;

        if (exact.Count == 0)
        {
            // limit.Effective 而不是夹到 Limits.MaxLimit:`--limit all` 在别处一律解除行上限
            // (SKILL.md 的总纲就一句「lifts the row cap」),只有这里把它翻译成 2000,
            // 而截断时给的补救仍是「pass --limit all」—— 指着调用方刚用过的那个参数。
            var (hits, hitTotal, hitMatched) =
                ctx.Db.KeyedSearch(query, limit.Effective, offset, placeholdersOnly);
            rows = hits;
            ftsTotal = hitTotal;
            matchedTotal = hitMatched;
            matchedOn = "text";
        }
        else if (placeholdersOnly)
        {
            // 一个 key 的几条来源一次全在手上,所以这里的筛是全量筛,不是页内筛。
            rows = exact.Where(r => r.Placeholder).ToList();
            ftsTotal = rows.Count;
        }

        // --offset 在精确命中这一路原先被读进来却从不使用。一个 key 通常只有几条来源,
        // 翻页确实没什么用 —— 但「参数给了、输出一个字没变、也没人说一句」正是本项目
        // 点名清过的那个形状:`--offset 1` 与 `--offset 0` 印出一模一样的表,读的人
        // 无从知道自己刚才那个参数根本没生效。
        //
        // 两路的 offset 在不同的地方施加(文案那路在 SQL 里,这一路在内存里),但施加
        // **之后**两路一律走同一套分页文法报数,于是「这一页几条 / 总共几条 / 还有没有」
        // 不再随命中方式换一套说法。
        if (matchedOn == "key" && offset > 0)
            rows = rows.Skip(offset).ToList();

        if (rows.Count == 0)
        {
            // 五种互斥成因。合成一句「没找到」会让前四种被读成第五种,而第五种
            // (这个环境里真没有)是最强的那个结论。
            //
            // 翻过头排在最前:它与「没有」的区别最大,而这条命令此前根本没判它 ——
            // 一次翻页会被读成一次否定,正是别处点名清过的那个形状。
            if (offset > 0 && ftsTotal > 0)
            {
                ctx.Report.PastEnd(offset,
                    $"{Tally.Complete(ftsTotal).Render("key")} match '{query}' in all.");
                return 1;
            }

            if (placeholdersOnly && matchedTotal > 0)
            {
                // 主语是 --placeholders(固定单数),计数进从句 —— 见下面那条注释。
                ctx.Report.Notice(NoticeKind.Filter,
                    $"--placeholders filtered out every match: {Tally.Complete(matchedTotal).Render("key")} " +
                    $"matched '{query}', and none of them is a placeholder — each has a real translation. " +
                    "Drop --placeholders to see them.");
                return 1;
            }

            var close = Suggestion.Closest(ctx.Db.AllKeyedKeys(), query);

            ctx.Report.Notice(NoticeKind.NextStep,
                $"No keyed translation matches '{query}'." + Suggestion.Say(close));

            // 「问的其实是个 def 名」是这条命令最常见的落空成因,而它是当场判得出来的。
            // NameLookup 不管这一档:它的调用方(search / inherit)本来就在查 def,
            // 「这是个 def」对它们不是新消息 —— 对 keyed 却恰恰是答案。InheritCommand
            // 同样自己判了一次,成例在那儿。
            var defs = ctx.Db.GetDefsNamed(query);
            if (defs.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{query}' is a def in this snapshot, and a def's label and description are translated " +
                    $"through DefInjected rather than through a key: 'rimsearcher get {query}' shows them with " +
                    "the translation table attached, and 'rimsearcher search' matches on translated text too.");
                return 1;
            }

            var sighting = NameLookup.Locate(ctx, query);
            if (sighting is not null)
                ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
            else
                // 两条射程线,都是这一层原理上到不了的地方 —— 说破它们,免得「这里没有」
                // 被读成「游戏里没有这句话」。
                ctx.Report.Notice(NoticeKind.Boundary,
                    "Two things are outside this layer by construction. A def's own label or description is " +
                    "translated through DefInjected, not through a key: 'rimsearcher search " + query + "' " +
                    "covers those. And a key the code assembles at runtime ('\"Stat_\" + x') exists in the " +
                    "language files but appears in no source line as a literal, so searching the code for it " +
                    "finds nothing even though this command can still show it by name.");
            return 1;
        }

        var shown = limit.IsAll ? rows : rows.Take(limit.Effective).ToList();

        // 名词跟着「数的是什么」变,不是跟着命令名变:按 key 精确命中时数的是这个 key 的
        // 几条来源(in effect 一条,on disk 可以另有几条),按文案搜时数的是命中了几个 key。
        // 两者混用一个词,读的人就会把「一个 key 三条来源」读成「三个 key」(R7)。
        //
        // 变的只有名词。计数形态两路同一套 —— 原先按 key 那一路走 CountNotice 加一句写死的
        // 「pass --limit all for the rest」,于是 `--limit all --offset N` 会拿到一句指着
        // 调用方刚用过的那个参数的补救,而真正让它变短的是 --offset。
        Emit(ctx, shown, matchedOn == "key" ? "keyed translation" : "key", offset, ftsTotal, placeholdersOnly);

        // 命中是靠文案搜到的时候,下一步几乎总是「那么是哪段代码印的」。key 在手上,
        // 那条命令是可以直接给出来的 —— 而 97% 的调用点把 key 写成紧邻的字面量。
        //
        // 只在**表里只有一个 key** 时把命令填好。同一句界面文案由几个 key 各自承载是常态
        // (真数据里「转至事件发生地点」同时是 JumpToLocation 与 ClickToJumpToProblem),
        // 那时填第一个就等于替读的人挑了一个 —— 而表里另外那几行长得一模一样,挑错了
        // 看不出来。挑不了就说破要按行挑,别把「有几个候选」印成「就是这个」。
        if (matchedOn == "text" && shown.Count > 0)
        {
            var keys = shown.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
            ctx.Report.Notice(NoticeKind.NextStep, keys.Count == 1
                ? "To find the code that prints it, search for the key as a literal: " +
                  $"'rimsearcher code-search \"\\\"{keys[0]}\\\"\"'. Most call sites write the key inline, " +
                  "but not all of them do — a key assembled from parts will not appear that way."
                  // 计数上面那句已经报过了,这里再报一遍是同一个数占两行 ——
                  // 这句要说的不是「有几个」,是「哪一个由你挑」。
                : "These rows do not all carry the same key, so the code search goes after whichever row is " +
                  "the one you meant: 'rimsearcher code-search \"\\\"<key>\\\"\"' with the key from that row. " +
                  "Most call sites write the key inline, but not all of them do — a key assembled from parts " +
                  "will not appear that way.");
        }

        return 0;
    }

    /// <summary>
    /// 不给查询词的那一半:整层枚举,<c>--placeholders</c> 在这里是它本来的形状 —— 作用在
    /// 整层上的过滤器,而不是「先搜到什么再从里面筛」。
    ///
    /// 分页走的是文案搜索那一路的文法(LIMIT/OFFSET + <see cref="Output.Report.PageNotice"/>),
    /// 不是精确 key 那一路 —— 那一路一个 key 就那几条来源,一次全在手上,而这里的分母是
    /// 整层的两千多条,「这一页几条 / 总共几条 / 下一页怎么要」三件事一件都不能少。
    /// </summary>
    private static int RunAll(CommandContext ctx, LimitValue limit, int offset, bool placeholdersOnly)
    {
        // 分页在 SQL 里做,不是取全量再切 —— 整层是两千到一万几千行,`--limit 25` 却只要
        // 二十五行,把全部读进内存再扔掉是按最坏情况付账。`--limit all` 那一档
        // LimitValue.Effective 给的是 int.MaxValue(真的解除上限,不是夹到 2000),
        // 于是「全给」这条承诺在这里也是字面成立的。
        var (rows, total, layerTotal) = ctx.Db.KeyedAll(limit.Effective, offset, placeholdersOnly);

        if (rows.Count == 0)
        {
            // 翻过头排在最前,与上面那一路同序:它与「没有」的区别最大,而一次翻页
            // 被读成一次否定正是本项目点名清过的形状。
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, placeholdersOnly
                    ? $"{Tally.Complete(total).Render("keyed translation")} in this snapshot are placeholders."
                    : $"this snapshot holds {Tally.Complete(total).Render("keyed translation")} in all.");
                return 1;
            }

            // 整层非空(上面已经判过),枚举又空 —— 只剩一种成因:一条占位都没有。
            // 这是一个**完整的肯定回答**,不是一次落空的查找,而按行数它照样走 exit 1
            // (R12 约定),所以句子必须自己把这件事说清:读退出码的脚本会把它读成失败。
            ctx.Report.Notice(NoticeKind.Filter,
                "No keyed translation in this snapshot is a placeholder: all " +
                $"{Tally.Complete(layerTotal).Render("keyed translation")} carry a real translation, so nothing " +
                "in this layer is left for the game to fall back to English on. That is a complete answer about " +
                "coverage rather than a lookup that came up empty — the exit code is still non-zero because no " +
                "rows were printed. Coverage of a def's own label and description is a different layer: those " +
                "are injected through DefInjected, and 'rimsearcher get <defName>' shows them.");
            return 1;
        }

        Emit(ctx, rows, "keyed translation", offset, total, placeholdersOnly);
        return 0;
    }

    /// <summary>
    /// 表 + 三件分页事 + 两条说破。三条路(精确 key / 文案搜 / 整层枚举)共用一份 ——
    /// 分开写的话,「占位是什么意思」这句只会长在想起来的那两条上。
    /// </summary>
    private static void Emit(CommandContext ctx, IReadOnlyList<Storage.KeyedRow> shown, string noun,
                             int offset, int total, bool placeholdersOnly)
    {
        ctx.Report.Table("keys", ["key", "translated", "original", "origin", "placeholder", "mod", "source"],
            shown.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["key"] = r.Key,
                ["translated"] = r.Translated,
                ["original"] = r.Original,
                // 「in effect」/「on disk」而不是 runtime/harvested:后者说的是数据怎么来的,
                // 前者说的是读的人真正要判的那件事 —— 这一句游戏会不会显示。
                ["origin"] = r.Origin == TranslationOrigin.Runtime ? "in effect" : "on disk",
                // 恒在,不按有无条件出现。条件出现的列会让「量过了,不是占位」与
                // 「这一格根本没量」印出来一模一样(R6 三次复发的那个形状)。
                ["placeholder"] = r.Placeholder,
                ["mod"] = r.SourceMod,
                ["source"] = r.SourceLine > 0 ? $"{r.SourceFile}:{r.SourceLine}" : r.SourceFile,
            }).ToList());

        ctx.Report.PageNotice(noun, shown.Count, offset, total);

        // origin 那一列印着「in effect」,读的人自然读出「另有 on disk 的没印出来」。
        // 这份库要是没量过磁盘,那个对照根本不存在 —— 说破它。
        DiskLayer.NoteIfUnmeasured(ctx);

        // 占位译文实际显示的是英文 —— 表里它与真译文同形,所以点名说破。
        //
        // 主语是固定单数(the language file),计数进宾语。NounRegistry 管名词的复数,
        // **不管主谓一致**,所以「1 keyed translation … are」这种错加不了登记项来修,
        // 只能靠句子结构避开 —— 这一课 04 记过两次,写这一节时又踩了三次。
        var placeholders = shown.Count(r => r.Placeholder);
        if (placeholders > 0)
            ctx.Report.Notice(NoticeKind.Boundary, placeholdersOnly
                // --placeholders 在场时表里每一行都是占位,再报一遍数就是同一个数占两行
                // (上面那句分页计数已经报过了)—— 这句要说的只剩「占位是什么意思」。
                ? "Placeholder means the language file declares the key without a translation, so the game " +
                  "displays the English text instead of what the translated column shows. Every row above is " +
                  "one of those, which is what --placeholders selects."
                : "Placeholder means the language file declares the key without a translation, so the game " +
                  "displays the English text instead of what the translated column shows: that is the case for " +
                  // 这里数的是**表里的行**,所以名词固定是 keyed translation,不跟着上面那句的
                  // 「按 key 还是按文案命中」变 —— 一个 key 可以有好几行,其中几行是占位。
                  // 顺带:名词闸是扫源码里的字面量的,交给变量它就看不见了(实测红过一次)。
                  $"{Tally.Complete(placeholders).Render("keyed translation")} above.");
    }
}
