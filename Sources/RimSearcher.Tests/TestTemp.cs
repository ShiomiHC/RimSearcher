using System.Diagnostics;

namespace RimSearcher.Tests;

/// <summary>
/// 本次测试进程独占的临时根。
///
/// 从前整套测试共用 <c>%TEMP%\rimsearcher-tests\</c>,于是「谁在写这个文件」这个问题
/// **跨出了进程边界**:<see cref="ProcessTests"/> 真起 <c>rimsearcher.exe</c> 子进程读
/// <c>fixture.db</c>,而连着跑几轮 <c>dotnet test</c> 时,上一轮还没收干净的进程仍握着
/// 同一份库的句柄 —— 下一轮 <see cref="Fixture.Db"/> 的 <c>Import</c> 走到
/// <c>File.Delete</c> 就是「另一个进程正在使用该文件」。两个次生症状在同一条路上:
/// 子进程读到的是刚被删了一半的库,于是 stdout 空、JSON 解析炸。
///
/// 进程内加锁挡不住这一层(锁只在进程内),而重试掩盖成因、串行化拖长跑时,都不取。
/// 所以改成**根本不共用**:一个测试进程一个根,名字带 pid;启动时把 pid 已经不在的
/// 那些旧根删掉,于是既不会互相踩,也不会无限堆积(一轮约 54M)。
/// </summary>
internal static class TestTemp
{
    /// <summary>本进程独占的临时根。第一次取用时建好,之后整个进程共用。</summary>
    internal static string Root { get; } = Create();

    private const string Prefix = "run-";

    private static string Create()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests");
        Directory.CreateDirectory(baseDir);
        Prune(baseDir);

        var root = Path.Combine(baseDir, Prefix + Environment.ProcessId);
        // 同一个 pid 同时只可能有一个活进程,所以这份必然是某个死掉的旧进程留下的。
        TryDelete(root);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// 把 pid 已经不在的旧根删掉。删不掉就算了 —— 它只占盘,不参与任何判定。
    /// </summary>
    private static void Prune(string baseDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(baseDir, Prefix + "*"))
        {
            if (!int.TryParse(Path.GetFileName(dir).AsSpan(Prefix.Length), out var pid)) continue;
            if (pid != Environment.ProcessId && Alive(pid)) continue;
            TryDelete(dir);
        }
    }

    /// <summary>问不出来就当它活着 —— 宁可留下垃圾,也不删掉别人正在跑的那份库。</summary>
    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
