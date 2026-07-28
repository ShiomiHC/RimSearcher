namespace RimSearcher.Core;

// 输出文法里与「数几个」有关的共享原语。放在 Core 而不是 Server.Tools/ScopeArgs，
// 是因为 RoslynHelper 的大纲折叠行也要用它，而 Core 引不到 Server。
public static class OutputText
{
    // 折叠行与计数的名词槽收的都是复数式（"C# types" / "entries" / "content matches"），
    // 而 N 可以是 1。R5 已经为 locate 表头定过这条规矩（不写 "1 C# types"），其余槽位一直
    // 漏着——全语料里 `... +1 more C# types` 这类出现在 locate / inspect / trace 三个工具上。
    public static string NounFor(int n, string plural) => n == 1 ? Singularize(plural) : plural;

    public static string Quantity(int n, string plural) => $"{n} {NounFor(n, plural)}";

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
