using RimSearcher.Cli;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 手工接挂 —— 给「人在游戏里,想用设置页那个按钮导一次」准备的。
///
/// <c>export</c> 自己会接、会断,平常用不到这两条命令。它们存在的理由有两个:
/// 游戏内那个入口没有 CLI 可以替它接挂;以及导出被强杀之后需要一个把残骸收掉的地方。
/// </summary>
public sealed class DataModAttachCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "datamod attach",
        Summary = "Make the exporter mod visible to the game until it is detached again.",
        Remarks =
            "The exporter is not a mod you play with, so it is kept out of the game's mod folder and attached only " +
            "for as long as it is needed. 'export' does this by itself; attach it by hand when you want to run the " +
            "export from the mod's own settings page inside the game.\n\n" +
            "Attaching only makes the mod visible. Enabling it is still a choice made in the game's mod list, and " +
            "the next 'export' detaches it again.",
        UsesGlobals = false,
        Examples = ["rimsearcher datamod attach"],
        JsonKeys = [new() { Key = "attached", What = "an object: where the junction was made and what it points at." }],
    };

    public override int Run(CommandContext ctx)
    {
        var before = DataModLink.Inspect(ctx.Config);
        if (before == DataModLink.LinkState.Installed)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"A real folder is already installed at {DataModLink.Path(ctx.Config)}, so nothing was attached. " +
                "That copy is yours to manage: the game sees the exporter whether or not anything is attached. " +
                "Delete it if you want this command to take over.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(ctx.Config.DataModDir))
            throw new CliUsageException(
                "'datamod_dir' is not set in the config file, so there is nothing to attach. Point it at the mod " +
                "folder that building Sources/RimSearcher.DataMod stages.");

        using var attachment = DataModLink.Attach(ctx.Config);
        attachment.Keep();   // 这条命令的意义就是「接上之后留着」

        ctx.Report.Detail("attached",
        [
            new("path", attachment.LinkPath),
            new("source", attachment.Source),
            new("was_already_there", attachment.WasAlreadyThere),
        ]);
        ctx.Report.Notice(NoticeKind.NextStep,
            "The game will list the exporter until 'rimsearcher datamod detach' runs, or until the next 'export' " +
            "finishes — that command always leaves it detached.");
        return 0;
    }
}

public sealed class DataModDetachCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "datamod detach",
        Summary = "Hide the exporter mod from the game again.",
        Remarks =
            "'export' already detaches on its way out, including when it fails. This is for the case where it was " +
            "killed before it could — the leftover is a link the game lists but does not enable, and this removes it.\n\n" +
            "A real folder installed at the same place is left alone: only a link this tool could have made is removed.",
        UsesGlobals = false,
        Examples = ["rimsearcher datamod detach"],
        JsonKeys = [new() { Key = "detached", What = "an object: which junction was removed." }],
    };

    public override int Run(CommandContext ctx)
    {
        var path = DataModLink.Path(ctx.Config);
        var state = DataModLink.Inspect(ctx.Config);

        switch (state)
        {
            case DataModLink.LinkState.Installed:
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"What is at {path} is a real folder, not a link, so it was left alone. " +
                    "The game will keep listing the exporter until you remove that folder yourself.");
                return 0;

            case DataModLink.LinkState.Attached:
                DataModLink.Detach(ctx.Config);
                ctx.Report.Detail("detached", [new("path", path)]);
                return 0;

            default:
                ctx.Report.Notice(NoticeKind.Count,
                    "The exporter is not attached, so the game does not list it.");
                return 0;
        }
    }
}

/// <summary>
/// 接挂点现在是什么样。<see cref="DataModLink.LinkState"/> 的**四**种状态下一步动作各不
/// 相同,所以四句话分开说。
///
/// (原先这里写的是「三种」,而枚举一直是四个 —— <c>Installed</c> 那一档由 <c>_</c> 兜着,
/// 从注释上看不见。**注释里关于自己覆盖面的数字,正是别人判断「还需不需要补」的依据**,
/// 数错了挡住的不是一次误读,是后续所有补漏动机。同一形态本轮在 `where` 的子串提示上
/// 出过一次大的。)
///
/// <c>_</c> 没改成点名 <c>Installed</c>:本仓只把 nullable 当错误,枚举加一档时非穷尽
/// switch 只是个警告,而运行期会抛。宁可让新状态先落到一句**已知会被念出来**的话上,
/// 也不让它在使用者手里炸 —— 但那句话届时是错的,所以加状态时这里必须一起改。
/// </summary>
public sealed class DataModStatusCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "datamod status",
        Summary = "Report whether the game can currently see the exporter mod.",
        UsesGlobals = false,
        Examples = ["rimsearcher datamod status"],
        JsonKeys = [new() { Key = "datamod", What = "an object: whether the exporter is attached to the game's Mods directory right now." }],
    };

    public override int Run(CommandContext ctx)
    {
        var state = DataModLink.Inspect(ctx.Config);
        ctx.Report.Detail("datamod",
        [
            new("state", state.ToString().ToLowerInvariant()),
            new("path", DataModLink.Path(ctx.Config)),
            new("source", ctx.Config.DataModDir),
        ]);

        ctx.Report.Notice(NoticeKind.Count, state switch
        {
            DataModLink.LinkState.NotManaged =>
                "'datamod_dir' is not set, so this tool does not attach or detach anything. The exporter has to be " +
                "installed in the game's mod folder for 'export' to work.",
            DataModLink.LinkState.Detached =>
                "The exporter is not attached: the game does not list it. 'export' attaches it for the duration of " +
                "a run and detaches it afterwards.",
            DataModLink.LinkState.Attached =>
                "The exporter is attached, so the game lists it. That is expected while an export is running; " +
                "otherwise 'rimsearcher datamod detach' removes it.",
            // Installed(及将来任何新档):见类型注释 —— 兜底而不是点名是有意的,
            // 代价是新档会先被念成这一句,所以加档时这里必须一起改。
            _ =>
                "A real folder is installed at the attach point, so the game always lists the exporter. " +
                "Nothing is attached or detached while that folder is there.",
        });
        return 0;
    }
}
