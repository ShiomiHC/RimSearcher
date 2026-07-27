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
    public static async Task<HostElectionResult> ElectAsync(
        AppConfig config,
        bool hasPaths,
        string hostFingerprint,
        TextWriter protocolOut)
    {
        if (!IsSharingPossible(config, hasPaths))
        {
            if (config.ShareIndexHost && hasPaths && !IndexHost.IsSupported)
                await ServerLogger.Info("HostElection", "Index host sharing unavailable on this platform, running standalone");

            return new HostElectionResult(ServerRole.Standalone, null);
        }

        // 走共享路径的进程在这里就把 watchdog 起起来；独立进程则等到调用方把工具装好之后再起
        ProcessGuard.Start(config.IdleTimeoutMinutes);

        // 代理判定必须先于建索引：连上已有宿主的进程不该再花 4 秒和 1 GB 建第二份
        if (await IndexHost.TryRunAsProxyAsync(hostFingerprint, protocolOut))
        {
            await ServerLogger.Info("HostElection", "Proxy session ended");
            return new HostElectionResult(ServerRole.ProxyFinished, null);
        }

        var slot = IndexHost.TryBecomeHost(hostFingerprint);
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
