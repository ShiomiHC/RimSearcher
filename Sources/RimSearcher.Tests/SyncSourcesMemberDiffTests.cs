using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// action='diff' 的成员级视图与版本选择。与 PathSecurityTests 同一 collection：
// diff 的读路径要过 PathSecurity 的静态白名单。
[Collection("PathSecurity")]
public class SyncSourcesMemberDiffTests : IDisposable
{
    private static readonly string RelativeFile = Path.Combine("RimWorld", "CompShield.cs");

    // 两个方法，后面只动其中一个——method 参数若没起作用，另一个方法的改动会一起漏出来
    private const string Generation1 = """
        namespace RimWorld
        {
            public class CompShield
            {
                public void CompTick()
                {
                    energy = 1;
                }

                public void PostDraw()
                {
                    draw = 1;
                }
            }
        }
        """;

    private const string Generation2 = """
        namespace RimWorld
        {
            public class CompShield
            {
                public void CompTick()
                {
                    energy = 2;
                }

                public void PostDraw()
                {
                    draw = 1;
                }
            }
        }
        """;

    // 第三代：CompTick 再改一次、PostDraw 删掉、AbsorbDamage 新增
    private const string Generation3 = """
        namespace RimWorld
        {
            public class CompShield
            {
                public void CompTick()
                {
                    energy = 3;
                }

                public void AbsorbDamage()
                {
                    absorbed = true;
                }
            }
        }
        """;

    private readonly TempWorkspace _workspace = new();
    private readonly string _sourceDirectory;
    private readonly SourceSyncService _service;
    private readonly SyncSourcesTool _tool;

    public SyncSourcesMemberDiffTests()
    {
        _sourceDirectory = _workspace.Dir("src");

        var config = new AppConfig { SourceHistoryDepth = 3, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = _sourceDirectory,
            AssemblyPaths = [_workspace.Dir("assemblies")]
        };

        _service = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));

        // 三代内容依次转正，历史里因此留下 v0001（第一代状态）与 v0002（第二代状态）
        Promote(_service, Generation1, Generation2);
        Promote(_service, Generation2, Generation3);

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([_sourceDirectory]);

        _tool = new SyncSourcesTool(_service);
    }

    // Capture 归档的是「旧树」，故先让 src 处于旧状态、staging 放新状态，归档完再把 src 换成新状态
    private void Promote(SourceSyncService service, string before, string after)
    {
        var staging = _workspace.Dir("staging");
        _workspace.WriteFile(Path.Combine("src", RelativeFile), before);
        _workspace.WriteFile(Path.Combine("staging", RelativeFile), after);

        service.History.Capture("Core", _sourceDirectory, staging);
        _workspace.WriteFile(Path.Combine("src", RelativeFile), after);
    }

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private async Task<ToolResult> Run(object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return await _tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task Method_DiffsOnlyThatMember()
    {
        var result = await Run(new { action = "diff", file = RelativeFile, method = "CompTick", limit = 200 });

        Assert.False(result.IsError);
        Assert.Contains("energy = 2", result.Content);
        Assert.Contains("energy = 3", result.Content);

        // PostDraw 在这两代之间被删掉了，但它不是被问的那个成员
        Assert.DoesNotContain("PostDraw", result.Content);
    }

    [Fact]
    public async Task Method_AddedInThisVersion_IsReportedAsSuch()
    {
        var result = await Run(new { action = "diff", file = RelativeFile, method = "AbsorbDamage" });

        Assert.False(result.IsError);
        Assert.Contains("added in this version", result.Content);
        Assert.Contains("absorbed = true", result.Content);
    }

    [Fact]
    public async Task Method_RemovedInThisVersion_IsReportedAsSuch()
    {
        var result = await Run(new { action = "diff", file = RelativeFile, method = "PostDraw" });

        Assert.False(result.IsError);
        Assert.Contains("removed in this version", result.Content);
        Assert.Contains("draw = 1", result.Content);
    }

    [Fact]
    public async Task Method_MissingFromBothVersions_IsAnError()
    {
        var result = await Run(new { action = "diff", file = RelativeFile, method = "NoSuchMember" });

        Assert.True(result.IsError);
        Assert.Contains("NoSuchMember", result.Content);
        Assert.Contains("inspect", result.Content);
    }

    // method 单独给、没给 file 时不能悄悄退化成整源的文件列表
    [Fact]
    public async Task Method_WithoutFile_IsAnError()
    {
        var result = await Run(new { action = "diff", method = "CompTick" });

        Assert.True(result.IsError);
        Assert.Contains("'method' needs a 'file'", result.Content);
    }

    [Fact]
    public async Task MembersGranularity_ListsChangedMembersPerFile()
    {
        var result = await Run(new { action = "diff", granularity = "members" });

        Assert.False(result.IsError);
        Assert.Contains("~ RimWorld.CompShield.CompTick()", result.Content);
        Assert.Contains("+ RimWorld.CompShield.AbsorbDamage()", result.Content);
        Assert.Contains("- RimWorld.CompShield.PostDraw()", result.Content);
    }

    // 默认粒度不该付解析代价，也不该多出成员行
    [Fact]
    public async Task DefaultGranularity_ListsFilesOnly()
    {
        var result = await Run(new { action = "diff" });

        Assert.False(result.IsError);
        Assert.Contains("CompShield.cs", result.Content);
        Assert.DoesNotContain("CompTick", result.Content);
    }

    [Fact]
    public async Task NumericVersion_CountsBackwards()
    {
        var mostRecent = await Run(new { action = "diff", file = RelativeFile, version = 1, limit = 200 });
        var oneBefore = await Run(new { action = "diff", file = RelativeFile, version = 2, limit = 200 });

        Assert.False(mostRecent.IsError);
        Assert.False(oneBefore.IsError);

        // v0002 存的是第二代，v0001 存的是第一代；当前是第三代
        Assert.Contains("energy = 2", mostRecent.Content);
        Assert.DoesNotContain("energy = 1", mostRecent.Content);
        Assert.Contains("energy = 1", oneBefore.Content);
    }

    // 「上一个」写成 -1 与写成 1 是同一个意思
    [Fact]
    public async Task NegativeVersion_MeansTheSameAsPositive()
    {
        var positive = await Run(new { action = "diff", file = RelativeFile, version = 1, limit = 200 });
        var negative = await Run(new { action = "diff", file = RelativeFile, version = -1, limit = 200 });

        Assert.Equal(positive.Content, negative.Content);
    }

    [Fact]
    public async Task OutOfRangeVersion_ClampsToOldestAndSaysSo()
    {
        var result = await Run(new { action = "diff", file = RelativeFile, version = 99, limit = 200 });

        Assert.False(result.IsError);
        Assert.Contains("only 2 kept", result.Content);
        Assert.Contains("v0001", result.Content);
        Assert.Contains("energy = 1", result.Content);
    }

    // 缺陷回归：列表模式此前不把 versionId 传给 DiffAgainst，version 参数看着被接受、实际无效
    [Fact]
    public async Task FileListing_HonoursVersion()
    {
        var result = await Run(new { action = "diff", version = 2 });

        Assert.False(result.IsError);
        Assert.Contains("v0001", result.Content);
    }

    // file + granularity=members：已经收窄到一个文件，成员清单不再截断
    [Fact]
    public async Task FileWithMembersGranularity_ListsEveryChangedMember()
    {
        var result = await Run(new { action = "diff", file = RelativeFile, granularity = "members" });

        Assert.False(result.IsError);
        Assert.Contains("3 members changed", result.Content);
        Assert.Contains("~ RimWorld.CompShield.CompTick()", result.Content);
        Assert.Contains("+ RimWorld.CompShield.AbsorbDamage()", result.Content);
        Assert.Contains("- RimWorld.CompShield.PostDraw()", result.Content);

        // 行级内容属于 method 那一层，清单这一层不该夹带
        Assert.DoesNotContain("energy = 3", result.Content);
    }

    // limit 小于变更总数时，剩下的必须可达——否则「全量查」根本没有出口
    [Fact]
    public async Task Offset_PagesThroughChangesBeyondLimit()
    {
        // 再归档一代，并让转正后的源与它有两处不同：一个文件被改写、一个文件是新增
        var staging = _workspace.Dir("staging2");
        _workspace.WriteFile(Path.Combine("staging2", RelativeFile), Generation3);
        _service.History.Capture("Core", _sourceDirectory, staging);

        _workspace.WriteFile(Path.Combine("src", RelativeFile), Generation1);
        _workspace.WriteFile(
            Path.Combine("src", "RimWorld", "Other.cs"), "namespace RimWorld { public class Other { } }");

        var first = await Run(new { action = "diff", limit = 1 });
        Assert.False(first.IsError);
        Assert.Contains("CompShield.cs", first.Content);
        Assert.DoesNotContain("Other.cs", first.Content);
        Assert.Contains("1 more of 2", first.Content);
        Assert.Contains("offset=1", first.Content);

        var second = await Run(new { action = "diff", limit = 1, offset = 1 });
        Assert.False(second.IsError);
        Assert.Contains("Other.cs", second.Content);
        Assert.DoesNotContain("CompShield.cs", second.Content);
    }

    // 缺陷回归：未知 action 此前静默落到 check，调用方拿到一份看似正常的检查报告
    [Fact]
    public async Task UnknownAction_IsAnErrorRatherThanASilentCheck()
    {
        var result = await Run(new { action = "dif" });

        Assert.True(result.IsError);
        Assert.Contains("Unknown action", result.Content);
        Assert.DoesNotContain("Source check", result.Content);
    }
}
