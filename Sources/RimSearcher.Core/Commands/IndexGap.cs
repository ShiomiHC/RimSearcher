using RimSearcher.Cli;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public static class IndexGap
{
    public const string Table = "index_gap";
    public static readonly string[] Columns = ["asked", "state", "next"];

    public const string NullOrEmptyOnThisDef = "本 def 无值";
    public const string NullOrEmptyOnType = "全类型无值";
    public const string Undeclared = "类型未声明";
    public const string ValueNotPath = "是值非路径";
    public const string EmptyInXml = "XML 空元素";

    public static readonly CliJsonKey JsonKey = new(Table,
        "所查路径文本没有对应取值、且成因可判定时，每条一行：asked（原样给出的文本；状态相同的几条以 ', ' 连接）、" +
        "state（全类型无值：类型声明该字段，没有 def 有值；类型未声明：类型没有叫这个名字的字段；" +
        "是值非路径：该文本是某个字段的取值）、next（能查到现有内容的命令，可直接粘贴）。" +
        "路径有匹配，或成因无法判定时为空。",
        Rows: true);

    public const string HelpGet =
        "路径在值索引里没有内容、且成因可判定时，输出一行说明，其「状态」为：" +
        "本 def 无值（同类型的其他 def 带有该路径）、" +
        "XML 空元素（本 def 的 XML 写有该元素但其中为空，带「空元素」列的表列出该元素）、" +
        "是值非路径（该文本是某个字段的取值）。";

    public static void Say(CommandContext ctx, string asked, string state, string? next)
    {
        if (next is not null && ctx.Args.Value(GlobalOptions.Snapshot) is { Length: > 0 } snap)
            next += $" --snapshot {CommandContext.QuoteArg(snap)}";
        ctx.Report.AppendRows(Table, Columns,
        [
            new Dictionary<string, object?> { ["asked"] = asked, ["state"] = state, ["next"] = next },
        ]);
    }
}
