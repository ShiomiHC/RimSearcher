namespace RimSearcher.Server.Tools.Output;

// 正文与脚注之间、脚注与脚注之间，恰好一个空行。
//
// 这条间距此前由**每条脚注自己带的前缀**决定，而各处带的不一样：`ConditionalReport.Render()`
// 带 `"\n\n"`，`ScopeReport.Render()` 带 `"\n"`，工具自备的四条带 `"\n\n"`，
// `Fold.PerFilePreviewCap` / `ScanReport.NotScannedInFull` 一个都不带、由调用处补。于是「一个
// 空行」成立与否取决于**正文末尾有没有换行**，而那件事各 renderer 各不相同：
//
//   - 两个扫盘工具的正文以文件块收尾、不带尾换行  → 各条都恰好一个空行（碰巧对）；
//   - locate / inheritors 的正文逐行 AppendLine    → conditional、短词、缺文件三条各多一个空行，
//     而同一份返回里越界那条又是对的（它只带一个 `\n`）；
//   - locate 的零命中路径以一句话收尾、不带尾换行  → 越界那条**一个空行都没有**，直接贴在
//     第一句下面。
//
// 同一条脚注在同一屏、乃至同一份返回里有三种间距。空行是版面上唯一的分段信号，读者会去找那个
// 多出来（或少掉）的空行意味着什么——而它什么也不意味，只是三个 renderer 的正文尾巴不一样。
//
// 故间距不再由脚注自己带：谁都可以带、也可以不带，进这里一律削平，由这一处补。空脚注（null /
// 全空白）直接不占位——「这条不在场」与「这条在场但没内容」在版面上必须同形。
public static class FootnoteBlock
{
    public static string After(string body, params string?[] notes)
    {
        var kept = notes
            .Where(n => !string.IsNullOrWhiteSpace(n))
            // 只削首尾的换行：脚注内部本来就可能分段（前缀提示那条会连着列一遍过滤器清单）。
            .Select(n => n!.Trim('\n'))
            .ToList();

        // 正文的尾换行也削掉。没有脚注时它由 ToolResult 的 TrimEnd 收口，削与不削同果；
        // 有脚注时它正是三种间距的来源。
        var trimmed = body.TrimEnd('\n');

        return kept.Count == 0 ? trimmed : $"{trimmed}\n\n{string.Join("\n\n", kept)}";
    }
}
