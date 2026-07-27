using RimSearcher.Core;

namespace RimSearcher.Tests;

public class AssemblyScannerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // mod 的多版本布局是 ModRoot/1.6/Assemblies/*.dll，游戏只加载当前版本那一份
    [Theory]
    [InlineData(@"C:\mods\X\1.6\Assemblies\a.dll", "1.6")]
    [InlineData(@"C:\mods\X\1.0\Assemblies\a.dll", "1.0")]
    [InlineData(@"C:/mods/X/1.5/Assemblies/a.dll", "1.5")]
    [InlineData(@"C:\mods\X\Assemblies\a.dll", null)]
    [InlineData(@"C:\game\Managed\Assembly-CSharp.dll", null)]
    public void ExtractGameVersion_ReadsVersionDirectories(string path, string? expected)
        => Assert.Equal(expected, AssemblyScanner.ExtractGameVersion(path));

    // 取最后一个匹配段：mod 根自身若带 1.x 目录名会误伤，但版本目录总在更深处
    [Fact]
    public void ExtractGameVersion_PrefersTheDeepestMatch()
        => Assert.Equal("1.6", AssemblyScanner.ExtractGameVersion(@"C:\mods\Pack1.4\1.6\Assemblies\a.dll"));

    [Theory]
    // 精确名：整个程序集名就是它
    [InlineData("mscorlib.dll", true)]
    [InlineData("netstandard.dll", true)]
    [InlineData("System.dll", true)]
    [InlineData("UnityEngine.dll", true)]
    [InlineData("I18N.dll", true)]
    [InlineData("Newtonsoft.Json.dll", true)]
    [InlineData("websocket-sharp.dll", true)]
    // 点分家族：真·子命名空间
    [InlineData("System.Text.Json.dll", true)]
    [InlineData("System.Xml.dll", true)]
    [InlineData("UnityEngine.CoreModule.dll", true)]
    [InlineData("Unity.TextMeshPro.dll", true)]
    [InlineData("Microsoft.CSharp.dll", true)]
    [InlineData("Mono.Security.dll", true)]
    [InlineData("I18N.West.dll", true)]
    [InlineData("Assembly-CSharp.dll", false)]
    [InlineData("0Harmony.dll", false)]
    [InlineData("AlienRace.dll", false)]
    public void IsRuntimeAssembly_ExcludesEngineAndRuntimeFamilies(string fileName, bool expected)
        => Assert.Equal(expected, AssemblyScanner.IsRuntimeAssembly(fileName));

    // 回归：裸前缀 StartsWith 会把这些正常 mod 程序集整批当成运行时库排掉，
    // 它们的源码永远进不了索引，用户查不到还看不出原因。
    [Theory]
    [InlineData("SystematicWeapons.dll")]
    [InlineData("SystemicInfection.dll")]
    [InlineData("UnityEngineTweaks.dll")]
    [InlineData("I18NPlus.dll")]
    [InlineData("MonoModTweaks.dll")]
    [InlineData("UnityToolbag.dll")]
    [InlineData("MicrosoftLikeUI.dll")]
    [InlineData("NewtonsoftJsonShim.dll")]
    public void IsRuntimeAssembly_KeepsModAssembliesThatMerelyShareAPrefix(string fileName)
        => Assert.False(AssemblyScanner.IsRuntimeAssembly(fileName));

    [Fact]
    public void Enumerate_SkipsRuntimeAssembliesByDefault()
    {
        var root = _workspace.Dir("Managed");
        _workspace.WriteFile(Path.Combine("Managed", "Assembly-CSharp.dll"), "x");
        _workspace.WriteFile(Path.Combine("Managed", "UnityEngine.CoreModule.dll"), "x");

        var entries = AssemblyScanner.Enumerate([root], gameVersion: null);

        Assert.Equal("Assembly-CSharp.dll", Path.GetFileName(Assert.Single(entries).Path));
    }

    [Fact]
    public void Enumerate_FiltersByGameVersion()
    {
        var root = _workspace.Dir("Mod");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Assemblies", "Current.dll"), "x");
        _workspace.WriteFile(Path.Combine("Mod", "1.4", "Assemblies", "Legacy.dll"), "x");
        _workspace.WriteFile(Path.Combine("Mod", "Common", "Shared.dll"), "x");

        var names = AssemblyScanner.Enumerate([root], gameVersion: "1.6")
            .Select(entry => Path.GetFileName(entry.Path))
            .ToList();

        Assert.Contains("Current.dll", names);
        Assert.DoesNotContain("Legacy.dll", names);
        // 不在版本目录下的一律保留 —— 无法判定它属于哪一版
        Assert.Contains("Shared.dll", names);
    }

    [Fact]
    public void Enumerate_DeduplicatesAndSortsPaths()
    {
        var root = _workspace.Dir("Managed");
        _workspace.WriteFile(Path.Combine("Managed", "B.dll"), "x");
        _workspace.WriteFile(Path.Combine("Managed", "A.dll"), "x");

        var entries = AssemblyScanner.Enumerate([root, root], gameVersion: null);

        Assert.Equal(2, entries.Count);
        Assert.Equal("A.dll", Path.GetFileName(entries[0].Path));
        Assert.Equal("B.dll", Path.GetFileName(entries[1].Path));
    }

    [Fact]
    public void Enumerate_IgnoresMissingRoots()
        => Assert.Empty(AssemblyScanner.Enumerate([@"C:\definitely\not\here"], gameVersion: null));

    // 大小 + 修改时间都没变就认为内容没变，省掉一次全文件哈希
    [Fact]
    public void FillHashes_ReusesPreviousHashWhenQuickDigestMatches()
    {
        var root = _workspace.Dir("Managed");
        _workspace.WriteFile(Path.Combine("Managed", "A.dll"), "content");

        var entries = AssemblyScanner.Enumerate([root], gameVersion: null);
        var hashed = AssemblyScanner.FillHashes(entries);
        var realHash = Assert.Single(hashed).Sha256;
        Assert.NotNull(realHash);

        var previous = hashed.ToDictionary(
            entry => entry.Path,
            entry => entry with { Sha256 = "STALE-BUT-REUSED" },
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal("STALE-BUT-REUSED", Assert.Single(AssemblyScanner.FillHashes(entries, previous)).Sha256);
    }

    [Fact]
    public void FillHashes_RecomputesWhenQuickDigestChanged()
    {
        var root = _workspace.Dir("Managed");
        _workspace.WriteFile(Path.Combine("Managed", "A.dll"), "content");

        var entries = AssemblyScanner.Enumerate([root], gameVersion: null);
        var expected = Assert.Single(AssemblyScanner.FillHashes(entries)).Sha256;

        // 长度不同 → QuickDigest 不同 → 必须重算
        var previous = entries.ToDictionary(
            entry => entry.Path,
            entry => entry with { Sha256 = "STALE", Length = entry.Length + 1 },
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, Assert.Single(AssemblyScanner.FillHashes(entries, previous)).Sha256);
    }

    [Fact]
    public void CatalogDigest_ChangesWithContentAndIsStableOtherwise()
    {
        var root = _workspace.Dir("Managed");
        _workspace.WriteFile(Path.Combine("Managed", "A.dll"), "one");

        var before = AssemblyScanner.ComputeCatalogDigest(
            AssemblyScanner.FillHashes(AssemblyScanner.Enumerate([root], null)));

        Assert.Equal(before, AssemblyScanner.ComputeCatalogDigest(
            AssemblyScanner.FillHashes(AssemblyScanner.Enumerate([root], null))));

        _workspace.WriteFile(Path.Combine("Managed", "A.dll"), "two-different");

        Assert.NotEqual(before, AssemblyScanner.ComputeCatalogDigest(
            AssemblyScanner.FillHashes(AssemblyScanner.Enumerate([root], null))));
    }

    // 假 dll 不是 PE 文件，读元数据必须返回 null 而不是抛
    [Fact]
    public void ReadMetadata_ReturnsNullForNonPeFiles()
        => Assert.Null(AssemblyScanner.ReadMetadata(_workspace.WriteFile("fake.dll", "not a PE file")));
}
