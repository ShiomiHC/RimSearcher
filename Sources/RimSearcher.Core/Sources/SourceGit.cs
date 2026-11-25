using System.Diagnostics;

namespace RimSearcher.Sources;

public enum GitTreeState
{
    Clean,
    Dirty,
    Error,
}

public static class SourceGit
{
    public static bool IsRepository(string dir)
        => Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    public static (GitTreeState State, string? Error) TreeStatus(string gitRoot, string treeName)
    {
        if (!IsRepository(gitRoot) || string.IsNullOrWhiteSpace(treeName)) return (GitTreeState.Clean, null);
        var status = Run(gitRoot, "status", "--porcelain", "--", treeName);
        if (status.Code != 0) return (GitTreeState.Error, status.FirstLine);
        return status.Stdout.AsSpan().Trim().Length > 0
            ? (GitTreeState.Dirty, null)
            : (GitTreeState.Clean, null);
    }

    public static bool HasUncommittedChanges(string gitRoot, string treeName)
        => TreeStatus(gitRoot, treeName).State != GitTreeState.Clean;

    public static CommitOutcome CommitTree(string gitRoot, string treeName, string subject, string body)
    {
        var add = Run(gitRoot, "add", "-A", "--", treeName);
        if (add.Code != 0) return CommitOutcome.Failed(add.FirstLine);

        var diff = Run(gitRoot, "diff", "--cached", "--quiet", "--", treeName);
        if (diff.Code == 0) return CommitOutcome.Unchanged;

        var commit = Run(gitRoot, "commit", "-q", "-m", subject, "-m", body, "--", treeName);
        return commit.Code == 0 ? CommitOutcome.Committed : CommitOutcome.Failed(commit.FirstLine);
    }

    private readonly record struct GitRun(int Code, string Stdout, string Stderr)
    {
        public string FirstLine
            => (Stderr.Trim().Length > 0 ? Stderr : Stdout).Trim().Split('\n')[0].Trim() is { Length: > 0 } line
                ? line
                : $"git 返回退出码 {Code}";
    }

    private static GitRun Run(string gitRoot, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("safe.directory=*");
            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("--literal-pathspecs");
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(gitRoot);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var proc = Process.Start(psi);
            if (proc is null) return new GitRun(-1, "", "无法启动 git");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch {  }
                try { proc.WaitForExit(5_000); } catch {  }
                return new GitRun(-1, "", "git 超时");
            }

            return new GitRun(proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new GitRun(-1, "", ex.Message);
        }
    }
}

public sealed record CommitOutcome(bool Ok, bool Changed, string? Error)
{
    public static readonly CommitOutcome Committed = new(true, true, null);
    public static readonly CommitOutcome Unchanged = new(true, false, null);
    public static CommitOutcome Failed(string error) => new(false, false, error);
}
