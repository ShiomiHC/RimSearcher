using Microsoft.Data.Sqlite;
using RimSearcher.Storage;

namespace RimSearcher.Snapshot;

public sealed record DiffDefRow(string DefName, string DefType, string? Mod);

public sealed record DiffFieldRow(string DefName, string DefType, string Path,
                                  string? Old, string? New, string? Mod);

public sealed record SnapshotDiffResult(
    IReadOnlyList<DiffDefRow> Added, int AddedTotal,
    IReadOnlyList<DiffDefRow> Removed, int RemovedTotal,
    IReadOnlyList<DiffFieldRow> Fields, int FieldsTotal,
    IReadOnlyList<DiffFieldRow> AllFields,
    int TruncatedDefs,
    Storage.SnapshotDb.TruncationSpread TruncatedSpread,
    int? ReprintedOnly);

public static class SnapshotDiff
{
    public static SnapshotDiffResult Compare(string oldPath, string newPath, int limit,
                                             UnityEraSide unitySide = UnityEraSide.Neither)
    {
        using var db = new SqliteConnection("Data Source=:memory:;Pooling=False");
        db.Open();
        db.CreateFunction("unity_same", (string? unity, string? waylith)
            => unity is not null && waylith is not null && EngineNotation.SameValue(unity, waylith),
            isDeterministic: true);
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

            var newerFv = FieldView("newer");
            var priorFv = FieldView("prior");

            var reprinted = unitySide switch
            {
                UnityEraSide.Old => "unity_same(ov.value, fv.value)",
                UnityEraSide.New => "unity_same(fv.value, ov.value)",
                _ => "0",
            };

            var FieldUnion = $"""
                SELECT n.def_name AS def_name, n.def_type AS def_type, fv.path AS path,
                       ov.value AS old_value, fv.value AS new_value, n.source_mod AS mod,
                       {reprinted} AS reprinted
                FROM newer.defs n
                JOIN {newerFv} fv ON fv.def_id = n.id
                JOIN prior.defs o ON o.def_type = n.def_type AND o.def_name = n.def_name
                LEFT JOIN {priorFv} ov ON ov.def_id = o.id AND ov.path = fv.path
                WHERE ov.rid IS NULL OR ov.value IS NOT fv.value
                UNION ALL
                SELECT o.def_name, o.def_type, ov.path,
                       ov.value, NULL, o.source_mod, 0
                FROM prior.defs o
                JOIN {priorFv} ov ON ov.def_id = o.id
                JOIN newer.defs n ON n.def_type = o.def_type AND n.def_name = o.def_name
                LEFT JOIN {newerFv} fv ON fv.def_id = n.id AND fv.path = ov.path
                WHERE fv.rid IS NULL
                """;

            using (var materialise = db.CreateCommand())
            {
                materialise.CommandText = "CREATE TEMP TABLE field_diff AS " + FieldUnion;
                materialise.ExecuteNonQuery();
            }

            int? reprintedOnly = unitySide == UnityEraSide.Neither
                ? null
                : Scalar(db, "SELECT COUNT(*) FROM field_diff WHERE reprinted");
            var truncatedDefs = Scalar(db,
                "SELECT COUNT(*) FROM newer.defs n JOIN prior.defs o " +
                "ON o.def_type = n.def_type AND o.def_name = n.def_name " +
                "WHERE n.fields_truncated > 0 OR o.fields_truncated > 0");
            var allFields = ReadFields(db,
                "SELECT def_name, def_type, path, old_value, new_value, mod FROM field_diff " +
                "WHERE NOT reprinted ORDER BY def_type, def_name, path");

            return new SnapshotDiffResult(added, addedTotal, removed, removedTotal,
                                          allFields.Take(limit).ToList(), allFields.Count, allFields,
                                          truncatedDefs, TruncationSplit(db),
                                          reprintedOnly);
        }
        finally
        {
            using var detach = db.CreateCommand();
            detach.CommandText = "DETACH DATABASE prior; DETACH DATABASE newer;";
            try { detach.ExecuteNonQuery(); } catch (SqliteException) {  }
        }
    }

    private static SnapshotDb.TruncationSpread TruncationSplit(SqliteConnection db)
    {
        const string joined = "FROM newer.defs n JOIN prior.defs o "
                            + "ON o.def_type = n.def_type AND o.def_name = n.def_name ";
        var lost = $"({Missing("n")} > 0 OR {Missing("o")} > 0)";
        return new SnapshotDb.TruncationSpread(
            Scalar(db, $"SELECT COUNT(*) {joined}WHERE {lost}"),
            Scalar(db, $"SELECT COUNT(*) {joined}"
                     + $"WHERE (n.fields_truncated > 0 OR o.fields_truncated > 0) AND NOT {lost}"));

        static string Missing(string a)
            => $"{a}.truncated_by_cap + {a}.truncated_by_depth + {a}.truncated_by_items + {a}.truncated_by_error";
    }

    private static string FieldView(string alias)
        => "(SELECT v.def_id AS def_id, p.path AS path, w.value AS value, v.rowid AS rid "
             + $"   FROM {alias}.field_values v "
             + $"   JOIN {alias}.field_value_paths p ON p.id = v.path_id"
             + $" LEFT JOIN {alias}.field_value_values w ON w.id = v.value_id)";

    private static void Attach(SqliteConnection db, string path, string alias)
    {
        using var cmd = db.CreateCommand();
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
