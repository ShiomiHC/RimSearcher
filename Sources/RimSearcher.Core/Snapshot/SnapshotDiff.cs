using Microsoft.Data.Sqlite;

namespace RimSearcher.Snapshot;

public sealed record DiffDefRow(string DefName, string DefType, string? Mod);

public sealed record DiffFieldRow(string DefName, string DefType, string Path,
                                  string? Old, string? New, string? Mod);

public sealed record SnapshotDiffResult(
    IReadOnlyList<DiffDefRow> Added, int AddedTotal,
    IReadOnlyList<DiffDefRow> Removed, int RemovedTotal,
    IReadOnlyList<DiffFieldRow> Fields, int FieldsTotal,
    int TruncatedDefs);

/// <summary>
/// 两份快照之间已解析 def / 字段的差。磁盘 XML 与 git 都不在这里 —— 比的是 import 之后
/// 的 <c>defs</c> 与 <c>field_values</c>。
/// </summary>
public static class SnapshotDiff
{
    public static SnapshotDiffResult Compare(string oldPath, string newPath, int limit)
    {
        // Pooling=False 的成因写在 SnapshotDb.Open 那里。这条连接自己虽是内存库,
        // ATTACH 进来的两个**是磁盘文件** —— 连接一旦入池,它们就跟着连接留在池里开着,
        // 而调用方(SnapshotRetention.Install)紧接着就要移动其中一个。
        using var db = new SqliteConnection("Data Source=:memory:;Pooling=False");
        db.Open();
        Attach(db, Path.GetFullPath(oldPath), "prior");
        Attach(db, Path.GetFullPath(newPath), "newer");

        try
        {
            var addedTotal = Scalar(db,
                "SELECT COUNT(*) FROM newer.defs n LEFT JOIN prior.defs o " +
                "ON o.def_type = n.def_type AND o.def_name = n.def_name WHERE o.id IS NULL");
            var added = ReadDefs(db,
                "SELECT n.def_name, n.def_type, n.source_mod FROM newer.defs n " +
                "LEFT JOIN prior.defs o ON o.def_type = n.def_type AND o.def_name = n.def_name " +
                "WHERE o.id IS NULL ORDER BY n.def_type, n.def_name LIMIT " + limit);

            var removedTotal = Scalar(db,
                "SELECT COUNT(*) FROM prior.defs o LEFT JOIN newer.defs n " +
                "ON n.def_type = o.def_type AND n.def_name = o.def_name WHERE n.id IS NULL");
            var removed = ReadDefs(db,
                "SELECT o.def_name, o.def_type, o.source_mod FROM prior.defs o " +
                "LEFT JOIN newer.defs n ON n.def_type = o.def_type AND n.def_name = o.def_name " +
                "WHERE n.id IS NULL ORDER BY o.def_type, o.def_name LIMIT " + limit);

            const string FieldUnion = """
                SELECT n.def_name AS def_name, n.def_type AS def_type, fv.path AS path,
                       ov.value AS old_value, fv.value AS new_value, n.source_mod AS mod
                FROM newer.defs n
                JOIN newer.field_values fv ON fv.def_id = n.id
                JOIN prior.defs o ON o.def_type = n.def_type AND o.def_name = n.def_name
                LEFT JOIN prior.field_values ov ON ov.def_id = o.id AND ov.path = fv.path
                WHERE ov.rowid IS NULL OR ov.value IS NOT fv.value
                UNION ALL
                SELECT o.def_name, o.def_type, ov.path,
                       ov.value, NULL, o.source_mod
                FROM prior.defs o
                JOIN prior.field_values ov ON ov.def_id = o.id
                JOIN newer.defs n ON n.def_type = o.def_type AND n.def_name = o.def_name
                LEFT JOIN newer.field_values fv ON fv.def_id = n.id AND fv.path = ov.path
                WHERE fv.rowid IS NULL
                """;

            using (var materialise = db.CreateCommand())
            {
                materialise.CommandText = "CREATE TEMP TABLE field_diff AS " + FieldUnion;
                materialise.ExecuteNonQuery();
            }

            var fieldsTotal = Scalar(db, "SELECT COUNT(*) FROM field_diff");
            var truncatedDefs = Scalar(db,
                "SELECT COUNT(*) FROM newer.defs n JOIN prior.defs o " +
                "ON o.def_type = n.def_type AND o.def_name = n.def_name " +
                "WHERE n.fields_truncated > 0 OR o.fields_truncated > 0");
            var fields = ReadFields(db,
                "SELECT def_name, def_type, path, old_value, new_value, mod FROM field_diff " +
                "ORDER BY def_type, def_name, path LIMIT " + limit);

            return new SnapshotDiffResult(added, addedTotal, removed, removedTotal,
                                          fields, fieldsTotal, truncatedDefs);
        }
        finally
        {
            using var detach = db.CreateCommand();
            detach.CommandText = "DETACH DATABASE prior; DETACH DATABASE newer;";
            try { detach.ExecuteNonQuery(); } catch (SqliteException) { /* 主路径已失败 */ }
        }
    }

    private static void Attach(SqliteConnection db, string path, string alias)
    {
        using var cmd = db.CreateCommand();
        // ATTACH 的路径历史上不吃绑定参数,写成字面量。别名是本文件写死的 prior / newer。
        cmd.CommandText = $"ATTACH DATABASE '{path.Replace("'", "''", StringComparison.Ordinal)}' AS {alias}";
        cmd.ExecuteNonQuery();
    }

    private static int Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static List<DiffDefRow> ReadDefs(SqliteConnection db, string sql)
    {
        var rows = new List<DiffDefRow>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            rows.Add(new DiffDefRow(rd.GetString(0), rd.GetString(1),
                                    rd.IsDBNull(2) ? null : rd.GetString(2)));
        return rows;
    }

    private static List<DiffFieldRow> ReadFields(SqliteConnection db, string sql)
    {
        var rows = new List<DiffFieldRow>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            rows.Add(new DiffFieldRow(rd.GetString(0), rd.GetString(1), rd.GetString(2),
                                      rd.IsDBNull(3) ? null : rd.GetString(3),
                                      rd.IsDBNull(4) ? null : rd.GetString(4),
                                      rd.IsDBNull(5) ? null : rd.GetString(5)));
        return rows;
    }
}
