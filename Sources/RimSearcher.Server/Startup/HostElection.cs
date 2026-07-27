namespace RimSearcher.Server;

public enum ServerRole
{
    // 自建自用一份索引
    Standalone,

    // 持有席位，对外提供索引
    Host,

    // 已作为代理跑完整个会话，调用方应立即退出，绝不可再建索引
    ProxyFinished
}

public sealed record HostElectionResult(ServerRole Role, HostSlot? Slot)
{
    public bool ShouldExitImmediately => Role == ServerRole.ProxyFinished;
}

// 「本进程要不要、能不能共用别人的索引」这个决定原先摊在 Program 的两处条件里，
// 两处写法还不一样（一处 ShareIndexHost && hasPaths 再嵌套 IsSupported，一处三个否定并列），
// 改动时很容易只改一边。收拢到这里，条件只有 IsSharingPossible 一处。
public static class HostElection
{
    public static bool IsSharingPossible(AppConfig config, bool hasPaths)
        => config.ShareIndexHost && hasPaths && IndexHost.IsSupported;

    // protocolOut 必须是真正的 stdout：进程启动时 Console.Out 已被改指 stderr。
    //
    // attachToHost 只为测试留缝：真实竞态的窗口是毫秒级的，靠时序去复现「两个进程同时冷启动」
    // 的测试要么偶发失败要么什么也没验证到，而这里最需要被钉住的恰是抢位失败之后还要再试一次。
    // 生产调用不传这个参数，走 IndexHost.TryRunAsProxyAsync。
    public static async Task<HostElectionResult> ElectAsync(
        AppConfig config,
        bool hasPaths,
        string hostFingerprint,
        TextWriter protocolOut,
        Func<string, TextWriter, Task<bool>>? attachToHost = null)
    {
        // 方法组直接转成委托会因为 TryRunAsProxyAsync 那个可选参数而不成立，故包一层
        var attach = attachToHost ?? ((fingerprint, output) => IndexHost.TryRunAsProxyAsync(fingerprint, output));

        if (!IsSharingPossible(config, hasPaths))
        {
            if (config.ShareIndexHost && hasPaths && !IndexHost.IsSupported)
                await ServerLogger.Info("HostElection", "Index host sharing unavailable on this platform, running standalone");

            return new HostElectionResult(ServerRole.Standalone, null);
        }

        // 走共享路径的进程在这里就把 watchdog 起起来；独立进程则等到调用方把工具装好之后再起
        ProcessGuard.Start(config.IdleTimeoutMinutes);

        // 代理判定必须先于建索引：连上已有宿主的进程不该再花 4 秒和 1 GB 建第二份
        if (await attach(hostFingerprint, protocolOut))
        {
            await ServerLogger.Info("HostElection", "Proxy session ended");
            return new HostElectionResult(ServerRole.ProxyFinished, null);
        }

        var slot = IndexHost.TryBecomeHost(hostFingerprint);
        if (slot == null)
        {
            // 抢位失败只有一个成因：别的进程刚刚抢到了席位。两个进程同时冷启动时，上面那次
            // 代理探测对双方都落在「还没有宿主」上并立即返回，于是没抢到席位的这个若就地降级，
            // 就在最该共享的那一刻多建了一份约 1 GB 的索引。
            // 赢家此刻大概正在建索引（约 4 秒）、管道还没开，所以必须再探一次：
            // TryRunAsProxyAsync 自带为这个空档设计的有界重试窗口，且席位一旦不再被人持有就
            // 立即返回，不会在赢家已经死掉的情况下干等。
            await ServerLogger.Info("HostElection", "Lost the host slot race, waiting for the winner's pipe");

            if (await attach(hostFingerprint, protocolOut))
            {
                await ServerLogger.Info("HostElection", "Proxy session ended");
                return new HostElectionResult(ServerRole.ProxyFinished, null);
            }

            // 管道等不来，最常见的成因是赢家在建索引途中死了——席位随它的句柄一起消失，
            // 此时我们还能顶上去当宿主，让后来的进程仍有可挂靠的对象。
            slot = IndexHost.TryBecomeHost(hostFingerprint);
        }

        // 席位与管道都确认不可用才自建。多一份索引可以忍，卡在这里等不行。
        if (slot == null)
        {
            await ServerLogger.Info("HostElection", "Could not claim host slot, running standalone");
            return new HostElectionResult(ServerRole.Standalone, null);
        }

        return new HostElectionResult(ServerRole.Host, slot);
    }

    // 只有最后一个连接断开后宿主才真正退出——它的寿命不跟着第一个 client 走
    private static readonly TimeSpan ConnectionGrace = TimeSpan.FromSeconds(60);

    public static void StartServing(string hostFingerprint, IReadOnlyList<Tools.ITool> tools)
    {
        ProcessGuard.ShouldStayAlive = () => IndexHost.ShouldStayAliveForConnections(ConnectionGrace);

        // 必须与 TryBecomeHost 用同一个指纹：在一个名字上占席位、却在另一个名字上开管道，
        // 等于谁也连不上，且席位还被占着
        IndexHost.StartAcceptLoop(hostFingerprint, tools);
    }

    // 本地 stdio 结束（自己的 client 走了）但仍有管道连接时，宿主继续服务到最后一个断开
    public static async Task DrainAsync(HostSlot slot)
    {
        while (IndexHost.ShouldStayAliveForConnections(ConnectionGrace))
            await Task.Delay(TimeSpan.FromSeconds(15));

        await ServerLogger.Info("HostElection", "Index host shutting down");
        slot.Dispose();
    }
}
