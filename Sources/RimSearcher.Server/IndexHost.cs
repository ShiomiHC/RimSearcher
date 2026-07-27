using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RimSearcher.Server;

// 宿主席位的持有凭证。刻意用命名信号量而非 Mutex：
//  - Mutex 的所有权绑定获取它的那个线程。本进程是在若干 await 之后抢席位的（控制台应用
//    无同步上下文，续体落在任意线程池线程），关机时的释放几乎必然发生在另一个线程上，
//    ReleaseMutex 会抛 ApplicationException，席位反而变成 abandoned。
//  - Mutex 的 AbandonedMutexException 语义是「异常抛出时锁已由本线程获得」，极易被误处理成
//    「再 new 一个 initiallyOwned:true 的 Mutex」——而 initiallyOwned 对已存在的命名对象无效，
//    结果是进程自认为是宿主却并不持有席位。
// 信号量没有所有权概念，上面两个坑都不存在，释放可发生在任意线程。
public sealed class HostSlot : IDisposable
{
    private Semaphore? _semaphore;

    internal HostSlot(Semaphore semaphore) => _semaphore = semaphore;

    // 可在任意线程调用；重复调用无副作用
    public void Dispose()
    {
        var semaphore = Interlocked.Exchange(ref _semaphore, null);
        if (semaphore == null) return;

        try { semaphore.Release(); }
        catch (SemaphoreFullException) { }
        finally { semaphore.Dispose(); }
    }
}

// 每个 MCP client 各 spawn 一个进程，各自持一份完整索引（实测约 1 GB/实例）。开启共享后：
// 首个实例抢到宿主锁 → 建索引 + 起命名管道；后续实例连上管道，只做 stdio↔管道的逐行转发，
// 自身不建索引。全机因此只保留一份索引。
//
// 平台范围：仅 Windows。命名 Mutex 在 Unix 上不跨进程，缺了它无法排除双宿主竞态，
// 故非 Windows 平台走原本的独立模式。
public static class IndexHost
{
    private static int _activeConnections;
    private static DateTime _lastConnectionCloseUtc = DateTime.UtcNow;
    private static volatile bool _isHost;
    private static volatile bool _everHadConnection;

    // 命名信号量是 Windows-only 的；标成守卫后，检查过 IsSupported 的方法体内不再报 CA1416
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsHost => _isHost;

    public static int ActiveConnections => Volatile.Read(ref _activeConnections);

    public static string BuildPipeName(string configFingerprint)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configFingerprint)))[..12];
        return $"RimSearcher.host.{IndexCacheSchemaTag}.{hash}";
    }

    // 索引结构变更后新旧实例不得混用同一管道
    private const string IndexCacheSchemaTag = "v1";

    // 后缀与旧版的 ".mutex" 刻意不同：同名的 Mutex 与 Semaphore 无法互相打开
    // （OpenExisting 会抛 WaitHandleCannotBeOpenedException），换名让新旧两代各走各的席位。
    internal static string BuildSlotName(string pipeName) => $@"Global\{pipeName}.slot";

    // 返回 true 表示本进程已作为代理完成全部工作（调用方应直接退出，不要建索引）。
    // protocolOut 必须是真正的 stdout：进程启动时 Console.Out 已被改指 stderr，
    // 拿它当下行出口会把响应全写进 stderr，client 就只能干等到超时。
    public static async Task<bool> TryRunAsProxyAsync(string configFingerprint, TextWriter protocolOut)
    {
        if (!IsSupported) return false;

        var pipeName = BuildPipeName(configFingerprint);

        // 宿主可能正在建索引（首次冷启动数秒），给它一个窗口
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await TryProxyOnceAsync(pipeName, attempt == 0 ? 1500 : 4000, protocolOut))
                return true;

            // 没有宿主在跑，且本进程也没抢到宿主位——交回调用方自建
            if (!SlotHeldByAnotherProcess(pipeName)) return false;

            await Task.Delay(500);
        }

        return false;
    }

    // 仅经 TryRunAsProxyAsync 的 IsSupported 检查之后可达
    [SupportedOSPlatform("windows")]
    private static bool SlotHeldByAnotherProcess(string pipeName)
    {
        try
        {
            // 只探测存在性，不做 WaitOne —— 那会把计数抢走，让正在选举的宿主拿不到席位。
            // 席位对象随宿主进程的句柄存活，故「存在」即「有宿主在跑」。
            if (!Semaphore.TryOpenExisting(BuildSlotName(pipeName), out var existing)) return false;

            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> TryProxyOnceAsync(string pipeName, int timeoutMs, TextWriter protocolOut)
    {
        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs);
        }
        catch (Exception)
        {
            client?.Dispose();
            return false;
        }

        await ServerLogger.Info("IndexHost", "Attached to existing index host as proxy", ("pipe", pipeName));

        using (client)
        {
            var pipeReader = new StreamReader(client, new UTF8Encoding(false));
            var pipeWriter = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };

            var upstream = PumpAsync(Console.In, pipeWriter);
            var downstream = PumpAsync(pipeReader, protocolOut);

            // 任一方向结束即收工：stdin EOF 表示 client 走了，管道 EOF 表示宿主没了
            await Task.WhenAny(upstream, downstream);
        }

        return true;
    }

    private static async Task PumpAsync(TextReader from, TextWriter to)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await from.ReadLineAsync();
            }
            catch (IOException)
            {
                return;
            }

            if (line == null) return;

            try
            {
                await to.WriteLineAsync(line);
                await to.FlushAsync();
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    // 宿主侧：抢到席位才建索引并对外服务。返回的 HostSlot 须存活到进程结束。
    // 上一任宿主异常退出时其句柄随进程关闭，内核对象随之销毁，下一个调用者会重新创建出
    // 计数为 1 的新席位——不需要 Mutex 那套 abandoned 处理。
    public static HostSlot? TryBecomeHost(string configFingerprint)
    {
        if (!IsSupported) return null;

        var slotName = BuildSlotName(BuildPipeName(configFingerprint));

        Semaphore semaphore;
        try
        {
            semaphore = new Semaphore(1, 1, slotName, out _);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            if (!semaphore.WaitOne(0))
            {
                semaphore.Dispose();
                return null;
            }
        }
        catch (Exception)
        {
            semaphore.Dispose();
            return null;
        }

        _isHost = true;
        return new HostSlot(semaphore);
    }

    // 每个连接一个独立 RimSearcher 会话，共享传入的 tool 实例（tool 只持索引引用，无会话状态）
    public static void StartAcceptLoop(string configFingerprint, IReadOnlyList<Tools.ITool> tools)
    {
        var pipeName = BuildPipeName(configFingerprint);
        _ = Task.Run(() => AcceptLoopAsync(pipeName, tools));
    }

    private static async Task AcceptLoopAsync(string pipeName, IReadOnlyList<Tools.ITool> tools)
    {
        await ServerLogger.Info("IndexHost", "Serving as index host", ("pipe", pipeName));

        while (true)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                await ServerLogger.Error("IndexHost", "Failed to create pipe instance", ("reason", ex.Message));
                return;
            }

            try
            {
                await server.WaitForConnectionAsync();
            }
            catch (Exception ex)
            {
                await ServerLogger.Warning("IndexHost", "Pipe wait failed", ("reason", ex.Message));
                server.Dispose();
                continue;
            }

            _ = Task.Run(() => ServeConnectionAsync(server, tools));
        }
    }

    private static async Task ServeConnectionAsync(NamedPipeServerStream server, IReadOnlyList<Tools.ITool> tools)
    {
        Interlocked.Increment(ref _activeConnections);
        _everHadConnection = true;
        ProcessGuard.NotifyActivity();

        try
        {
            using (server)
            {
                var reader = new StreamReader(server, new UTF8Encoding(false));
                var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

                var session = new RimSearcher(writer, registerGlobalLogger: false);
                foreach (var tool in tools) session.RegisterTool(tool);

                await session.RunAsync(reader);
            }
        }
        catch (IOException)
        {
            // 客户端断开
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            _lastConnectionCloseUtc = DateTime.UtcNow;
            ProcessGuard.NotifyActivity();
        }
    }

    // 宿主不能随第一个 client 一起死——别的 client 可能正连着它。
    public static bool ShouldStayAliveForConnections(TimeSpan graceAfterLastConnection)
    {
        if (!_isHost) return false;
        if (ActiveConnections > 0) return true;

        // 从未有过管道连接的宿主没有别人可服务，直接随自己的 client 走；
        // grace 只用于覆盖「代理刚断、新代理正在连上来」的空档。
        if (!_everHadConnection) return false;

        return DateTime.UtcNow - _lastConnectionCloseUtc < graceAfterLastConnection;
    }
}
