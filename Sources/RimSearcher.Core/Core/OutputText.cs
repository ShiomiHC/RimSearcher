namespace RimSearcher.Core;

// 输出文法里 Core 这一侧也要用到的共享原语。放在这儿而不是 Server 的 Tools/Output/，
// 是因为 RoslynHelper 的大纲折叠行也要用它们，而 Core 引不到 Server。
//
// 边界是「文法 vs 成因」而不是「呈现 vs 数据」：能落在这里的只有**不需要知道任何成因**
// 就能拼出来的句子（怎么构词、一行折叠长什么样）。凡是要判断「是谁砍掉的」「顶到硬上限没有」
// 的，都在 Server 的 Fold 里——那是 limit / scope 两个参数的语义，Core 一无所知也不该知道。
public static class OutputText
{
    // 折叠行与计数的名词槽收的都是复数式（"C# types" / "entries" / "content matches"），
    // 而 N 可以是 1。R5 已经为 locate 表头定过这条规矩（不写 "1 C# types"），其余槽位一直
    // 漏着——全语料里 `... +1 more C# types` 这类出现在 locate / inspect / trace 三个工具上。
    public static string NounFor(int n, string plural) => n == 1 ? Singularize(plural) : plural;

    public static string Quantity(int n, string plural) => $"{n} {NounFor(n, plural)}";

    // 折叠行里「下一步由调用方显式给出」的那一形：`<缩进>... +N more [of M ]<名词> (<下一步>)`。
    //
    // 全服折叠行的入口是 Server 的 Fold，那里的三分支要靠「是谁砍掉的」才分得开；唯独这一形
    // 与成因无关——分页、定长上限、成员大纲配额都不经过 ScopeFilter，下一步整句由调用方给
    // （`pass offset=N` / `pass startLine=N` / `pass limit:'all' …`），这里只负责把它和构词
    // 拼成一行。故它能、也只有它能落在 Core：`Fold.Explicit` 是转发，不是第二个产地。
    //
    // 名词的单复数一并在这里定，调用方一处也不许自己拼——五个调用方此前全都不做这件事
    // （`+1 more entries` / `+1 more lines` / `+1 more types`），而同一份输出的表头是走构词的。
    // 名词跟哪个数走：给了总数就跟总数（`+1 more of 13 changed files` 是属格复数），
    // 没给就跟增量。同 Fold.PerFile 与 Tally.Cell 的 R30 判据。
    public static string? FoldLine(
        int hiddenCount, string noun, string nextStep, int? total = null, string indent = "  ")
    {
        // 没被折叠就没有这一行。各调用方自己那些别的在场条件（分页到底、顶到定长上限）
        // 留在调用方，这里只兜住「增量为 0」。
        if (hiddenCount <= 0) return null;

        var what = total is { } m
            ? $"of {m} {NounFor(m, noun)}"
            : NounFor(hiddenCount, noun);

        return $"{indent}... +{hiddenCount} more {what} ({nextStep})";
    }

    // 裸去 's' 在 entries / content matches / properties 上都会写错，故按英文构词回推。
    // 覆盖的是本服务实际用到的那批名词，不是通用英文形态学。
    private static string Singularize(string plural)
    {
        if (plural.EndsWith("ies", StringComparison.Ordinal)) return plural[..^3] + "y";
        if (plural.EndsWith("es", StringComparison.Ordinal))
        {
            // sses / ches / shes / xes / zes 的 "es" 整个是词尾，去掉两个字母；
            // types / lines 这类只是词干末尾恰好有 e，去一个。
            var stem = plural[..^2];
            if (stem.EndsWith("s", StringComparison.Ordinal) || stem.EndsWith("x", StringComparison.Ordinal)
                || stem.EndsWith("z", StringComparison.Ordinal) || stem.EndsWith("ch", StringComparison.Ordinal)
                || stem.EndsWith("sh", StringComparison.Ordinal))
                return stem;
        }
        return plural.EndsWith("s", StringComparison.Ordinal) ? plural[..^1] : plural;
    }
}
