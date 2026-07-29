namespace RimSearcher.Core;

// 输出文法里 Core 这一侧也要用到的共享原语。放在这儿而不是 Server 的 Tools/Output/，
// 是因为 RoslynHelper 的大纲折叠行也要用它，而 Core 引不到 Server。
//
// 边界是「文法 vs 成因」而不是「呈现 vs 数据」：能落在这里的只有**不需要知道任何成因**
// 就能拼出来的句子（一行折叠长什么样）。凡是要判断「是谁砍掉的」「顶到硬上限没有」的，
// 都在 Server 的 Fold 里——那是 limit / scope 两个参数的语义，Core 一无所知也不该知道。
//
// 构词曾经也在这里（`NounFor` / `Quantity`），现已随名词一起搬进 `CountedNoun`：单复数是
// 名词自身的属性，跟着名词走比跟着「文本原语」走更实在。同一条边界，划得更细了一格。
public static class OutputText
{
    // 折叠行里「下一步由调用方显式给出」的那一形：`<缩进>... +N more [of M ]<名词> (<下一步>)`。
    //
    // 全服折叠行的入口是 Server 的 Fold，那里要靠「是谁砍掉的」才分得出三种下一步；而**排版**
    // 与成因无关，这一行只把「下一步那句话」和构词拼成一行，不看它是怎么来的。故排版这件事
    // 落得进 Core，成因判断落不进：`Fold.Explicit` 与 `Fold.Line` 都是转发，这里是它们**共同的**
    // 唯一产地。Explicit 那条路上调用方连成因都没有（分页、定长上限、成员大纲配额都不经过
    // ScopeFilter，下一步整句由调用方给：`pass offset=N` / `pass startLine=N` / `pass limit:'all' …`）；
    // Line 那条路上成因判断留在 Server，算完的那句话再交到这里。
    //
    // 「共同的唯一产地」是 M2 的判据在用的东西：闸问「这一行渲染得出来吗」，两处各插值一遍
    // 同一套文法时它得挑一个信，转发之后就没得挑了。
    //
    // 名词的单复数一并在这里定，调用方一处也不许自己拼——五个调用方此前全都不做这件事
    // （`+1 more entries` / `+1 more lines` / `+1 more types`），而同一份输出的表头是走构词的。
    // 名词跟哪个数走：给了总数就跟总数（`+1 more of 13 changed files` 是属格复数），
    // 没给就跟增量。同 Fold.PerFile 与 Tally.Cell 的 R30 判据。
    public static string? FoldLine(
        int hiddenCount, CountedNoun noun, string nextStep, int? total = null, string indent = "  ")
    {
        // 没被折叠就没有这一行。各调用方自己那些别的在场条件（分页到底、顶到定长上限）
        // 留在调用方，这里只兜住「增量为 0」。
        if (hiddenCount <= 0) return null;

        var what = total is { } m
            ? $"of {m} {noun.For(m)}"
            : noun.For(hiddenCount);

        return $"{indent}... +{hiddenCount} more {what} ({nextStep})";
    }
}
