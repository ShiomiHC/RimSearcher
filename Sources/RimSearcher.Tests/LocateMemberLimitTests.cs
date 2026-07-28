using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：`limit` 对 Members 段既不是上限也不是下限，而 schema 写的是「result cap per
// section」。成因是展示层按 `perGroup = max(3, limit/2)` **给每个种类各切一份配额**：
//   · `energy` limit:10 → 13 条（Properties 5 + Fields 5 + Methods 3），超了
//   · `method:CompTick` limit:10 → 5 条（单一种类只拿得到一份配额），欠了
//   · 任何查询 limit:1 → 3 条（`max(3, 0)` 把下界顶到 3）
// 改为按**总量**切、各组轮流取一条：总量守住上限，同时不让某一类把配额吃光。
public class LocateMemberLimitTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const int PerKind = 6;

    // 三个种类各 6 个成员，名字共享 `ZqLimit` 前缀，故一条查询就能同时命中三类。
    private LocateTool BuildTool()
    {
        var root = _workspace.Dir("Core");

        var source = new System.Text.StringBuilder();
        source.AppendLine("namespace Zq");
        source.AppendLine("{");
        source.AppendLine("    public class ZqLimitHolder");
        source.AppendLine("    {");
        for (var i = 0; i < PerKind; i++)
        {
            var suffix = (char)('A' + i);
            source.AppendLine($"        public int zqLimitField{suffix};");
            source.AppendLine($"        public int ZqLimitProp{suffix} {{ get; set; }}");
            source.AppendLine($"        public void ZqLimitMethod{suffix}() {{ }}");
        }
        source.AppendLine("    }");
        source.AppendLine("}");

        _workspace.WriteFile(Path.Combine("Core", "ZqLimitHolder.cs"), source.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(LocateTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // Members 段的条目行以两个空格 + "- " 打头；折叠行以 "  ..." 打头，不会被数进去。
    private static int CountMemberRows(string content)
    {
        var start = content.IndexOf("**Members**", StringComparison.Ordinal);
        if (start < 0) return 0;

        var section = content[start..];
        var end = section.IndexOf("\n**", StringComparison.Ordinal);
        if (end >= 0) section = section[..end];

        return section.Split('\n').Count(line => line.StartsWith("  - ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task MemberSection_NeverListsMoreThanLimit(int limit)
    {
        var content = await Run(BuildTool(), $$"""{"query":"ZqLimit","limit":{{limit}}}""");

        Assert.InRange(CountMemberRows(content), 1, limit);
    }

    // 单一种类时也要拿满 limit：旧写法下 `method:` 查询只有一组，而一组只发 perGroup 份，
    // limit:5 于是只回 3 条（`max(3, 5/2)`）。
    [Fact]
    public async Task SingleKindQuery_FillsTheWholeLimit()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZqLimit","limit":5}""");

        Assert.Equal(5, CountMemberRows(content));
    }

    // 但配额也不能被头一类吃光——那正是 F10 把 kind 过滤推到取回层要防的事，
    // 这里防的是无前缀查询里的组间挤占。
    [Fact]
    public async Task MixedKindQuery_GivesEveryKindARow()
    {
        var content = await Run(BuildTool(), """{"query":"ZqLimit","limit":6}""");

        Assert.Contains("Properties:", content);
        Assert.Contains("Fields:", content);
        Assert.Contains("Methods:", content);
        Assert.Equal(6, CountMemberRows(content));
    }

    // limit:'all' 仍是「全都要」，不受上面的总量切分影响
    [Fact]
    public async Task UnlimitedStillListsEveryKind()
    {
        var content = await Run(BuildTool(), """{"query":"ZqLimit","limit":"all"}""");

        Assert.Equal(PerKind * 3, CountMemberRows(content));
    }
}
