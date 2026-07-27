using System.Formats.Tar;
using RimSearcher.Core;
using RimSearcher.Server;

namespace RimSearcher.Tests;

public class LocalizationTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string Chinese = "ChineseSimplified (简体中文)";

    // ── LanguageReader：目录形态 ──────────────────────────────────────

    [Fact]
    public void Read_Directory_CollectsTopLevelLabelAndDescription()
    {
        var pack = WriteDirectoryPack("Mod", Chinese, "ThingDef", "Drugs.xml", """
            <LanguageData>
              <Beer.label>啤酒</Beer.label>
              <Beer.description>除了水以外人类所消耗的第一饮料。</Beer.description>
            </LanguageData>
            """);

        var entry = Assert.Single(LanguageReader.Read(pack));

        Assert.Equal("ThingDef", entry.DefType);
        Assert.Equal("Beer", entry.DefName);
        Assert.Equal("啤酒", entry.Label);
        Assert.Equal("除了水以外人类所消耗的第一饮料。", entry.Description);
    }

    // 嵌套键译的是 def 内部某个子对象（工具、阶段），挂到 def 头上就是张冠李戴。
    // 实测本体中文包 4284 个 .label 里有 1143 个是这类。
    [Fact]
    public void Read_DropsNestedKeys()
    {
        var pack = WriteDirectoryPack("Mod", Chinese, "ThingDef", "Drugs.xml", """
            <LanguageData>
              <Beer.label>啤酒</Beer.label>
              <Beer.tools.bottle.label>瓶子</Beer.tools.bottle.label>
              <Beer.ingestible.ingestCommandString>喝{0}</Beer.ingestible.ingestCommandString>
              <AlcoholHigh.stages.drunk.label>醉酒</AlcoholHigh.stages.drunk.label>
            </LanguageData>
            """);

        var entry = Assert.Single(LanguageReader.Read(pack));
        Assert.Equal("啤酒", entry.Label);
    }

    // 只译了 description 没译 label 的少见但存在，漏掉会让 inspect 缺一块
    [Fact]
    public void Read_KeepsDescriptionOnlyEntries()
    {
        var pack = WriteDirectoryPack("Mod", Chinese, "ThingDef", "Misc.xml", """
            <LanguageData>
              <Wort.description>还未发酵的啤酒。</Wort.description>
            </LanguageData>
            """);

        var entry = Assert.Single(LanguageReader.Read(pack));

        Assert.Null(entry.Label);
        Assert.Equal("还未发酵的啤酒。", entry.Description);
    }

    // 类型目录下允许再分子目录（ThingDef/Weapons/Guns.xml），DefType 仍取紧邻的那一层
    [Fact]
    public void Read_Directory_RecursesIntoTypeSubfolders()
    {
        var packDir = _workspace.Dir("Mod", "Languages", Chinese);
        _workspace.WriteFile(
            Path.Combine("Mod", "Languages", Chinese, "DefInjected", "ThingDef", "Weapons", "Guns.xml"),
            "<LanguageData><Gun_Revolver.label>左轮手枪</Gun_Revolver.label></LanguageData>");

        var entry = Assert.Single(LanguageReader.Read(LanguagePack.ForDirectory(packDir)));

        Assert.Equal("ThingDef", entry.DefType);
        Assert.Equal("左轮手枪", entry.Label);
    }

    [Fact]
    public void Read_Directory_IgnoresKeyedAndStrings()
    {
        var packDir = _workspace.Dir("Mod", "Languages", Chinese);
        _workspace.WriteFile(
            Path.Combine("Mod", "Languages", Chinese, "Keyed", "Misc.xml"),
            "<LanguageData><SomeKey.label>不该被收</SomeKey.label></LanguageData>");

        Assert.Empty(LanguageReader.Read(LanguagePack.ForDirectory(packDir)));
    }

    // ── LanguageReader：tar 形态（本体官方语言自 1.6 起就是这个） ──────

    [Fact]
    public void Read_Archive_CollectsEntriesUnderDefInjected()
    {
        var archive = WriteArchivePack("Data", Chinese, new Dictionary<string, string>
        {
            ["DefInjected/ThingDef/Drugs.xml"] =
                "<LanguageData><Beer.label>啤酒</Beer.label></LanguageData>",
            ["DefInjected/TraitDef/Traits.xml"] =
                "<LanguageData><Tough.label>坚韧</Tough.label></LanguageData>",
            // 语言包里的这两类不该进 def 译文表
            ["Keyed/Misc.xml"] =
                "<LanguageData><Ignored.label>不该被收</Ignored.label></LanguageData>",
            ["LanguageInfo.xml"] = "<LanguageInfo />"
        });

        var entries = LanguageReader.Read(archive).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.DefType == "ThingDef" && e.Label == "啤酒");
        Assert.Contains(entries, e => e.DefType == "TraitDef" && e.Label == "坚韧");
    }

    // 目录名 "ChineseSimplified (简体中文)" / tar 名 "...(简体中文).tar"，
    // 而用户在 config 里通常只写 "ChineseSimplified"
    [Theory]
    [InlineData("ChineseSimplified (简体中文)", "ChineseSimplified", true)]
    [InlineData("ChineseSimplified (简体中文).tar", "ChineseSimplified", true)]
    [InlineData("ChineseSimplified (简体中文)", "ChineseSimplified (简体中文)", true)]
    [InlineData("chinesesimplified (简体中文)", "ChineseSimplified", true)]
    [InlineData("ChineseTraditional (繁體中文)", "ChineseSimplified", false)]
    [InlineData("English", "ChineseSimplified", false)]
    public void NameMatches_AcceptsBareLanguageName(string candidate, string requested, bool expected)
        => Assert.Equal(expected, LanguageReader.NameMatches(candidate, requested));

    [Fact]
    public void Find_PrefersDirectoryAndFallsBackToArchive()
    {
        var languages = _workspace.Dir("Mod", "Languages");
        WriteArchiveAt(Path.Combine(languages, $"{Chinese}.tar"), new Dictionary<string, string>
        {
            ["DefInjected/ThingDef/A.xml"] = "<LanguageData><A.label>甲</A.label></LanguageData>"
        });

        var archivePack = LanguageReader.Find(languages, "ChineseSimplified");
        Assert.NotNull(archivePack);
        Assert.True(archivePack.IsArchive);

        Directory.CreateDirectory(Path.Combine(languages, Chinese));
        var directoryPack = LanguageReader.Find(languages, "ChineseSimplified");
        Assert.NotNull(directoryPack);
        Assert.False(directoryPack.IsArchive);
    }

    [Fact]
    public void Find_ReturnsNullWhenLanguageAbsent()
    {
        var languages = _workspace.Dir("Mod", "Languages");
        Directory.CreateDirectory(Path.Combine(languages, "English"));

        Assert.Null(LanguageReader.Find(languages, "ChineseSimplified"));
    }

    // ── LocalizationIndex：查表与覆盖 ─────────────────────────────────

    // 光本体一份中文包里就有 205 个 defName 跨 DefType 重名、其中 49 个译文不同
    // （Animals 在 SkillDef 下是「驯兽」，在 MainButtonDef 下是「动物」）。
    // 主键必须带类型，否则这些全是抛硬币。
    [Fact]
    public void Lookup_KeyedByDefTypeAndDefName()
    {
        var index = new LocalizationIndex();
        index.Add("SkillDef", "Animals", new LocalizedDef("驯兽", null), 0, 0);
        index.Add("MainButtonDef", "Animals", new LocalizedDef("动物", null), 0, 0);

        Assert.Equal("驯兽", index.Lookup("SkillDef", "Animals")?.Label);
        Assert.Equal("动物", index.Lookup("MainButtonDef", "Animals")?.Label);

        // 类型对不上就不显示——没有「只按 defName」的回退
        Assert.Null(index.Lookup("ThingDef", "Animals"));
    }

    // config 里靠后的源 = 加载靠后 = 覆盖前面的
    [Fact]
    public void Add_LaterSourceOverridesEarlier()
    {
        var index = new LocalizationIndex();
        index.Add("ThingDef", "Beer", new LocalizedDef("啤酒", null), sourceRank: 0, folderRank: 0);
        index.Add("ThingDef", "Beer", new LocalizedDef("麦酒", null), sourceRank: 3, folderRank: 0);

        Assert.Equal("麦酒", index.Lookup("ThingDef", "Beer")?.Label);
    }

    // 并行扫描下谁先写完是不确定的，故胜负只能由权重定，不能由写入顺序定
    [Fact]
    public void Add_OutcomeIndependentOfInsertionOrder()
    {
        var forward = new LocalizationIndex();
        forward.Add("ThingDef", "Beer", new LocalizedDef("先写", null), sourceRank: 0, folderRank: 0);
        forward.Add("ThingDef", "Beer", new LocalizedDef("后写", null), sourceRank: 2, folderRank: 0);

        var reverse = new LocalizationIndex();
        reverse.Add("ThingDef", "Beer", new LocalizedDef("后写", null), sourceRank: 2, folderRank: 0);
        reverse.Add("ThingDef", "Beer", new LocalizedDef("先写", null), sourceRank: 0, folderRank: 0);

        Assert.Equal("后写", forward.Lookup("ThingDef", "Beer")?.Label);
        Assert.Equal(forward.Lookup("ThingDef", "Beer")?.Label, reverse.Lookup("ThingDef", "Beer")?.Label);
    }

    // 同一个源内部按 mod 布局的目录优先级，靠前的赢（1.6\Languages 压过根 Languages）
    [Fact]
    public void Add_WithinSameSourceEarlierFolderWins()
    {
        var index = new LocalizationIndex();
        index.Add("ThingDef", "Beer", new LocalizedDef("根目录", null), sourceRank: 1, folderRank: 5);
        index.Add("ThingDef", "Beer", new LocalizedDef("版本目录", null), sourceRank: 1, folderRank: 0);

        Assert.Equal("版本目录", index.Lookup("ThingDef", "Beer")?.Label);
    }

    [Fact]
    public void Scan_SkipsDescriptionWhenDisabled()
    {
        var pack = WriteDirectoryPack("Mod", Chinese, "ThingDef", "Drugs.xml", """
            <LanguageData>
              <Beer.label>啤酒</Beer.label>
              <Beer.description>不该出现。</Beer.description>
            </LanguageData>
            """);

        var index = new LocalizationIndex();
        index.Scan([new LocalizationSource(pack, 0, 0)], includeDescription: false);

        var localized = index.Lookup("ThingDef", "Beer");
        Assert.Equal("啤酒", localized?.Label);
        Assert.Null(localized?.Description);
    }

    [Fact]
    public void Snapshot_RoundTripsThroughExportImport()
    {
        var index = new LocalizationIndex();
        index.Add("ThingDef", "Beer", new LocalizedDef("啤酒", "描述"), 0, 0);
        index.FreezeIndex();

        var restored = new LocalizationIndex();
        restored.ImportSnapshot(index.ExportSnapshot());
        restored.FreezeIndex();

        Assert.Equal("啤酒", restored.Lookup("ThingDef", "Beer")?.Label);
        Assert.Equal("描述", restored.Lookup("ThingDef", "Beer")?.Description);
    }

    // ── ModLayout：纯汉化包 ───────────────────────────────────────────

    // 这类 mod 在 workshop 里是一大类（一台机器 249 个订阅里就有 65 个）。
    // 以前 HasContent 为假会让它整个被跳过，连带它译的 def 全部没有译名。
    [Fact]
    public void Resolve_LocalizationOnlyMod_ExposesLanguageDirs()
    {
        _workspace.WriteFile(
            Path.Combine("Trans", "1.6", "Languages", Chinese, "DefInjected", "ThingDef", "A.xml"),
            "<LanguageData><A.label>甲</A.label></LanguageData>");

        var layout = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Trans"), "1.6");

        Assert.NotNull(layout);
        Assert.False(layout.HasContent);
        Assert.True(layout.HasLocalization);
        Assert.Equal("1.6", layout.Version);
        Assert.Contains(Path.Combine(_workspace.Root, "Trans", "1.6", "Languages"), layout.LanguageDirs);
    }

    // 只适配到 1.5 的汉化包，游戏 1.6 时仍要能找到——否则它译的 def 一个译名都没有
    [Fact]
    public void Resolve_LocalizationOnlyMod_FallsBackToOlderVersionFolder()
    {
        _workspace.WriteFile(
            Path.Combine("Trans", "1.5", "Languages", Chinese, "DefInjected", "ThingDef", "A.xml"),
            "<LanguageData><A.label>甲</A.label></LanguageData>");

        var layout = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Trans"), "1.6");

        Assert.NotNull(layout);
        Assert.Equal("1.5", layout.Version);
        Assert.Contains(Path.Combine(_workspace.Root, "Trans", "1.5", "Languages"), layout.LanguageDirs);
    }

    // 普通 mod 的版本选择必须继续只看 Defs/Assemblies：让 Languages 参与判定，
    // 会把「某个旧版本目录下只剩翻译」的 mod 选到那个错的版本上
    [Fact]
    public void Resolve_NormalMod_VersionChoiceIgnoresLanguages()
    {
        _workspace.WriteFile(
            Path.Combine("Mod", "1.5", "Languages", Chinese, "DefInjected", "ThingDef", "A.xml"),
            "<LanguageData><A.label>旧</A.label></LanguageData>");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Things.xml"), "<Defs />");

        var layout = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Mod"), "1.6");

        Assert.NotNull(layout);
        Assert.Equal("1.6", layout.Version);
        Assert.DoesNotContain(
            Path.Combine(_workspace.Root, "Mod", "1.5", "Languages"), layout.LanguageDirs);
    }

    // ── 配置：来源发现与 scope 词表 ───────────────────────────────────

    // 纯汉化包的语言目录要收，但 Csharp/Xml 必须仍为空——ScopeCatalog 的词表按那两个列表建，
    // 混进去会让 scope 里多出几十个「搜什么都是空」的源名
    [Fact]
    public void ResolveSources_LocalizationOnlyMod_ContributesNoScopeEntry()
    {
        _workspace.WriteFile(
            Path.Combine("Trans", "Languages", Chinese, "DefInjected", "ThingDef", "A.xml"),
            "<LanguageData><A.label>甲</A.label></LanguageData>");

        var config = new AppConfig
        {
            Sources =
            [
                new SourceDefinition
                {
                    Name = "trans",
                    HasExplicitName = true,
                    Mods = [Path.Combine(_workspace.Root, "Trans")]
                }
            ]
        };

        var resolved = config.ResolveSources(_workspace.Root);

        Assert.Empty(resolved.Csharp);
        Assert.Empty(resolved.Xml);
        Assert.Single(resolved.Languages);
        Assert.Equal("trans", resolved.Languages[0].Name);

        // scope 词表就是从 AllSources 建的，这里空即代表 scope 里不会冒出 "trans"
        Assert.Empty(ScopeCatalog.Build(resolved.AllSources, null, null).Sources);
    }

    // vanilla 那条源指的是 Data，各 DLC 平铺在下面（Data\Core\Languages），
    // 不走 mod 布局解析，语言目录只能靠向下探两层找到
    [Fact]
    public void ResolveSources_DiscoversLanguagesUnderPlainXmlPath()
    {
        _workspace.WriteFile(Path.Combine("Data", "Core", "Defs", "Things.xml"), "<Defs />");
        _workspace.Dir("Data", "Core", "Languages");
        _workspace.Dir("Data", "Royalty", "Languages");

        var config = new AppConfig
        {
            Sources =
            [
                new SourceDefinition
                {
                    Name = "vanilla",
                    HasExplicitName = true,
                    Xml = [Path.Combine(_workspace.Root, "Data")]
                }
            ]
        };

        var resolved = config.ResolveSources(_workspace.Root);

        Assert.Equal(2, resolved.Languages.Count);
        Assert.Contains(resolved.Languages, entry =>
            entry.Path == Path.Combine(_workspace.Root, "Data", "Core", "Languages"));
        Assert.Contains(resolved.Languages, entry =>
            entry.Path == Path.Combine(_workspace.Root, "Data", "Royalty", "Languages"));
    }

    // config 里靠后的源 SourceRank 更大，即覆盖靠前的
    [Fact]
    public void ResolveSources_AssignsSourceRankByConfigOrder()
    {
        _workspace.Dir("A", "Languages");
        _workspace.Dir("B", "Languages");

        var config = new AppConfig
        {
            Sources =
            [
                new SourceDefinition { Name = "a", HasExplicitName = true, Xml = [Path.Combine(_workspace.Root, "A")] },
                new SourceDefinition { Name = "b", HasExplicitName = true, Xml = [Path.Combine(_workspace.Root, "B")] }
            ]
        };

        var resolved = config.ResolveSources(_workspace.Root);

        Assert.Equal(2, resolved.Languages.Count);
        Assert.Equal(0, resolved.Languages.Single(entry => entry.Name == "a").SourceRank);
        Assert.Equal(1, resolved.Languages.Single(entry => entry.Name == "b").SourceRank);
    }

    // ── 配置解析 ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_LocalizationDefaultsToAuto()
    {
        var config = AppConfig.Parse("");

        Assert.NotNull(config);
        Assert.Equal(AppConfig.LocalizationAuto, config.Localization);
        Assert.False(config.LocalizationDescription);
    }

    [Theory]
    [InlineData("localization = \"ChineseSimplified\"")]
    [InlineData("language = \"ChineseSimplified\"")]
    [InlineData("lang = \"ChineseSimplified\"")]
    public void Parse_AcceptsLocalizationAliases(string toml)
    {
        var config = AppConfig.Parse(toml);

        Assert.NotNull(config);
        Assert.Equal("ChineseSimplified", config.Localization);
        Assert.Equal("ChineseSimplified", config.ResolveLanguage());
    }

    [Fact]
    public void Parse_LocalizationDescriptionOptIn()
    {
        var config = AppConfig.Parse("localization_description = true");

        Assert.NotNull(config);
        Assert.True(config.LocalizationDescription);
    }

    // "off" 关掉整个特性，而不是去找一个叫 off 的语言
    [Fact]
    public void ResolveLanguage_OffDisablesLocalization()
    {
        var config = AppConfig.Parse("localization = \"off\"");

        Assert.NotNull(config);
        Assert.Null(config.ResolveLanguage());
    }

    [Fact]
    public void LocalizationLayout_ResolvesOnlyRequestedLanguage()
    {
        _workspace.WriteFile(
            Path.Combine("Mod", "Languages", Chinese, "DefInjected", "ThingDef", "A.xml"),
            "<LanguageData><A.label>甲</A.label></LanguageData>");
        _workspace.Dir("Mod", "Languages", "English");

        var sources = new ResolvedSources([], [])
        {
            Languages = [new LanguageDirEntry("mod", Path.Combine(_workspace.Root, "Mod", "Languages"), 0, 0)]
        };

        var resolved = LocalizationLayout.Resolve(sources, "ChineseSimplified");

        var source = Assert.Single(resolved);
        Assert.Equal(Path.Combine(_workspace.Root, "Mod", "Languages", Chinese), source.Pack.Path);

        Assert.Empty(LocalizationLayout.Resolve(sources, null));
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    private LanguagePack WriteDirectoryPack(
        string modDir, string language, string defType, string fileName, string content)
    {
        _workspace.WriteFile(
            Path.Combine(modDir, "Languages", language, "DefInjected", defType, fileName), content);

        return LanguagePack.ForDirectory(Path.Combine(_workspace.Root, modDir, "Languages", language));
    }

    private LanguagePack WriteArchivePack(string dataDir, string language, Dictionary<string, string> files)
    {
        var languages = _workspace.Dir(dataDir, "Languages");
        var archivePath = Path.Combine(languages, $"{language}.tar");
        WriteArchiveAt(archivePath, files);

        return LanguagePack.ForArchive(archivePath);
    }

    // 本体的官方语言包就是这个形态：未压缩 tar，条目直接从 "DefInjected/" 打头，没有语言目录那层
    private static void WriteArchiveAt(string archivePath, Dictionary<string, string> files)
    {
        var staging = Path.Combine(Path.GetDirectoryName(archivePath)!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            TarFile.CreateFromDirectory(staging, archivePath, includeBaseDirectory: false);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }
}
