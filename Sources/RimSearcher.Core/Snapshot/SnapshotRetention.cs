using RimSearcher.Cli;

namespace RimSearcher.Snapshot;

public enum SnapshotInstallKind
{
    /// <summary>目录里本来没有这份名字,新文件就位。</summary>
    Written,

    /// <summary>旧文件进了 <c>.prev</c>,新文件就位。</summary>
    Replaced,

    /// <summary>解析结果与现文件相同,两份都没动。</summary>
    Unchanged,
}

/// <summary>
/// 同名快照的一代对照物。<c>export</c> 与 <c>snapshot import</c> 共用这一份,
/// 否则两条造库口会一个旋转一个覆盖。
/// </summary>
public static class SnapshotRetention
{
    public static readonly OptionSpec ReplacePrev = new()
    {
        Name = "replace-prev",
        Aliases = ["replace-previous", "discard-prev"],
        Arity = Arity.Flag,
        Help = "Discard '{name}.prev' when replacing a snapshot that still differs from it. " +
               "Without this the write is refused, so an unread comparison is not lost. " +
               "Prefer --name <other> to keep both.",
    };

    /// <summary>
    /// 把刚建好的库装到登记名下。调用方把文件写到 <paramref name="incomingPath"/>
    /// (不要直接写目标,否则没机会先比)。
    /// <paramref name="reuseExportFile"/> 在拒绝安装时指出那份中间文件还在,
    /// 改个名字 import 就不必再起一次游戏。
    /// </summary>
    public static SnapshotInstallKind Install(string incomingPath, string destPath, string name,
                                              bool replacePrev, string? reuseExportFile = null)
    {
        try
        {
            if (File.Exists(destPath) && EmptyDiff(incomingPath, destPath))
                return SnapshotInstallKind.Unchanged;

            var prev = PrevPath(destPath);
            if (File.Exists(destPath) && File.Exists(prev) && !replacePrev && !EmptyDiff(prev, destPath))
                throw new CliUsageException(WouldDiscard(name, reuseExportFile));

            if (File.Exists(destPath))
            {
                File.Copy(destPath, prev, overwrite: true);
                File.Delete(destPath);
                File.Move(incomingPath, destPath);
                return SnapshotInstallKind.Replaced;
            }

            File.Move(incomingPath, destPath);
            return SnapshotInstallKind.Written;
        }
        finally
        {
            if (File.Exists(incomingPath))
                try { File.Delete(incomingPath); } catch { /* 主路径已就位或失败 */ }
        }
    }

    public static string WouldDiscard(string name, string? reuseExportFile = null)
    {
        var text =
            $"Replacing '{name}' would discard '{name}.prev', which still differs from '{name}'. " +
            $"'rimsearcher snapshot diff {name}.prev {name}' is the comparison that would be lost. " +
            $"To keep both and still take a new snapshot, pass --name {name}-0817. " +
            "Pass --replace-prev only if that previous generation can go.";
        if (reuseExportFile is { Length: > 0 })
            text += $" The new snapshot was not installed; '{CommandRegistry.ExeName} snapshot import {reuseExportFile} --name {name}-0817' keeps both without running the game again.";
        return text;
    }

    public static string KeptPrevious(string name)
        => $"The previous '{name}' was kept as '{name}.prev'. " +
           $"'rimsearcher snapshot diff {name}.prev {name}' compares them.";

    public static string Unchanged(string name)
        => $"The incoming snapshot's resolved defs and field values match '{name}', " +
           "so the existing file was left in place.";

    public static string IncomingPath(string destPath) => destPath + ".incoming";

    public static string PrevPath(string destPath)
    {
        var dir = Path.GetDirectoryName(destPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(destPath);
        return Path.Combine(dir, name + ".prev.db");
    }

    private static bool EmptyDiff(string oldPath, string newPath)
    {
        var diff = SnapshotDiff.Compare(oldPath, newPath, limit: 1);
        return diff.AddedTotal == 0 && diff.RemovedTotal == 0 && diff.FieldsTotal == 0;
    }
}
