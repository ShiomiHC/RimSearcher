using System.Text;
using RimSearcher.Output;

namespace RimSearcher.Cli;

/// <summary>
/// 声明层的两个渲染器之一:<c>--help</c>。
/// 另一个是 <see cref="MarkdownRenderer"/>(生成 references/cli-reference.md)。
/// 两个渲染器读同一份 <see cref="CommandSpec"/> —— 上游的反面对照是三份声明零同步
/// (贫瘠 --help + SKILL.md 速查表 + 手写 cli-reference)。
/// </summary>
public static class HelpRenderer
{
    public static string RenderOverview(string exeName, IReadOnlyList<CommandSpec> commands,
                                        IReadOnlyList<OptionSpec> globals, string tagline)
    {
        var sb = new StringBuilder();
        sb.Append(tagline).Append(OutputText.Newline).Append(OutputText.Newline);
        sb.Append($"Usage: {exeName} <command> [arguments] [options]").Append(OutputText.Newline);
        sb.Append(OutputText.Newline).Append("Commands:").Append(OutputText.Newline);

        var width = commands.Max(c => c.Name.Length);
        foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
            sb.Append("  ").Append(c.Name.PadRight(width)).Append("  ").Append(c.Summary).Append(OutputText.Newline);

        sb.Append(OutputText.Newline).Append("Global options:").Append(OutputText.Newline);
        AppendOptions(sb, globals);

        sb.Append(OutputText.Newline)
          .Append($"Run '{exeName} <command> --help' for the arguments of one command.")
          .Append(OutputText.Newline);
        return OutputText.Finish(sb.ToString());
    }

    public static string RenderCommand(string exeName, CommandSpec spec, IReadOnlyList<OptionSpec> globals)
    {
        var sb = new StringBuilder();
        sb.Append($"Usage: {exeName} {spec.Name}");
        foreach (var p in spec.Positionals)
            sb.Append(p.Required ? $" <{p.Name}>" : $" [{p.Name}]").Append(p.Variadic ? "..." : "");
        if (spec.Options.Length > 0 || (spec.UsesGlobals && globals.Count > 0)) sb.Append(" [options]");
        sb.Append(OutputText.Newline).Append(OutputText.Newline);

        sb.Append(spec.Summary).Append(OutputText.Newline);
        if (spec.Remarks is { Length: > 0 })
            sb.Append(OutputText.Newline).Append(spec.Remarks).Append(OutputText.Newline);

        if (spec.Positionals.Length > 0)
        {
            sb.Append(OutputText.Newline).Append("Arguments:").Append(OutputText.Newline);
            var w = spec.Positionals.Max(p => p.Name.Length);
            foreach (var p in spec.Positionals)
                sb.Append("  ").Append(("<" + p.Name + ">").PadRight(w + 2)).Append("  ")
                  .Append(p.Help).Append(p.Required ? "" : " (optional)").Append(OutputText.Newline);
        }

        if (spec.Options.Length > 0)
        {
            sb.Append(OutputText.Newline).Append("Options:").Append(OutputText.Newline);
            AppendOptions(sb, spec.Options);
        }

        if (spec.UsesGlobals && globals.Count > 0)
        {
            sb.Append(OutputText.Newline).Append("Global options:").Append(OutputText.Newline);
            AppendOptions(sb, globals);
        }

        if (spec.Examples.Length > 0)
        {
            sb.Append(OutputText.Newline).Append("Examples:").Append(OutputText.Newline);
            foreach (var e in spec.Examples)
                sb.Append("  ").Append(e).Append(OutputText.Newline);
        }

        return OutputText.Finish(sb.ToString());
    }

    internal static string Signature(OptionSpec o)
    {
        var head = o.Short is { } c ? $"-{c}, --{o.Name}" : $"    --{o.Name}";
        if (o.Arity != Arity.Flag) head += " " + (o.Placeholder ?? "<value>");
        return head;
    }

    private static void AppendOptions(StringBuilder sb, IReadOnlyList<OptionSpec> options)
    {
        if (options.Count == 0) return;
        var sigs = options.Select(Signature).ToArray();
        var width = sigs.Max(s => s.Length);
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var tail = o.Help;
            if (o.Choices.Length > 0) tail += $" One of: {string.Join(", ", o.Choices)}.";
            if (o.Default is { Length: > 0 }) tail += $" Default: {o.Default}.";
            if (o.Required) tail += " [required]";
            sb.Append("  ").Append(sigs[i].PadRight(width)).Append("  ").Append(tail).Append(OutputText.Newline);
            if (o.Aliases.Length > 0)
                sb.Append("  ").Append(new string(' ', width)).Append("  ")
                  .Append("Also accepted: ").Append(string.Join(", ", o.Aliases.Select(a => "--" + a)))
                  .Append('.').Append(OutputText.Newline);
        }
    }
}

/// <summary>
/// 声明层的第二个渲染器:markdown。产物是 <c>skills/rimsearcher/references/cli-reference.md</c>,
/// 由 <c>docs</c> 命令生成、由字节级闸守着 —— 手改无效,闸会红
/// (master 七份 tools/list 基线的同一纪律,判据零新写)。
/// </summary>
public static class MarkdownRenderer
{
    public static string Render(string exeName, IReadOnlyList<CommandSpec> commands,
                                IReadOnlyList<OptionSpec> globals, string tagline)
    {
        var sb = new StringBuilder();
        sb.Append("# rimsearcher CLI reference").Append(OutputText.Newline).Append(OutputText.Newline);
        sb.Append("<!-- Generated by `").Append(exeName).Append(" docs`. Do not edit by hand: a byte-level test compares this file with the renderer output. -->")
          .Append(OutputText.Newline).Append(OutputText.Newline);
        sb.Append(tagline).Append(OutputText.Newline).Append(OutputText.Newline);

        sb.Append("## Commands at a glance").Append(OutputText.Newline).Append(OutputText.Newline);
        sb.Append("| Command | What it answers |").Append(OutputText.Newline);
        sb.Append("|---|---|").Append(OutputText.Newline);
        foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
            sb.Append("| `").Append(c.Name).Append("` | ").Append(Escape(c.Summary)).Append(" |").Append(OutputText.Newline);
        sb.Append(OutputText.Newline);

        // R12:退出码原先在任何文档里都没有出现过 —— 三份盲测轨迹各自撞上「正文是一句正确、
        // 有信息量的结论,退出码却说失败」,而参考页花了很多篇幅讲不要管道过滤,却没防住
        // 同样会丢信息的 `&&`。约定本身是清晰的(Runner 的三个常量),缺的只是把它写下来。
        sb.Append("## Exit codes").Append(OutputText.Newline).Append(OutputText.Newline);
        sb.Append("| Code | Meaning |").Append(OutputText.Newline);
        sb.Append("|---|---|").Append(OutputText.Newline);
        sb.Append("| `0` | The command ran. |").Append(OutputText.Newline);
        sb.Append("| `1` | This query returned no rows. |").Append(OutputText.Newline);
        sb.Append("| `2` | Usage error: unknown command, unknown option, bad value. |").Append(OutputText.Newline);
        sb.Append("| `70` | A defect in the tool itself, not in what you asked for. |").Append(OutputText.Newline);
        sb.Append(OutputText.Newline);
        sb.Append("A `1` is an answer rather than a failure: \"nothing in this snapshot has that value\" ")
          .Append("is information, and the reasoning behind it goes to stdout either way. Chain commands with ")
          .Append("`;` rather than `&&`, or a `1` on a query that answered your question will silently drop ")
          .Append("whatever you queued after it.").Append(OutputText.Newline).Append(OutputText.Newline);

        sb.Append("## Global options").Append(OutputText.Newline).Append(OutputText.Newline);
        AppendOptionTable(sb, globals);

        foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            sb.Append("## `").Append(c.Name).Append('`').Append(OutputText.Newline).Append(OutputText.Newline);
            sb.Append(Escape(c.Summary)).Append(OutputText.Newline).Append(OutputText.Newline);

            sb.Append("```").Append(OutputText.Newline).Append(exeName).Append(' ').Append(c.Name);
            foreach (var p in c.Positionals)
                sb.Append(p.Required ? $" <{p.Name}>" : $" [{p.Name}]").Append(p.Variadic ? "..." : "");
            if (c.Options.Length > 0) sb.Append(" [options]");
            sb.Append(OutputText.Newline).Append("```").Append(OutputText.Newline).Append(OutputText.Newline);

            if (c.Remarks is { Length: > 0 })
                sb.Append(c.Remarks).Append(OutputText.Newline).Append(OutputText.Newline);

            if (c.Positionals.Length > 0)
            {
                sb.Append("| Argument | Meaning |").Append(OutputText.Newline);
                sb.Append("|---|---|").Append(OutputText.Newline);
                foreach (var p in c.Positionals)
                    sb.Append("| `<").Append(p.Name).Append(">` | ").Append(Escape(p.Help))
                      .Append(p.Required ? "" : " *(optional)*").Append(" |").Append(OutputText.Newline);
                sb.Append(OutputText.Newline);
            }

            if (c.Options.Length > 0) AppendOptionTable(sb, c.Options);

            if (c.Examples.Length > 0)
            {
                sb.Append("Examples:").Append(OutputText.Newline).Append(OutputText.Newline);
                sb.Append("```").Append(OutputText.Newline);
                foreach (var e in c.Examples) sb.Append(e).Append(OutputText.Newline);
                sb.Append("```").Append(OutputText.Newline).Append(OutputText.Newline);
            }
        }

        return OutputText.Finish(sb.ToString());
    }

    private static void AppendOptionTable(StringBuilder sb, IReadOnlyList<OptionSpec> options)
    {
        if (options.Count == 0) return;
        sb.Append("| Option | Meaning | Also accepted |").Append(OutputText.Newline);
        sb.Append("|---|---|---|").Append(OutputText.Newline);
        foreach (var o in options)
        {
            var name = o.Short is { } c ? $"`-{c}`, `--{o.Name}`" : $"`--{o.Name}`";
            if (o.Arity != Arity.Flag) name += " " + (o.Placeholder ?? "`<value>`");
            var help = Escape(o.Help);
            if (o.Choices.Length > 0) help += $" One of: {string.Join(", ", o.Choices.Select(v => $"`{v}`"))}.";
            if (o.Default is { Length: > 0 }) help += $" Default: `{o.Default}`.";
            if (o.Required) help += " **required**";
            var aliases = o.Aliases.Length == 0 ? "" : string.Join(", ", o.Aliases.Select(a => $"`--{a}`"));
            sb.Append("| ").Append(name).Append(" | ").Append(help).Append(" | ").Append(aliases).Append(" |")
              .Append(OutputText.Newline);
        }
        sb.Append(OutputText.Newline);
    }

    private static string Escape(string s) => s.Replace("|", "\\|");
}
