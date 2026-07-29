using System.Text.Json;
using System.Text.RegularExpressions;
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

    // ---- 洞-5 / 洞-6：缺参提示那份名单，与 schema、与读取点 ----
    //
    // 事实侧是**真跑一次**：给工具喂一个空参数对象，接住它自己抛的那条缺参消息，从里面切出
    // `Required:` 与 `All parameters:` 两行。要验的就是调用方缺参时看到的那段字，不是产品
    // 内部某个私有字段的值——后者一样是「拿声明对声明」。
    //
    // 没有必填参数的工具（sync_sources）不在这里调用：它一跑就是真去同步。它由洞-6 那条
    // 从两侧同时盖住——schema 的 required 为空，源码里也确实没有 GetRequired* 调用点。

    [Fact]
    public async Task EveryParameterIsListedInTheUsageHint()
    {
        var violations = new List<ParamViolation>();

        foreach (var tool in EveryTool())
        {
            if (ParamRules.RequiredOf(tool).Count == 0) continue;

            var (_, allParameters) = ParamRules.UsageIn(await MissingArgumentMessage(tool));
            violations.AddRange(ParamRules.AllParametersMatchesTheSchema(FactsFor(tool), allParameters));
        }

        Assert.True(violations.Count == 0, ParamRules.Describe("洞-5", violations));
    }

    [Fact]
    public async Task EveryAliasOfARequiredParameterIsListedInTheUsageHint()
    {
        var violations = new List<ParamViolation>();

        foreach (var tool in EveryTool())
        {
            var required = ToolSourceKeys.RequiredBy(tool);
            if (required.Count == 0) continue;

            var (summary, _) = ParamRules.UsageIn(await MissingArgumentMessage(tool));
            violations.AddRange(ParamRules.RequiredAliasesAreComplete(FactsFor(tool), summary, required));
        }

        Assert.True(violations.Count == 0, ParamRules.Describe("洞-5′", violations));
    }

    [Fact]
    public void EveryRequiredPropertyHasAGetRequiredCallSite()
    {
        var violations = EveryTool()
            .SelectMany(tool => ParamRules.RequiredListMatchesTheReadingPoints(
                FactsFor(tool), ParamRules.RequiredOf(tool), ToolSourceKeys.RequiredBy(tool)))
            .ToList();

        Assert.True(violations.Count == 0, ParamRules.Describe("洞-6", violations));
    }

    private static async Task<string> MissingArgumentMessage(ITool tool)
    {
        var error = await Assert.ThrowsAsync<ToolArgumentException>(() => Run(tool, new { }));
        return error.Message;
    }

    // ---- 洞-2 / 洞-3：schema 广告的 default 与 maximum，就是服务端真正做的那件事 ----
    //
    // 这两条的事实侧是**行为**，不是另一份声明。理由是判据甲：拿 schema 的 default 去比对
    // 「schema 的 default 引用的那个常量」，两边同时错时一片绿——那正是「schema 验 schema」。
    // 故这里从 schema 反射把数取出来（名单侧），再造一份刚好越过它的语料真跑一次（事实侧）。
    //
    // 现成的反例就在指导 §3：list_directory 的 100 独立写了四遍（P5 已收成一个产地），
    // 1000 写了三遍；而现有的三条夹紧测试验的都是「服务端确实夹了」，**没有一条验
    // 「夹到了 schema 广告的那个数」**——服务端夹到 999 而 schema 广告 1000，一条都不会红。
    //
    // 只覆盖这两个工具，因为只有它们的 schema 真的发了 default / maximum 这两个键。
    // 走 LimitSchemaProperty 的那三个（locate / trace / search_regex）一个都不发（丙-1），
    // 无从比对——那不是这条闸照不到，是**没有东西可照**，属于 P7 那条政策要定的事。

    [Fact]
    public async Task ListDirectory_ClampsToTheMaximumItAdvertises()
    {
        var advertised = SchemaNumber("rimworld-searcher__list_directory", "limit", "maximum");

        var (tool, directory) = ListDirectoryOver(advertised);
        var content = await Run(tool, new { path = directory, limit = advertised * 10 });

        Assert.Equal(advertised, CountEntries(content));
    }

    [Fact]
    public async Task ListDirectory_DefaultsToTheLimitItAdvertises()
    {
        var advertised = SchemaNumber("rimworld-searcher__list_directory", "limit", "default");

        var (tool, directory) = ListDirectoryOver(advertised);
        var content = await Run(tool, new { path = directory });

        Assert.Equal(advertised, CountEntries(content));
    }

    [Fact]
    public async Task ReadCode_ClampsToTheMaximumItAdvertises()
    {
        var advertised = SchemaNumber("rimworld-searcher__read_code", "lineCount", "maximum");

        var (tool, file) = ReadCodeFileOver(advertised);
        var content = await Run(tool, new { path = file, startLine = 0, lineCount = advertised * 10 });

        Assert.Equal(advertised, CountNumberedLines(content));
    }

    [Fact]
    public async Task ReadCode_DefaultsToTheLineCountItAdvertises()
    {
        var advertised = SchemaNumber("rimworld-searcher__read_code", "lineCount", "default");

        var (tool, file) = ReadCodeFileOver(advertised);
        var content = await Run(tool, new { path = file, startLine = 0 });

        Assert.Equal(advertised, CountNumberedLines(content));
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

    // 用法说明少列一个 schema 属性。§2 丁那一形：加一个 schema 属性、忘了加进散文，缺参提示
    // 就少列一个参数，而调用方照这句改会以为这个工具没有这个参数。
    [Fact]
    public void ARuleFires_WhenTheUsageHintOmitsASchemaProperty()
    {
        var violation = Assert.Single(
            ParamRules.AllParametersMatchesTheSchema(FactsWithSchema("path", "limit"), "path (required)."));

        Assert.Contains("'limit'", violation.Detail);
    }

    // 反方向：列了一个 schema 里没有的名字。照这句传进来会吃到一句未知参数提示。
    [Fact]
    public void ARuleFires_WhenTheUsageHintListsAParameterThatDoesNotExist()
    {
        var violation = Assert.Single(
            ParamRules.AllParametersMatchesTheSchema(FactsWithSchema("path"), "path (required), limit."));

        Assert.Contains("'limit'", violation.Detail);
    }

    // 括号里是说明不是名单。`fileFilter (aliases: fileExtension, extension, ext)` 里那三个
    // 逗号后面的词一个都不是参数名——不剥括号的话这条规则会把它们全报成「schema 里没有」，
    // 那是**误报**，而误报会逼着下一个人把规则关掉。
    [Fact]
    public void TheRuleIsSilent_ForNamesThatOnlyAppearInsideAParenthetical()
    {
        Assert.Empty(ParamRules.AllParametersMatchesTheSchema(
            FactsWithSchema("pattern", "fileFilter"),
            "pattern (required), fileFilter (aliases: fileExtension, extension, ext)."));
    }

    // 读取点收的别名，缺参提示没列。locate 的 `term` 就是这一形（P6 已修）。
    [Fact]
    public void ARuleFires_WhenARequiredParametersAliasIsNotListed()
    {
        var violation = Assert.Single(
            ParamRules.RequiredAliasesAreComplete(
                FactsWithSchema("symbol"), "symbol (a class). Aliases accepted: query.",
                new Dictionary<string, string[]> { ["symbol"] = ["query", "symbolName"] }));

        Assert.Contains("'symbolName'", violation.Detail);
    }

    // 反方向：列了一个读取点不认的别名。这一条比漏列更坏——照这句传进去，那个必填参数照样算缺。
    [Fact]
    public void ARuleFires_WhenTheUsageHintInventsAnAlias()
    {
        var violation = Assert.Single(
            ParamRules.RequiredAliasesAreComplete(
                FactsWithSchema("symbol"), "symbol (a class). Aliases accepted: query, nope.",
                new Dictionary<string, string[]> { ["symbol"] = ["query"] }));

        Assert.Contains("'nope'", violation.Detail);
    }

    // 两个必填参数，而 `Aliases accepted:` 没说这串是谁的。判红而不是挑一个猜：猜错的方向是
    // 把另一个参数的别名算作已列出，那是变松。
    [Fact]
    public void ARuleFires_WhenAliasesAreListedWithoutSayingWhichParameterOwnsThem()
    {
        var violations = ParamRules.RequiredAliasesAreComplete(
            FactsWithSchema("symbol", "mode"), "symbol and mode. Aliases accepted: query.",
            new Dictionary<string, string[]> { ["symbol"] = ["query"], ["mode"] = [] }).ToList();

        Assert.Contains(violations, v => v.Detail.Contains("没说是谁的"));
    }

    [Fact]
    public void ARuleFires_WhenSchemaCallsAParameterRequiredButNoCodeDoes()
    {
        var violation = Assert.Single(
            ParamRules.RequiredListMatchesTheReadingPoints(
                FactsWithSchema("name"), ["name"], new Dictionary<string, string[]>()));

        Assert.Contains("'name'", violation.Detail);
    }

    [Fact]
    public void ARuleFires_WhenCodeDemandsAParameterSchemaDoesNotCallRequired()
    {
        var violation = Assert.Single(
            ParamRules.RequiredListMatchesTheReadingPoints(
                FactsWithSchema("name"), [], new Dictionary<string, string[]> { ["name"] = [] }));

        Assert.Contains("'name'", violation.Detail);
    }

    // ---- 洞-2 / 洞-3 的取数与语料 ----

    // 从 schema 里把那个数取出来。**只取名单，不取判断**：取的是「这个工具对外广告了多少」，
    // 拿它去比对的是真跑一次的行为，不是另一份声明。
    private int SchemaNumber(string toolName, string property, string key)
    {
        var tool = Assert.Single(EveryTool(), t => t.Name == toolName);

        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(tool.JsonSchema));

        var found = schema.RootElement
            .GetProperty("properties").GetProperty(property)
            .TryGetProperty(key, out var value);

        Assert.True(found, $"{toolName} 的 schema 没给 '{property}' 声明 '{key}'，这条判据无从比对。");
        return value.GetInt32();
    }

    // 刚好越过那个数一条的目录。多一条就够：要验的是「夹到了广告的那个数」，不是「夹住了」。
    private (ListDirectoryTool Tool, string Directory) ListDirectoryOver(int count)
    {
        var root = _workspace.Dir("Paged");
        for (var i = 0; i <= count; i++) _workspace.WriteFile(Path.Combine("Paged", $"Zz{i:D5}.cs"), "// x\n");

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([root]);
        return (new ListDirectoryTool(), root);
    }

    private (ReadCodeTool Tool, string File) ReadCodeFileOver(int lines)
    {
        var root = _workspace.Dir("Long");
        _workspace.WriteFile(
            Path.Combine("Long", "ZzLong.cs"),
            string.Join("\n", Enumerable.Range(1, lines + 1).Select(i => $"// line {i}")) + "\n");

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([root]);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null)),
            Path.Combine(root, "ZzLong.cs"));
    }

    // 条目行：`Zz00001.cs`。表头与折叠行都不以 Zz 打头，故按前缀数就够，不必解析版面。
    private static int CountEntries(string content)
        => content.Split('\n').Count(line => line.StartsWith("Zz", StringComparison.Ordinal));

    // 行区间模式的正文行：`L123: …`
    private static int CountNumberedLines(string content)
        => content.Split('\n').Count(line => Regex.IsMatch(line, @"^L\d+: "));

    private static async Task<string> Run(ITool tool, object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
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

    private static ParamRules.Facts FactsWithSchema(params string[] properties)
        => new(new ProseTool("no prose."), properties,
            SchemaDescriptions: new Dictionary<string, string>(), KeysActuallyRead: properties);

    private static Dictionary<string, IReadOnlyList<string>> OtherToolOwns(string parameter)
        => new() { ["rimworld-searcher__locate"] = new[] { parameter } };

    private sealed class ProseTool(string description) : ITool
    {
        public string Name => "rimworld-searcher__prose";
        public string Description => description;
        public object JsonSchema => new { type = "object" };

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments, CancellationToken cancellationToken,
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
