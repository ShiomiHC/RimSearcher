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
                Help = "A translation key, or a phrase from the interface in any language the snapshot has.",
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
                       "translation-coverage question wants.",
            },
        ],
        Examples =
        [
            "rimsearcher keyed CannotUseNoPower",
            "rimsearcher keyed 没有电力",
            "rimsearcher keyed Command --limit all",
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
        var query = ctx.Args.Positional(0)!;
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
                "property of the snapshot, not an answer about '" + query + "'. Two exports look like this and " +
                "this line cannot tell them apart: one written before this layer was measured at all, and one " +
                "written from a game whose language data was not loaded. The fix is the same either way — export " +
                "again; 'rimsearcher snapshot status' names the snapshot in use.");
            return 1;
        }

        // 精确 key 命中优先。key 与界面文案不会同形,所以这一步不会抢走「按文案搜」的意图。
        var exact = ctx.Db.KeyedByKey(query);
        var rows = exact;
        var matchedOn = "key";
        var ftsTotal = exact.Count;

        if (exact.Count == 0)
        {
            var (hits, hitTotal) = ctx.Db.KeyedSearch(query, limit.IsAll ? Limits.MaxLimit : limit.Effective, offset);
            rows = hits;
            ftsTotal = hitTotal;
            matchedOn = "text";
        }

        if (placeholdersOnly)
            rows = rows.Where(r => r.Placeholder).ToList();

        if (rows.Count == 0)
        {
            // 四种互斥成因。合成一句「没找到」会让前三种被读成第四种,而第四种
            // (这个环境里真没有)是最强的那个结论。
            if (placeholdersOnly && ftsTotal > 0)
            {
                // 主语是 --placeholders(固定单数),计数进从句 —— 见下面那条注释。
                ctx.Report.Notice(NoticeKind.Filter,
                    $"--placeholders filtered out every match: {Tally.Complete(ftsTotal).Render("key")} " +
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

        // 名词跟着「数的是什么」变,不是跟着命令名变:按 key 精确命中时数的是这个 key 的
        // 几条来源(in effect 一条,on disk 可以另有几条),按文案搜时数的是命中了几个 key。
        // 两者混用一个词,读的人就会把「一个 key 三条来源」读成「三个 key」(R7)。
        ctx.Report.CountNotice(Tally.Of(shown.Count, placeholdersOnly ? rows.Count : ftsTotal),
            matchedOn == "key" ? "keyed translation" : "key",
            "pass --limit all for the rest.");

        // 占位译文实际显示的是英文 —— 表里它与真译文同形,所以点名说破。
        //
        // 主语是固定单数(the language file),计数进宾语。NounRegistry 管名词的复数,
        // **不管主谓一致**,所以「1 keyed translation … are」这种错加不了登记项来修,
        // 只能靠句子结构避开 —— 这一课 04 记过两次,写这一节时又踩了三次。
        var placeholders = shown.Count(r => r.Placeholder);
        if (placeholders > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "Placeholder means the language file declares the key without a translation, so the game " +
                "displays the English text instead of what the translated column shows: that is the case for " +
                // 这里数的是**表里的行**,所以名词固定是 keyed translation,不跟着上面那句的
                // 「按 key 还是按文案命中」变 —— 一个 key 可以有好几行,其中几行是占位。
                // 顺带:名词闸是扫源码里的字面量的,交给变量它就看不见了(实测红过一次)。
                $"{Tally.Complete(placeholders).Render("keyed translation")} above.");

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
}
