using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// F34：条件加载目录的**逐条打标**。
//
// 病灶写在第九轮台账里：Cinders 的 `1.6/CE/Patches/Weapons_Mech.xml` 与一份无条件补丁
// 在返回里完全同形——裸 `<Patch>`、无 mod 守卫、正文照改 defaultProjectile——而守卫在
// loadFolders.xml 那一层，那一层不在任何返回里。R59 当时只能立一句常驻的能力边界，
// 它对每一次调用都成立，因而对**手上这一条**什么也没说。
//
// 这一组守的是两个方向，缺一不可：
//   正面——真落在条件目录里时，行内有键、脚注有成因，且两者是同一个字符串（F33 规则甲）；
//   反面——不在条件目录里、或条件已被 active_mods 判定过时，一个字都不许多说。
//         后者尤其要紧：脚注最后一句写着「没标记的就不在条件目录里」，那句话一旦不成立，
//         这个记号就退化成只能单向使用的装饰。
public class ConditionalFolderTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static async Task<string> Run(ITool tool, object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // ---- 一、布局解析：哪些目录该进这张表 ----

    private ModLayout ResolveWithConditionalCe(IReadOnlyCollection<string>? activeMods = null)
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li>/</li>
                    <li>1.6</li>
                    <li IfModActive="CETeam.CombatExtended,CETeam.CombatExtended_steam">1.6/CE</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Plain.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Patches", "Guns.xml"), "<Patch />");

        var layout = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", activeMods);
        Assert.NotNull(layout);
        return layout;
    }

    [Fact]
    public void Layout_ListsTheContentDirsOfUnevaluatedConditionalFolders()
    {
        var layout = ResolveWithConditionalCe();

        var area = Assert.Single(layout.ConditionalDirs);
        Assert.Equal(Path.Combine(_workspace.Root, "Mod", "1.6", "CE", "Patches"), area.Path);
        // 键取 loadFolders.xml 里那条 li 的写法，而不是绝对路径——脚注与行内标记共用它
        Assert.Equal("1.6/CE", area.Folder);
        // `_steam` 后缀在 packageId 规范化时脱掉，两个发行版归成一个（见 ModLayoutResolver）
        Assert.Equal("CETeam.CombatExtended active", area.Condition);
    }

    // 无条件目录不该混进来：整张表就是靠「在表里 = 条件没判过」这条判据用的
    [Fact]
    public void Layout_LeavesUnconditionalContentDirsOut()
    {
        var layout = ResolveWithConditionalCe();

        Assert.DoesNotContain(
            layout.ConditionalDirs,
            area => area.Path.Contains(Path.Combine("1.6", "Defs"), StringComparison.OrdinalIgnoreCase));
    }

    // config 给了 active_mods 时条件已经有答案了。此时再打标就是把一个已判定的问题
    // 重新说成悬案——而调用方读到标记只会以为工具答不了。
    [Fact]
    public void Layout_SaysNothingWhenActiveModsAlreadyDecidedTheCondition()
    {
        var layout = ResolveWithConditionalCe(["CETeam.CombatExtended"]);

        Assert.Contains(
            Path.Combine(_workspace.Root, "Mod", "1.6", "CE", "Patches"),
            layout.XmlDirs);
        Assert.Empty(layout.ConditionalDirs);
    }

    // 三种条件各有各的读法，而 `!` 与 `&` 在返回文本里既不是英文也不是任何调用方认得的语法
    [Theory]
    [InlineData("IfModActive=\"A\"", "A active")]
    [InlineData("IfModActive=\"A,B\"", "any of A, B active")]
    [InlineData("IfModActiveAll=\"A,B\"", "all of A, B active")]
    [InlineData("IfModNotActive=\"A\"", "A not active")]
    public void Layout_SpellsTheConditionOutInEnglish(string attribute, string expected)
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), $"""
            <loadFolders>
                <v1.6>
                    <li>1.6</li>
                    <li {attribute}>Extra</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Plain.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Extra", "Defs", "More.xml"), "<Defs />");

        var layout = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Mod"), "1.6");

        Assert.Equal(expected, Assert.Single(layout!.ConditionalDirs).Condition);
    }

    // ---- 二、查表：前缀匹配与「全有全无」 ----

    private ConditionalFolders TwoAreas(out string conditionalDir, out string plainDir)
    {
        conditionalDir = _workspace.Dir("Mod", "1.6", "CE", "Patches");
        plainDir = _workspace.Dir("Mod", "1.6", "Defs");

        return ConditionalFolders.Build([
            new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "Cinders")
        ]);
    }

    [Fact]
    public void Lookup_MatchesFilesBelowTheArea_AndNothingElse()
    {
        var folders = TwoAreas(out var conditionalDir, out var plainDir);

        Assert.Equal("1.6/CE", folders.Of(Path.Combine(conditionalDir, "Deep", "Guns.xml"))?.Folder);
        Assert.Null(folders.Of(Path.Combine(plainDir, "Plain.xml")));
        // 前缀比较不是裸的 StartsWith：兄弟目录 `…/PatchesExtra` 不算落在 `…/Patches` 下
        Assert.Null(folders.Of(conditionalDir + "Extra" + Path.DirectorySeparatorChar + "X.xml"));
    }

    // 同一个符号有一份无条件声明时不打标：它在任何实机上都在，标记就成了假警报。
    [Fact]
    public void LookupAll_NeedsEveryDeclarationToBeConditional()
    {
        var folders = TwoAreas(out var conditionalDir, out var plainDir);
        var conditionalFile = Path.Combine(conditionalDir, "Guns.xml");
        var plainFile = Path.Combine(plainDir, "Plain.xml");

        Assert.NotNull(folders.OfAll([conditionalFile]));
        Assert.Null(folders.OfAll([conditionalFile, plainFile]));
        Assert.Null(folders.OfAll([]));
    }

    // ---- 三、返回文本：行内的键与脚注的成因 ----

    private (SearchRegexTool Tool, ConditionalFolders Folders) BuildCorpus()
    {
        var conditionalDir = _workspace.Dir("Mod", "1.6", "CE", "Patches");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Patches", "Guns.xml"), "<Patch>ZzNeedle</Patch>");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Plain.xml"), "<Defs>ZzNeedle</Defs>");

        var root = Path.Combine(_workspace.Root, "Mod");
        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var folders = ConditionalFolders.Build([
            new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "Cinders")
        ]);

        return (new SearchRegexTool(indexer, ScopeCatalog.Build([("Cinders", root)], null, null), folders), folders);
    }

    [Fact]
    public async Task SearchRegex_TagsTheConditionalFileAndOnlyThatOne()
    {
        var (tool, _) = BuildCorpus();

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        Assert.Contains("`Guns.xml` [conditional: 1.6/CE]", content);
        Assert.Contains("`Plain.xml`\n", content);
        Assert.DoesNotContain("`Plain.xml` [conditional", content);
    }

    // 行内只放键，成因整份说一次，两者共用同一个字符串——否则读者拿着键没处兑换（F33 规则甲）
    [Fact]
    public async Task SearchRegex_FootnoteRedeemsTheTagAndPinsTheReadingOfItsAbsence()
    {
        var (tool, _) = BuildCorpus();

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        Assert.Contains("`[conditional: …]` marks a mod folder", content);
        Assert.Contains("`1.6/CE` [Cinders] needs CETeam.CombatExtended active", content);
        Assert.Contains("never evaluates the condition", content);
        // 反面读法：没标记的就不在条件目录里。不说这句，这个记号只能单向使用。
        Assert.Contains("Untagged results are not inside such a folder", content);
    }

    // loadFolders.xml 里的一条 li 展开成好几个内容目录（Defs / Patches / Assemblies），
    // 而说给调用方听的是同一句话。按路径去重时脚注会把它原样印两遍——真语料上就是这么
    // 印的：`… needs CETeam.CombatExtended active; … needs CETeam.CombatExtended active`。
    [Fact]
    public async Task SearchRegex_ListsOneLiOnceEvenWhenItSpansSeveralContentDirs()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Patches", "Guns.xml"), "<Patch>ZzNeedle</Patch>");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Defs", "Ammo.xml"), "<Defs>ZzNeedle</Defs>");

        var root = Path.Combine(_workspace.Root, "Mod");
        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new SearchRegexTool(
            indexer,
            ScopeCatalog.Build([("Cinders", root)], null, null),
            ConditionalFolders.Build([
                new ConditionalArea(
                    _workspace.Dir("Mod", "1.6", "CE", "Patches"), "1.6/CE", "CETeam.CombatExtended active", "Cinders"),
                new ConditionalArea(
                    _workspace.Dir("Mod", "1.6", "CE", "Defs"), "1.6/CE", "CETeam.CombatExtended active", "Cinders")
            ]));

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        // 两个文件都要打上标记……
        Assert.Contains("`Guns.xml` [conditional: 1.6/CE]", content);
        Assert.Contains("`Ammo.xml` [conditional: 1.6/CE]", content);
        // ……而脚注里那句成因只说一遍
        var footnote = content[content.IndexOf("marks a mod folder", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOf(footnote, "`1.6/CE` [Cinders] needs CETeam.CombatExtended active"));
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // 一处都没打标就一个字都不说（同 R9：没发生的事不说）
    [Fact]
    public async Task SearchRegex_SaysNothingWhenNothingIsConditional()
    {
        var root = _workspace.Dir("Mod");
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "Plain.xml"), "<Defs>ZzNeedle</Defs>");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("Cinders", root)], null, null));

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        Assert.DoesNotContain("conditional", content);
    }

    // read_code 整份返回只讲一个文件，故键与成因合成一句、跟着别的 note 一起进代码围栏。
    // 它必须挂在**失败分支**上也成立——「这里没有这个东西」正是最需要知道读的是不是
    // 一份条件性内容的时刻（同 ReadCodeTool 里那三条 note 的判据）。
    [Fact]
    public async Task ReadCode_ExplainsTheConditionInline()
    {
        var conditionalDir = _workspace.Dir("Mod", "1.6", "CE", "Patches");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Patches", "Guns.xml"), "<Patch />\n");

        var root = Path.Combine(_workspace.Root, "Mod");
        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new ReadCodeTool(
            indexer,
            ScopeCatalog.Build([("Cinders", root)], null, null),
            ConditionalFolders.Build([
                new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "Cinders")
            ]));

        // 按基名读、不给绝对路径：PathSecurity.AllowedRoots 是进程级静态且只追加不清空，
        // 传绝对路径就得把这个类拖进 "PathSecurity" 串行集合，为一条用例锁住十六条。
        // 走索引解析这条路同样能拿到解析后的绝对路径，标记正是挂在那上面的。
        var content = await Run(tool, new { path = "Guns.xml", startLine = 0, lineCount = 5 });

        Assert.Contains("<!-- note: [conditional: 1.6/CE] loadFolders.xml loads this folder only with "
                        + "CETeam.CombatExtended active;", content);
        Assert.Contains("not evidence that it takes effect at runtime -->", content);
    }

    // def 行不印文件路径（R20），故这个标记是 locate 那一段里唯一能看出「这条 def 来自
    // 条件目录」的地方——而默认 scope 'base' 里就有这种 def（HAR 的 1.6/Mods/Ideology）。
    [Fact]
    public async Task Locate_TagsDefsFromConditionalFolders()
    {
        var conditionalDir = _workspace.Dir("Mod", "1.6", "CE", "Patches");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Patches", "Guns.xml"), """
            <Defs>
              <ThingDef>
                <defName>ZzGun</defName>
              </ThingDef>
            </Defs>
            """);

        var root = Path.Combine(_workspace.Root, "Mod");
        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();

        var tool = new LocateTool(
            indexer, defIndexer, ScopeCatalog.Build([("Cinders", root)], null, null), null,
            ConditionalFolders.Build([
                new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "Cinders")
            ]));

        var content = await Run(tool, new { query = "ZzGun" });

        Assert.Contains("`ZzGun` (100%) - ThingDef [conditional: 1.6/CE]", content);
        Assert.Contains("`1.6/CE` [Cinders] needs CETeam.CombatExtended active", content);
    }

    // inspect 的 `File:` 与 Resolved XML 合起来读就是「游戏里的那个 def」。R62 已经说了
    // 「PatchOperation 不被应用」，这一条补它的姊妹缺口：这份 XML 未必会被加载。
    [Fact]
    public async Task Inspect_NotesTheConditionNextToTheDefFile()
    {
        var conditionalDir = _workspace.Dir("Mod", "1.6", "CE", "Patches");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "CE", "Patches", "Guns.xml"), """
            <Defs>
              <ThingDef>
                <defName>ZzGun</defName>
              </ThingDef>
            </Defs>
            """);

        var root = Path.Combine(_workspace.Root, "Mod");
        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();

        var tool = new InspectTool(
            indexer, defIndexer, ScopeCatalog.Build([("Cinders", root)], null, null), null,
            ConditionalFolders.Build([
                new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "Cinders")
            ]));

        var content = await Run(tool, new { name = "ZzGun" });

        Assert.Contains("_Note: [conditional: 1.6/CE] loadFolders.xml loads this folder only with "
                        + "CETeam.CombatExtended active;", content);
    }

    // ---- 四、文法闸 ----

    // 单源 scope 下来源标签整段不印，于是几条同一目录的结果行会各挂一个逐字相同的
    // `[conditional: …]`。规则六判的是「该上提到段头的来源标签」，而这个记号按设计就该
    // 逐行挂——哪一行受影响是逐行不同的事实。两者撞在同一个位置上，故必须显式豁免。
    [Fact]
    public async Task TaggedOutput_PassesTheStandingGrammarGate()
    {
        var conditionalDir = _workspace.Dir("Mod", "1.6", "CE", "Patches");
        for (var i = 0; i < 3; i++)
            _workspace.WriteFile(
                Path.Combine("Mod", "1.6", "CE", "Patches", $"Guns{i}.xml"), "<Patch>ZzNeedle</Patch>");

        var root = Path.Combine(_workspace.Root, "Mod");
        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new SearchRegexTool(
            indexer, ScopeCatalog.Build([("Cinders", root)], null, null),
            ConditionalFolders.Build([
                new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "Cinders")
            ]));

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        var violations = GrammarRules.Check(content);
        Assert.True(violations.Count == 0, GrammarRules.Describe("search_regex 条件标记", violations));
    }
}
