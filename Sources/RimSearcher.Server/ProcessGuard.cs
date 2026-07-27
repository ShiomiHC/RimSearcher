using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RimSearcher.Server;

// stdio 服务器的正常退出条件是 stdin EOF。但父进程被强杀、或 stdin 写端句柄被兄弟进程继承时，
// EOF 不会到来——服务器就永久停在 ReadLine 上，每个残留实例带着完整索引常驻内存。
// 这里独立于 stdin 监视父进程，父进程一消失即退出。
public static class ProcessGuard
{
    private static DateTime _lastActivityUtc = DateTime.UtcNow;
    private static volatile bool _shuttingDown;

    public static void NotifyActivity() => _lastActivityUtc = DateTime.UtcNow;

    public static void Start(int idleTimeoutMinutes)
    {
        var parentId = TryGetParentProcessId();
        if (parentId is int ppid && ppid > 0)
        {
            _ = Task.Run(() => WatchParentAsync(ppid));
        }
        else
        {
            _ = ServerLogger.Warning("ProcessGuard", "Parent process id unavailable; relying on stdin EOF only");
        }

        if (idleTimeoutMinutes > 0)
        {
            _ = Task.Run(() => WatchIdleAsync(idleTimeoutMinutes));
        }
    }

    private static async Task WatchParentAsync(int parentId)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            await ExitAsync("parent process already gone", parentId);
            return;
        }

        await ServerLogger.Info("ProcessGuard", "Watching parent process", ("pid", parentId), ("name", SafeName(parent)));

        try
        {
            await parent.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            // 拿不到退出事件就退化成轮询，不能让守护本身成为单点
            await ServerLogger.Warning("ProcessGuard", "WaitForExit failed, falling back to polling", ("reason", ex.Message));
            while (!_shuttingDown)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (!IsAlive(parentId)) break;
            }
        }

        await ExitAsync("parent process exited", parentId);
    }

    private static async Task WatchIdleAsync(int idleTimeoutMinutes)
    {
        var timeout = TimeSpan.FromMinutes(idleTimeoutMinutes);
        while (!_shuttingDown)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            if (DateTime.UtcNow - _lastActivityUtc >= timeout)
            {
                await ExitAsync($"idle for {idleTimeoutMinutes} minute(s)", null);
                return;
            }
        }
    }

    // 宿主进程的父亲只是第一个连上来的 client；它退出时别的 client 可能仍在用这份索引，
    // 故退出前先问一次，有连接就转为孤儿宿主继续服务。
    public static Func<bool>? ShouldStayAlive;

    private static async Task ExitAsync(string reason, int? parentId)
    {
        if (_shuttingDown) return;

        if (ShouldStayAlive?.Invoke() == true)
        {
            await ServerLogger.Info("ProcessGuard", "Deferring shutdown: still serving other clients",
                ("trigger", reason));

            while (ShouldStayAlive?.Invoke() == true)
                await Task.Delay(TimeSpan.FromSeconds(15));

            await ServerLogger.Info("ProcessGuard", "Last client disconnected, resuming shutdown");
        }

        _shuttingDown = true;

        if (parentId.HasValue)
            await ServerLogger.Info("ProcessGuard", "Shutting down", ("reason", reason), ("parentPid", parentId.Value));
        else
            await ServerLogger.Info("ProcessGuard", "Shutting down", ("reason", reason));

        Environment.Exit(0);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch { return "unknown"; }
    }

    private static int? TryGetParentProcessId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetParentProcessIdWindows();

            return GetParentProcessIdUnix();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    private static int? GetParentProcessIdWindows()
    {
        var info = new ProcessBasicInformation();
        using var current = Process.GetCurrentProcess();
        var status = NtQueryInformationProcess(current.Handle, 0, ref info, Marshal.SizeOf(info), out _);
        if (status != 0) return null;
        return info.InheritedFromUniqueProcessId.ToInt32();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getppid();

    private static int? GetParentProcessIdUnix() => getppid();
}
