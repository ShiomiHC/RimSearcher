using RimSearcher.Cli;
using RimTooling.Cli;

namespace RimSearcher.Output;

public sealed record EmptyCause(string Filter, int Hidden, string Next)
{
    public static readonly CliJsonKey JsonKey = JsonKeyCounting("def");

    public static CliJsonKey JsonKeyCounting(string unit) => new(Report.EmptyBecauseTable,
        Cjk.Join("本次调用里单独一项就让查询无结果的选项，每项一行：filter（选项原文）、hidden（只去掉这一项后返回的",
                 NounRegistry.Noun(unit), "数") + "）、next（去掉该项之后的同一条调用，可直接粘贴）。查询有结果，或没有哪一项能单独解释无结果时，这张表为空。",
        Rows: true);
}
