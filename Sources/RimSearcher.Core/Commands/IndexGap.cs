using RimSearcher.Cli;

namespace RimSearcher.Commands;

/// <summary>
/// 「问的是路径,值索引底下没有」这一族成因的**唯一产地**:一张 <c>index_gap</c> 表
/// (asked / state / next)。get 与 fields 印同一张(Docs/25 乙3,§20)。
///
/// 行只在成因**当场算得出来**时出:同类别的 def 有值 / 类型声明了但没人赋值 / 类型根本
/// 没声明 / 那段文本其实是某个字段的取值。算不出来是哪一种(值索引空、声明层也答不出)
/// 的那一档仍是 <see cref="Completeness.NoteIndexHoldsValuesOnly"/> 那句 —— 它压着一轮
/// 实测(10/10 对 0/10),改形态要重测。
///
/// 成因为什么让值缺席(null 不进索引、名值对把字段名搬进了值列)是机制,住 <see cref="Help"/>,
/// 两条命令的 Remarks 引它;表里只有状态词与填好参数的下一条命令。
/// </summary>
public static class IndexGap
{
    public const string Table = "index_gap";
    public static readonly string[] Columns = ["asked", "state", "next"];

    /// <summary>state 列的封闭词表。</summary>
    public const string NullOnThisDef = "null-on-this-def";
    public const string NullOnType = "null-on-type";
    public const string Undeclared = "undeclared";
    public const string ValueNotPath = "value-not-path";

    public static readonly JsonKeySpec JsonKey = new()
    {
        Key = Table,
        Rows = true,
        What = "one row per path text asked for that the value index has nothing under, when the cause could " +
               "be told: asked (the text as given), state (null-on-this-def: other defs of the type carry the " +
               "path, this def has it null / null-on-type: the type declares it, no def has a value / undeclared: " +
               "no field of the type is called that / value-not-path: the text is a value some field holds, not " +
               "a path), next (a command that reaches what there is, ready to paste). Empty when the paths " +
               "matched, or when the cause could not be told.",
    };

    /// <summary>成因的机制,住 help;两条会印这张表的命令把它接在 Remarks 后面。</summary>
    public const string Help =
        "A path that the value index has nothing under prints an index_gap row when the cause can be told. " +
        "A value that is null on a def never enters the index, and neither does a field the game marks as an " +
        "unsaved runtime cache. null-on-this-def: other defs of the type carry the path. null-on-type: the type " +
        "declares it and no def carries a value. undeclared: no field of the type is called that. " +
        "value-not-path: a name/value pair such as statBases[N].stat = MarketValue puts the field's own name in " +
        "the value column, where a path filter cannot reach it; 'where --value' reads that column.";

    /// <summary>next 是另一条命令;调用方点了 --snapshot 的话跟着走,不然贴回去查的是别的快照。</summary>
    public static void Say(CommandContext ctx, string asked, string state, string? next)
    {
        if (next is not null && ctx.Args.Value("snapshot") is { Length: > 0 } snap)
            next += $" --snapshot {CommandContext.QuoteArg(snap)}";
        ctx.Report.AppendRows(Table, Columns,
        [
            new Dictionary<string, object?> { ["asked"] = asked, ["state"] = state, ["next"] = next },
        ]);
    }
}
