using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 折叠行：「还有多少没印出来，以及怎么拿到」。
//
// 全服一套文法 `<缩进>... +N more [of M ]<名词> (<下一步>)`，由 GrammarRules 规则一与规则九
// 常驻把守。这些成员此前寄居在 ScopeAndLimitArgs 里——那是 scope / limit 两个**参数**的家，折叠行
// 与它没有关系，只是因为六个工具都 using 着它才被放在那儿。
//
// 唯一仍指向参数层的东西是 ScopeAndLimitArgs.HardLimit 与 ResultLimit：折叠行的三个分支要靠
// 「调用方要过 'all' 没有」和「顶到服务端上限没有」才分得开，那两件事本来就是参数语义。
public static class Fold
{
    // 「藏起来的是哪一批」这个槽的居民。
    //
    // 三条共通的判据：它们描述的是**被折叠那批**，不是列出来那批。折叠行只说「还剩多少、
    // 怎么拿」时，读者的默认读法是「列出来的是个样本」——而三种截断留下的都是有系统偏向的
    // 前缀（分数最高的那批 / 最浅的那批），样本读法一次都不成立。
    //
    // 词收在这里一处，条件留在各自知道事实的地方（locate 知道自己有没有被断层收口砍过，
    // inheritors 知道切片有没有触到最深层）——同 §3 判据六：共用名单，不共用判断。
    public static class HiddenBatch
    {
        // 断层收口砍掉的是「相对首条掉了 40 分以上」的那批。
        public const string LowerRelevance = "lower relevance";

        // 切片已经触到最深层：藏起来的是同深度里的其余。
        public const string ShallowestFirst = "shallowest first";

        // 切片没触到最深层：藏起来的里面有更深的。与上一条是**两件不同的事实**，不是一件事的
        // 两个精度，故不叠着说。
        //
        // 「below depth N」的方向要靠读者自己定：depth 数值越大越深，而「below」在版面上指的是
        // 「下面那些行」。第十三轮盲测里被测方当场读反了一次。故写 deeper than。
        public static string NothingDeeperThan(int depth)
            => $"nothing deeper than depth {depth} is listed";
    }

    // 折叠行。断层收口时说明被折叠的是低匹配度结果，免得读者以为还有同等相关的东西没显示。
    //
    // 下一步的建议必须按「是谁砍掉的」分开给，三种情况的正确动作互不相同：
    //   - limit 砍的，且还没要过 'all'  → 'all' 真的能展开，劝它；
    //   - limit 砍的，且已经顶到硬上限  → 再要一次 'all' 是原地重试，只能劝收窄查询；
    //   - 只有断层收口砍的              → 那部分调多大的 limit 都拿不回来（见 ScopeFilter.Apply
    //     的 effectiveLimit = Min(limit, cutoff)）。原先这里一律劝 'all'，调用方照做后
    //     一条也没多出来，还会把「+N more」读成服务端在敷衍。
    public static string? Line<T>(
        ScopedResult<T> result, CountedNoun noun, string indent = "  ", ResultLimit? limit = null,
        string? capAction = null, string? hiddenBatch = null)
        => Line(
            result.HiddenCount, result.Items.Count,
            // 调用方没自己说「藏的是哪一批」时，由结果自己答：断层收口砍过就是低匹配度那批。
            hiddenBatch ?? (result.TruncatedByScoreGap ? HiddenBatch.LowerRelevance : null),
            result.TruncatedByLimit, noun, indent, limit, capAction);

    // 显式计数的重载。分段显示的场景（locate 的 Members 按 method/property/field 分组）里，
    // 真正被藏起来的条数由「ScopeFilter 的 limit」和「每组的显示配额」两层共同决定，
    // ScopedResult.HiddenCount 只看得见第一层。
    public static string? Line(
        int hiddenCount,
        int shownCount,
        // 「藏起来的是哪一批」。null = 这一形说不出（也就不硬说）。取值见 HiddenBatch。
        // 此前这里是个 bool，只答得出「是不是断层收口」——于是 inheritors 那条同样有系统偏向的
        // 截断（留下的恒是最浅的那批）在折叠行上无处可说，只能挂在表头「这次列了几个」那一格
        // 后面，与它挤成一句。
        string? hiddenBatch,
        bool truncatedByLimit,
        CountedNoun noun,
        string indent = "  ",
        ResultLimit? limit = null,
        // 顶到硬上限时「怎么才能看到剩下的」因工具而异。inheritors 没有 offset、也没有任何
        // 参数能抬这个顶，唯一出路是换一个子树根重跑——而「narrow the query」在一棵继承树上
        // 根本不是个可执行动作（查询词就是那个类名，没得再窄）。盲测里调用方为此拿 9 次 trace
        // 盲探，其中两次纯白跑。留给调用方自己填的那半句，各工具自己说。
        string? capAction = null)
    {
        if (hiddenCount <= 0) return null;

        // Unlimited 只说明调用方要过 'all'，不等于真的产出了 HardLimit 条——
        // 五条结果的查询也会走进这里，原先照样宣布「server cap 200 reached」。
        var capReached = limit?.Unlimited == true && shownCount >= ScopeAndLimitArgs.HardLimit;

        var hint = truncatedByLimit
            ? capReached
                ? $"server cap {ScopeAndLimitArgs.HardLimit} reached, {capAction ?? "narrow the query"}"
                : limit?.Unlimited == true
                    ? "narrow the query to see the rest"
                    // 'all' 也只到硬上限。藏起来的比上限还多时，`to expand` 会被读成「照做就拿全了」
                    // ——`... +767 more C# types (pass limit:'all' to expand)` 照做仍差 567 条，
                    // 而调用方没有任何线索能察觉。同一件事 trace usages 那边是说清了的
                    // （`raise the cap to 200`），这里跟上。
                    : shownCount + hiddenCount > ScopeAndLimitArgs.HardLimit
                        ? $"pass limit:'all' for the first {ScopeAndLimitArgs.HardLimit}; the rest needs a narrower query"
                        : "pass limit:'all' to expand"
            // 断层收口砍掉的是「相对首条掉了 40 分以上」的结果，要够到它们只能让首条不再那么
            // 突出——换个更宽泛的词，或改用 search_regex。原先写的是 refine（收窄），方向正好反了：
            // 照做只会把这些结果推得更远。
            //
            // 后来写成 "broaden or reword" 仍不够：reword 不带方向，而调用方手上最顺的
            // 「换个说法」恰恰是**加限定**（盲测里加了 `type:` 前缀，那是收窄），照做后被折叠的
            // 那批更够不着，于是把一条查得到的结果写成了 unanswerable（实测
            // `locate type:Shield scope:'all' limit:'all'` 一次就列出 DrawNewCompShieldPatch）。
            // 方向必须写死在句子里。
            : "use a shorter, less specific query; folding is relative to the top score, so narrowing "
              + "never brings these back and limit does not expand them";

        // 名词槽不留空。locate 的 Members 段是分种类子组印的，折叠行又与组内条目同缩进，
        // 于是 `... +1938 more` 紧跟在 Properties 组末尾时读起来像「还有 1938 个 property」，
        // 而它数的是三类之和。全服文法（README「低 Token 消耗」一节）本就要求这个槽有名词。
        var batch = hiddenBatch is { Length: > 0 } b ? $"{b}, " : string.Empty;
        return $"{indent}... +{hiddenCount} more {noun.For(hiddenCount)} ({batch}{hint})";
    }

    // 下一步由调用方**显式给出**的那一形。
    //
    // 上面那两个重载的三分支全建立在「是谁砍掉的」上，而那件事只有 ScopedResult 答得出来。
    // 分页与定长上限这两类折叠不经过 ScopeFilter：list_directory 的下一步是 `pass offset=N`，
    // read_code 的是 `pass startLine=N`，两者都与 limit 的三分支无关——limit:'all' 在这里
    // 不是「展开」而是「换一页」，劝 narrow the query 更是无从执行。
    //
    // 故这一形只共用**文法**，不共用建议。此前三处调用方各手拼一条模仿它的字符串，其中
    // list_directory 那处上一行的注释还写着「文法与全服统一的截断脚注一致……见 Fold.Line」
    // ——契约靠模仿加测试事后拦截，而不是按构造成立。
    //
    // 名词的单复数也在这里定，调用方一处也不许自己拼——它们此前全都不做这件事
    // （`+1 more entries` / `+1 more lines` / `+1 more types`），而 list_directory 同一份输出的
    // 表头是走构词的（`12 entries`，那处注释还专门写着「不写 `1 entries`」）：同一屏上同一个
    // 名词数同一批东西、四行之隔两种写法。其余 19 种折叠行全走构词，故那三处是例外而不是
    // 另一种规矩。
    //
    // 实现在 Core 的 OutputText.FoldLine，这里只是入口。搬下去是因为**第六个**调用方在 Core 里：
    // 成员大纲的文本整段由 RoslynHelper 拼（inspect 只是把它原样接进返回），它够不到这个
    // 命名空间，于是那一条折叠行一直是全服唯一手拼的一条。而这一形不含任何成因判断——
    // 下一步整句由调用方给——正好落在 OutputText 那条既有边界的可下沉一侧（构词因同一个理由
    // 早已在 Core，现在住在 CountedNoun）。上面三个成员下不去：它们要读 ScopeAndLimitArgs.HardLimit
    // 与 ResultLimit。
    public static string? Explicit(
        int hiddenCount, CountedNoun noun, string nextStep, int? total = null, string indent = "  ")
        => OutputText.FoldLine(hiddenCount, noun, nextStep, total, indent);

    // 每文件预览的折叠行。search_regex 与 trace usages 共用，且它是全语料里出现最频的一条
    // 折叠行（92/181），此前却是唯一两个槽都空着的一条：`... +77 more in this file`。
    // 名词按 ScanReport.FoundCount 同一条判据补成 matching lines。
    //
    // 增量之外还要给总数。只印 `+19 more` 时，读者要拿它和上面印出来的行数相加才得到 22，
    // 而「上面印了几行」是常数 3 这条规则**并不总成立**：扫描停在预览配额上时，最后一个文件
    // 只印了 1–2 行也带这条折叠（本语料的 Alert_Exhaustion.cs 印 2 行、折叠 2 条）。于是那条
    // 被诱导出来的「加 3」心算在一部分文件上给出错数，而这一行自己看不出落在哪种情况。
    // 沿用 R33 的 `N of M` 读法：**这一个计数惯用法**里的 of 表示没给全（不是「凡 of 皆截断」
    // ——一半的 `of` 是普通介词，见 GrammarRules 规则三）。
    public static string PerFile(int hiddenCount, int totalInFile, string indent = "  ")
        => $"{indent}... +{hiddenCount} more of {CountedNoun.MatchingLines.Quantity(totalInFile)} in this file";

    // 「怎么才能拿到更多」这半句不逐文件印，整份返回里说一次（同 §R19：逐行一模一样的东西
    // 上提到表头/脚注）。且只在这次真有文件被折叠时才印——没有折叠就没有这条。
    //
    // 其余 19 种折叠行都以 `(pass limit:'all' …)` 之类收尾，于是「留空」会被读成「这条漏印了
    // 参数名」。而这里每文件预览条数是常数、没有任何参数放得宽，这件事推不出来，必须明说。
    public static string PerFilePreviewCap(int previewsPerFile)
        => $"... previews are capped at {previewsPerFile} lines per file and no parameter widens that; "
           + "use read_code on a file to see the rest";
}
