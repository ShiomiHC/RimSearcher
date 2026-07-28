using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：**带扩展名的文件名查询拿不到路径**。
//
// 文件名是 locate 的一等查询目标（README「支持内容」头一条就列着它），而 `locate 'CompShield.cs'`
// 回的是一条 54% 的类型名、一条路径都没有——调用方多半正是要拿路径去喂 read_code，
// 而 54% 读起来像「这个文件不在索引里，只找到个沾边的类」。
//
// 三道闸叠加，缺一条都修不好：
//   ① 比较层：`Path.GetFileNameWithoutExtension(entry) == rawQuery`，左边去了扩展名、
//      右边没去，带扩展名时恒不等；
//   ② 去重层：`.cs` 反编译产物的文件名逐个对应类型名，故同名类型必然已在 C# Types 段列过，
//      去重必然命中。①②叠加使 `.cs` 文件名查询的 Files 段**在结构上永远不可达**；
//   ③ 打分层（原 spec 未记，2026-07-28 实测发现）：模糊文件搜索的键是**去掉扩展名**的基名，
//      于是分数是拿 `Pawn.cs` 跟 `Pawn` 比的，编辑距离恒为 3——长名靠 70×similarity 还能得
//      几十分（`CompShield.cs` 54%），短名连 `similarity >= 0.6` 都够不到、直接 0 分出局。
//      实测 `locate 'Pawn.cs'` 回 671 条成员、一条路径都没有。
//      也就是说这一支此前的可达性取决于文件名有多长，而那不是任何人立过的判据。
//
// 判据：调用方**显式打了扩展名**，那就是在问文件、不是在问类型。此时「文件与同名类型是同一件事
// 的两种写法」这条去重判据不成立——它成立的前提是调用方没说清要哪一个。
public class LocateFileNameQueryTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private LocateTool BuildTool(params (string RelPath, string Body)[] files)
    {
        var root = _workspace.Dir("Core");
        foreach (var (relPath, body) in files)
            _workspace.WriteFile(Path.Combine("Core", relPath), body);

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

    // Files 段的条目行以 "- " 打头且是一条绝对路径
    private static List<string> FileRows(string content)
    {
        var start = content.IndexOf("**Files**", StringComparison.Ordinal);
        if (start < 0) return [];

        var section = content[start..];
        var end = section.IndexOf("\n**", StringComparison.Ordinal);
        if (end >= 0) section = section[..end];

        return section.Split('\n')
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .ToList();
    }

    private const string ShieldSource = """
        namespace Zz
        {
            public class ZzShield
            {
                public void ZzAbsorb() { }
            }
        }
        """;

    // ── ① + ② 两道闸：`.cs` 文件名查询 ────────────────────────────────

    // 本文件的要害用例。C# Types 段有命中（`ZzShield` 40%，走的是 55×similarity 那一支），
    // 故走的是「精确补充」而不是零命中兜底——正是此前在结构上不可达的那一支。
    [Fact]
    public async Task DottedCsQuery_ReturnsThePathEvenThoughTheSameNamedTypeIsAlreadyListed()
    {
        var tool = BuildTool(("ZzShield.cs", ShieldSource));

        var content = await Run(tool, """{"query":"ZzShield.cs"}""");

        var rows = FileRows(content);
        Assert.Single(rows);
        // 行尾的 (100%) 是 R57 那条契约在这一段的兑现处，一并钉住
        Assert.EndsWith("ZzShield.cs (100%)", rows[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathRooted(rows[0]), $"Files 段给的必须是能直接喂 read_code 的全路径：{rows[0]}");

        // 两段并存不算把同一条结果说两遍：调用方指名了要文件，类型段回答的是另一个问题
        Assert.Contains("`ZzShield`", content);
    }

    // 反面：**没**打扩展名时去重判据仍然成立，Files 段不该出现。
    // 这一条是上一条的保险——把去重整个删掉也能让上一条变绿，那才是真正的回退。
    [Fact]
    public async Task BareTypeNameQuery_StillHidesTheFileThatMerelyRepeatsTheTypeName()
    {
        var tool = BuildTool(("ZzShield.cs", ShieldSource));

        var content = await Run(tool, """{"query":"ZzShield"}""");

        Assert.Contains("`ZzShield` (100%)", content);
        Assert.Empty(FileRows(content));
    }

    // 带命名空间的全名查询是 locate 的一等输入，而 `Path.GetExtension("Zz.Ns.ZzShield")`
    // 会把 `.ZzShield` 算成扩展名。判定必须收窄到索引真正收的那两种（见 SourceIndexer 的扫描判据），
    // 否则每一个全名查询都会被当成文件名查询。
    [Theory]
    [InlineData("Zz.Ns.ZzShield")]
    [InlineData("Zz.ZzShield")]
    public async Task NamespacedSymbolQuery_IsNotMistakenForAFileName(string query)
    {
        var tool = BuildTool(("ZzShield.cs", ShieldSource));

        var content = await Run(tool, $$"""{"query":"{{query}}"}""");

        Assert.Empty(FileRows(content));
    }

    // ── ③ 打分层：短文件名 ────────────────────────────────────────────

    // `Zqa` 只有 3 个字符，对 `Zqa.cs` 的编辑距离是 3、maxLength 是 6，similarity 0.5——
    // `ed <= 3 && similarity >= 0.75` 与 `similarity >= 0.6` 两支都够不到，子串支也不成立
    // （`zqa` 里找不到 `zqa.cs`），**分数归零**。于是只把上面两道 Where 改对，这一条仍是红的。
    [Fact]
    public async Task ShortCsFileName_IsReachableThoughItScoresZeroOnTheFuzzyPath()
    {
        var tool = BuildTool(("Zqa.cs", "namespace Zq { public class Zqa { } }"));

        var content = await Run(tool, """{"query":"Zqa.cs"}""");

        var rows = FileRows(content);
        Assert.Single(rows);
        Assert.EndsWith("Zqa.cs (100%)", rows[0], StringComparison.OrdinalIgnoreCase);
    }

    // 同一条判据对 .xml 同样成立，且 XML 那边没有「同名类型」可去重——钉住的是打分层那一道
    [Fact]
    public async Task ShortXmlFileName_IsReachableToo()
    {
        var tool = BuildTool(("Zqb.xml", "<Defs></Defs>"));

        var content = await Run(tool, """{"query":"Zqb.xml"}""");

        Assert.Contains("Zqb.xml", string.Join("\n", FileRows(content)));
    }

    // ── 零命中兜底那一支不许变形 ──────────────────────────────────────

    // 兜底支的用途就是模糊列出若干条（查 `Bodies_Humanlike.xml` 会顺带给出 `Races_Humanlike.xml`，
    // 那是有用的）。精确命中只是补进最前面，不许把邻居挤掉，也不许把自己列两遍。
    [Fact]
    public async Task ZeroHitFallback_StillListsFuzzyNeighbours_AndDoesNotDuplicateTheExactHit()
    {
        var tool = BuildTool(
            ("ZzAlpha.xml", "<Defs></Defs>"),
            ("ZzAlphaBeta.xml", "<Defs></Defs>"));

        var content = await Run(tool, """{"query":"ZzAlpha.xml"}""");

        var rows = FileRows(content);
        Assert.Equal(2, rows.Count);
        // 精确那条必须带 100%——它同时在模糊结果里出现过，而去重若保留模糊那一份，这条真正
        // 逐字同名的文件会印成四十来分，跟表头的 `(1 at 100%)` 当场打架
        Assert.Contains(rows, r => r.EndsWith("ZzAlpha.xml (100%)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.Contains("ZzAlphaBeta.xml (", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(rows.Count, rows.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 表头就地说清「4 条里有几条是你问的那个文件」。混合情形（精确 + 近名）在版面上
        // 逐字同形，而 bare N 按 F30 的契约读作完整集——完整不等于精确，第十轮盲测差点
        // 把这里的 2 读成 4。
        Assert.Contains("2 files (1 at 100%)", content);
    }

    // ── 准入条件不动（F26 立的判据） ──────────────────────────────────

    // `type:` 这类带过滤前缀的查询本来就该只回那一段。扩展名的新判据不许把 Files 段
    // 塞回带前缀的查询里。
    [Fact]
    public async Task FilteredQueryWithAnExtension_StillReturnsOnlyItsOwnSection()
    {
        var tool = BuildTool(("ZzShield.cs", ShieldSource));

        var content = await Run(tool, """{"query":"type:ZzShield.cs"}""");

        Assert.Empty(FileRows(content));
    }

    // 同名文件散在多个源里时全都要给出来——「读哪一份」是调用方的决定，
    // 而 read_code 收基名时会按 scope 优先级静默几选一（见 F16d）。
    [Fact]
    public async Task SameNamedFilesInSeveralSources_AreAllListed()
    {
        var vanilla = _workspace.Dir("Vanilla");
        var mod = _workspace.Dir("Mod");
        _workspace.WriteFile(Path.Combine("Vanilla", "ZzShared.xml"), "<Defs></Defs>");
        _workspace.WriteFile(Path.Combine("Mod", "ZzShared.xml"), "<Defs></Defs>");

        var indexer = new SourceIndexer();
        indexer.Scan(vanilla);
        indexer.Scan(mod);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new LocateTool(
            indexer, defIndexer, ScopeCatalog.Build([("vanilla", vanilla), ("mod", mod)], null, null));

        var content = await Run(tool, """{"query":"ZzShared.xml"}""");

        var rows = FileRows(content);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Contains(vanilla, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.Contains(mod, StringComparison.OrdinalIgnoreCase));
    }
}
