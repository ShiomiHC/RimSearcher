using RimSearcher.Core;
using RimSearcher.Server;

namespace RimSearcher.Tests;

// 指纹有两个用途且要求相反：缓存键必须随内容变，宿主管道名必须不随内容变。
// 这里钉住的是这条分界线本身。
public class ConfigFingerprintTests
{
    private static string PathOnly(params string[] roots)
        => IndexCacheService.ComputeConfigFingerprint(roots, [], includeContentDigest: false);

    private static string WithContent(params string[] roots)
        => IndexCacheService.ComputeConfigFingerprint(roots, [], includeContentDigest: true);

    [Fact]
    public void PathOnlyFingerprint_IsStable_WhenSourcesChange()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var before = PathOnly(root);

        // 模拟一次 mod 更新：路径集合没变，内容变了
        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");
        workspace.WriteFile("src/Extra.cs", "public class Extra { }");

        Assert.Equal(before, PathOnly(root));
    }

    // #5 的回归：管道名曾经掺了内容摘要，于是源一变新进程就算出另一个名字，
    // 连不上正在跑的宿主，转头自建第二份 1 GB 索引。
    [Fact]
    public void ContentSensitiveFingerprint_Changes_WhenSourcesChange()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var before = WithContent(root);
        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");

        Assert.NotEqual(before, WithContent(root));
    }

    [Fact]
    public void TwoModes_NeverProduceTheSameValue_ForTheSamePaths()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        Assert.NotEqual(PathOnly(root), WithContent(root));
    }

    [Fact]
    public void PathOnlyFingerprint_StillDistinguishesDifferentPathSets()
    {
        using var workspace = new TempWorkspace();
        var a = workspace.Dir("a");
        var b = workspace.Dir("b");

        Assert.NotEqual(PathOnly(a), PathOnly(b));
        Assert.NotEqual(PathOnly(a), PathOnly(a, b));
        Assert.Equal(PathOnly(a), PathOnly(a));
    }

    // 宿主指纹喂给 BuildPipeName，所以「内容变了但管道名不变」才是真正要保证的性质
    [Fact]
    public void PipeName_SurvivesASourceUpdate()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var before = IndexHost.BuildPipeName(PathOnly(root));
        workspace.WriteFile("src/Thing.cs", "// re-decompiled\npublic class Thing { }");
        workspace.WriteFile("src/New.cs", "public class New { }");

        Assert.Equal(before, IndexHost.BuildPipeName(PathOnly(root)));
    }
}
