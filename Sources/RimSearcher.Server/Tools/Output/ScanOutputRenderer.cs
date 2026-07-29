using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 把 ScanOutput 印成文本。全服文法在这里只存在一份。
//
// 这个类型存在的意义不是「集中拼字符串」，是把此前散在各工具 ExecuteAsync 里的**判据**收进
// 一处，让它们不再能各自漏一半。逐条对应指导文档 §4 的已知耦合：
//
//   1. 有文件没扫全 ⇒ 表头改口 `at least` + 就地挂成因引用 + 尾注点名文件。三处都从
//      Completeness 派生（Header / Footnotes 各读一次同一个值），漏不掉。
//   2. 扫描停在配额 ⇒ 表头换 `first N preview lines` 形，**且**最后一个文件那条不足 3 行的
//      折叠不算进 preview-cap 脚注的触发条件。后者由 BlockText 自己按「shown 是否真撞上限」
//      判，不由工具置旗——原先那面 anyFileFolded 旗是工具在 string.Join 的惰性枚举里置的，
//      读它的时机对不对全靠注释提醒。
//   3. 文件块被截断 ⇒ 段头印全集来源构成，**且只在扫描没停时**（停了的话剩下的候选文件根本
//      没打开过，任何构成陈述都是编的）。这条判据整个在 Labels() 里，工具看不见也就改不坏。
//
// 尾注的**排序**同样只存在于这里一处。此前它只活在各 ExecuteAsync 的代码顺序里，没有任何一处
// 把它写成规则；见 Footnotes()。
public static class ScanOutputRenderer
{
    public static string Render(ScanOutput output)
        => output.Blocks.Count == 0
            ? Empty(output)
            : Matches(output);

    // 零命中形。措辞由工具给（它要回显本工具独有的参数），但**挂什么、什么顺序**在这里。
    //
    // 这一形不挂 HardScopeFilter：它与 RetryWider 是同一件事的两种说法，前者给有结果的返回、
    // 后者给零命中的。两句并排时同一个「改用 scope:'all'」会被两套措辞各说一遍，读者以为是
    // 两条不同的提示。也不挂 conditional 脚注——零命中时一个行内标记都没打，那条脚注会是
    // 一段兑换不到东西的说明。
    private static string Empty(ScanOutput output)
        => output.EmptyLine
           + ScopeNotices.RetryWider(output.Scope)
           + output.ScopeNotice;

    private static string Matches(ScanOutput output)
    {
        var listed = output.FileListCap is { } cap
            ? output.Blocks.Take(cap).ToList()
            : output.Blocks.ToList();

        // 消歧只看**列出来的**那些文件：没印出来的文件不参与重名判断，否则会为一个读者看不见的
        // 冲突把名字加长。判据与 R1/R8/R20 同源——推得出来就不印。
        var displayNames = FileNames.Disambiguate(listed.Select(b => b.Path));
        var labels = Labels(output, listed);

        // 每文件折叠是否真撞上了「每文件 N 行」这个上限。preview-cap 脚注只为这一种成因存在，
        // 故由块自己回答，不由工具置旗。
        var anyBlockHitTheCap = false;
        var blocks = listed.Select(block =>
        {
            var text = BlockText(output, block, displayNames[block.Path], labels, out var hitTheCap);
            anyBlockHitTheCap |= hitTheCap;
            return text;
        }).ToList();

        var body = $"{output.Subject} ({Headline(output)}){labels.Header}:\n\n"
                   + string.Join("\n\n", blocks);

        return body
               + FileListOverflow(output)
               + Footnotes(output, anyBlockHitTheCap);
    }

    // 表头括号里那一段。三态由两个事实决定，工具没法只改一半：
    //   扫描停了            → 换量纲，数的是**印出来的**预览行（确定值，不该也不能改口）；
    //   有文件没扫全        → 总数降格成下界，且记号旁边就地给出成因引用；
    //   两者都没有          → 确定的命中总数。
    private static string Headline(ScanOutput output)
    {
        var echoes = string.Concat(output.ParameterEchoes.Select(e => $", {e}"));
        var where = $"in scope '{output.Scope.Expression}'{echoes}";

        if (output.ScanStopped)
            return $"first {PreviewLinesCollected(output)} preview lines {where}";

        var incomplete = output.Completeness.AnyIncomplete;
        return $"{ScanReport.FoundCount(output.TotalMatchingLines, incomplete)} {where}"
               + ScanReport.LowerBoundReason(incomplete);
    }

    // 本次收下的预览行总数，含没列出来的那些文件。表头的 `first N preview lines` 与
    // scan-stopped 那句里的 N 是同一个量，故只算一次。
    private static int PreviewLinesCollected(ScanOutput output)
        => output.Blocks.Sum(b => Math.Min(b.Previews.Count, output.PreviewCapPerFile));

    // 来源标签印在哪儿、段头那个方括号描述哪个量。
    //
    // 段头的方括号恒描述**这一段的总数**，而不是列出来的那几行：第九轮盲测里调用方据 50 个文件的
    // 来源标签断言「97 个文件清一色落在那 11 个源内」。故文件块被截断时段头改印全集构成——
    // 但只在扫描没停时：停了的话 Blocks 只是「恰好扫到的那批」，剩下的候选文件根本没打开过，
    // 它们的来源服务端确实不知道。
    private static SourceLabeling Labels(ScanOutput output, IReadOnlyList<ScanFileBlock> listed)
    {
        var scopeTotals = !output.ScanStopped
                          && output.FileListCap is { } cap
                          && output.Blocks.Count > cap
                          && output.Scope.ShowLabels
            ? output.Blocks
                .Select(b => output.Scope.SourceNameOf(b.Path))
                .Where(name => !string.IsNullOrEmpty(name))
                .GroupBy(name => name!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count()))
                .ToList()
            : null;

        return SourceLabeling.Of(listed.Select(b => SourceNameOf(output, b)), scopeTotals);
    }

    private static string? SourceNameOf(ScanOutput output, ScanFileBlock block)
        => output.Scope.ShowLabels ? output.Scope.SourceNameOf(block.Path) : null;

    private static string BlockText(
        ScanOutput output, ScanFileBlock block, string displayName, SourceLabeling labels,
        out bool hitTheCap)
    {
        var shown = Math.Min(block.Previews.Count, output.PreviewCapPerFile);
        var previews = block.Previews.Take(shown).Select(p => $"  L{p.Line}: {p.Preview}");

        // 脚注说的是「每文件 N 行上限」，故只有真撞上那个上限的折叠才算数。扫描停在预览配额上时，
        // 最后一个文件的 shown 会不足 N——它的折叠成因是配额耗尽（scan-stopped 那句已经说了），
        // 把它也算进来会让脚注对这个文件给出错误归因：读者会以为「这个文件最多只能看到 N 行」，
        // 而其实放宽 limit 就能多印一行。
        hitTheCap = block.TotalInFile > shown && shown >= output.PreviewCapPerFile;

        var fold = block.TotalInFile > shown
            ? "\n" + Fold.PerFile(block.TotalInFile - shown, block.TotalInFile)
            : string.Empty;

        // 条件标记排在来源标签**之前**：行尾的 `[x]` 是全服的来源标签位（见 SourceLabeling 与
        // 文法闸规则六），别的记号挤进去会让「同源就提到表头」那条判据在这一行上读不出来。
        return $"`{displayName}`{output.Conditional.Tag(block.Path)}{labels.Row(SourceNameOf(output, block))}\n"
               + $"{string.Join("\n", previews)}{fold}";
    }

    // 文件数超限的两形。文法不同且各自都对：扫描停了就不知道还剩多少（那些文件根本没打开过），
    // 只有扫描没停时数得出准数，能用全服统一的 `... +N more`。
    //
    // 两形都要说「本次列了几个」。原先只有 scan-stopped 那一形写了 `only the first 50 files are
    // listed`，另一形不写，读者只能做 97−47 的减法——第九轮盲测里那个减法结出了错误推理。
    private static string FileListOverflow(ScanOutput output)
    {
        // 没有文件数上限时这一格恒为 0：藏起来的文件一个也没有，正文里的文件块就是全部
        // 扫到的文件（trace usages 走的是这一支，它的配额是全局预览行数）。
        var hidden = output.FileListCap is { } cap ? output.Blocks.Count - cap : 0;

        if (output.ScanStopped)
        {
            // 截断时 Blocks 只是「已扫到的那批」里的文件，不是命中文件总数——扫描早已在命中上限处
            // 停下。原先无条件称其为 "matching files"，那个数比真实值小一到两个数量级。
            var extra = hidden > 0
                ? new[]
                {
                    $"only the first {output.FileListCap} files are listed, and the {output.Blocks.Count} "
                    + "distinct files seen so far are not the total number of matching files"
                }
                : null;

            return "\n\n" + ScanReport.ScanStopped(PreviewLinesCollected(output), output.Limit, extra);
        }

        if (hidden <= 0) return string.Empty;

        // 表头数的是**行**、正文分的是**文件**、这一行数的是**没列出来的文件**——三个口径三个
        // 名词。扫描没被截断时 Blocks.Count 就是命中文件总数（确定值），直接给出来，读者不必去
        // 数正文里的文件块。
        //
        // 走 Fold.Explicit：文件数上限是个定长常数，没有参数放得宽它，下一步是收窄 pattern
        // 或 scope，与 limit 的三分支无关。这一行此前是 renderer 里唯一还在手拼共用文法的。
        return "\n\n" + Fold.Explicit(
            hidden, "matching files",
            $"{output.FileListCap} listed; narrow the pattern or the scope",
            total: output.Blocks.Count, indent: string.Empty);
    }

    // 尾注的排序。此前它只活在各 ExecuteAsync 的代码顺序里，没有任何一处把它写成规则——而两个
    // 扫盘工具恰好同序纯属它们是照着彼此写的。规则是：
    //
    //   1. 本段自己的量（preview-cap：印出来的行为什么只有这么多）
    //   2. 整份结果的完整性（not-scanned-in-full：命中集为什么可能不全）
    //   3. 行内记号的成因（conditional：那些方括号怎么兑换）
    //   4. scope 相关（hard-scope：外面有没有 / scopeNotice：范围与你以为的不同）
    //
    // 由近及远：越靠前的越只解释紧邻的那几行，越靠后的越是对整份返回的限定。scan-stopped 与
    // 文件数折叠不在这张表里——它们是正文的收尾（`... +N more`），由 FileListOverflow 紧贴正文印。
    private static string Footnotes(ScanOutput output, bool anyBlockHitTheCap)
    {
        var text = string.Empty;

        if (anyBlockHitTheCap)
            text += "\n\n" + Fold.PerFilePreviewCap(output.PreviewCapPerFile);

        // 「没有尾注即完整命中集」是 search_regex 写在 Description 里的契约，被跳过/被弃扫的
        // 文件必须破这个契约。表头此刻已经改口成 `at least`（同出于 Completeness）。
        if (output.Completeness.AnyIncomplete)
            text += "\n\n" + ScanReport.NotScannedInFull(output.Completeness.Reasons());

        // 条件目录的成因整份说一次（行内只放键，见 ConditionalReport）
        text += output.Conditional.Render() ?? string.Empty;

        // 这两个工具是硬 scope 过滤、没有逐源的越界计数，而那条脚注的**缺席**会被读成
        // 「scope 外没有」。故把缺席的含义明说一次。
        text += ScopeNotices.HardScopeFilter(output.Scope);
        text += output.ScopeNotice;

        return text;
    }
}
