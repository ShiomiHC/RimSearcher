using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 参数层重构期间的**字节级**闸，与呈现层那 73 份同一个作用、同一套机制（SnapshotGate）。
//
// 立它的理由在参数层指导 §4 乙：`Description` 与 schema 里每个 `description` 走 tools/list、
// 不走 tools/call，故一个字都不进那 73 份基线。**改文案改错了，全量测试一条都不会红。**
// 这是参数层与前两层最大的不同，也是最大的风险——前两层动手时至少有字节级的 diff 兜着。
//
// 这份基线**不判对错**，只回答「这次改动动了哪些字」。判对错是别的闸的事（schema 与取值
// 代码对不对得上、散文里的数与常量对不对得上），它们要建在这份 diff 看得见的前提上。
//
// 一个工具一份，不是整份响应一份：后者更简单，但一个工具改一个字会让所有人的 diff 都变。
//
// 走的是**真协议**：起一个 RimSearcher、注册产品那七个工具、喂一行 tools/list，从响应里取。
// 不在这里照着 RimSearcher.cs 重拼一份 `{name, title, description, inputSchema, annotations}`
// ——那就是判据甲说的「拿闸自己写的一份去验产品那一份」，投影漏了个字段时两边一起漏，照绿。
[Collection("PathSecurity")]
public class ToolListSnapshotTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public ToolListSnapshotTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    // 这一族基线在 Snapshots/ 下的目录名。SnapshotGrammarGateTests 要按它把这一族排除在
    // 共用输出文法之外（理由见那边），故它得有一个产地——两处各写一遍 "tools_list" 的话，
    // 这里改个名，那边的排除会静默失效或静默扩大，而两种失效都表现为「继续绿」。
    public const string Area = "tools_list";

    // 工具名单从注册表来。少一个工具、多一个工具，这条 Theory 的用例数跟着变——多出来的那个
    // 第一次跑必然红（基线不存在），这正是「新加工具要补基线」该有的表现。
    public static TheoryData<string> EveryTool => [.. RegisteredTools.Titles];

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task ToolsList_IsUnchanged(string title)
    {
        var listed = await ListToolsAsync();

        var tool = Assert.Single(
            listed, entry => entry.GetProperty("title").GetString() == title);

        SnapshotGate.Verify($"{Area}/{title}", Normalize(Render(tool)));
    }

    // ---- 语料 ----
    //
    // Description 与 schema 里有两处**读运行时状态**，故语料不能是空的，也不能只有一个源：
    //   - locate / inspect / trace / read_code / search_regex 的 `scope` 说明走
    //     ScopeAndLimitArgs.ScopeSchemaProperty(catalog)，把真实源名注进去；
    //   - list_directory 的 Description 末尾走 RootsSentence，把 PathSecurity 的根、根数、
    //     源数注进去，且 PathSecurity 关掉时整句换成另一句。
    // 两个源两个根、且 PathSecurity 开着，是能同时照到这两处的最小语料。
    private async Task<JsonElement[]> ListToolsAsync()
    {
        var vanillaRoot = _workspace.Dir("Vanilla");
        var miliraRoot = _workspace.Dir("Milira");
        PathSecurity.Initialize([vanillaRoot, miliraRoot]);

        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var catalog = ScopeCatalog.Build(
            [("vanilla", vanillaRoot), ("milira", miliraRoot)], null, null);

        var config = new AppConfig { SourceHistoryDepth = 1, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "vanilla", Path = vanillaRoot, AssemblyPaths = [_workspace.Dir("asm")]
        };
        var sync = new SourceSyncService(
            config, new ResolvedSources([entry], []), _workspace.Dir("cache"));

        var output = new StringWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);
        foreach (var tool in ToolRegistry.Create(indexer, defIndexer, catalog, sync))
            server.RegisterTool(tool);

        await server.RunAsync(new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));

        var response = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line.Trim()).RootElement.Clone())
            .Single(line => line.TryGetProperty("result", out var r) && r.TryGetProperty("tools", out _));

        return [.. response.GetProperty("result").GetProperty("tools").EnumerateArray()];
    }

    // ---- 呈现 ----

    // 缩进重排是为了让基线可读、可 diff——一份 tools/list 挤成一行的话，「动了哪些字」
    // 这个问题在 review 里没法回答。转义放宽同理：schema 的说明里满是 'scope' 这类单引号，
    // 默认编码器会把它们全写成 '。
    //
    // 代价要说清：这份基线钉的是**内容**，不是线上那一行的逐字形态——序列化选项本身
    // （缩进与否、编码器）不在它的射程内。参数层要防的是文案与声明改动，那些都在内容里。
    private static readonly JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Render(JsonElement tool) => JsonSerializer.Serialize(tool, Readable);

    // 随环境变的只有临时目录那一段。JSON 里的路径分隔符是转义过的（`\\`），故先按转义形
    // 替换一遍，再把 <ROOT> 之后那一段的分隔符归一——只动这一段，别处的反斜杠不是路径。
    private string Normalize(string content)
    {
        var root = _workspace.Root;

        content = content
            .Replace(root.Replace("\\", "\\\\"), "<ROOT>", StringComparison.OrdinalIgnoreCase)
            .Replace(root, "<ROOT>", StringComparison.OrdinalIgnoreCase)
            .Replace(root.Replace('\\', '/'), "<ROOT>", StringComparison.OrdinalIgnoreCase);

        return Regex.Replace(content, @"<ROOT>[^\s""]*", m => m.Value.Replace("\\\\", "/").Replace('\\', '/'));
    }
}
