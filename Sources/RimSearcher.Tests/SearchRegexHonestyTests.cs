using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// search_regex 对调用方的契约写在它自己的 Description 里：「两处截断总会写进尾注，
// 所以没有尾注的输出就是完整命中集」。这一组守的就是那句话——凡是会让命中集不完整、
// 或让某个数字被读成结论的路径，都必须在输出里留下痕迹。
public class SearchRegexHonestyTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private SearchRegexTool BuildTool(int fileCount, int matchesPerFile = 1)
    {
        var root = _workspace.Dir("Core");
        for (var i = 0; i < fileCount; i++)
        {
            var body = string.Concat(Enumerable.Repeat("// ZzNeedle\n", matchesPerFile));
            _workspace.WriteFile(Path.Combine("Core", $"File{i}.cs"), body);
        }

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(SearchRegexTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // fileFilter 把候选集筛成 0 时，原措辞把它说成「scope 里没有这个模式」——
    // 而 scope 里有的是命中，只是没有一个 .txt 文件。调用方据此得出的是相反的结论。
    [Fact]
    public async Task ZeroMatchesCausedByTheFileFilter_SaysSo()
    {
        var content = await Run(BuildTool(3), """{"pattern":"ZzNeedle","fileFilter":".txt"}""");

        Assert.Contains("fileFilter '.txt'", content);
        Assert.Contains("0 file(s) matched that filter", content);
        Assert.Contains("the filter, not the pattern", content);
    }

    // 过滤留下了文件、只是没命中，就不该把锅推给过滤器
    [Fact]
    public async Task ZeroMatchesWithAMatchingFilter_DoesNotBlameTheFilter()
    {
        var content = await Run(BuildTool(3), """{"pattern":"ZzAbsentPattern","fileFilter":".cs"}""");

        Assert.Contains("fileFilter '.cs'", content);
        Assert.DoesNotContain("the filter, not the pattern", content);
    }

    // 扫描在命中上限处就停了，后面的候选文件根本没打开过。此时 allFiles 只是
    // 「已扫到的那批预览」里的文件数，把它称作 "matching files" 会把一个比真实值
    // 小一到两个数量级的数字塞给调用方当结论。
    [Fact]
    public async Task FileCountUnderTruncation_IsNotCalledTheTotalMatchingFiles()
    {
        var content = await Run(BuildTool(400), """{"pattern":"ZzNeedle","limit":100}""");

        Assert.Contains("scanning stopped at", content);
        Assert.Contains("not the total number of matching files", content);
    }

    // 未截断时那个数才真的是命中文件总数，措辞该回到原样
    [Fact]
    public async Task FileCountWithoutTruncation_IsStatedPlainly()
    {
        var content = await Run(BuildTool(60), """{"pattern":"ZzNeedle","limit":"all"}""");

        Assert.Contains("matching files are listed", content);
        Assert.DoesNotContain("not the total number of matching files", content);
    }

    // 命中上限是这轮唯一能立刻放开的旋钮，原先的出路（「narrow the pattern or the scope」）
    // 偏偏把它藏了起来
    [Fact]
    public async Task TruncationAtTheLimit_OffersRaisingTheLimit()
    {
        var content = await Run(BuildTool(400), """{"pattern":"ZzNeedle","limit":20}""");

        Assert.Contains("limit:'all'", content);
    }

    // 已经要过 'all' 的调用方不该再被劝一次 'all'
    [Fact]
    public async Task TruncationAtTheHardCap_DoesNotSuggestAllAgain()
    {
        var content = await Run(BuildTool(400), """{"pattern":"ZzNeedle","limit":"all"}""");

        Assert.DoesNotContain("limit:'all'", content);
    }

    // 单文件扫描行数上限是第三处静默减少命中的地方，此前一个字都不说
    [Fact]
    public async Task FilesCutOffByTheLineCap_AreReported()
    {
        var root = _workspace.Dir("Big");
        var lines = new List<string>();
        for (var i = 0; i < 25_000; i++) lines.Add(i == 0 ? "// ZzNeedle" : "// filler");
        _workspace.WriteFile(Path.Combine("Big", "Huge.cs"), string.Join("\n", lines));

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, """{"pattern":"ZzNeedle"}""");

        Assert.Contains("Incomplete scan", content);
        Assert.Contains("only scanned to line", content);
    }

    // 一切正常时不得凭空多出尾注——「没有尾注即完整」的另一半
    [Fact]
    public async Task CompleteScan_CarriesNoIncompletenessNote()
    {
        var content = await Run(BuildTool(3), """{"pattern":"ZzNeedle"}""");

        Assert.DoesNotContain("Incomplete scan", content);
        Assert.DoesNotContain("scanning stopped at", content);
    }
}
