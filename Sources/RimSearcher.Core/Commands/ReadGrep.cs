using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using System.Text.RegularExpressions;

namespace RimSearcher.Commands;

internal sealed class ReadGrep(Regex regex, string pattern, int before, int after, string? contextEcho)
{
    public static ReadGrep? From(CommandContext ctx, bool outline)
    {
        var pattern = ctx.Args.Value(ReadCommand.Matching);
        var contextSpec = ctx.Args.Value(ReadCommand.Context);
        if (pattern is not { Length: > 0 })
        {
            if (contextSpec is { Length: > 0 })
                throw new CliUsageException(
                    "--context 显示命中周围的行，须同时指定 --matching <regex> 说明匹配什么。");
            return null;
        }
        if (outline)
            throw new CliUsageException(
                "--outline 与 --matching 不能同时传入：--outline 列出声明，--matching 挑选行。");

        var (before, after) = CodeSearchCommand.ParseContext(contextSpec, out var rewritten);
        try
        {
            return new ReadGrep(new Regex(pattern, RegexOptions.None,
                                          TimeSpan.FromMilliseconds(Limits.CodeSearchRegexTimeoutMs)),
                                pattern, before, after,
                                rewritten is null ? null : $"--context {contextSpec!.Trim()}{ReadCommand.ReadAsPhrase}{rewritten}。");
        }
        catch (ArgumentException ex)
        {
            throw new CliUsageException($"这不是有效的正则表达式：{ex.Message}");
        }
    }

    public int Read(CommandContext ctx, string rel, string[] text,
                    IReadOnlyList<string> members, string? type, string? range, bool batch, bool batchMisses,
                    List<string> lines, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var cap = ReadCommand.Cap(ctx);
        var echo = contextEcho ?? "";
        var stretches = new List<(int From, int To, CsDecl? Decl)>();
        string where;
        if (members.Count > 0 || type is { Length: > 0 })
        {
            var picked = ReadCommand.Pick(ctx, rel, text, members, type, batchMisses).SelectMany(g => g.Hits).ToList();
            if (picked.Count == 0) return 1;
            stretches.AddRange(picked.Select(d => (d.StartLine, d.EndLine, (CsDecl?)d)));
            where = Tally.Complete(picked.Count).Render("declaration", scope: rel);
        }
        else
        {
            var (from, to) = ReadCommand.ParseRange(range, text.Length, int.MaxValue, out var rewritten);
            if (rewritten is not null) echo = $"--lines {range!.Trim()}{ReadCommand.ReadAsPhrase}{rewritten}。" + echo;
            if (from > text.Length)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    echo + $"{rel} 有 {Tally.Complete(text.Length).Render("line")}，因此 --lines {range} 的起点超出文件末尾。");
                return 1;
            }
            to = Math.Min(to, text.Length);
            stretches.Add((from, to, null));
            where = from == 1 && to == text.Length
                ? $"{rel}（{Tally.Complete(text.Length).Render("line")}）"
                : $"{rel}:{from}-{to}";
        }

        var found = new List<List<int>>();
        try
        {
            foreach (var (from, to, _) in stretches)
                found.Add([.. Enumerable.Range(from, to - from + 1).Where(i => regex.IsMatch(text[i - 1]))]);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new CliUsageException(
                $"--matching '{pattern}' 在 {rel} 上运行超过 {Limits.CodeSearchRegexTimeoutMs} 毫秒，已停止；须简化该模式。");
        }

        var total = found.Sum(f => f.Count);
        if (total == 0)
        {
            ctx.Report.Notice(NoticeKind.Count, echo + Cjk.Join(where, $"中没有行匹配 '{pattern}'") + "。");
            if (stretches[0].Decl is not null)
                ctx.Report.Notice(NoticeKind.NextStep, "不带 --member/--type 时搜索整个文件。");
            else if (stretches[0].From > 1 || stretches[0].To < text.Length)
                ctx.Report.Notice(NoticeKind.NextStep, "不带 --lines 时搜索整个文件。");
            return 1;
        }

        var printed = 0;
        for (var s = 0; s < stretches.Count && printed < cap; s++)
        {
            var (from, to, decl) = stretches[s];
            var hits = found[s].Take(cap - printed).ToList();
            if (hits.Count == 0) continue;
            printed += hits.Count;

            if (decl is not null || batch)
            {
                if (lines.Count > 0) lines.Add("--");
                lines.Add(decl is not null
                    ? $"{rel}:{decl.StartLine}-{decl.EndLine}  {CsKind.Of(decl.Kind)} {decl.Qualified}"
                    : $"{rel}:{from}-{to}");
            }

            var isHit = hits.ToHashSet();
            var firstGroup = true;
            for (var i = 0; i < hits.Count; i++)
            {
                var start = Math.Max(from, hits[i] - before);
                var end = Math.Min(to, hits[i] + after);
                while (i + 1 < hits.Count && hits[i + 1] - before <= end + 1)
                    end = Math.Max(end, Math.Min(to, hits[++i] + after));
                if (!firstGroup && (before > 0 || after > 0)) lines.Add("--");
                firstGroup = false;
                for (var n = start; n <= end; n++)
                {
                    var row = new Dictionary<string, object?> { ["file"] = rel, ["line"] = n };
                    if (decl is not null)
                    {
                        row["kind"] = CsKind.Of(decl.Kind);
                        row["declaration"] = decl.Qualified;
                    }
                    row["is_match"] = isHit.Contains(n);
                    row["text"] = text[n - 1].TrimEnd();
                    lines.Add($"{n,6}{(isHit.Contains(n) ? ':' : '-')} {text[n - 1].TrimEnd()}".TrimEnd());
                    rows.Add(row);
                }
            }
        }

        ctx.Report.Notice(printed < total ? NoticeKind.Truncation : NoticeKind.Count,
            echo + Cjk.Join(where, "中有", Tally.Complete(total).Render("match")) +
            (printed < total
                ? Cjk.Join("；--limit 使输出止于", Tally.Complete(printed).Render("match")) + "。"
                : "。"));
        return 0;
    }
}
