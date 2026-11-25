using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

internal static class DefTypeMiss
{
    public static string Say(string typed, IEnumerable<string> known, string verb)
    {
        var close = Suggestion.Closest(known, typed);
        return Head(typed) +
               (close.Count == 0
                   ? ""
                   : Suggestion.Say(close) + $"若指 {close[0]}，运行 'rimsearcher {verb} {close[0]}'。");
    }

    private static string Head(string typed) => $"本快照中没有名为 '{typed}' 的 def 类型。";

    public static string KeepsNone(string typed, IReadOnlyList<string> named) =>
        Head(typed) + $"--type 筛除以下全部名字：{NameList.Render(named, named.Count)}。";

    public static string? InSourceInstead(CommandContext ctx, string typed)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        if (!typed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;

        var hits = new List<string>();
        try
        {
            for (var tier = 0; tier < Sources.SourcePlanner.NameTiers - 1 && hits.Count == 0; tier++)
            {
                foreach (var tree in SourcesShared.TreesOfTier(root, tier))
                    hits.AddRange(Directory
                        .EnumerateFiles(Path.Combine(root, tree), typed + ".cs", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (hits.Count == 0) return null;
        hits.Sort(StringComparer.OrdinalIgnoreCase);

        return $"{typed} 是反编译源码中的类型：" +
               $"{NameList.Render(hits, Limits.MaxSuggestions)}。" +
               $"'rimsearcher read {(hits.Count > 1 ? hits[0] : typed + ".cs")} --outline' 按花括号匹配" +
               "列出其中找到的内容及各自的行范围。";
    }
}
