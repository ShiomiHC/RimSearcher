using System.Text;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 把 LocateOutput 印成文本。
//
// 两条路径的**脚注排序**是这个类型存在的主要理由：它们此前只活在 ExecuteAsync 的代码顺序里，
// 中间还隔着一个 early return，于是「有结果时挂七条、零命中时挂五条、其中三条只挂一边」这件事
// 没有任何一处写得下来。见 Footnotes() 与 Empty()。
//
// 其余三条耦合逐条对应 LocateOutput 头部的清单：Tally 一格 ⇔ 一段（Header 里 Select 出来）、
// 段头构成只在截断时印（SectionLabels）、折叠行按总量计（SectionText）。
public static class LocateRenderer
{
    public static string Render(LocateOutput output)
        => output.Sections.Count == 0
            ? Empty(output)
            : Body(output);

    private static string Body(LocateOutput output)
    {
        var sb = new StringBuilder();
        foreach (var section in output.Sections)
            sb.Append(SectionText(section, output.Limit));

        return FootnoteBlock.After(Header(output) + "\n" + sb, Footnotes(output));
    }

    // 表头。Tally 的格子由各段自己算，故「段在、格子不在」不可能发生。
    private static string Header(LocateOutput output)
    {
        var header = new StringBuilder($"## '{output.Query}'");

        header.Append(" — ");
        header.Append(string.Join(", ", output.Sections.Select(TallyCell)));

        // 全域时不打 scope 标注：那时它对每一次调用都成立，是常亮。
        if (!output.Scope.IncludesEverything)
            header.Append($" _(scope: {output.Scope.Expression})_");

        return header.ToString();
    }

    private static string TallyCell(LocateSection section)
        => Tally.Cell(
            section.Shown, section.Total, section.Noun,
            section.TotalIsLowerBound, section.FullScoreCount);

    // 一段：段头（含来源构成）+ 行 + 折叠行。
    private static string SectionText(LocateSection section, ResultLimit limit)
    {
        var labels = SectionLabels(section);

        var sb = new StringBuilder($"\n**{section.Name}**{labels.Header}:\n");
        foreach (var row in section.Rows)
            sb.Append(row.Text).Append(labels.Row(row.SourceName)).Append('\n');

        // 折叠行与 Tally 的 `of` 同判据（`Total > Shown`）：Fold.Line 在 hidden <= 0 时返回 null，
        // 而 hidden 就是 `Total - Shown`。两处此前各算一遍，且 Files 段的两支算法还不同。
        if (section.Foldable)
        {
            var fold = Fold.Line(
                section.Total - section.Shown, section.Shown,
                section.TruncatedByScoreGap ? Fold.HiddenBatch.LowerRelevance : null,
                section.TruncatedByLimit,
                section.Noun, indent: "  ", limit: limit);
            if (fold != null) sb.Append(fold).Append('\n');
        }

        return sb.ToString();
    }

    // 段头的方括号按**全集**判：它描述的是这一段的总数，不是印出来的那几行（见 SourceLabeling）。
    // 未截断时不传构成——那时行本身就是构成，再印一遍是 R19 删掉的噪音。
    //
    // 子组标题不参与判定：它们的 SourceName 恒为 null，而 SourceLabeling.Of 见到一个空名字就
    // 认定「这批本来就不该印标签」，于是 Members 段会整段丢掉标签。
    private static SourceLabeling SectionLabels(LocateSection section)
        => SourceLabeling.Of(
            section.Rows.Where(r => !r.IsGroupHeader).Select(r => r.SourceName),
            section.Total > section.Shown ? section.SourcesInScope : null);

    // 有结果时的脚注排序。与 ScanOutputRenderer 同一条规则（由近及远），这一形的四档是：
    //   1. 行内记号的成因（conditional）——五段的标记都在它上面，中间隔着别的脚注就又成了
    //      「记号与成因之间没有可指认的连接」那一形；
    //   2. scope 外还有什么（越界报告）；
    //   3. 本次查询的能力边界（缺文件 → 某段总数是下界 → 短词没被查）；
    //   4. 参数被怎么理解了（scopeNotice → 前缀）。
    //
    // MissingFile 只挂在这一路：零命中时整份返回的第一句就是 "No results for 'X'"，
    // 再说一遍「没有叫 X 的文件」是同一件事说两遍。
    //
    // 下界成因不是一个整份字段而是逐段问出来的：Tally 里哪一格改口成 `at least`，成因就跟着
    // 那一格来（见 LocateSection.TotalIsLowerBound）。两者分居两个字段时，改口而不给成因是个
    // 静默分支。
    private static string?[] Footnotes(LocateOutput output) =>
    [
        output.Conditional.Render(),
        output.OutOfScope.Render(output.Scope),
        output.MissingFile,
        .. output.Sections.Where(s => s.TotalIsLowerBound).Select(s => s.LowerBoundNotice),
        output.ShortTokens,
        output.ScopeNotice,
        output.PrefixNotice,
    ];

    // 零命中形。
    //
    // 零命中是一个正常结果，不是调用失败——isError 留给「工具没能执行」。同一个服务器里
    // trace 查不到子类、search_regex 零命中都是 false，locate 独自为 true 只会让调用方两套判据。
    // （这条由工具决定，此处只管文本。）
    private static string Empty(LocateOutput output)
    {
        var footer = output.OutOfScope.Render(output.Scope);

        // 越界脚注在场时 RetryWider 让位：两句并排会把同一个「改用 scope:'all'」用两套措辞
        // 各说一遍，读者以为是两条不同的提示。它是**同一句话的续写**（同行、空格分隔），
        // 不是脚注，故与第一句一起作正文交给 FootnoteBlock。
        var opening = output.EmptyLine + ScopeNotices.RetryWider(output.Scope, footer != null);

        return FootnoteBlock.After(
            opening,
            footer,

            // 短词那句在这一路也要说，且槽位与 Footnotes() 同一档（「本次查询的能力边界」在
            // 「参数被怎么理解了」之前）。此前它只挂在有结果那一路——而零命中恰恰是它最要紧的
            // 一形：有结果时读者至少还能看到别的段落，零命中时整份返回只有「No results」一句，
            // 最自然的读法就是「索引里没有」，而真相是那个短词一次都没被查过。
            //
            // 同一档的另外两条不挂，各有理由：MissingFile 与第一句说的是同一件事（`No results
            // for 'X'` 已经涵盖「没有叫 X 的文件」）；下界成因要 Tally 里有格子改口，而这一路
            // 没有段。
            output.ShortTokens,

            output.ScopeNotice,
            output.PrefixNotice,

            // 过滤器清单只列一次。PrefixNotice 在「前缀没被识别」时已经列过一遍（那正是最该
            // 看到它的场合），这里再列就是同一行字紧挨着说两遍。
            output.FilterListAlreadyShown
                ? "Try: partial names, or search_regex for patterns."
                : "Try: partial names, query filters (type:, method:, field:, def:), "
                  + "or search_regex for patterns.");
    }
}
