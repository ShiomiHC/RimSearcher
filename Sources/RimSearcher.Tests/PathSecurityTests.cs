using System.Diagnostics;
using RimSearcher.Core;

namespace RimSearcher.Tests;

// PathSecurity 的白名单是静态的，Initialize 只追加不清空。并行跑的话，一个用例装进去的根
// 会让另一个用例的「应当拒绝」变成放行——凡是碰这份静态状态的测试都必须串行。
[CollectionDefinition("PathSecurity")]
public class PathSecurityCollection;

// junction 不需要管理员权限（符号链接需要，故一律用 junction），但组策略仍可能禁掉它。
// xunit 2.x 没有运行期 skip，条件跳过只能在发现期决定：建不出 junction 的环境上把用例
// 标成 skipped，而不是让它随环境随机变红。
public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        var reason = JunctionSupport.UnavailableReason;
        if (reason != null) Skip = $"本环境建不出 junction（{reason}）";
    }
}

internal static class JunctionSupport
{
    // 探一次复用整轮：每个 [JunctionFact] 的构造都会问一次
    private static readonly Lazy<string?> Probe = new(Detect);

    public static string? UnavailableReason => Probe.Value;

    public static bool TryCreateJunction(string link, string target, out string reason)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                reason = "cmd.exe 启不起来";
                return false;
            }

            process.WaitForExit(15_000);

            if (process.ExitCode != 0 || !Directory.Exists(link))
            {
                reason = $"mklink /J 退出码 {process.ExitCode}";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static string? Detect()
    {
        if (!OperatingSystem.IsWindows()) return "junction 是 Windows 概念";

        var root = Path.Combine(
            Path.GetTempPath(), "rimsearcher-tests", "junction-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            return TryCreateJunction(Path.Combine(root, "link"), target, out var reason) ? null : reason;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* 探测残留不该让整轮测试变红 */ }
        }
    }
}

[Collection("PathSecurity")]
public class PathSecurityTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public PathSecurityTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    // 缺陷回归：Path.GetFullPath 不解析 junction，而旧实现只看末段有没有 ReparsePoint。
    // 叶子文件自身不带该属性，于是「根内的一个目录指向根外」能把整个白名单绕过去。
    [JunctionFact]
    public void IntermediateJunctionPointingOutsideRoot_IsRejected()
    {
        var allowed = _workspace.Dir("allowed");
        var outside = _workspace.Dir("outside");
        _workspace.WriteFile(Path.Combine("outside", "secret.txt"), "top secret");

        var junction = Path.Combine(allowed, "escape");
        Assert.True(JunctionSupport.TryCreateJunction(junction, outside, out var reason), reason);

        PathSecurity.Initialize([allowed]);

        Assert.False(PathSecurity.IsPathSafe(Path.Combine(junction, "secret.txt")));
        Assert.False(PathSecurity.IsPathSafe(junction));
    }

    // 反向保险：允许的根自身就可能是 junction（把源码目录做成链接指向别的盘很常见）。
    // Initialize 不把根也解析成最终目标的话，合法子路径会被全部误拒。
    [JunctionFact]
    public void RootItselfIsJunction_StillAllowsItsChildren()
    {
        var real = _workspace.Dir("real");
        _workspace.WriteFile(Path.Combine("real", "A.cs"), "class A;");

        var linkedRoot = Path.Combine(_workspace.Root, "linked");
        Assert.True(JunctionSupport.TryCreateJunction(linkedRoot, real, out var reason), reason);

        PathSecurity.Initialize([linkedRoot]);

        Assert.True(PathSecurity.IsPathSafe(Path.Combine(linkedRoot, "A.cs")));
        Assert.True(PathSecurity.IsPathSafe(Path.Combine(real, "A.cs")));
    }

    [Fact]
    public void ParentTraversalOutOfRoot_IsRejected()
    {
        var allowed = _workspace.Dir("allowed");
        _workspace.WriteFile(Path.Combine("outside", "secret.txt"), "top secret");

        PathSecurity.Initialize([allowed]);

        Assert.False(PathSecurity.IsPathSafe(Path.Combine(allowed, "..", "outside", "secret.txt")));
        Assert.True(PathSecurity.IsPathSafe(Path.Combine(allowed, "..", "allowed", "A.cs")));
    }

    // 根名前缀碰撞：允许 <ws>/src 时 <ws>/src2 是另一个目录。
    // 靠 root + 分隔符 比较已经处理了，钉住它别被改成裸 StartsWith。
    [Fact]
    public void SiblingRootSharingANamePrefix_IsRejected()
    {
        var allowed = _workspace.Dir("src");
        _workspace.WriteFile(Path.Combine("src2", "x.cs"), "class X;");

        PathSecurity.Initialize([allowed]);

        Assert.False(PathSecurity.IsPathSafe(Path.Combine(_workspace.Root, "src2", "x.cs")));
        Assert.True(PathSecurity.IsPathSafe(Path.Combine(allowed, "x.cs")));
    }

    // Windows 路径大小写不敏感，同一个目录换个大小写写法仍是它自己
    [WindowsFact("Unix 路径大小写敏感，换大小写就是另一个目录")]
    public void CaseDifferingPath_IsAllowedOnWindows()
    {
        var allowed = _workspace.Dir("Allowed");
        _workspace.WriteFile(Path.Combine("Allowed", "A.cs"), "class A;");

        PathSecurity.Initialize([allowed]);

        Assert.True(PathSecurity.IsPathSafe(Path.Combine(allowed.ToUpperInvariant(), "A.cs")));
        Assert.True(PathSecurity.IsPathSafe(Path.Combine(allowed.ToLowerInvariant(), "A.cs")));
    }

    // 关掉校验时必须一律放行：这是用户显式配置的逃生口，收紧它等于把功能关死
    [Fact]
    public void WhenDisabled_EverythingIsAllowed()
    {
        PathSecurity.Initialize([_workspace.Dir("allowed")], enabled: false);

        Assert.True(PathSecurity.IsPathSafe(OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd"));
        Assert.True(PathSecurity.IsPathSafe(Path.Combine(_workspace.Root, "outside", "secret.txt")));
    }

    [Fact]
    public void ResolveInsideRoot_RefusesAbsoluteAndParentSegments()
    {
        var root = _workspace.Dir("root");

        Assert.Null(PathSecurity.ResolveInsideRoot(root, Path.Combine("..", "outside", "secret.txt")));
        Assert.Null(PathSecurity.ResolveInsideRoot(root, Path.Combine(_workspace.Root, "outside", "secret.txt")));
        Assert.Null(PathSecurity.ResolveInsideRoot(root, "a/../../b.cs"));
        Assert.Null(PathSecurity.ResolveInsideRoot(root, ""));

        Assert.Equal(
            Path.Combine(root, "RimWorld", "CompShield.cs"),
            PathSecurity.ResolveInsideRoot(root, "RimWorld/CompShield.cs"));
    }
}
