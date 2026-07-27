using System.Text.Json;
using RimSearcher.Server;

namespace RimSearcher.Tests;

// SourceChangeProbe 未 Configure 时 Pending / LastSync 恒为 null，Consume 只剩「记录问过什么」
// 这条路径——正好是并发出问题的地方，无需搭出整套同步服务即可覆盖。
public class SessionUpdateNoticeTests
{
    private static string? Consume(SessionUpdateNotice notice, string query)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query }));
        return notice.Consume("rimworld-searcher__locate", args.RootElement, "result");
    }

    // 回归：_askedAbout 曾是裸 HashSet，而 RimSearcher 每条协议消息各起一个任务，
    // 同一会话最多 10 个工具调用并发进 Consume。并发 Add 会破坏 HashSet 的内部桶结构，
    // 表现为计数错乱、抛 IndexOutOfRangeException 或死循环。
    [Fact]
    public void Consume_UnderConcurrency_KeepsEveryTerm()
    {
        var notice = new SessionUpdateNotice();

        const int threads = 16;
        const int termsPerThread = 30;
        const int repeats = 8;

        Parallel.For(0, threads, thread =>
        {
            // 重复问同一批词，制造真实的写竞争而不只是并行插入不同键
            for (var round = 0; round < repeats; round++)
            {
                for (var term = 0; term < termsPerThread; term++)
                {
                    Consume(notice, $"Term{thread}x{term}");
                }
            }
        });

        Assert.Equal(threads * termsPerThread, notice.TrackedTermCount);
    }

    // 长会话不能让问过的词无界增长
    [Fact]
    public void TrackedTerms_AreCapped()
    {
        var notice = new SessionUpdateNotice();

        for (var i = 0; i < 2000; i++) Consume(notice, $"Term{i}");

        // MaxTrackedTerms = 512（单线程下到顶即停，计数是确定的）
        Assert.Equal(512, notice.TrackedTermCount);
    }

    // 'def:Foo' / 'RimWorld.Pawn' 要拆开，否则和变更提示里的裸类型名对不上
    [Fact]
    public void RecordQuery_SplitsQualifiedNames_AndSkipsShortTokens()
    {
        var notice = new SessionUpdateNotice();

        Consume(notice, "ab.RimWorld.Pawn");

        // "ab" 不足 3 字符被丢弃，只留 RimWorld 与 Pawn
        Assert.Equal(2, notice.TrackedTermCount);
    }

    [Fact]
    public void Consume_WithNonObjectArguments_DoesNotThrow()
    {
        var notice = new SessionUpdateNotice();

        using var args = JsonDocument.Parse("\"not-an-object\"");
        var result = notice.Consume("rimworld-searcher__locate", args.RootElement, "result");

        Assert.Null(result);
        Assert.Equal(0, notice.TrackedTermCount);
    }

    // 未配置 SourceChangeProbe 时不该凭空产生提示
    [Fact]
    public void Consume_WithoutPendingChanges_ReturnsNull()
    {
        var notice = new SessionUpdateNotice();

        Assert.Null(Consume(notice, "CompShield"));
    }
}
