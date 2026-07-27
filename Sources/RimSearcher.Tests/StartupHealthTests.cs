using RimSearcher.Server;

namespace RimSearcher.Tests;

// 缺陷回归：配置加载失败 / 没有源 / 源里一个文件都没有时，服务照常启动并对每一条查询
// 回一句体面的 "No results for 'X' in scope 'all'"。启动期那几行诊断只到 stderr——
// 那时 ServerLogger.OnLogAsync 还没接上 MCP 通道——而调用方是只读工具返回文本的 LLM，
// 于是「索引是空的」被它读成「这个符号不存在」。
//
// 静态状态：整个类串行跑，每条用例自己复位。
[Collection("StartupHealth")]
public class StartupHealthTests : IDisposable
{
    public StartupHealthTests() => StartupHealth.ResetForTests();

    public void Dispose() => StartupHealth.ResetForTests();

    [Fact]
    public void HealthyServer_SaysNothing()
    {
        StartupHealth.Record(null, []);
        var notice = new StartupHealth.SessionNotice();

        Assert.Null(notice.Consume());
        Assert.Null(notice.Consume());
    }

    // 阻塞级不能「只说一次」：这个会话里的每一条「没找到」都同样不可信
    [Fact]
    public void UnusableIndex_WarnsOnEveryCall()
    {
        StartupHealth.Record("The configuration at 'x.toml' failed to load (file not found).", []);
        var notice = new StartupHealth.SessionNotice();

        var first = notice.Consume();
        var second = notice.Consume();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Contains("not evidence of absence", first);
        Assert.Contains("not evidence of absence", second);
        Assert.Contains("failed to load", second);
    }

    // 忠告级只说一次：索引可用，重复只是噪音
    [Fact]
    public void LayoutAdvisories_AreSaidOncePerSession()
    {
        StartupHealth.Record(null, ["Mod: mutually exclusive conditional folders, both included"]);
        var notice = new StartupHealth.SessionNotice();

        var first = notice.Consume();
        var second = notice.Consume();

        Assert.NotNull(first);
        Assert.Contains("mutually exclusive", first);
        Assert.Null(second);
    }

    // 每会话一份：另一个会话仍要收到属于它的那一次
    [Fact]
    public void LayoutAdvisories_AreNotSharedAcrossSessions()
    {
        StartupHealth.Record(null, ["Mod: something was shadowed"]);

        Assert.NotNull(new StartupHealth.SessionNotice().Consume());
        Assert.NotNull(new StartupHealth.SessionNotice().Consume());
    }

    // 索引不可信的时候，布局取舍是次要问题，不该抢占那条警告
    [Fact]
    public void BlockingReason_TakesPrecedenceOverAdvisories()
    {
        StartupHealth.Record("No source paths configured.", ["Mod: something was shadowed"]);

        var first = new StartupHealth.SessionNotice().Consume();

        Assert.Contains("not evidence of absence", first);
        Assert.DoesNotContain("shadowed", first);
    }

    [Fact]
    public void BlankReason_IsTreatedAsHealthy()
    {
        StartupHealth.Record("   ", ["  "]);

        Assert.Null(StartupHealth.BlockingReason);
        Assert.Empty(StartupHealth.Advisories);
        Assert.Null(new StartupHealth.SessionNotice().Consume());
    }
}
