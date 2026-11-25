using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

internal static class Completeness
{
    public static string? ValuesLiveDeeper(CommandContext ctx, string path, ScopeFilter scope)
    {
        if (path.Contains('.') || path.Contains('[')) return null;
        var (samples, total, topType) = ctx.Db.PathsBelow(path, scope, 1);
        if (total == 0) return null;

        var top = samples[0];
        return $"{path} 下是对象；" +
               $"值在下一层的 {Tally.Complete(total).Render("field path")}上。覆盖 def 最多的是 " +
               $"{top.Path}（{Tally.Complete(top.Defs).Render("def")}）：'rimsearcher values {top.Path}' 读取该路径" +
               (topType is null
                   ? "。"
                   : $"，其余见 'rimsearcher fields {topType} --path-contains {path}'。");
    }

    public static string DeclarationSearch(string? path)
        => Leaf(path) is { } leaf
            ? $"{CommandRegistry.ExeName} code-search \"[\\w<>,\\[\\] ]+ {leaf}( =[^;]*)?;\""
            : $"{CommandRegistry.ExeName} code-search";

    private static string? Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var last = path.Split('.')[^1];
        var bracket = last.IndexOf('[');
        if (bracket >= 0) last = last[..bracket];
        return last.Length > 0 && last.All(c => char.IsLetterOrDigit(c) || c == '_') ? last : null;
    }

    public static void NoteWidelySharedValues(
        CommandContext ctx, DefRow def, IReadOnlyList<FieldRow> fields,
        bool defaultRowsListed, int defaulted)
    {
        if (fields.Count == 0) return;
        var shared = ctx.Db.SharedValues(def.DefType, fields.Select(f => (f.Path, f.Value)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listed = fields
            .Where(f => f.Value is not null && shared.ContainsKey((f.Path, f.Value)) && seen.Add(f.Path))
            .Select(f => (f.Path, N: shared[(f.Path, f.Value!)]))
            .OrderByDescending(x => x.N).ThenBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => $"{x.Path}（{x.N}）")
            .ToList();
        var total = ctx.Db.CountDefsOfType(
            def.DefType, Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));

        var carryYesMeans = defaultRowsListed && defaulted > 0;
        var notCompared = carryYesMeans ? "值与新建实例相同的行未参与比较。" : "";
        var ofType = Tally.Complete(total).Render("def", scope: Cjk.Join("本快照中 def 类型", def.DefType, "下"));
        ctx.Report.Notice(NoticeKind.Advisory, listed.Count > 0
            ? Cjk.Join(ofType, "中多数带有以下值（括号内为带有该值的 def 数）：") +
              $"{NameList.Render(listed, listed.Count)}。" + notCompared
            : Cjk.Join("上面与新建实例不同的值，在", ofType, "中都没有过半且不少于 8 个的 def 带有。") + notCompared);
    }

    public static readonly CliJsonKey JsonKey = new("completeness",
        "对象，仅当范围内有 def 的导出被截断时出现：scope（所覆盖的 def 类型，文字描述）、defs_cut_short（个数）、" +
        "types（每个 def 类型一行，带各自的个数）、verify（列出这些 def 的现成命令）。" +
        "无此键表示该范围内没有 def 的导出被截断。");

    public static void NoteIndexedPathsOnly(CommandContext ctx, TruncationScope affected, string basis)
    {
        if (affected.Count == 0) return;

        var shown = affected.ByType.Take(Limits.MaxSuggestions).ToList();
        var cmd = shown.Count < affected.ByType.Count
            ? "rimsearcher snapshot truncated"
            : "rimsearcher snapshot truncated" + string.Concat(shown.Select(t => $" --type {t.Type}"));

        ctx.Report.Add(new CompletenessBlock("completeness", basis, shown, affected.Count, cmd));
    }
}
