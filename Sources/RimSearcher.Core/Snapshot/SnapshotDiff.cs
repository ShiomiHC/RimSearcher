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
    int TruncatedDefs,
    /// <summary>
    /// 被截过的那批分成「少了路径 / 只切了值」两拨。null = **两侧至少有一侧**没分过类,
    /// 那时分不出来 —— 而按 <c>--keep</c> 留下的 `.prev` 全是旧代,这一路是常态不是意外。
    /// </summary>
    Storage.SnapshotDb.TruncationSpread? TruncatedSpread);

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

            // 两侧各自包一层,把 field_values 摊平成同一个形状 (def_id, path, value, rid)。
            // **两侧的形状可以不同** —— 新库的路径住在字典表里、`--keep` 留下的旧代还是
            // 逐行存字符串,而 diff 的正主就是拿旧代比新库。逐 alias 探列名,而不是假定同形。
            var newerFv = FieldView(db, "newer");
            var priorFv = FieldView(db, "prior");

            var FieldUnion = $"""
                SELECT n.def_name AS def_name, n.def_type AS def_type, fv.path AS path,
                       ov.value AS old_value, fv.value AS new_value, n.source_mod AS mod
                FROM newer.defs n
                JOIN {newerFv} fv ON fv.def_id = n.id
                JOIN prior.defs o ON o.def_type = n.def_type AND o.def_name = n.def_name
                LEFT JOIN {priorFv} ov ON ov.def_id = o.id AND ov.path = fv.path
                WHERE ov.rid IS NULL OR ov.value IS NOT fv.value
                UNION ALL
                SELECT o.def_name, o.def_type, ov.path,
                       ov.value, NULL, o.source_mod
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

            var fieldsTotal = Scalar(db, "SELECT COUNT(*) FROM field_diff");
            var truncatedDefs = Scalar(db,
                "SELECT COUNT(*) FROM newer.defs n JOIN prior.defs o " +
                "ON o.def_type = n.def_type AND o.def_name = n.def_name " +
                "WHERE n.fields_truncated > 0 OR o.fields_truncated > 0");
            var fields = ReadFields(db,
                "SELECT def_name, def_type, path, old_value, new_value, mod FROM field_diff " +
                "ORDER BY def_type, def_name, path LIMIT " + limit);

            return new SnapshotDiffResult(added, addedTotal, removed, removedTotal,
                                          fields, fieldsTotal, truncatedDefs, TruncationSplit(db));
        }
        finally
        {
            using var detach = db.CreateCommand();
            detach.CommandText = "DETACH DATABASE prior; DETACH DATABASE newer;";
            try { detach.ExecuteNonQuery(); } catch (SqliteException) { /* 主路径已失败 */ }
        }
    }

    /// <summary>
    /// 被截过的 def 分成两拨。**两侧都得分过类**才答得出:一侧分过一侧没分,把没分的那侧
    /// 一律算成「只切了值」就会把它丢掉的路径说成没丢,而那正是这句话要防的读法。
    ///
    /// 「分过类」= 那四列在,且真有一行非零。只看列在不在会把「旧导出进了新库、四列拿
    /// DEFAULT 0」当成分好类的,那种库上每个被截的 def 都会被判成只切了值。
    /// </summary>
    private static SnapshotDb.TruncationSpread? TruncationSplit(SqliteConnection db)
    {
        if (!Classified("newer") || !Classified("prior")) return null;

        const string joined = "FROM newer.defs n JOIN prior.defs o "
                            + "ON o.def_type = n.def_type AND o.def_name = n.def_name ";
        var lost = $"({Missing("n")} > 0 OR {Missing("o")} > 0)";
        return new SnapshotDb.TruncationSpread(
            Scalar(db, $"SELECT COUNT(*) {joined}WHERE {lost}"),
            Scalar(db, $"SELECT COUNT(*) {joined}"
                     + $"WHERE (n.fields_truncated > 0 OR o.fields_truncated > 0) AND NOT {lost}"));

        static string Missing(string a)
            => $"{a}.truncated_by_cap + {a}.truncated_by_depth + {a}.truncated_by_items";

        bool Classified(string alias)
            => HasColumn(db, alias, "defs", "truncated_by_cap")
               && Scalar(db, $"SELECT COUNT(*) FROM {alias}.defs WHERE truncated_by_cap "
                           + "+ truncated_by_length + truncated_by_depth + truncated_by_items > 0") > 0;
    }

    /// <summary>
    /// 一侧的字段行,摊平成 (def_id, path, value, rid)。字典化的库从 field_value_paths 取
    /// 路径,旧库直接取自己的 path 列 —— 上层那段 SQL 因此两种库通吃,也吃得下两种混着比。
    ///
    /// <c>rid</c> 单列出来:外层拿它判 LEFT JOIN 有没有配上,而子查询没有 rowid。
    /// </summary>
    private static string FieldView(SqliteConnection db, string alias)
    {
        if (!HasColumn(db, alias, "field_values", "path_id"))
            return $"(SELECT v.def_id AS def_id, v.path AS path, v.value AS value, v.rowid AS rid "
                 + $"   FROM {alias}.field_values v)";

        // 值也可能进了字典。**LEFT** JOIN:value_id 可空,内连会把值为空的行整个丢掉,
        // 而「这一侧没有值」正是 diff 要报的一种变化。
        var valueDict = HasColumn(db, alias, "field_values", "value_id");
        return "(SELECT v.def_id AS def_id, p.path AS path, "
             + (valueDict ? "w.value" : "v.value") + " AS value, v.rowid AS rid "
             + $"   FROM {alias}.field_values v "
             + $"   JOIN {alias}.field_value_paths p ON p.id = v.path_id"
             + (valueDict ? $" LEFT JOIN {alias}.field_value_values w ON w.id = v.value_id" : "")
             + ")";
    }

    private static bool HasColumn(SqliteConnection db, string alias, string table, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}', '{alias}') WHERE name = $c";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
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
