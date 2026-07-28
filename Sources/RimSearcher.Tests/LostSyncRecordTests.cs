using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 「sync 记录丢了、反编译产物还在」与「这台机器从来没反编译过」在旧输出里逐字同形：
// 两种情形都渲染成 `N unrecorded ... (of N assemblies)`，而该做的事完全相反——前者直接
// 开查即可，后者非跑一次全量 sync 不可。第十二轮盲测的被测方照着这行判了「C# 侧是空的」，
// 劝用户先跑一次十一个源的重反编译；那次重反编译换不来任何一处查询结果的变化。
//
// 三处印出点各自都要分岔：check 的逐行、check 的结论行、以及挂在每次查询末尾的过期提示。
[Collection("PathSecurity")]
public class LostSyncRecordTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        SourceChangeProbe.SetPendingForTests(null);
        _workspace.Dispose();
    }

    // 一个源 = 一个输出目录（可选带归属标记）+ 一个放 dll 的目录。
    // 不写 assembly-state.json，于是每个程序集都是 unrecorded——正是「记录丢了」那一侧。
    private SyncSourcesTool ToolFor(params (string Name, bool OutputPresent)[] sources)
    {
        var entries = new List<SourcePathEntry>();
        var roots = new List<string>();

        foreach (var (name, outputPresent) in sources)
        {
            var output = _workspace.Dir($"{name}-src");
            var assemblies = _workspace.Dir($"{name}-asm");
            _workspace.WriteFile(Path.Combine($"{name}-asm", $"{name}.dll"), name);
            // IsWritableOutput 与 OutputPresent 共用这个标记文件。冷启动那侧输出目录留空
            // ——往里塞别的文件会撞上「这个目录不归我们管」那道闸，走成 Blocker 而非本用例。
            if (outputPresent) _workspace.WriteFile(Path.Combine($"{name}-src", ".rimsearcher-decompiled"), "");

            entries.Add(new SourcePathEntry { Name = name, Path = output, AssemblyPaths = [assemblies] });
            roots.Add(output);
        }

        PathSecurity.ResetForTests();
        PathSecurity.Initialize(roots);

        var config = new AppConfig { GameVersion = "1.6" };
        var service = new SourceSyncService(config, new ResolvedSources(entries, []), _workspace.Dir("cache"));
        return new SyncSourcesTool(service);
    }

    private static async Task<string> Check(SyncSourcesTool tool)
    {
        using var args = JsonDocument.Parse("{\"action\":\"check\"}");
        return (await tool.ExecuteAsync(args.RootElement, CancellationToken.None)).Content;
    }

    // 记录丢了、产物还在：结论必须是「可以就这么用」，而不是「先跑 sync」
    [Fact]
    public async Task LostRecord_WithOutputOnDisk_DoesNotAdviseAFullResync()
    {
        var content = await Check(ToolFor(("Core", true)));

        Assert.Contains("No assembly content differs", content);
        Assert.Contains("usable as it stands", content);
        // 这一句是盲测里被照做的那句，不能再出现在这条路径上
        Assert.DoesNotContain("Changes detected.", content);
    }

    // 冷启动：这时候确实该跑 sync，上一条的分岔不许把它一起吃掉
    [Fact]
    public async Task ColdStart_WithNoOutput_StillAdvisesASync()
    {
        var content = await Check(ToolFor(("Core", false)));

        Assert.Contains("no decompiled output yet", content);
        Assert.Contains("Changes detected.", content);
        Assert.DoesNotContain("usable as it stands", content);
    }

    // 逐行后缀在「清一色记录丢了」时是 R19 判掉的那种噪音：每行一模一样，
    // 而结论行已经整份说过一次。混杂时它才承载信息，那时必须逐行印。
    [Fact]
    public async Task UniformLostRecords_SayItOnceInTheConclusion_NotOnEveryRow()
    {
        var content = await Check(ToolFor(("Core", true), ("Milira", true)));

        Assert.DoesNotContain("decompiled output already present", content);
        Assert.Contains("missing record next to decompiled output that is still on disk", content);
    }

    [Fact]
    public async Task MixedSources_KeepThePerRowSuffix_BecauseItNowDiscriminates()
    {
        var content = await Check(ToolFor(("Core", true), ("Milira", false)));

        Assert.Contains("decompiled output already present", content);
        Assert.Contains("no decompiled output yet", content);
        Assert.Contains("Changes detected.", content);
    }

    // ---- 挂在每次查询末尾的那条提示 ------------------------------------------

    private static string? Notice(PendingSourceUpdate pending, string query)
    {
        SourceChangeProbe.SetPendingForTests(pending);
        var notice = new SessionUpdateNotice();
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query }));
        return notice.Consume("rimworld-searcher__locate", args.RootElement, "result");
    }

    private static PendingSourceUpdate Pending(
        string[] changed, string[] unverified)
        => new()
        {
            DetectedAtUtc = DateTime.UtcNow,
            ChangedAssemblySources = changed,
            UnverifiedAssemblySources = unverified,
            Hints = changed.Concat(unverified).ToList()
        };

    // 这条比 sync_sources 自己那条更容易被照做：它挂在**每一次**查询返回的末尾。
    // 一处差异都没观察到时说「源变了、结果可能过时」，两句都是假的。
    [Fact]
    public void PendingNotice_WithOnlyLostRecords_DoesNotClaimAnythingChanged()
    {
        var notice = Notice(Pending([], ["vanilla"]), "vanilla");

        Assert.NotNull(notice);
        Assert.Contains("cannot confirm", notice);
        Assert.Contains("no sync record to compare against in: vanilla", notice);
        Assert.DoesNotContain("changed since this session started", notice);
        Assert.DoesNotContain("may be stale", notice);
        Assert.DoesNotContain("action='sync'", notice);
    }

    // 真有内容差异时，原来那句一个字都不该动
    [Fact]
    public void PendingNotice_WithRealChanges_StillWarnsAndPointsAtSync()
    {
        var notice = Notice(Pending(["vanilla"], []), "vanilla");

        Assert.NotNull(notice);
        Assert.Contains("changed since this session started", notice);
        Assert.Contains("assemblies changed in: vanilla", notice);
        Assert.Contains("action='sync'", notice);
    }

    // 混杂：既不能因为有「验不了」的源就压掉警告，也不能把它们算成变更
    [Fact]
    public void PendingNotice_WithBoth_WarnsAboutTheChangedOnesAndNamesTheRest()
    {
        var notice = Notice(Pending(["Milira"], ["vanilla"]), "Milira");

        Assert.NotNull(notice);
        Assert.Contains("assemblies changed in: Milira", notice);
        Assert.Contains("no sync record to compare against in: vanilla", notice);
        Assert.DoesNotContain("assemblies changed in: vanilla", notice);
    }

    // 判据本身：三处印出点共用它，写错一处就是三处一起错
    [Theory]
    [InlineData(2, 0, 0, true, true)]    // 全部无记录、产物在 → 验不了
    [InlineData(2, 0, 0, false, false)]  // 全部无记录、产物不在 → 冷启动，是真的要 sync
    [InlineData(1, 1, 0, true, false)]   // 有一个内容变了 → 是变更
    [InlineData(1, 0, 1, true, false)]   // 有一个没了 → 是变更
    public void IsLostRecordOnly_OnlyHoldsWhenNoDifferenceWasObserved(
        int added, int modified, int removed, bool outputPresent, bool expected)
    {
        var change = new SourceChange
        {
            SourceName = "Core",
            HasChanges = true,
            Added = added,
            Modified = modified,
            Removed = removed,
            TotalAssemblies = 2,
            OutputPresent = outputPresent
        };

        Assert.Equal(expected, change.IsLostRecordOnly);
    }
}
