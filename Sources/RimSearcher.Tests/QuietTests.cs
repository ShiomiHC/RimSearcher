using System.Text.Json;
using RimSearcher.Cli;
using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// <c>--quiet</c> / <c>--data-only</c>:stdout 只出数据;不带这个旗时与改前逐字相同。
/// </summary>
public class QuietTests
{
    [Fact]
    public void 不带quiet时与带quiet的对照臂stdout不同且对照臂仍有声明()
    {
        var plain = Fixture.Run("get", "Apparel_ShieldBelt");
        var quiet = Fixture.Run("get", "Apparel_ShieldBelt", "--quiet");
        Assert.Equal(0, plain.Code);
        Assert.Equal(plain.Code, quiet.Code);
        Assert.Equal(plain.Stderr, quiet.Stderr);
        Assert.NotEqual(plain.Stdout, quiet.Stdout);
        Assert.Contains("12 fields.", plain.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("12 fields.", quiet.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void quiet文本没有声明也没有快照标签但数据块一行不少()
    {
        var plain = Fixture.Run("get", "Apparel_ShieldBelt");
        var quiet = Fixture.Run("get", "Apparel_ShieldBelt", "--quiet");
        var json = Fixture.Run("get", "Apparel_ShieldBelt", "--json");
        using var doc = JsonDocument.Parse(json.Stdout);
        foreach (var note in doc.RootElement.GetProperty("notes").EnumerateArray())
            Assert.DoesNotContain(note.GetProperty("text").GetString()!, quiet.Stdout, StringComparison.Ordinal);

        Assert.DoesNotContain("[", quiet.Stdout.Split('\n')[0], StringComparison.Ordinal);
        foreach (var line in quiet.Stdout.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            Assert.Contains(line, plain.Stdout, StringComparison.Ordinal);
        }
        Assert.Contains("Apparel_ShieldBelt", quiet.Stdout, StringComparison.Ordinal);
        Assert.Contains("soundImpactDefault", quiet.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void quiet加上json时notes是空数组且数据键不变()
    {
        var plain = Fixture.Run("get", "Apparel_ShieldBelt", "--json");
        var quiet = Fixture.Run("get", "Apparel_ShieldBelt", "--json", "--quiet");
        using var a = JsonDocument.Parse(plain.Stdout);
        using var b = JsonDocument.Parse(quiet.Stdout);
        Assert.Equal(JsonValueKind.Array, b.RootElement.GetProperty("notes").ValueKind);
        Assert.Equal(0, b.RootElement.GetProperty("notes").GetArrayLength());
        Assert.True(a.RootElement.GetProperty("notes").GetArrayLength() > 0);

        var skip = new HashSet<string>(StringComparer.Ordinal) { "notes" };
        var keysA = a.RootElement.EnumerateObject().Select(p => p.Name).Where(n => !skip.Contains(n)).OrderBy(n => n).ToList();
        var keysB = b.RootElement.EnumerateObject().Select(p => p.Name).Where(n => !skip.Contains(n)).OrderBy(n => n).ToList();
        Assert.Equal(keysA, keysB);
        foreach (var key in keysA)
            Assert.Equal(a.RootElement.GetProperty(key).GetRawText(), b.RootElement.GetProperty(key).GetRawText());
    }

    [Fact]
    public void 零结果带quiet时stdout为空且退出码仍为一()
    {
        var plain = Fixture.Run("search", "zzzznothing");
        var quiet = Fixture.Run("search", "zzzznothing", "--quiet");
        Assert.Equal(Runner.ExitNoResults, plain.Code);
        Assert.Equal(Runner.ExitNoResults, quiet.Code);
        Assert.NotEqual("", plain.Stdout);
        Assert.Equal("", quiet.Stdout);
        Assert.Equal(plain.Stderr, quiet.Stderr);
    }

    [Fact]
    public void 别名data_only与quiet行为一致()
    {
        var quiet = Fixture.Run("get", "Apparel_ShieldBelt", "--quiet");
        var alias = Fixture.Run("get", "Apparel_ShieldBelt", "--data-only");
        Assert.Equal(quiet.Stdout, alias.Stdout);
        Assert.Equal(quiet.Stderr, alias.Stderr);
        Assert.Equal(quiet.Code, alias.Code);

        var qj = Fixture.Run("get", "Apparel_ShieldBelt", "--json", "--quiet");
        var aj = Fixture.Run("get", "Apparel_ShieldBelt", "--json", "--data-only");
        Assert.Equal(qj.Stdout, aj.Stdout);
        Assert.Equal(qj.Code, aj.Code);
    }

    [Fact]
    public void 快照标签在quiet下不出现包括没有声明行时自己成行的那种()
    {
        var tagged = Fixture.Run("search", "shield", Fixture.Pinned);
        var quiet = Fixture.Run("search", "shield", Fixture.Pinned, "--quiet");
        Assert.StartsWith("[fixture] ", tagged.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("[fixture]", quiet.Stdout, StringComparison.Ordinal);
        Assert.Contains("Apparel_ShieldBelt", quiet.Stdout, StringComparison.Ordinal);
        Assert.Contains("Apparel_ShieldBelt", tagged.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void 渲染器quiet为false与无参逐字节相同()
    {
        var report = new Report { SnapshotTag = "fixture" }
            .Notice(NoticeKind.Count, "1 def.")
            .Table("defs", ["def_name"], [new Dictionary<string, object?> { ["def_name"] = "A" }])
            .Notice(NoticeKind.Advisory, "A footnote.", footnote: true);
        Assert.Equal(TextRenderer.Render(report), TextRenderer.Render(report, quiet: false));
        Assert.Equal(JsonRenderer.Render(report), JsonRenderer.Render(report, quiet: false));

        var quiet = TextRenderer.Render(report, quiet: true);
        Assert.DoesNotContain("1 def.", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("A footnote.", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("[fixture]", quiet, StringComparison.Ordinal);
        Assert.Contains("A", quiet, StringComparison.Ordinal);
    }

    [Fact]
    public void 用法错不受quiet影响()
    {
        var plain = Fixture.Run("search", "shield", "--nonsense");
        var quiet = Fixture.Run("search", "shield", "--quiet", "--nonsense");
        Assert.Equal(Runner.ExitUsage, plain.Code);
        Assert.Equal(Runner.ExitUsage, quiet.Code);
        Assert.Equal(plain.Stdout, quiet.Stdout);
        Assert.Equal(plain.Stderr, quiet.Stderr);
        Assert.Contains("--nonsense", quiet.Stderr, StringComparison.Ordinal);
    }
}
