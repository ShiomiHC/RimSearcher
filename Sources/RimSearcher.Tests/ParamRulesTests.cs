using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// ParamRules 的遍历器与反面用例，与输出层 GrammarRulesTests 同一个分工。
//
// 反面用例是必需的，理由与 GrammarRulesTests 开头那段逐字相同：**本层每一个洞的失效签名都是
// 「继续绿」**。一道规则写歪了（正则不匹配、名单取空、循环没进去）的表现同样是绿，与「没有
// 违规」逐字同形。故每条规则都配一条故意违规的输入，验它真的会红。
[Collection("PathSecurity")]
public class ParamRulesTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public ParamRulesTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    // ---- 遍历器 ----

    [Fact]
    public void EverySchemaPropertyHasAReadingPoint()
    {
        var violations = EveryTool()
            .SelectMany(tool => ParamRules.DeclaredPropertiesAreRead(FactsFor(tool)))
            .ToList();

        Assert.True(violations.Count == 0, ParamRules.Describe("洞-1", violations));
    }

    [Fact]
    public void EveryQuotedParameterNameInProseResolves()
    {
        var tools = EveryTool();
        var parametersByTool = tools.ToDictionary(
            tool => tool.Name,
            tool => (IReadOnlyList<string>)ParamRules.SchemaOf(tool).Properties);

        var violations = tools
            .SelectMany(tool => ParamRules.QuotedParameterNamesResolve(FactsFor(tool), parametersByTool))
            .ToList();

        Assert.True(violations.Count == 0, ParamRules.Describe("洞-9", violations));
    }

    // ---- 反面用例 ----

    // schema 声明了一个谁也不读的属性。这正是洞-1 要防的那一形：UnknownKeyNotice 从 schema
    // 反射属性名，故它会把这个键认作合法**然后静默吞掉**——调用方传进来既不生效也不提示。
    [Fact]
    public void ARuleFires_WhenASchemaPropertyHasNoReadingPoint()
    {
        var facts = new ParamRules.Facts(
            EveryTool()[0],
            SchemaProperties: ["path", "fileFilter"],
            SchemaDescriptions: new Dictionary<string, string>(),
            KeysActuallyRead: ["path"]);

        var violation = Assert.Single(ParamRules.DeclaredPropertiesAreRead(facts));
        Assert.Contains("fileFilter", violation.Detail);
    }

    // 散文里带引号地提到**别的工具**的参数名，句中又没点出是哪个工具。§2 己-6 就是这一形。
    [Fact]
    public void ARuleFires_WhenProseNamesAnotherToolsParameterWithoutSayingWhose()
    {
        var violation = Assert.Single(
            ParamRules.QuotedParameterNamesResolve(
                FactsWithProse("Entries are listed under 'scope'."),
                OtherToolOwns("scope")));

        Assert.Contains("'scope'", violation.Detail);
    }

    // 同一句里点了工具名就不算骗人——对照组是 InspectTool 那处跨工具引用（`read_code
    // extractClass truncates at …`），§1 把它列为「已经做对的做法」。
    [Fact]
    public void TheRuleIsSilent_WhenTheSentenceNamesTheOtherTool()
    {
        Assert.Empty(
            ParamRules.QuotedParameterNamesResolve(
                FactsWithProse("Entries are listed under locate's 'scope'."),
                OtherToolOwns("scope")));
    }

    // **值**不是参数名。'all' / 'usages' / 'inheritors' 这类记号满篇都是，把它们卷进来这条
    // 规则就成了噪音发生器——射程只到「记号指认得上指认不上」，见 ParamRules 里那段。
    [Fact]
    public void TheRuleIsSilent_ForQuotedValuesThatAreNotAnyToolsParameter()
    {
        Assert.Empty(
            ParamRules.QuotedParameterNamesResolve(
                FactsWithProse("Pass 'all' to expand up to the server cap."),
                OtherToolOwns("scope")));
    }

    // ---- 语料 ----

    private ParamRules.Facts FactsFor(ITool tool)
    {
        var (properties, descriptions) = ParamRules.SchemaOf(tool);
        return new ParamRules.Facts(tool, properties, descriptions, [.. ToolSourceKeys.ReadBy(tool)]);
    }

    private ParamRules.Facts FactsWithProse(string description)
        => new(new ProseTool(description), SchemaProperties: ["path"],
            SchemaDescriptions: new Dictionary<string, string>(), KeysActuallyRead: ["path"]);

    private static Dictionary<string, IReadOnlyList<string>> OtherToolOwns(string parameter)
        => new() { ["rimworld-searcher__locate"] = new[] { parameter } };

    private sealed class ProseTool(string description) : ITool
    {
        public string Name => "rimworld-searcher__prose";
        public string Description => description;
        public object JsonSchema => new { type = "object" };

        public Task<ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement arguments, CancellationToken cancellationToken,
            IProgress<double>? progress = null)
            => Task.FromResult(new ToolResult("ok"));
    }

    // 两个源两个根，与 ToolListSnapshotTests 同一套：scope 的说明与 list_directory 的
    // RootsSentence 都读运行时状态，空语料下它们说的是另一句话。
    private ITool[] EveryTool()
    {
        var vanillaRoot = _workspace.Dir("Vanilla");
        var miliraRoot = _workspace.Dir("Milira");
        PathSecurity.Initialize([vanillaRoot, miliraRoot]);

        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", vanillaRoot), ("milira", miliraRoot)], null, null);

        var config = new AppConfig { SourceHistoryDepth = 1, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "vanilla", Path = vanillaRoot, AssemblyPaths = [_workspace.Dir("asm")]
        };
        var sync = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));

        return ToolRegistry.Create(indexer, defIndexer, catalog, sync);
    }
}
