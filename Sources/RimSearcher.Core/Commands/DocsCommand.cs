using RimSearcher.Cli;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 声明层的第二个出口。<c>--help</c> 与这里读的是同一份 CommandSpec,所以「参数怎么说」
/// 在全系统只有一个产地 —— 上游的反面对照是三份声明零同步。
///
/// 产物 <c>skills/rimsearcher/references/cli-reference.md</c> 由字节级闸守着:测试跑一遍
/// 渲染器,与仓里那份逐字节比对,漂移即红。手改那个文件是无效动作。
/// </summary>
public sealed class DocsCommand : Command
{
    /// <summary>生成产物在仓里的位置。闸与本命令共用这一个常量。</summary>
    public const string ReferenceRelativePath = "skills/rimsearcher/references/cli-reference.md";

    public override CommandSpec Spec => new()
    {
        Name = "docs",
        Summary = "Render the command reference from the declarations in the code.",
        Remarks =
            "Maintenance command. The reference file that ships with the skill is this renderer's output, and a test " +
            "compares the two byte for byte, so editing that file by hand only turns the test red.",
        Options =
        [
            new OptionSpec
            {
                Name = "out",
                Aliases = ["output", "file", "path"],
                Placeholder = "<path>",
                Help = "Write to this file instead of standard output.",
            },
            new OptionSpec
            {
                Name = "check",
                Arity = Arity.Flag,
                Aliases = ["verify"],
                Help = "Compare with the file instead of writing it, and fail if they differ.",
            },
        ],
        UsesGlobals = false,
        Examples = ["rimsearcher docs", $"rimsearcher docs --out {ReferenceRelativePath}"],
    };

    public override int Run(CommandContext ctx)
    {
        var registry = new CommandRegistry();
        var markdown = MarkdownRenderer.Render(CommandRegistry.ExeName, registry.Specs,
                                               GlobalOptions.All, CommandRegistry.Tagline);

        var outPath = ctx.Args.Value("out");
        if (outPath is null)
        {
            ctx.Report.Text("reference", markdown.Split('\n'));
            return 0;
        }

        if (ctx.Args.Flag("check"))
        {
            var current = File.Exists(outPath) ? File.ReadAllText(outPath) : "";
            if (current == markdown)
            {
                ctx.Report.Detail("check", [new("file", outPath), new("result", "up to date")]);
                return 0;
            }
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{outPath}' differs from what the declarations render. Re-run without --check to regenerate it.");
            return Runner.ExitNoResults;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, markdown);
        ctx.Report.Detail("written", [new("file", outPath), new("bytes", markdown.Length)]);
        return 0;
    }
}
