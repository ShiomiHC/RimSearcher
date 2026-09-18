using RimSearcher.Cli;

namespace RimSearcher.Output;

/// <summary>
/// 零行成因表(<c>empty_because</c>,Docs/25 乙1)的一行:**自己给的哪个筛子**把结果筛空了。
/// <paramref name="Filter"/> 是那个参数照命令行的写法(<c>--type ThingDef</c>);
/// <paramref name="Hidden"/> 是只拿掉它、别的照旧时能回来的 def 数;
/// <paramref name="Next"/> 是拿掉它的同一条命令,原样可贴。
///
/// 此前 27 处各拼一句「--scope X is what emptied this: N defs … Drop --scope to see them」;
/// 句式各异,而弱档只读表。成因只在筛子确实挡掉了东西时出行 —— 一行都没有就是「域外也没有」,
/// 那一态由各命令的落空句自己说。
/// </summary>
public sealed record EmptyCause(string Filter, int Hidden, string Next)
{
    /// <summary>每条会出这张表的命令在 JsonKeys 里挂一份声明;行式键,零行时是 <c>[]</c>。数 def 的用这份。</summary>
    public static readonly JsonKeySpec JsonKey = JsonKeyCounting("def");

    /// <summary>hidden 列数的不是 def 的命令(list 的类型面数 def type,members 数 member…)各点自己的名词。</summary>
    public static JsonKeySpec JsonKeyCounting(string unit) => new()
    {
        Key = Report.EmptyBecauseTable,
        Rows = true,
        What = "one row per option given on this call that, alone, emptied the result: filter (the option " +
               $"as written), hidden (how many {NounRegistry.Form(unit, 2)} come back with just that option " +
               "dropped), next (the same call without it, ready to paste). Empty when the result was not " +
               "empty, or when no single option accounts for it.",
    };
}
