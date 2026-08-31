using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Contract;

namespace RimSearcher.Snapshot;

/// <summary>
/// 一套快照的三处文件:<c>snapshots/名.db</c>、<c>modlists/名.rml</c>、
/// <c>exports/名.rsx.jsonl.gz</c>。路径公式各自只有一个产地,这里只编排改名。
/// </summary>
public enum SnapshotSlot { Snapshot, ModList, Export }

/// <param name="Moves">真正要挪的 (源, 目标) 对,缺席的不在里面。</param>
/// <param name="Status">三处各自一句:挪了什么,或在哪一处没找到。缺席不许省略。</param>
public sealed record SnapshotRenamePlan(
    string From,
    string To,
    IReadOnlyList<(string Source, string Destination)> Moves,
    IReadOnlyDictionary<SnapshotSlot, string> Status,
    bool PinFollows,
    string? PinWas);

/// <summary>
/// 先把三处看全、撞名一次拒完,再动手。中途失败尽量回滚;
/// 回滚也失败就把已经挪走的路径印出来,好手工收尾。
/// </summary>
public static class SnapshotRename
{
    public static SnapshotRenamePlan Prepare(RimConfig config, string from, string to)
    {
        CheckName(from, "old");
        CheckName(to, "new");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("Those names are the same. A rename needs two different names.");

        var oldDb = SnapshotCatalog.DatabasePath(config, from);
        var newDb = SnapshotCatalog.DatabasePath(config, to);
        var oldRml = ModListIo.LocalPath(config, from);
        var newRml = ModListIo.LocalPath(config, to);
        var oldExport = SnapshotCatalog.ExportPath(config, from);
        var newExport = SnapshotCatalog.ExportPath(config, to);

        var dbPresent = File.Exists(oldDb);
        var rmlPresent = File.Exists(oldRml);
        var exportPresent = File.Exists(oldExport);

        if (!dbPresent && !rmlPresent && !exportPresent)
            throw new CliUsageException(
                $"Nothing named '{from}' is in any of the three places this command moves: " +
                $"no '{from}.db' in the snapshot directory, " +
                $"no '{from}{ModListIo.Extension}' next to the config file, " +
                $"no '{from}{IntermediateFormat.FileExtension}' in the export directory.");

        var occupied = new List<string>();
        if (File.Exists(newDb))
            occupied.Add($"'{to}.db' in the snapshot directory");
        if (File.Exists(newRml))
            occupied.Add($"'{to}{ModListIo.Extension}' next to the config file");
        if (File.Exists(newExport))
            occupied.Add($"'{to}{IntermediateFormat.FileExtension}' in the export directory");

        var prevMoves = new List<(string Src, string Dest)>();
        if (dbPresent)
        {
            for (var gen = 1; File.Exists(SnapshotRetention.PrevPath(oldDb, gen)); gen++)
            {
                var dest = SnapshotRetention.PrevPath(newDb, gen);
                prevMoves.Add((SnapshotRetention.PrevPath(oldDb, gen), dest));
                if (File.Exists(dest))
                    occupied.Add($"'{Path.GetFileName(dest)}' in the snapshot directory");
            }
        }

        if (occupied.Count > 0)
            throw new CliUsageException(
                $"Cannot rename '{from}' to '{to}': that name is already used by " +
                JoinAnd(occupied) + ". Nothing was moved.");

        var moves = new List<(string Source, string Destination)>();
        var status = new Dictionary<SnapshotSlot, string>();

        if (dbPresent)
        {
            moves.Add((oldDb, newDb));
            var bits = new List<string> { $"{from}.db to {to}.db" };
            foreach (var (src, dest) in prevMoves)
            {
                moves.Add((src, dest));
                bits.Add($"{Path.GetFileName(src)} to {Path.GetFileName(dest)}");
            }
            status[SnapshotSlot.Snapshot] = string.Join("; ", bits);
        }
        else
        {
            status[SnapshotSlot.Snapshot] =
                $"not present (looked for '{from}.db' in the snapshot directory)";
        }

        if (rmlPresent)
        {
            moves.Add((oldRml, newRml));
            status[SnapshotSlot.ModList] = $"{from}{ModListIo.Extension} to {to}{ModListIo.Extension}";
        }
        else
        {
            status[SnapshotSlot.ModList] =
                $"not present (looked for '{from}{ModListIo.Extension}' next to the config file)";
        }

        if (exportPresent)
        {
            moves.Add((oldExport, newExport));
            status[SnapshotSlot.Export] =
                $"{from}{IntermediateFormat.FileExtension} to {to}{IntermediateFormat.FileExtension}";
        }
        else
        {
            status[SnapshotSlot.Export] =
                $"not present (looked for '{from}{IntermediateFormat.FileExtension}' in the export directory)";
        }

        var pin = config.ActiveSnapshot;
        var pinFollows = pin is { Length: > 0 } &&
                         string.Equals(pin, from, StringComparison.OrdinalIgnoreCase);

        return new SnapshotRenamePlan(from, to, moves, status, pinFollows, pin);
    }

    public static void Execute(IReadOnlyList<(string Source, string Destination)> moves)
    {
        var done = new List<(string Src, string Dest)>();
        try
        {
            foreach (var (src, dest) in moves)
            {
                File.Move(src, dest);
                done.Add((src, dest));
            }
        }
        catch (Exception ex)
        {
            var stuck = new List<string>();
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try { File.Move(done[i].Dest, done[i].Src); }
                catch { stuck.Add($"'{Path.GetFileName(done[i].Src)}' is now at '{done[i].Dest}'"); }
            }

            if (stuck.Count > 0)
                throw new CliUsageException(
                    $"Rename failed ({ex.Message}) after moving some files, and rolling back failed for: " +
                    string.Join("; ", stuck) + ". Move those back by hand to finish.");

            throw new CliUsageException(
                $"Rename failed ({ex.Message}). Files were restored to their original names.");
        }
    }

    public static string PinStatus(SnapshotRenamePlan plan)
        => plan.PinFollows ? "followed"
         : plan.PinWas is { Length: > 0 } pin ? $"unchanged (still '{pin}')"
         : "not pinned";

    private static void CheckName(string name, string which)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CliUsageException($"<{which}> is empty.");
        if (name is "." or ".." || name != Path.GetFileName(name))
            throw new CliUsageException(
                $"'{name}' is not a snapshot name: it must be a single file name, not a path.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CliUsageException(
                $"'{name}' is not a snapshot name: it contains a character that a file name cannot.");
    }

    private static string JoinAnd(IReadOnlyList<string> items)
        => items.Count == 1 ? items[0]
         : items.Count == 2 ? items[0] + " and " + items[1]
         : string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1];
}
