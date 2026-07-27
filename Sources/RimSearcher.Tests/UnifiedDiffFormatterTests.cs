using RimSearcher.Core;

namespace RimSearcher.Tests;

public class UnifiedDiffFormatterTests
{
    private static string Numbered(int count) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"line {i}"));

    [Fact]
    public void IdenticalText_SaysSo()
    {
        var diff = UnifiedDiffFormatter.Format("a\nb", "a\nb", "T");

        Assert.Contains("no textual differences", diff);
    }

    // 单个类动几行、文件却有上千行时，未变部分会把真正的改动淹掉
    [Fact]
    public void UnchangedRegionsFarFromChanges_AreOmitted()
    {
        var oldText = Numbered(40);
        var newText = oldText.Replace("line 20", "line 20 CHANGED");

        var diff = UnifiedDiffFormatter.Format(oldText, newText, "T", contextLines: 2, maxLines: 400);

        Assert.Contains("-line 20", diff);
        Assert.Contains("+line 20 CHANGED", diff);
        Assert.Contains("line 18", diff);          // 上下文保留
        Assert.Contains("line 22", diff);
        Assert.DoesNotContain("line 35", diff);    // 远处未变行被折叠
    }

    [Fact]
    public void HunkHeader_MarksTheGap()
    {
        var oldText = Numbered(40);
        var newText = oldText.Replace("line 20", "line 20 CHANGED");

        Assert.Contains("@@ line", UnifiedDiffFormatter.Format(oldText, newText, "T", contextLines: 2));
    }

    [Fact]
    public void Label_IsEmittedAsTheHeader()
        => Assert.StartsWith("--- Core/CompShield.cs @ v0002",
            UnifiedDiffFormatter.Format("a", "b", "Core/CompShield.cs @ v0002"));

    // 同一变更块内先出全部删除行、再出全部新增行，改动前后的对照才立得住
    [Fact]
    public void WithinAHunk_RemovalsPrecedeAdditions()
    {
        var oldText = "keep\nold1\nold2\nkeep2";
        var newText = "keep\nnew1\nnew2\nkeep2";

        var diff = UnifiedDiffFormatter.Format(oldText, newText, "T", contextLines: 1, maxLines: 400);
        var lines = diff.Split('\n');

        var lastRemoval = Array.FindLastIndex(lines, l => l.StartsWith('-') && !l.StartsWith("---"));
        var firstAddition = Array.FindIndex(lines, l => l.StartsWith('+'));

        Assert.True(lastRemoval >= 0 && firstAddition >= 0);
        Assert.True(lastRemoval < firstAddition, $"removals must come first:\n{diff}");
    }

    [Fact]
    public void MaxLines_TruncatesAndSaysSo()
    {
        var oldText = Numbered(200);
        var newText = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"changed {i}"));

        var diff = UnifiedDiffFormatter.Format(oldText, newText, "T", contextLines: 3, maxLines: 10);

        Assert.Contains("truncated", diff);
        Assert.True(diff.Split('\n').Length < 40, "截断后不该还输出几百行");
    }

    [Fact]
    public void PureAddition_AndPureRemoval_AreBothRendered()
    {
        Assert.Contains("+added", UnifiedDiffFormatter.Format("a\nb", "a\nadded\nb", "T"));
        Assert.Contains("-b", UnifiedDiffFormatter.Format("a\nb\nc", "a\nc", "T"));
    }

    [Fact]
    public void EmptySides_DoNotThrow()
    {
        Assert.Contains("+a", UnifiedDiffFormatter.Format("", "a", "T"));
        Assert.Contains("-a", UnifiedDiffFormatter.Format("a", "", "T"));
    }
}
