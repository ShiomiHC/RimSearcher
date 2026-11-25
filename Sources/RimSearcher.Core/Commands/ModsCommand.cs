using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class ModsCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("mod");

    public override CliOperation Operation { get; } = new()
    {
        Name = "mods",
        Summary = "按加载顺序列出导出快照时启用的 mod。",
        Remarks = "加载顺序即 PatchOperation 的应用顺序。",
        Options = [LimitOption],
        Examples = ["rimsearcher mods"],
        JsonKeys = [new("mods",
            "每个 mod 一行，按加载顺序：order、package_id、name、version。",
            Rows: true)],
    };

    public override int Run(CommandContext ctx)
    {
        var all = ctx.Db.Mods;
        var limit = ctx.Limit(LimitOption);
        var mods = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        ctx.Report.CountNotice(Tally.Of(mods.Count, all.Count), "mod");
        ctx.Report.Table("mods", ["order", "package_id", "name", "version"],
            mods.Select((m, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["order"] = i,
                ["package_id"] = m.PackageId,
                ["name"] = m.Name,
                ["version"] = m.Version,
            }).ToList());
        return 0;
    }
}
