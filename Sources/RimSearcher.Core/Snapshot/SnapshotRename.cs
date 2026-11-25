using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Contract;

namespace RimSearcher.Snapshot;

public enum SnapshotSlot { Snapshot, ModList, Export }

public sealed record SnapshotRenamePlan(
    string From,
    string To,
    IReadOnlyList<(string Source, string Destination)> Moves,
    IReadOnlyDictionary<SnapshotSlot, string> Status,
    bool PinFollows,
    string? PinWas);

public static class SnapshotRename
{
    public static SnapshotRenamePlan Prepare(RimConfig config, string from, string to)
    {
        CheckName(from, "old");
        CheckName(to, "new");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("两个名称相同。改名需要两个不同的名称。");

        var oldDb = SnapshotCatalog.DatabasePath(config, from);
        var newDb = SnapshotCatalog.DatabasePath(config, to);
        var oldList = ModListIo.LocalPath(config, from);
        var newList = ModListIo.LocalPath(config, to);
        var oldExport = SnapshotCatalog.ExportPath(config, from);
        var newExport = SnapshotCatalog.ExportPath(config, to);

        var dbPresent = File.Exists(oldDb);
        var listPresent = File.Exists(oldList);
        var exportPresent = File.Exists(oldExport);

        if (!dbPresent && !listPresent && !exportPresent)
            throw new CliUsageException(
                $"本命令移动的三处都没有名为 '{from}' 的文件：" +
                $"快照目录中没有 {from}.db，" +
                $"配置文件旁没有 {from}{ModListIo.Extension}，" +
                $"导出目录中没有 {from}{IntermediateFormat.FileExtension}。");

        var occupied = new List<string>();
        if (File.Exists(newDb))
            occupied.Add($"快照目录中的 {to}.db");
        if (File.Exists(newList))
            occupied.Add($"配置文件旁的 {to}{ModListIo.Extension}");
        if (File.Exists(newExport))
            occupied.Add($"导出目录中的 {to}{IntermediateFormat.FileExtension}");

        var prevMoves = new List<(string Src, string Dest)>();
        if (dbPresent)
        {
            for (var gen = 1; File.Exists(SnapshotRetention.PrevPath(oldDb, gen)); gen++)
            {
                var dest = SnapshotRetention.PrevPath(newDb, gen);
                prevMoves.Add((SnapshotRetention.PrevPath(oldDb, gen), dest));
                if (File.Exists(dest))
                    occupied.Add($"快照目录中的 {Path.GetFileName(dest)}");
            }
        }

        if (occupied.Count > 0)
            throw new CliUsageException(
                Output.Cjk.Join($"无法把 {from} 改名为 {to}：该名称已被" + string.Join("、", occupied), "占用。") +
                "未移动任何文件。");

        var moves = new List<(string Source, string Destination)>();
        var status = new Dictionary<SnapshotSlot, string>();

        if (dbPresent)
        {
            moves.Add((oldDb, newDb));
            var bits = new List<string> { $"{from}.db 改名为 {to}.db" };
            foreach (var (src, dest) in prevMoves)
            {
                moves.Add((src, dest));
                bits.Add($"{Path.GetFileName(src)} 改名为 {Path.GetFileName(dest)}");
            }
            status[SnapshotSlot.Snapshot] = string.Join("；", bits);
        }
        else
        {
            status[SnapshotSlot.Snapshot] =
                $"不存在（在快照目录中查找 {from}.db）";
        }

        if (listPresent)
        {
            moves.Add((oldList, newList));
            status[SnapshotSlot.ModList] = $"{from}{ModListIo.Extension} 改名为 {to}{ModListIo.Extension}";
        }
        else
        {
            status[SnapshotSlot.ModList] =
                $"不存在（在配置文件旁查找 {from}{ModListIo.Extension}）";
        }

        if (exportPresent)
        {
            moves.Add((oldExport, newExport));
            status[SnapshotSlot.Export] =
                $"{from}{IntermediateFormat.FileExtension} 改名为 {to}{IntermediateFormat.FileExtension}";
        }
        else
        {
            status[SnapshotSlot.Export] =
                $"不存在（在导出目录中查找 {from}{IntermediateFormat.FileExtension}）";
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
                catch { stuck.Add($"{Path.GetFileName(done[i].Src)} 现位于 {done[i].Dest}"); }
            }

            if (stuck.Count > 0)
                throw new CliUsageException(
                    $"改名失败（{ex.Message}），此前已移动的部分文件中有以下文件回滚失败：" +
                    string.Join("；", stuck) + "。手动将这些文件移回即可收尾。");

            throw new CliUsageException(
                $"改名失败（{ex.Message}）。文件已恢复为原名。");
        }
    }

    public const string PinFollowed = "已跟随";

    public static string PinStatus(SnapshotRenamePlan plan)
        => plan.PinFollows ? PinFollowed
         : plan.PinWas is { Length: > 0 } pin ? $"未变（仍为「{pin}」）"
         : "未固定";

    private static void CheckName(string name, string which)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CliUsageException($"<{which}> 为空。");
        if (name is "." or ".." || name != Path.GetFileName(name))
            throw new CliUsageException(
                $"'{name}' 不是快照名：快照名须为单个文件名，不能是路径。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CliUsageException(
                $"'{name}' 不是快照名：其中含有文件名不允许的字符。");
    }
}
