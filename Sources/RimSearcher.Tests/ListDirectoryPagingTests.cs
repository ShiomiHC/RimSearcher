using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：目录项直接吃 EnumerateFileSystemEntries 的产出顺序（文件系统顺序、目录与文件混排），
// 于是 1755 项的目录默认只给出其中**任意** 100 条——调用方既无法据此断言「某文件不在这个目录」，
// 也无法预判把 limit 调大会补上哪些。顶到 1000 上限时脚注给的两条出路更是死路：被略去的多半
// 正是顶层文件（不在任何子目录里），而 search_regex 匹配的是文件正文行、fileFilter 只是路径
// 后缀，写不出「限定在这个目录下」。>1000 项的目录在当时的 API 下无法被完整枚举。
[Collection("PathSecurity")]
public class ListDirectoryPagingTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string BuildTree(int fileCount, int dirCount = 3)
    {
        var root = _workspace.Dir("Src");
        for (var i = 0; i < fileCount; i++)
            _workspace.WriteFile(Path.Combine("Src", $"Zz_{i:D4}.cs"), "// x");
        for (var i = 0; i < dirCount; i++)
            _workspace.Dir(Path.Combine("Src", $"Sub{i:D2}"));

        PathSecurity.Initialize([root]);
        return root;
    }

    private static async Task<string> ListAsync(string path, int? limit = null, int? offset = null)
    {
        var payload = new Dictionary<string, object> { ["path"] = path };
        if (limit.HasValue) payload["limit"] = limit.Value;
        if (offset.HasValue) payload["offset"] = offset.Value;

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await new ListDirectoryTool().ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    private static List<string> Entries(string content)
        => content.Split('\n')
            .Where(l => l.StartsWith("Zz_", StringComparison.Ordinal) || l.StartsWith("Sub", StringComparison.Ordinal))
            .ToList();

    // 排序在截断之前：截断的语义必须是「按名序的前 N 个」，缺席才是可推理的
    [Fact]
    public async Task EntriesAreSorted_DirectoriesFirstThenFilesByName()
    {
        var root = BuildTree(20);

        var entries = Entries(await ListAsync(root));

        var dirs = entries.TakeWhile(e => e.EndsWith("/", StringComparison.Ordinal)).ToList();
        var files = entries.Skip(dirs.Count).ToList();

        Assert.Equal(3, dirs.Count);
        Assert.DoesNotContain(files, f => f.EndsWith("/", StringComparison.Ordinal));
        Assert.Equal(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), files);
        Assert.Equal(dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase), dirs);
    }

    // 同一次调用重复跑必须给出同一段——这是「缺席可推理」的前提
    [Fact]
    public async Task TruncatedListing_IsTheAlphabeticalHead()
    {
        var root = BuildTree(30, dirCount: 0);

        var first = Entries(await ListAsync(root, 5));

        Assert.Equal(["Zz_0000.cs", "Zz_0001.cs", "Zz_0002.cs", "Zz_0003.cs", "Zz_0004.cs"], first);
    }

    // offset 是唯一能把 >1000 项的目录枚举完的路
    [Fact]
    public async Task Offset_PagesThroughWithoutOverlapOrGaps()
    {
        var root = BuildTree(25, dirCount: 0);

        var page1 = Entries(await ListAsync(root, 10));
        var page2 = Entries(await ListAsync(root, 10, offset: 10));
        var page3 = Entries(await ListAsync(root, 10, offset: 20));

        Assert.Equal(10, page1.Count);
        Assert.Equal(10, page2.Count);
        Assert.Equal(5, page3.Count);
        Assert.Equal(25, page1.Concat(page2).Concat(page3).Distinct().Count());
    }

    // 总数必须印出来：没有它，调用方只能靠试错逐级加倍
    [Fact]
    public async Task TotalCount_IsAlwaysStated()
    {
        var root = BuildTree(12, dirCount: 2);

        Assert.Contains("(14 entries", await ListAsync(root, 5));
    }

    // 脚注给的下一步必须是真能执行的那个
    [Fact]
    public async Task FootNote_GivesTheNextOffset()
    {
        var root = BuildTree(20, dirCount: 0);

        var content = await ListAsync(root, 6);

        Assert.Contains("14 more", content);
        Assert.Contains("pass offset=6", content);
    }

    [Fact]
    public async Task OffsetPastTheEnd_SaysSoRatherThanLookingEmpty()
    {
        var root = BuildTree(5, dirCount: 0);

        var content = await ListAsync(root, 10, offset: 99);

        Assert.Contains("past the end", content);
        Assert.Contains("(5 entries)", content);
    }

    // 越界拒绝必须报出白名单里的具体根，否则调用方冷启动时无从自纠
    [Fact]
    public async Task OutOfBoundsRefusal_NamesTheAllowedRoots()
    {
        var root = BuildTree(2, dirCount: 0);
        var outside = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = outside }));
        var result = await new ListDirectoryTool().ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains(root, result.Content);
    }

    // 同一份根清单也要注进 description，跟 locate 的 scope 一样在 tools/list 阶段就说清
    [Fact]
    public void Description_NamesTheAllowedRoots()
    {
        var root = BuildTree(1, dirCount: 0);

        Assert.Contains(root, new ListDirectoryTool().Description);
    }
}
