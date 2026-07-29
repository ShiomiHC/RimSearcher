using Microsoft.Data.Sqlite;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

public sealed record DefRow(long Id, string DefType, string DefName, string? Label, string? Description,
                            string? SourceMod, string? SourceFile, bool Generated, string? Class,
                            string? Parent, int FieldsTruncated);

public sealed record FieldRow(string Path, string Leaf, string? Value);

public sealed record TranslationRow(string DefName, string Path, string? Translated, string? Original,
                                   string? Language, string? SourceMod, string Origin);

/// <summary>
/// 快照库的只读查询面。所有带上限的查询都同时回传总数 —— 三态文法要求调用方能区分
/// 「就这么多」与「被截了」(02-1:上游全 CLI 返回裸数组,LIMIT 命中与否不可区分,
/// 这是 master 上被盲测反复验证过的第一优先级问题)。
/// </summary>
public sealed class SnapshotDb : IDisposable
{
    private readonly SqliteConnection _db;

    public string Path { get; }
    public ExportMeta Meta { get; }
    public IReadOnlyList<ModRef> Mods { get; }

    private SnapshotDb(SqliteConnection db, string path, ExportMeta meta, IReadOnlyList<ModRef> mods)
    {
        _db = db; Path = path; Meta = meta; Mods = mods;
    }

    public static SnapshotDb Open(string path)
    {
        if (!File.Exists(path))
            throw new SnapshotFormatError(
                $"No snapshot database at '{path}'. Run 'rimsearcher snapshot list' to see what is registered, " +
                "or 'rimsearcher export' to produce one from the game.");

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        db.Open();

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM meta";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) meta[rd.GetString(0)] = rd.IsDBNull(1) ? "" : rd.GetString(1);
        }
        catch (SqliteException)
        {
            db.Dispose();
            throw new SnapshotFormatError(
                $"'{System.IO.Path.GetFileName(path)}' has no meta table, so it cannot say which game and mods it " +
                "came from. Databases built by other tools are not read. Export again with this version.");
        }

        if (!meta.TryGetValue(SnapshotSchema.MetaKeySchemaVersion, out var vs) ||
            !int.TryParse(vs, out var v) || v != SnapshotSchema.Version)
        {
            db.Dispose();
            throw new SnapshotFormatError(
                $"'{System.IO.Path.GetFileName(path)}' was built with snapshot schema version {vs ?? "unknown"}, " +
                $"and this build reads version {SnapshotSchema.Version}. Re-import the export file " +
                "('rimsearcher snapshot import') to rebuild it.");
        }

        var exportMeta = ExportMeta.Parse(meta[SnapshotSchema.MetaKeyRaw]);

        var mods = new List<ModRef>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT package_id, name, version FROM mods ORDER BY ordinal";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                mods.Add(new ModRef(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1),
                                    rd.IsDBNull(2) ? null : rd.GetString(2)));
        }

        return new SnapshotDb(db, path, exportMeta, mods);
    }

    public void Dispose() => _db.Dispose();

    // ---------- 计数 ----------

    public int DefCount() => Scalar("SELECT COUNT(*) FROM defs");

    public int TruncatedDefCount() => Scalar("SELECT COUNT(*) FROM defs WHERE fields_truncated > 0");

    public IReadOnlyList<(string Type, int Count)> Types(ScopeFilter scope)
    {
        var p = new Dictionary<string, object?>();
        var where = scope.SqlPredicate("source_mod", p);
        var sql = "SELECT def_type, COUNT(*) FROM defs" + (where is null ? "" : $" WHERE {where}") +
                  " GROUP BY def_type ORDER BY COUNT(*) DESC, def_type";
        var result = new List<(string, int)>();
        using var rd = Query(sql, p);
        while (rd.Read()) result.Add((rd.GetString(0), rd.GetInt32(1)));
        return result;
    }

    public IReadOnlyList<string> PackageIds() => Mods.Select(m => m.PackageId).ToList();

    public IReadOnlyList<string> AllDefNames(ScopeFilter scope)
    {
        var p = new Dictionary<string, object?>();
        var where = scope.SqlPredicate("source_mod", p);
        var result = new List<string>();
        using var rd = Query("SELECT def_name FROM defs" + (where is null ? "" : $" WHERE {where}"), p);
        while (rd.Read()) result.Add(rd.GetString(0));
        return result;
    }

    // ---------- 查询 ----------

    public (IReadOnlyList<DefRow> Rows, int Total) SearchFts(string query, ScopeFilter scope, string? defType, int limit)
    {
        var match = FtsText.BuildMatchQuery(query);
        var p = new Dictionary<string, object?> { ["@m"] = match, ["@q"] = query };
        var conds = new List<string> { "defs_fts MATCH @m" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }
        var from = "FROM defs_fts f JOIN defs d ON d.id = f.rowid WHERE " + string.Join(" AND ", conds);

        var total = Scalar($"SELECT COUNT(*) {from}", p);

        // 排序:名字整体命中 > 名字里某一段命中 > bm25 相关度 > 名字短的在前。
        // 光按名字长度排,查 "shield" 会把 ResearchProjectDef 排在 Apparel_ShieldBelt 前面 ——
        // 对调用方来说那是错的答案排在了对的答案前面。列权重让 def_name 压过 description。
        // 「有 label」是一条真信号而不是权宜:带 label 的是玩家看得见的东西,不带的是
        // EffecterDef/SoundDef 一类基础设施。搜 "shield" 的人要的是护盾腰带,不是护盾音效。
        var order = "ORDER BY (d.def_name = @q COLLATE NOCASE) DESC, " +
                    "(d.def_name LIKE @q || '%' COLLATE NOCASE) DESC, " +
                    "(d.label IS NOT NULL AND d.label != '') DESC, " +
                    "bm25(defs_fts, 10.0, 4.0, 1.0, 3.0), LENGTH(d.def_name), d.def_name";
        var rows = ReadDefs($"SELECT {DefColumns} {from} {order} LIMIT {limit}", p);
        return (rows, total);
    }

    public (IReadOnlyList<DefRow> Rows, int Total) ByNames(IReadOnlyList<string> names, int limit)
    {
        if (names.Count == 0) return ([], 0);
        var p = new Dictionary<string, object?>();
        var keys = new List<string>();
        for (var i = 0; i < names.Count; i++) { p["@n" + i] = names[i]; keys.Add("@n" + i); }
        var where = $"WHERE d.def_name IN ({string.Join(",", keys)})";
        var rows = ReadDefs($"SELECT {DefColumns} FROM defs d {where} LIMIT {limit}", p);
        // 保持传入顺序(模糊打分的排序不能被 SQL 打乱)
        var byName = rows.ToDictionary(r => r.DefName, StringComparer.Ordinal);
        var ordered = names.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        return (ordered, names.Count);
    }

    public DefRow? GetDef(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        return ReadDefs($"SELECT {DefColumns} FROM defs d WHERE d.def_name = @n COLLATE NOCASE LIMIT 2", p)
            .FirstOrDefault();
    }

    public IReadOnlyList<DefRow> GetDefsNamed(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        return ReadDefs($"SELECT {DefColumns} FROM defs d WHERE d.def_name = @n COLLATE NOCASE", p);
    }

    public (IReadOnlyList<FieldRow> Rows, int Total) Fields(long defId, int limit)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        var total = Scalar("SELECT COUNT(*) FROM field_values WHERE def_id = @id", p);
        var rows = new List<FieldRow>();
        using var rd = Query($"SELECT path, leaf, value FROM field_values WHERE def_id = @id ORDER BY rowid LIMIT {limit}", p);
        while (rd.Read()) rows.Add(new FieldRow(rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        return (rows, total);
    }

    public (IReadOnlyList<DefRow> Rows, int Total) ListByType(string defType, ScopeFilter scope, int limit, int offset)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var conds = new List<string> { "d.def_type = @t COLLATE NOCASE" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);
        var total = Scalar($"SELECT COUNT(*) FROM defs d {where}", p);
        var rows = ReadDefs($"SELECT {DefColumns} FROM defs d {where} ORDER BY d.def_name LIMIT {limit} OFFSET {offset}", p);
        return (rows, total);
    }

    /// <summary>
    /// 反查:哪些 def 的某字段等于某值。路径按**后缀**匹配(上游 <c>find</c> 的语义),
    /// 因为调用方通常只知道末段(<c>compClass</c>),不知道完整路径(<c>comps[3].compClass</c>)。
    /// </summary>
    public (IReadOnlyList<(DefRow Def, string Path, string? Value)> Rows, int Total)
        FindByField(string pathSuffix, string? value, bool exact, ScopeFilter scope, int limit)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        var leaf = NoiseFilter.Leaf(pathSuffix);
        if (pathSuffix.Contains('.') || pathSuffix.Contains('['))
        {
            p["@path"] = "%" + pathSuffix;
            conds.Add("fv.path LIKE @path");
        }
        else
        {
            p["@leaf"] = leaf;
            conds.Add("fv.leaf = @leaf COLLATE NOCASE");
        }

        if (value is { Length: > 0 })
        {
            if (exact) { p["@v"] = value; conds.Add("fv.value = @v COLLATE NOCASE"); }
            else { p["@v"] = "%" + value + "%"; conds.Add("fv.value LIKE @v"); }
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);

        var where = "WHERE " + string.Join(" AND ", conds);
        var total = Scalar($"SELECT COUNT(*) FROM field_values fv JOIN defs d ON d.id = fv.def_id {where}", p);

        var rows = new List<(DefRow, string, string?)>();
        using var rd = Query(
            $"SELECT {DefColumns}, fv.path, fv.value FROM field_values fv JOIN defs d ON d.id = fv.def_id {where} " +
            $"ORDER BY d.def_name LIMIT {limit}", p);
        while (rd.Read())
            rows.Add((ReadDefRow(rd), rd.GetString(11), rd.IsDBNull(12) ? null : rd.GetString(12)));
        return (rows, total);
    }

    public (IReadOnlyList<(string Path, int Count)> Rows, int Total) FieldPathsForType(string defType, int limit)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var total = Scalar(
            "SELECT COUNT(*) FROM (SELECT DISTINCT fv.path FROM field_values fv JOIN defs d ON d.id = fv.def_id " +
            "WHERE d.def_type = @t COLLATE NOCASE)", p);
        var rows = new List<(string, int)>();
        using var rd = Query(
            "SELECT fv.path, COUNT(*) c FROM field_values fv JOIN defs d ON d.id = fv.def_id " +
            $"WHERE d.def_type = @t COLLATE NOCASE GROUP BY fv.path ORDER BY c DESC, fv.path LIMIT {limit}", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
        return (rows, total);
    }

    public (IReadOnlyList<(string Value, int Count)> Rows, int Total) DistinctValues(string pathSuffix, ScopeFilter scope, int limit)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();
        if (pathSuffix.Contains('.') || pathSuffix.Contains('['))
        {
            p["@path"] = "%" + pathSuffix;
            conds.Add("fv.path LIKE @path");
        }
        else
        {
            p["@leaf"] = NoiseFilter.Leaf(pathSuffix);
            conds.Add("fv.leaf = @leaf COLLATE NOCASE");
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT fv.value FROM field_values fv JOIN defs d ON d.id = fv.def_id {where})", p);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT fv.value, COUNT(*) c FROM field_values fv JOIN defs d ON d.id = fv.def_id {where} " +
            $"GROUP BY fv.value ORDER BY c DESC, fv.value LIMIT {limit}", p);
        while (rd.Read()) rows.Add((rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetInt32(1)));
        return (rows, total);
    }

    public IReadOnlyList<TranslationRow> Translations(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        var rows = new List<TranslationRow>();
        using var rd = Query(
            "SELECT def_name, path, translated, original, language, source_mod, origin FROM translations " +
            "WHERE def_name = @n COLLATE NOCASE ORDER BY origin, path", p);
        while (rd.Read())
            rows.Add(new TranslationRow(rd.GetString(0), rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.GetString(6)));
        return rows;
    }

    public int CountTranslationsOutside(IEnumerable<string> defNames)
    {
        var names = defNames.ToList();
        if (names.Count == 0) return 0;
        var p = new Dictionary<string, object?> { ["@o"] = TranslationOrigin.HarvestedOutside };
        var keys = new List<string>();
        for (var i = 0; i < names.Count; i++) { p["@n" + i] = names[i]; keys.Add("@n" + i); }
        return Scalar($"SELECT COUNT(DISTINCT def_name) FROM translations WHERE origin = @o AND def_name IN ({string.Join(",", keys)})", p);
    }

    // ---------- 底层 ----------

    private const string DefColumns =
        "d.id, d.def_type, d.def_name, d.label, d.description, d.source_mod, d.source_file, d.generated, d.class, d.parent, d.fields_truncated";

    private static DefRow ReadDefRow(SqliteDataReader rd) => new(
        rd.GetInt64(0), rd.GetString(1), rd.GetString(2),
        rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
        rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
        rd.GetInt32(7) != 0, rd.IsDBNull(8) ? null : rd.GetString(8),
        rd.IsDBNull(9) ? null : rd.GetString(9), rd.GetInt32(10));

    private List<DefRow> ReadDefs(string sql, IDictionary<string, object?> p)
    {
        var rows = new List<DefRow>();
        using var rd = Query(sql, p);
        while (rd.Read()) rows.Add(ReadDefRow(rd));
        return rows;
    }

    private SqliteDataReader Query(string sql, IDictionary<string, object?>? p = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        if (p is not null)
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteReader();
    }

    private int Scalar(string sql, IDictionary<string, object?>? p = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        if (p is not null)
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
