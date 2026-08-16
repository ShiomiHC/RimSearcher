using System.Diagnostics;

namespace RimSearcher.Sources;

/// <summary>
/// 反编译根目录作为 git 工作树时的只读探询。本工具不 add、不 commit,
/// 只在覆盖前问一句「这一棵有没有还没进历史的改动」。
/// </summary>
public static class SourceGit
{
    /// <summary>这个目录是不是一个 git 工作树的根。</summary>
    public static bool IsRepository(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// 这一棵树有没有还没进历史的改动(含未跟踪文件)。
    ///
    /// 不是 git 仓、或 git 里还没有这棵树:false —— 没有历史可丢,不拦。
    /// git 在场却问不出来(找不到 git、超时、非零退出):true —— 宁可不覆盖。
    /// </summary>
    public static bool HasUncommittedChanges(string gitRoot, string treeName)
    {
        if (!IsRepository(gitRoot) || string.IsNullOrWhiteSpace(treeName)) return false;

        try
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // safe.directory:测里的临时仓、以及偶发所有权不一致,都不能让「问一句状态」
            // 变成一次覆盖。这条 -c 只作用于这一次 status,不改任何人的 git config。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("safe.directory=*");
            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(gitRoot);
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(treeName);

            using var proc = Process.Start(psi);
            if (proc is null) return true;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 问不出来就按脏处理 */ }
                return true;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0) return true;
            return stdout.AsSpan().Trim().Length > 0;
        }
        catch
        {
            return true;
        }
    }
}
