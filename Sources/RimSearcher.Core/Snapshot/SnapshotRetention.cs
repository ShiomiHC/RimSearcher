using Microsoft.Data.Sqlite;
using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Contract;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Snapshot;

public enum SnapshotInstallKind
{
    Written,

    Replaced,

    Unchanged,
}

public sealed record SnapshotInstall(SnapshotInstallKind Kind, int Kept, IReadOnlyList<string> Dropped);

public static class SnapshotRetention
{
    public static readonly CliOption Keep = CliOption.Text("keep") with
    {
        Aliases = ["generations", "keep-generations"],
        Placeholder = "<n>",
        Help = "该快照名保留的世代数，计入正在写入的那一份。1 表示直接覆盖，不保留旧世代。" +
               "缺省取配置文件的 snapshot_keep。",
        Default = RimConfig.SnapshotKeepDefault.ToString(),
    };

    public static readonly CliOption ReplacePrev = CliOption.Flag("replace-prev") with
    {
        Aliases = ["replace-previous", "discard-prev"],
        Help = "该快照名不保留任何旧世代，等同 --keep 1。新快照与现有内容不同时，已有的每个 <名称>.prev 一并删除。",
    };

    public static int ResolveKeep(RimConfig config, CommandArgs args)
    {
        var replacePrev = args.Flag(ReplacePrev);
        var text = args.Value(Keep);
        if (text is null) return replacePrev ? 1 : config.SnapshotKeep;

        if (!int.TryParse(text, out var keep) || keep < 1)
            throw new CliUsageException(
                $"--keep 不接受 '{text}'：取保留的世代数，计入正在写入的那一份，至少为 1。");

        if (replacePrev && keep != 1)
            throw new CliUsageException(
                $"--replace-prev 等同 --keep 1，与 --keep {keep} 不能同时传入。");

        return keep;
    }

    public static SnapshotInstall Install(string incomingPath, string destPath, int keep)
    {
        try
        {
            if (File.Exists(destPath) && EmptyDiff(incomingPath, destPath))
            {
                RefreshFingerprint(incomingPath, destPath);
                return new SnapshotInstall(SnapshotInstallKind.Unchanged, ExistingGenerations(destPath), []);
            }

            if (!File.Exists(destPath))
            {
                File.Move(incomingPath, destPath);
                return new SnapshotInstall(SnapshotInstallKind.Written, ExistingGenerations(destPath), []);
            }

            var existing = FindExistingPrevs(destPath);
            var dropped = new List<string>();
            if (keep == 1)
            {
                foreach (var old in existing)
                {
                    File.Delete(old.Path);
                    dropped.Add(old.Alias);
                }
                File.Move(incomingPath, destPath, overwrite: true);
                return new SnapshotInstall(SnapshotInstallKind.Replaced, 0, dropped);
            }

            var maxOlder = keep - 2;
            var survivors = existing.Take(maxOlder).ToList();
            foreach (var old in existing.Skip(maxOlder))
            {
                File.Delete(old.Path);
                dropped.Add(old.Alias);
            }

            for (var i = survivors.Count - 1; i >= 0; i--)
            {
                var target = 2 + i;
                if (target > survivors[i].Gen)
                    File.Move(survivors[i].Path, PrevPath(destPath, target));
            }
            for (var i = 0; i < survivors.Count; i++)
            {
                var target = 2 + i;
                if (target < survivors[i].Gen)
                    File.Move(survivors[i].Path, PrevPath(destPath, target));
            }

            File.Move(destPath, PrevPath(destPath, 1));
            File.Move(incomingPath, destPath, overwrite: true);
            return new SnapshotInstall(SnapshotInstallKind.Replaced, 1 + survivors.Count, dropped);
        }
        finally
        {
            if (File.Exists(incomingPath))
                try { File.Delete(incomingPath); } catch {  }
        }
    }

    public static string KeptPrevious(string name, int kept)
        => kept == 1
            ? $"原有的 {name} 已保留为 {name}.prev。" +
              $"'rimsearcher snapshot diff {name}.prev {name}' 比较两者。"
            : $"原有的 {name} 已保留为 {name}.prev，再往前还有更早的 {Output.Tally.Complete(kept - 1).Render("generation")}，" +
              $"直至 {name}.prev{kept}。" +
              $"'rimsearcher snapshot diff {name}.prev {name}' 比较最新的两份。";

    public static string DroppedOldest(IReadOnlyList<string> dropped, int keep)
    {
        var names = Output.NameList.Render(dropped, 6);
        return keep == 1
            ? $"--keep 1 时这个名称不保留旧世代，因此 {names} 已删除。"
            : $"{names} 超出这个名称保留的 {Output.Tally.Complete(keep).Render("generation")}，已被删除。";
    }

    public static string Unchanged(string name)
        => $"新快照与 {name} 内容相同，因此未替换现有文件，旧世代不变。";

    public static string IncomingPath(string destPath) => destPath + ".incoming";

    public static string PrevPath(string destPath, int generation = 1)
    {
        if (generation <= 0) return destPath;
        var dir = Path.GetDirectoryName(destPath) ?? "";
        return Path.Combine(dir, PrevAlias(destPath, generation) + ".db");
    }

    public static string PrevAlias(string destPath, int generation)
        => Path.GetFileNameWithoutExtension(destPath) + ".prev" + (generation == 1 ? "" : generation.ToString());

    public static bool IsGeneration(string alias) => GenerationOf(alias).Generation > 0;

    public static (string Name, int Generation) GenerationOf(string alias)
    {
        var m = System.Text.RegularExpressions.Regex.Match(alias, @"^(.+)\.prev(\d*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return (alias, 0);
        var digits = m.Groups[2].Value;
        return (m.Groups[1].Value, digits.Length == 0 ? 1 : int.TryParse(digits, out var n) ? n : int.MaxValue);
    }

    private static int ExistingGenerations(string destPath)
    {
        var n = 0;
        while (File.Exists(PrevPath(destPath, n + 1))) n++;
        return n;
    }

    private static List<(int Gen, string Path, string Alias)> FindExistingPrevs(string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];
        var stem = Path.GetFileNameWithoutExtension(destPath);
        var found = new List<(int Gen, string Path, string Alias)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.db"))
        {
            var alias = Path.GetFileNameWithoutExtension(file);
            var (name, gen) = GenerationOf(alias);
            if (!string.Equals(name, stem, StringComparison.OrdinalIgnoreCase) || gen < 1) continue;
            if (!string.Equals(PrevAlias(destPath, gen), alias, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add((gen, file, alias));
        }
        found.Sort((a, b) => a.Gen.CompareTo(b.Gen));
        return found;
    }

    private static bool EmptyDiff(string oldPath, string newPath)
    {
        using var older = OpenReadOnly(oldPath);
        using var newer = OpenReadOnly(newPath);
        if (Layout(older) != Layout(newer) || Meta(older) != Meta(newer)) return false;

        foreach (var table in ContentTables(older))
        {
            string? a = null, b = null;
            Parallel.Invoke(() => a = TableHash(older, table), () => b = TableHash(newer, table));
            if (a != b) return false;
        }
        return true;
    }

    private static readonly string[] RunTraces =
    [
        SnapshotSchema.MetaKeyImportedAtUtc, SnapshotSchema.MetaKeySourcePath,
        SnapshotSchema.MetaKeyExportTimings, SnapshotSchema.MetaKeyImportTimings, SnapshotSchema.MetaKeyContent,
    ];

    private static readonly string[] RawRunTraces =
        [IntermediateFormat.KeyExportedAtUtc, IntermediateFormat.KeyModSettingsHash];

    private static string? Meta(SqliteConnection db)
        => Text(db,
            "SELECT group_concat(key || '=' || COALESCE(v, '\u0002'), '\u0001') FROM (" +
            $"SELECT key, CASE key WHEN '{SnapshotSchema.MetaKeyRaw}' THEN json_remove(value, " +
            string.Join(", ", RawRunTraces.Select(k => $"'$.{k}'")) + ") " +
            "ELSE value END AS v FROM meta WHERE key NOT IN (" +
            string.Join(", ", RunTraces.Select(k => $"'{k}'")) + ") ORDER BY key)");

    private static List<string> ContentTables(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT t.name FROM sqlite_master t WHERE t.type = 'table' AND t.name NOT LIKE 'sqlite\\_%' ESCAPE '\\' " +
            "AND t.name <> 'meta' AND t.sql NOT LIKE 'CREATE VIRTUAL TABLE%' AND NOT EXISTS (" +
            "SELECT 1 FROM sqlite_master v WHERE v.type = 'table' AND v.sql LIKE 'CREATE VIRTUAL TABLE%' " +
            "AND t.name LIKE v.name || '\\_%' ESCAPE '\\') ORDER BY t.name";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));
        return tables;
    }

    private static string TableHash(SqliteConnection db, string table)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\"";
        using var r = cmd.ExecuteReader();
        var head = new byte[9];
        var text = new byte[256];
        while (r.Read())
        {
            for (var i = 0; i < r.FieldCount; i++)
            {
                var type = r.IsDBNull(i) ? null : r.GetFieldType(i);
                if (type == typeof(long))
                {
                    head[0] = 1;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(1), r.GetInt64(i));
                    sha.AppendData(head);
                }
                else if (type == typeof(double))
                {
                    head[0] = 2;
                    System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(head.AsSpan(1), r.GetDouble(i));
                    sha.AppendData(head);
                }
                else if (type == typeof(string))
                {
                    var s = r.GetString(i);
                    var max = System.Text.Encoding.UTF8.GetMaxByteCount(s.Length);
                    if (text.Length < max) text = new byte[max];
                    var n = System.Text.Encoding.UTF8.GetBytes(s, text);
                    head[0] = 3;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(1), n);
                    sha.AppendData(head);
                    sha.AppendData(text, 0, n);
                }
                else if (type == typeof(byte[]))
                {
                    var blob = r.GetFieldValue<byte[]>(i);
                    head[0] = 4;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(1), blob.Length);
                    sha.AppendData(head);
                    sha.AppendData(blob);
                }
                else
                {
                    sha.AppendData([0]);
                }
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var db = new SqliteConnection($"Data Source={Path.GetFullPath(path)};Mode=ReadOnly;Pooling=False");
        db.Open();
        return db;
    }

    private static void RefreshFingerprint(string incomingPath, string destPath)
    {
        string? content;
        using (var incoming = new SqliteConnection(
                   $"Data Source={Path.GetFullPath(incomingPath)};Mode=ReadOnly;Pooling=False"))
        {
            incoming.Open();
            content = Text(incoming, $"SELECT value FROM meta WHERE key = '{SnapshotSchema.MetaKeyContent}'");
        }
        if (content is null) return;

        using var dest = new SqliteConnection($"Data Source={Path.GetFullPath(destPath)};Pooling=False;Default Timeout=30");
        dest.Open();
        using var cmd = dest.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", SnapshotSchema.MetaKeyContent);
        cmd.Parameters.AddWithValue("$v", content);
        cmd.ExecuteNonQuery();
    }

    private static string? Layout(SqliteConnection db)
    {
        var sql = Text(db,
            "SELECT group_concat(sql, ';') FROM (SELECT sql FROM sqlite_master " +
            "WHERE sql IS NOT NULL ORDER BY type, name)");
        if (sql is null) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(hash)[..12];
    }

    private static string? Text(SqliteConnection db, string sql)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : value.ToString();
        }
        catch (SqliteException) { return null; }
    }
}
