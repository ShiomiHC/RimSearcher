using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// JSON schema 里的 maximum 只是给 client 的提示、不是约束：client 照样能传任意大的
// limit，服务端不夹就真的会枚举并回那么多条。这个工具是唯一直接按调用方给的路径
// 访问文件系统的，所以它同时也是 PathSecurity 的把关点，两件事一起钉住。
[Collection("PathSecurity")]
public class ListDirectoryToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public ListDirectoryToolTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private string BuildTree(int fileCount)
    {
        var root = _workspace.Dir("Defs");
        for (var i = 0; i < fileCount; i++)
            _workspace.WriteFile(Path.Combine("Defs", $"Thing_{i:D5}.xml"), "<Defs />");

        PathSecurity.Initialize([root]);
        return root;
    }

    private static async Task<string> ListAsync(string path, int? limit)
    {
        var json = limit == null
            ? $$"""{"path": {{JsonSerializer.Serialize(path)}}}"""
            : $$"""{"path": {{JsonSerializer.Serialize(path)}}, "limit": {{limit}}}""";

        using var doc = JsonDocument.Parse(json);
        var result = await new ListDirectoryTool().ExecuteAsync(doc.RootElement, CancellationToken.None);
        return result.Content;
    }

    private static int CountEntries(string content)
        => content.Split('\n')
            .Count(line => line.StartsWith("Thing_", StringComparison.Ordinal));

    [Fact]
    public async Task Limit_BeyondSchemaMaximum_IsClampedByTheServer()
    {
        var root = BuildTree(1200);

        var content = await ListAsync(root, 999999);

        Assert.Equal(1000, CountEntries(content));
        Assert.Contains("more entries available", content);

        // 顶到服务端上限时「increase limit」是一条死路：limit 已经无法再高
        Assert.DoesNotContain("increase limit", content);
        Assert.Contains("server cap", content);
    }

    [Fact]
    public async Task UnderTheCap_TheHintStillPointsAtLimit()
    {
        var root = BuildTree(50);

        var content = await ListAsync(root, 7);

        Assert.Contains("increase limit", content);
    }

    [Fact]
    public async Task Limit_BelowOne_MeansNoCap_LikeEveryOtherTool()
    {
        var root = BuildTree(5);

        // limit<=0 在本服务器其余工具里一律是「别截断」。这里曾夹到 1，于是回一条外加
        // 「还有更多」，读起来像这个目录几乎是空的——而调用方要的恰恰是全部。
        var content = await ListAsync(root, 0);

        Assert.Equal(5, CountEntries(content));
        Assert.DoesNotContain("more entries available", content);
    }

    [Fact]
    public async Task ExplicitLimit_UnderTheCap_IsRespectedExactly()
    {
        var root = BuildTree(50);

        var content = await ListAsync(root, 7);

        Assert.Equal(7, CountEntries(content));
        Assert.Contains("more entries available", content);
    }

    [Fact]
    public async Task WithoutLimit_FallsBackToTheDocumentedDefault()
    {
        var root = BuildTree(150);

        var content = await ListAsync(root, null);

        Assert.Equal(100, CountEntries(content));
    }

    [Fact]
    public async Task PathOutsideAllowedRoots_IsRefused()
    {
        BuildTree(1);
        var outside = _workspace.Dir("Elsewhere");

        var content = await ListAsync(outside, null);

        Assert.Contains("outside allowed directories", content);
    }
}
