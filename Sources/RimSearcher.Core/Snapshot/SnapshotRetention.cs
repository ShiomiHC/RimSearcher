using RimSearcher.Cli;
using RimSearcher.Config;

namespace RimSearcher.Snapshot;

public enum SnapshotInstallKind
{
    /// <summary>目录里本来没有这份名字,新文件就位。</summary>
    Written,

    /// <summary>旧文件进了上一代,新文件就位。</summary>
    Replaced,

    /// <summary>解析结果与现文件相同,两份都没动。</summary>
    Unchanged,
}

/// <summary>装库的结果:落地形态,以及这一轮转掉了谁。</summary>
/// <param name="Kept">轮转后现存的旧代数(不含当前那份)。</param>
/// <param name="Dropped">被挤出保留窗口而删掉的那一份的别名,没有就是 null。</param>
public sealed record SnapshotInstall(SnapshotInstallKind Kind, int Kept, string? Dropped);

/// <summary>
/// 同名快照的世代轮转。<c>export</c> 与 <c>snapshot import</c> 共用这一份,
/// 否则两条造库口会一个旋转一个覆盖。
///
/// 代数由 <c>snapshot_keep</c> 定(含当前那一份),第一代旧文件仍叫 <c>{name}.prev</c>,
/// 再老的是 <c>{name}.prev2</c>、<c>{name}.prev3</c>…… 它们和别的库一样能被
/// <c>snapshot list</c> 发现、被 <c>snapshot diff</c> 点名。
/// </summary>
public static class SnapshotRetention
{
    public static readonly OptionSpec Keep = new()
    {
        Name = "keep",
        Aliases = ["generations", "keep-generations"],
        Placeholder = "<n>",
        Help = "How many generations of this name to keep, counting the one being written. The one it pushes " +
               "past that count is deleted. 1 overwrites with no comparison left behind. The default is the " +
               "config file's 'snapshot_keep'.",
        Default = RimConfig.SnapshotKeepDefault.ToString(),
    };

    public static readonly OptionSpec ReplacePrev = new()
    {
        Name = "replace-prev",
        Aliases = ["replace-previous", "discard-prev"],
        Arity = Arity.Flag,
        Help = "Keep no previous generation of this name at all: the same as --keep 1. Every '{name}.prev' " +
               "already on disk is deleted along with it.",
    };

    /// <summary>命令行 &gt; config &gt; 默认值。两个 flag 说的是同一件事,冲突时不猜。</summary>
    public static int ResolveKeep(RimConfig config, ParseResult args)
    {
        var replacePrev = args.Flag("replace-prev");
        var text = args.Value("keep");
        if (text is null) return replacePrev ? 1 : config.SnapshotKeep;

        if (!int.TryParse(text, out var keep) || keep < 1)
            throw new CliUsageException(
                $"--keep takes how many generations to keep, counting the one being written, so it is at least 1. " +
                $"'{text}' is not that. --keep 1 overwrites and leaves no comparison behind.");

        if (replacePrev && keep != 1)
            throw new CliUsageException(
                $"--replace-prev means --keep 1, but --keep {keep} was also given. Pass only one of them.");

        return keep;
    }

    /// <summary>
    /// 把刚建好的库装到登记名下。调用方把文件写到 <paramref name="incomingPath"/>
    /// (不要直接写目标,否则没机会先比)。
    /// </summary>
    public static SnapshotInstall Install(string incomingPath, string destPath, int keep)
    {
        try
        {
            if (File.Exists(destPath) && EmptyDiff(incomingPath, destPath))
                return new SnapshotInstall(SnapshotInstallKind.Unchanged, ExistingGenerations(destPath), null);

            if (!File.Exists(destPath))
            {
                File.Move(incomingPath, destPath);
                return new SnapshotInstall(SnapshotInstallKind.Written, ExistingGenerations(destPath), null);
            }

            // 从最老那一代往回移,免得后一次覆盖前一次。keep 含当前那份,所以旧代上限是 keep-1。
            var oldest = keep - 1;
            string? dropped = null;
            if (oldest >= 1 && File.Exists(PrevPath(destPath, oldest)))
            {
                dropped = PrevAlias(destPath, oldest);
                File.Delete(PrevPath(destPath, oldest));
            }

            for (var gen = oldest; gen >= 2; gen--)
                if (File.Exists(PrevPath(destPath, gen - 1)))
                    File.Move(PrevPath(destPath, gen - 1), PrevPath(destPath, gen));

            // keep=1 时连 .prev 都不留 —— 连同以前留下的那些代一起收掉,
            // 否则「不留对照」会留下一堆更老的对照。
            if (oldest == 0)
            {
                foreach (var stale in ExistingPrevPaths(destPath))
                {
                    dropped ??= PrevAlias(destPath, 1);
                    File.Delete(stale);
                }
                File.Delete(destPath);
            }
            else
            {
                File.Move(destPath, PrevPath(destPath, 1));
            }

            File.Move(incomingPath, destPath);
            return new SnapshotInstall(SnapshotInstallKind.Replaced, ExistingGenerations(destPath), dropped);
        }
        finally
        {
            if (File.Exists(incomingPath))
                try { File.Delete(incomingPath); } catch { /* 主路径已就位或失败 */ }
        }
    }

    public static string KeptPrevious(string name, int kept)
        => kept == 1
            ? $"The previous '{name}' was kept as '{name}.prev'. " +
              $"'rimsearcher snapshot diff {name}.prev {name}' compares them."
            : $"The previous '{name}' was kept as '{name}.prev', with {kept - 1} older " +
              $"generation{(kept == 2 ? "" : "s")} behind it up to '{name}.prev{kept}'. " +
              $"'rimsearcher snapshot diff {name}.prev {name}' compares the two newest.";

    public static string DroppedOldest(string dropped, int keep)
        => keep == 1
            ? $"No previous generation is kept under this name at --keep 1, so '{dropped}' and any older ones " +
              "were deleted along with the file they described."
            : $"'{dropped}' fell out of the {keep} generations kept under this name and was deleted. " +
              "Raise 'snapshot_keep' in the config file, or pass --keep, to keep more of them.";

    public static string Unchanged(string name)
        => $"The incoming snapshot's resolved defs and field values match '{name}', " +
           "so the existing file was left in place.";

    public static string IncomingPath(string destPath) => destPath + ".incoming";

    /// <summary>第 1 代是 <c>{name}.prev</c>,再老的带序号。0 就是当前那份。</summary>
    public static string PrevPath(string destPath, int generation = 1)
    {
        if (generation <= 0) return destPath;
        var dir = Path.GetDirectoryName(destPath) ?? "";
        return Path.Combine(dir, PrevAlias(destPath, generation) + ".db");
    }

    public static string PrevAlias(string destPath, int generation)
        => Path.GetFileNameWithoutExtension(destPath) + ".prev" + (generation == 1 ? "" : generation.ToString());

    /// <summary>磁盘上现有的旧代数 —— 从 1 起连续数,断了就停。</summary>
    private static int ExistingGenerations(string destPath)
    {
        var n = 0;
        while (File.Exists(PrevPath(destPath, n + 1))) n++;
        return n;
    }

    private static IEnumerable<string> ExistingPrevPaths(string destPath)
    {
        for (var gen = 1; File.Exists(PrevPath(destPath, gen)); gen++)
            yield return PrevPath(destPath, gen);
    }

    private static bool EmptyDiff(string oldPath, string newPath)
    {
        var diff = SnapshotDiff.Compare(oldPath, newPath, limit: 1);
        return diff.AddedTotal == 0 && diff.RemovedTotal == 0 && diff.FieldsTotal == 0;
    }
}
