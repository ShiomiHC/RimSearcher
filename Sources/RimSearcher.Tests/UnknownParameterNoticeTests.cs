using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：服务端对参数名极宽容（别名 + 大小写/下划线归一），调用方由此学到「这台服务器
// 对名字不挑」，于是把别的工具的参数类推过来是必然行为——locate 传 defType（inspect 才有）、
// trace 传 fileFilter（search_regex 才有）。这些键一律被丢弃，而返回是一份逐字正常、
// 看不出任何异常的结果，调用方据此得出的结论是「我按 X 过滤后就这些」。
public class UnknownParameterNoticeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private LocateTool BuildLocate()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzThing.cs"), "namespace Zz { public class ZzThing { } }");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static string? Notice(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        return ToolArgs.UnknownKeyNotice(tool, args.RootElement);
    }

    [Fact]
    public void UnknownKey_IsCalledOut()
    {
        var notice = Notice(BuildLocate(), """{"query":"ZzThing","bogusParam":1}""");

        Assert.NotNull(notice);
        Assert.Contains("bogusParam", notice);
        Assert.Contains("rimworld-searcher__locate accepts", notice);
    }

    // 最常见的真实误用：把 inspect 的 defType 类推到 locate 上
    [Fact]
    public void ParameterBorrowedFromAnotherTool_IsCalledOut()
    {
        var notice = Notice(BuildLocate(), """{"query":"ZzThing","defType":"ThingDef"}""");

        Assert.NotNull(notice);
        Assert.Contains("defType", notice);
    }

    // 健康调用零开销、零额外 token
    [Fact]
    public void DeclaredParameters_ProduceNoNotice()
    {
        Assert.Null(Notice(BuildLocate(), """{"query":"ZzThing","scope":"all","limit":5}"""));
    }

    // 合法别名不能被误报成被忽略——那比不提示更糟
    [Theory]
    [InlineData("""{"name":"ZzThing"}""")]
    [InlineData("""{"symbol":"ZzThing"}""")]
    [InlineData("""{"query":"ZzThing","maxResults":5}""")]
    [InlineData("""{"query":"ZzThing","sources":"vanilla"}""")]
    public void AcceptedAliases_ProduceNoNotice(string json)
    {
        Assert.Null(Notice(BuildLocate(), json));
    }

    // 大小写/下划线归一后仍算命中：max_results 与 maxResults 是同一个键
    [Fact]
    public void NormalisedKeySpelling_ProducesNoNotice()
    {
        Assert.Null(Notice(BuildLocate(), """{"query":"ZzThing","max_results":5}"""));
    }

    // 上面那条按一份手写清单抽查，覆盖到哪算哪。这一条把清单换成**产品源码自己**：
    // 每个工具文件里传给 ToolArgs 读取函数的键字面量，一个不落地问一遍「这个键会不会被
    // 报成被忽略」。
    //
    // 要防的是一种自相矛盾的返回：键读得进来、真的改变了结果，同一份返回却在末尾写着
    // `_Ignored unknown parameter: 'top'_`。别名此前有两个产地——读取点写一遍、
    // ExtraAcceptedKeys 再写一遍——两边对不上不会有任何东西喊出来。
    //
    // 判据只共用**名单**（源码里的那些字面量），判定仍走产品自己的 UnknownKeyNotice：
    // 闸这边不重写一份「什么算认得」，否则两边一起错时照绿。
    [Fact]
    public void EveryKeyAToolReads_IsDeclaredAsAccepted()
    {
        var undeclared = new List<string>();

        foreach (var (tool, source) in EveryToolWithItsSource())
        {
            var text = File.ReadAllText(source);

            var keys = KeyLiteralsIn(text);

            // scope / limit 两族的读取点住在 ScopeArgs 里，故按名单取——不在闸这边重列一遍
            if (text.Contains("ScopeArgs.Resolve", StringComparison.Ordinal))
                keys.UnionWith(ScopeArgs.ScopeKeys);
            if (text.Contains("ScopeArgs.GetDisplayLimit", StringComparison.Ordinal))
                keys.UnionWith(ScopeArgs.LimitKeys);

            foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (Notice(tool, $$"""{"{{key}}":1}""") != null)
                    undeclared.Add($"{tool.Name}: '{key}'");
            }
        }

        Assert.True(undeclared.Count == 0,
            $"{undeclared.Count} 个键读得进来却会被报成被忽略——同一份返回既按它改了结果、"
            + "又说它被丢掉了：\n" + string.Join("\n", undeclared));
    }

    // 反方向：ExtraAcceptedKeys 里的键必须真读得到。声明成认得却没有任何读取点，等于把这个
    // 键**静默吞掉**——调用方传 `symbolName` 拿到的既不是结果也不是提示。这一侧当年真的漏过：
    // trace 声明 symbolName / typeName 而两处读取点都不认，locate 声明 term 同理，
    // sync_sources 声明整个 scope 一族而它按源名工作、一个都不读。
    //
    // schema 里的属性名不在这条判据内：那是工具的正式参数，认得它们是定义而不是额外声明。
    [Fact]
    public void EveryKeyAToolDeclares_IsActuallyRead()
    {
        var unread = new List<string>();

        foreach (var (tool, source) in EveryToolWithItsSource())
        {
            var text = File.ReadAllText(source);

            var read = KeyLiteralsIn(text);
            if (text.Contains("ScopeArgs.Resolve", StringComparison.Ordinal))
                read.UnionWith(ScopeArgs.ScopeKeys);
            if (text.Contains("ScopeArgs.GetDisplayLimit", StringComparison.Ordinal))
                read.UnionWith(ScopeArgs.LimitKeys);

            foreach (var declared in tool.ExtraAcceptedKeys)
            {
                if (!read.Contains(declared)) unread.Add($"{tool.Name}: '{declared}'");
            }
        }

        Assert.True(unread.Count == 0,
            $"{unread.Count} 个键声明成认得却没有任何读取点，传进来会被静默吞掉：\n"
            + string.Join("\n", unread));
    }

    // 传给 ToolArgs 读取函数的键字面量。带空格的那些不是键（GetOptionalName 的
    // whatItNames 槽收的是 "a member name" 这类说明文字，用来拼缺参提示）。
    private static ISet<string> KeyLiteralsIn(string text)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match call in Regex.Matches(
                     text, @"ToolArgs\.(?:Get\w+|TryGetElement)\((?:[^()]|\([^()]*\))*\)"))
        foreach (Match literal in Regex.Matches(call.Value, "\"(?<key>[^\"]*)\""))
        {
            var key = literal.Groups["key"].Value;
            if (key.Length > 0 && !key.Contains(' ')) keys.Add(key);
        }

        return keys;
    }

    private IEnumerable<(ITool Tool, string Source)> EveryToolWithItsSource(
        [CallerFilePath] string here = "")
    {
        var root = _workspace.Dir("Every");
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var config = new AppConfig { SourceHistoryDepth = 1, GameVersion = "1.6" };
        var entry = new SourcePathEntry { Name = "Core", Path = root, AssemblyPaths = [_workspace.Dir("asm")] };
        var sync = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));

        ITool[] tools =
        [
            new LocateTool(indexer, defIndexer, catalog),
            new InspectTool(indexer, defIndexer, catalog),
            new ReadCodeTool(indexer, catalog),
            new TraceTool(indexer, catalog),
            new SearchRegexTool(indexer, catalog),
            new ListDirectoryTool(),
            new SyncSourcesTool(sync),
        ];

        var toolsDirectory = Path.Combine(
            Directory.GetParent(Path.GetDirectoryName(here)!)!.FullName, "RimSearcher.Server", "Tools");

        foreach (var tool in tools)
            yield return (tool, Path.Combine(toolsDirectory, $"{tool.GetType().Name}.cs"));
    }

    // 每个工具都要声明得住自己的别名，否则上线即误报
    [Fact]
    public void EveryToolAcceptsItsOwnAliases()
    {
        var root = _workspace.Dir("Core2");
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var cases = new (ITool Tool, string Json)[]
        {
            (new InspectTool(indexer, defIndexer, catalog), """{"defName":"Zz","defTypeName":"ThingDef","scope":"all"}"""),
            (new TraceTool(indexer, catalog), """{"symbolName":"Zz","traceMode":"usages","limit":5}"""),
            (new ReadCodeTool(indexer, catalog), """{"filePath":"Zz.cs","member":"Tick","typeName":"Zz"}"""),
            (new SearchRegexTool(indexer, catalog), """{"regex":"Zz","extension":".cs","caseInsensitive":true}"""),
            (new ListDirectoryTool(), """{"dir":"/tmp","count":5,"skip":0}"""),
        };

        foreach (var (tool, json) in cases)
            Assert.Null(Notice(tool, json));
    }
}
