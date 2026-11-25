using Microsoft.Data.Sqlite;
using RimSearcher.Contract;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

public enum TruncationCause { Cap, Length, Depth, Items, Error }

public sealed record TruncationCauses(int Cap, int Length, int Depth, int Items, int Error)
{
    public int Total => Cap + Length + Depth + Items + Error;

    public int Missing => Cap + Depth + Items + Error;

    public IEnumerable<(TruncationCause Cause, int Count)> Ranked()
        => new[]
           {
               (TruncationCause.Cap, Cap),
               (TruncationCause.Length, Length),
               (TruncationCause.Depth, Depth),
               (TruncationCause.Items, Items),
               (TruncationCause.Error, Error),
           }
           .Where(x => x.Item2 > 0)
           .OrderByDescending(x => x.Item2);
}

public sealed record DefRow(long Id, string DefType, string DefName, string? Label, string? Description,
                            string? SourceMod, string? SourceFile, bool Generated, string? Class,
                            int FieldsTruncated);

public sealed record FieldRow(string Path, string Leaf, string? Value, int Default);

public readonly record struct XmlOccurrence(string Text, string Mark);

public sealed record TruncationScope(int Count, IReadOnlyList<(string Type, int Defs)> ByType)
{
    public IReadOnlyList<string> Types => [.. ByType.Select(t => t.Type)];
}

public enum ValueMatch
{
    Substring,
    Exact,
    Identifier,
}

public readonly record struct PathQuery(string Text, bool Exact = false, bool IndexTolerant = false,
                                        IReadOnlyList<string>? Contains = null)
{
    public static implicit operator PathQuery(string text) => new(text);

    public bool CanTolerateIndex =>
        !Exact && !IndexTolerant &&
        !Text.Contains('[', StringComparison.Ordinal) &&
        Text.Count(c => c == '.') is > 0 and <= 4;

    public Search.EntrySelector? Selector => Search.EntrySelector.Parse(Text, out _);
}

public sealed record TranslationRow(string DefName, string? DefType, string Path, string? Translated,
                                   string? Original, string? Language, string? SourceMod, string Origin,
                                   string? SourceFile = null, int? SourceFileCount = null,
                                   string? Key = null, string? KeyState = null, bool? Applied = null);

public sealed record KeyedRow(string Key, string? Translated, string? Original, string? Language,
                              string? SourceFile, int SourceLine, string? SourceMod,
                              bool Placeholder, string Origin, int? SourceFileCount = null);

public sealed record XmlNodeRow(string DefType, string? Name, string? ParentName, bool Abstract,
                                string? DefName, string? Label, string? SourceMod, string? SourceFile,
                                int PatchOps,
                                int PatchOpsDefName = 0,
                                int PatchOpsLabel = 0);

public sealed record EconomyRow(long Id, string DefName, string? Label, string? Category, string? Mod,
                                double? MarketValue, bool Producible, bool MadeFromStuff,
                                bool IsWeapon, bool IsApparel, bool MarketValueDefined,
                                string CalcState, double? CalculatedMarketValue,
                                double? CostToMake, double? Profit, double? ProfitRate,
                                double? WorkToProduce, string? CostList,
                                string? CostDifficultyVar, bool CostDifficultyInverted,
                                double? ChainEndShare, double? CostDeep, double? ProfitDeep);

public sealed record ShaderSourceRow(string Stage, string File, string? ResolvedPath, string? Unresolved);

public sealed record EconomyChainRow(string ThingDef, int Count, double? UnitValue, bool ChainEnd);

public sealed record EconomyRecipeRow(string DefName, int ProductCount, double? WorkAmount,
                                      bool SelfReferential);

public sealed class SnapshotDb : IDisposable
{
    private readonly SqliteConnection _db;

    public string Path { get; }
    public ExportMeta Meta { get; }
    public IReadOnlyList<ModRef> Mods { get; }

    public bool Harvested { get; }

    public string? TranslationsHarvest { get; }

    public string? SourceFile { get; }

    public ContentScan? Content { get; }

    public string? EconomyState { get; }

    public string? EconomyError { get; }

    public IReadOnlyDictionary<string, long>? ExportTimings { get; }

    public IReadOnlyDictionary<string, long>? ImportTimings { get; }

    private SnapshotDb(SqliteConnection db, string path, ExportMeta meta, IReadOnlyList<ModRef> mods,
                       bool harvested, string? translationsHarvest, string? sourceFile,
                       ContentScan? content, string? economyState, string? economyError,
                       IReadOnlyDictionary<string, long>? exportTimings,
                       IReadOnlyDictionary<string, long>? importTimings)
    {
        _db = db; Path = path; Meta = meta; Mods = mods; Harvested = harvested;
        TranslationsHarvest = translationsHarvest; SourceFile = sourceFile; Content = content;
        EconomyState = economyState; EconomyError = economyError; ExportTimings = exportTimings;
        ImportTimings = importTimings;
    }

    public static string NoDatabaseAt(string path)
        => $"'{path}' 处没有快照库。已登记的快照见 'rimsearcher snapshot list'；'rimsearcher export --modlist <名称>' 可从游戏生成一份。";

    private static void Tune(SqliteConnection db)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                PRAGMA mmap_size  = 1073741824;
                PRAGMA cache_size = -65536;
                PRAGMA temp_store = MEMORY;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    public static SnapshotDb Open(string path)
    {
        if (!File.Exists(path)) throw new SnapshotFormatError(NoDatabaseAt(path));

        var file = System.IO.Path.GetFileName(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            db.Open();
            Tune(db);

            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM meta";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) meta[rd.GetString(0)] = rd.IsDBNull(1) ? "" : rd.GetString(1);
            }
            catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                throw new SnapshotFormatError(
                    $"'{file}' 没有 meta 表，无法作为快照读取。须用当前版本重新导出。");
            }
            catch (SqliteException)
            {
                throw new SnapshotFormatError($"'{file}' 不是有效的快照数据库，或文件已损坏。");
            }

            string? missing;
            try
            {
                missing = SnapshotSchema.FirstMissing(db);
            }
            catch (SqliteException)
            {
                throw new SnapshotFormatError($"'{file}' 不是有效的快照数据库，或文件已损坏。");
            }
            if (missing is not null)
                throw new SnapshotFormatError(
                    $"'{file}' 缺少本构建要读的{missing}：表结构出自较早的构建。" +
                    "重新导入这份导出文件（'rimsearcher snapshot import'）可重建。");

            if (!meta.TryGetValue(SnapshotSchema.MetaKeyRaw, out var raw) || string.IsNullOrWhiteSpace(raw))
                throw new SnapshotFormatError($"'{file}' 缺少必需的快照元数据，文件已损坏或非本工具生成。");

            ExportMeta exportMeta;
            try
            {
                exportMeta = ExportMeta.Parse(raw);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                throw new SnapshotFormatError($"'{file}' 的快照元数据损坏，无法解析。");
            }

            var mods = new List<ModRef>();
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT package_id, name, version FROM mods ORDER BY ordinal";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    mods.Add(new ModRef(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1),
                                        rd.IsDBNull(2) ? null : rd.GetString(2)));
            }
            catch (SqliteException)
            {
                throw new SnapshotFormatError($"'{file}' 不是有效的快照数据库，或文件已损坏。");
            }

            var harvested = meta.TryGetValue(SnapshotSchema.MetaKeyHarvestedRoots, out var hr) &&
                            int.TryParse(hr, out var roots) && roots > 0;
            meta.TryGetValue(SnapshotSchema.MetaKeyTranslationsHarvest, out var translationsHarvest);
            meta.TryGetValue(SnapshotSchema.MetaKeySourcePath, out var sourceFile);

            var content = meta.TryGetValue(SnapshotSchema.MetaKeyContent, out var cf)
                ? ContentScan.FromJson(cf)
                : null;

            meta.TryGetValue(SnapshotSchema.MetaKeyEconomyState, out var econState);
            meta.TryGetValue(SnapshotSchema.MetaKeyEconomyError, out var econError);

            IReadOnlyDictionary<string, long>? ReadTimings(string key)
            {
                if (!meta.TryGetValue(key, out var tj)) return null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(tj);
                    var d = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Value.TryGetInt64(out var ms)) d[prop.Name] = ms;
                    return d;
                }
                catch (System.Text.Json.JsonException) { return null; }
            }

            return new SnapshotDb(db, path, exportMeta, mods, harvested, translationsHarvest, sourceFile, content, econState, econError,
                                  ReadTimings(SnapshotSchema.MetaKeyExportTimings),
                                  ReadTimings(SnapshotSchema.MetaKeyImportTimings));
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose() => _db.Dispose();

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

    public IReadOnlyList<string> AllDefNames(ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();
        if (scope.SqlPredicate("source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("def_type = @dt COLLATE NOCASE"); }
        var result = new List<string>();
        using var rd = Query("SELECT def_name FROM defs" +
                             (conds.Count == 0 ? "" : $" WHERE {string.Join(" AND ", conds)}"), p);
        while (rd.Read()) result.Add(rd.GetString(0));
        return result;
    }

    public (IReadOnlyList<DefRow> Rows, int Total) SearchFts(string query, ScopeFilter scope, string? defType, int limit, int offset = 0)
    {
        var match = FtsText.BuildMatchQuery(query);
        var p = new Dictionary<string, object?> { ["@m"] = match, ["@q"] = query };
        var conds = new List<string> { "defs_fts MATCH @m" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }
        var from = "FROM defs_fts f JOIN defs d ON d.id = f.rowid WHERE " + string.Join(" AND ", conds);

        var total = Scalar($"SELECT COUNT(*) {from}", p);

        p["@qe"] = Escape(query);

        var order = "ORDER BY (d.def_name = @q COLLATE NOCASE) DESC, " +
                    "(d.label IS NOT NULL AND d.label != '') DESC, " +
                    "(d.def_name LIKE @qe || '%' ESCAPE '\\' COLLATE NOCASE) DESC, " +
                    "bm25(defs_fts, 10.0, 4.0, 1.0, 3.0), LENGTH(d.def_name), d.def_name";
        var rows = ReadDefs($"SELECT {DefColumns} {from} {order} LIMIT {limit} OFFSET {offset}", p);
        return (rows, total);
    }

    public (IReadOnlyList<DefRow> Rows, int Total) ContainingUnmatched(string query, ScopeFilter scope, string? defType,
                                                                       int limit, int offset = 0)
    {
        var p = new Dictionary<string, object?> { ["@m"] = FtsText.BuildMatchQuery(query), ["@q"] = "%" + Escape(query) + "%" };
        var conds = new List<string> { "d.def_name LIKE @q ESCAPE '\\'", "d.id NOT IN (SELECT rowid FROM defs_fts WHERE defs_fts MATCH @m)" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var from = $"FROM defs d WHERE {string.Join(" AND ", conds)}";
        return (ReadDefs($"SELECT {DefColumns} {from} ORDER BY LENGTH(d.def_name), d.def_name, d.def_type " +
                         $"LIMIT {limit} OFFSET {offset}", p),
                Scalar($"SELECT COUNT(*) {from}", p));
    }

    public (IReadOnlyList<DefRow> Rows, int Total) ByTranslationOriginal(string query, ScopeFilter scope, string? defType, int limit, int offset = 0)
    {
        var p = new Dictionary<string, object?> { ["@q"] = "%" + Escape(query) + "%" };
        var conds = new List<string> { "d.def_name IN (SELECT t.def_name FROM translations t WHERE t.original LIKE @q ESCAPE '\\')" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var from = $"FROM defs d WHERE {string.Join(" AND ", conds)}";
        return (ReadDefs($"SELECT {DefColumns} {from} ORDER BY LENGTH(d.def_name), d.def_name, d.def_type LIMIT {limit} OFFSET {offset}", p),
                Scalar($"SELECT COUNT(*) {from}", p));
    }

    public (IReadOnlyList<DefRow> Rows, int Total) ByNames(IReadOnlyList<string> names, ScopeFilter scope, string? defType,
                                                           int limit, int offset = 0)
    {
        if (names.Count == 0) return ([], 0);
        var p = new Dictionary<string, object?>();
        var keys = new List<string>();
        for (var i = 0; i < names.Count; i++) { p["@n" + i] = names[i]; keys.Add("@n" + i); }
        var conds = new List<string> { $"d.def_name IN ({string.Join(",", keys)})" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }
        var rows = ReadDefs($"SELECT {DefColumns} FROM defs d WHERE {string.Join(" AND ", conds)} ORDER BY d.id", p);

        var byName = rows.GroupBy(r => r.DefName, StringComparer.Ordinal)
                         .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var ordered = new List<DefRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in names)
            if (seen.Add(n) && byName.TryGetValue(n, out var group)) ordered.AddRange(group);

        var total = ordered.Count;
        return ([.. ordered.Skip(offset).Take(limit)], total);
    }

    public DefRow? GetDef(string defName) => GetDefsNamed(defName).FirstOrDefault();

    public IReadOnlyList<DefRow> GetDefsNamed(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        return ExactFirst(ReadDefs($"SELECT {DefColumns} FROM defs d WHERE d.def_name = @n COLLATE NOCASE ORDER BY d.id", p),
                          d => d.DefName == defName);
    }

    public static List<T> ExactFirst<T>(IEnumerable<T> rows, Func<T, bool> exact)
    {
        var all = rows.ToList();
        var hit = all.Where(exact).ToList();
        return hit.Count > 0 ? hit : all;
    }

    public IReadOnlyList<DefRow> DefsOfType(string defType)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        return ReadDefs($"SELECT {DefColumns} FROM defs d WHERE d.def_type = @t COLLATE NOCASE " +
                        "ORDER BY d.def_name, d.id", p);
    }

    public (IReadOnlyList<FieldRow> Rows, int Matched, int Total, int Defaulted,
            IReadOnlyList<string> MatchedPaths) Fields(
        long defId, int limit, IReadOnlyList<string>? pathFilters = null, bool includeDefaults = true,
        bool exactPath = false)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        var total = Scalar($"SELECT COUNT(*) FROM field_values fv {FvJoin} WHERE def_id = @id AND {FvPath} <> 'defName'", p);

        var filters = (pathFilters ?? []).Where(f => !string.IsNullOrEmpty(f)).ToList();
        var where = filters.Count == 0
            ? $"WHERE def_id = @id AND {FvPath} <> 'defName'"
            : "WHERE def_id = @id";
        if (filters.Count > 0)
        {
            var ors = new List<string>();
            for (var i = 0; i < filters.Count; i++)
            {
                p["@f" + i] = PathFilterLike(filters[i], exactPath);
                ors.Add($"{FvPath} LIKE @f{i} ESCAPE '\\'");
            }
            where += " AND (" + string.Join(" OR ", ors) + ")";
        }

        var matched = filters.Count == 0
            ? total
            : Scalar($"SELECT COUNT(*) FROM field_values fv {FvJoin} {where}", p);
        var defaulted = Scalar(
            $"SELECT COUNT(*) FROM field_values fv {FvJoin} {where} AND is_default = {Contract.DefaultState.Same}", p);

        var allPaths = new List<string>();
        using (var pr = Query($"SELECT {FvPath} FROM field_values fv {FvJoin} {where} ORDER BY fv.rowid", p))
            while (pr.Read()) allPaths.Add(pr.GetString(0));

        var listed = includeDefaults ? where : $"{where} AND is_default <> {Contract.DefaultState.Same}";
        var rows = new List<FieldRow>();
        using var rd = Query(
            $"SELECT {FvPath}, {FvLeaf}, {FvValue}, fv.is_default FROM field_values fv {FvJoin} {listed} " +
            $"ORDER BY fv.rowid LIMIT {limit}", p);
        while (rd.Read())
            rows.Add(new FieldRow(rd.GetString(0), rd.GetString(1),
                                  rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetInt32(3)));
        return (rows, matched, total, defaulted, allPaths);
    }

    public (Dictionary<long, List<string?>> Values, Dictionary<long, int> Containers) Project(
        IReadOnlyCollection<long> defIds, string column)
    {
        var values = new Dictionary<long, List<string?>>();
        var containers = new Dictionary<long, int>();
        if (defIds.Count == 0) return (values, containers);

        var ids = string.Join(",", defIds);
        var p = new Dictionary<string, object?>();
        var conds = new List<string> { $"fv.def_id IN ({ids})" };
        var query = new PathQuery(column, Exact: true);
        PathCondition(query, p, conds);
        using (var rd = Query($"SELECT fv.def_id, {FvValue} FROM field_values fv {FvJoin} " +
                              $"WHERE {string.Join(" AND ", conds)} ORDER BY fv.rowid", p))
            while (rd.Read())
            {
                var id = rd.GetInt64(0);
                if (!values.TryGetValue(id, out var list)) values[id] = list = [];
                list.Add(rd.IsDBNull(1) ? null : rd.GetString(1));
            }

        if (query.Selector is not null) return (values, containers);
        var cp = new Dictionary<string, object?>
        {
            ["@dot"] = PathLike(column) + ".%",
            ["@idx"] = PathLike(column) + "[%",
        };
        using (var rd = Query($"SELECT fv.def_id, COUNT(*) FROM field_values fv WHERE fv.def_id IN ({ids}) AND " +
                              PathIs("path LIKE @dot ESCAPE '\\' COLLATE NOCASE OR path LIKE @idx ESCAPE '\\' COLLATE NOCASE") +
                              " GROUP BY fv.def_id", cp))
            while (rd.Read())
                if (!values.ContainsKey(rd.GetInt64(0))) containers[rd.GetInt64(0)] = rd.GetInt32(1);
        return (values, containers);
    }

    public int ValueHits(long defId, string text)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        return Scalar("SELECT COUNT(*) FROM field_values fv WHERE fv.def_id = @id AND " + ValueContains(text, p), p);
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string PathLike(string s)
        => Escape(s).Replace("[]", "[%]", StringComparison.Ordinal);

    private static string PathFilterLike(string text, bool exact)
        => exact ? PathLike(text) : "%" + PathLike(text) + "%";

    public static bool PathFilterMatches(string path, IReadOnlyList<string> filters, bool exact)
    {
        foreach (var f in filters)
        {
            if (string.IsNullOrEmpty(f)) continue;
            var pattern = System.Text.RegularExpressions.Regex.Escape(f)
                .Replace(@"\[]", @"\[.*?]", StringComparison.Ordinal);
            if (exact) pattern = "^" + pattern + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(path, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                    | System.Text.RegularExpressions.RegexOptions.Singleline))
                return true;
        }
        return false;
    }

    private string PathContainsClause(IReadOnlyList<string> filters, Dictionary<string, object?> p)
    {
        var ors = new List<string>();
        for (var i = 0; i < filters.Count; i++)
        {
            p[$"@pc{i}"] = PathFilterLike(filters[i], exact: false);
            ors.Add($"path LIKE @pc{i} ESCAPE '\\'");
        }
        return PathIs(string.Join(" OR ", ors));
    }

    public (IReadOnlyList<DefRow> Rows, int Total) ListByType(
        string defType, ScopeFilter scope, int limit, int offset, string? className = null, string? nameLike = null)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var conds = new List<string> { "d.def_type = @t COLLATE NOCASE" };
        if (className is { Length: > 0 })
        {
            p["@c"] = className;
            p["@e"] = Escape(className);
            conds.Add("(d.class = @c COLLATE NOCASE OR d.class LIKE '%.' || @e ESCAPE '\\' COLLATE NOCASE)");
        }
        if (nameLike is { Length: > 0 })
        {
            p["@n"] = "%" + Escape(nameLike) + "%";
            conds.Add("(d.def_name LIKE @n ESCAPE '\\' COLLATE NOCASE OR d.label LIKE @n ESCAPE '\\' COLLATE NOCASE)");
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);
        var total = Scalar($"SELECT COUNT(*) FROM defs d {where}", p);
        var rows = ReadDefs($"SELECT {DefColumns} FROM defs d {where} ORDER BY d.def_name LIMIT {limit} OFFSET {offset}", p);
        return (rows, total);
    }

    public IReadOnlyList<(string Class, int Count)> ClassesInType(string defType, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var conds = new List<string> { "d.def_type = @t COLLATE NOCASE", "d.class IS NOT NULL" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT d.class, COUNT(*) c FROM defs d WHERE {string.Join(" AND ", conds)} " +
            "GROUP BY d.class ORDER BY c DESC, d.class", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
        return rows;
    }

    public IReadOnlyList<(string DefType, int Count)> TypesHoldingClass(string className, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?> { ["@c"] = className, ["@e"] = Escape(className) };
        var conds = new List<string> { "(d.class = @c COLLATE NOCASE OR d.class LIKE '%.' || @e ESCAPE '\\' COLLATE NOCASE)" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT d.def_type, COUNT(*) c FROM defs d WHERE {string.Join(" AND ", conds)} " +
            "GROUP BY d.def_type ORDER BY c DESC, d.def_type", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
        return rows;
    }

    public (IReadOnlyList<(string DefName, string DefType, int Dropped, TruncationCauses Causes)> Rows, int Total)
        TruncatedDefs(ScopeFilter scope, int limit,
                      IReadOnlyList<string>? defTypes = null, string? defName = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string> { "d.fields_truncated > 0" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defTypes is { Count: > 0 })
        {
            var keys = new List<string>();
            for (var i = 0; i < defTypes.Count; i++) { p["@dt" + i] = defTypes[i]; keys.Add("@dt" + i); }
            conds.Add($"d.def_type IN ({string.Join(",", keys)}) COLLATE NOCASE");
        }
        if (defName is { Length: > 0 })
        {
            p["@dn"] = defName;
            conds.Add("(d.def_name = @dn OR (d.def_name = @dn COLLATE NOCASE " +
                      "AND NOT EXISTS (SELECT 1 FROM defs e WHERE e.def_name = @dn)))");
        }
        var where = "WHERE " + string.Join(" AND ", conds);
        var total = Scalar($"SELECT COUNT(*) FROM defs d {where}", p);
        var rows = new List<(string, string, int, TruncationCauses)>();
        using var rd = Query(
            "SELECT d.def_name, d.def_type, d.fields_truncated, d.truncated_by_cap, d.truncated_by_length, " +
            $"d.truncated_by_depth, d.truncated_by_items, d.truncated_by_error FROM defs d {where} " +
            $"ORDER BY d.fields_truncated DESC, d.def_name LIMIT {limit}", p);
        while (rd.Read())
            rows.Add((rd.GetString(0), rd.GetString(1), rd.GetInt32(2),
                      new TruncationCauses(rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5), rd.GetInt32(6), rd.GetInt32(7))));
        return (rows, total);
    }

    public (IReadOnlyList<(DefRow Def, string Path, string? Value, int Default)> Rows, int Total, int Defs)
        FindByField(PathQuery path, string? value, bool exact, ScopeFilter scope, int limit, int offset = 0,
                    string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            conds.Add(exact ? ValueEquals(value, p) : ValueContains(value, p));
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var where = "WHERE " + string.Join(" AND ", conds);
        var from = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where}";
        int total = 0, defs = 0;
        using (var cnt = Query($"SELECT COUNT(*), COUNT(DISTINCT d.id) {from}", p))
            if (cnt.Read()) { total = cnt.GetInt32(0); defs = cnt.GetInt32(1); }

        var rows = new List<(DefRow, string, string?, int)>();
        using var rd = Query(
            $"SELECT {DefColumns}, {FvPath}, {FvValue}, fv.is_default {from} " +
            $"ORDER BY d.def_name, {FvPath}, fv.rowid LIMIT {limit} OFFSET {offset}", p);
        while (rd.Read())
            rows.Add((ReadDefRow(rd), rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetString(11), rd.GetInt32(12)));
        return (rows, total, defs);
    }

    public IReadOnlyList<(string Shape, int Count)> FindPathShapes(
        PathQuery path, string? value, bool exact, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            conds.Add(exact ? ValueEquals(value, p) : ValueContains(value, p));
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        using var rd = Query(
            $"SELECT {FvPath}, COUNT(*) FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
            $"WHERE {string.Join(" AND ", conds)} GROUP BY {FvPath}", p);
        while (rd.Read())
        {
            var shape = Search.PathSegments.Shape(rd.GetString(0));
            if (!shapes.ContainsKey(shape)) order.Add(shape);
            shapes[shape] = shapes.GetValueOrDefault(shape) + rd.GetInt32(1);
        }
        return [.. order.Select(k => (k, shapes[k])).OrderByDescending(t => t.Item2)];
    }

    public IReadOnlyList<(string Mod, int Defs)> FindModsHolding(
        PathQuery path, string? value, bool exact, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            conds.Add(exact ? ValueEquals(value, p) : ValueContains(value, p));
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT d.source_mod, COUNT(DISTINCT d.id) FROM field_values fv {FvJoin} " +
            $"JOIN defs d ON d.id = fv.def_id WHERE {string.Join(" AND ", conds)} " +
            "GROUP BY d.source_mod ORDER BY COUNT(DISTINCT d.id) DESC", p);
        while (rd.Read())
            rows.Add((rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetInt32(1)));
        return rows;
    }

    public int FindGeneratedDefs(
        PathQuery path, string? value, bool exact, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string> { "d.generated = 1" };

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            conds.Add(exact ? ValueEquals(value, p) : ValueContains(value, p));
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        return Scalar($"SELECT COUNT(DISTINCT d.id) FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
                      $"WHERE {string.Join(" AND ", conds)}", p);
    }

    public (int Defs, int Paths) TypeDefsWithPath(string defType, IReadOnlyList<string> pathFilters,
                                                  bool exactPath = false)
    {
        var filters = pathFilters.Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (filters.Count == 0) return (0, 0);

        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var any = new List<string>();
        for (var i = 0; i < filters.Count; i++)
        {
            p[$"@f{i}"] = PathFilterLike(filters[i], exactPath);
            any.Add(PathIs($"path LIKE @f{i} ESCAPE '\\'"));
        }
        var where = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
                    $"WHERE d.def_type = @t COLLATE NOCASE AND ({string.Join(" OR ", any)})";

        return (Scalar($"SELECT COUNT(DISTINCT fv.def_id) {where}", p),
                Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath} {where})", p));
    }

    public (IReadOnlyList<(string Path, int Count)> Rows, int Total, int WholeSegment) FieldPathsForType(
        string defType, int limit, IReadOnlyList<string>? pathFilters = null, int offset = 0,
        bool exactPath = false)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var where = "WHERE d.def_type = @t COLLATE NOCASE";
        var whole = "";
        var filters = (pathFilters ?? []).Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (filters.Count > 0)
        {
            var any = new List<string>();
            var anyWhole = new List<string>();
            for (var i = 0; i < filters.Count; i++)
            {
                var e = PathLike(filters[i]);
                p[$"@f{i}"] = PathFilterLike(filters[i], exactPath);
                any.Add(PathIs($"path LIKE @f{i} ESCAPE '\\'"));

                p[$"@s{i}_0"] = e;         p[$"@s{i}_1"] = e + ".%";        p[$"@s{i}_2"] = e + "[%";
                p[$"@s{i}_3"] = "%." + e;  p[$"@s{i}_4"] = "%." + e + ".%"; p[$"@s{i}_5"] = "%." + e + "[%";
                for (var k = 0; k < 6; k++) anyWhole.Add(PathIs($"path LIKE @s{i}_{k} ESCAPE '\\'"));
            }
            where += $" AND ({string.Join(" OR ", any)})";
            whole = $" AND ({string.Join(" OR ", anyWhole)})";
        }
        var total = Scalar(
            $"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath} FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where})", p);
        var wholeCount = whole.Length == 0 ? total : Scalar(
            $"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath} FROM field_values fv {FvJoin} " +
            $"JOIN defs d ON d.id = fv.def_id {where}{whole})", p);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT {FvPath}, COUNT(*) c FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where} " +
            $"GROUP BY {FvPath} ORDER BY c DESC, {FvPath} LIMIT {limit} OFFSET {offset}", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
        return (rows, total, wholeCount);
    }

    private string ValueWhere(string value, ValueMatch match, ScopeFilter scope,
                              Dictionary<string, object?> p, string? defType = null)
    {
        var conds = new List<string>();
        switch (match)
        {
            case ValueMatch.Exact:
                conds.Add(ValueEquals(value, p));
                break;
            case ValueMatch.Identifier:
                p["@v"] = value;
                p["@vq"] = "%." + Escape(value);
                conds.Add(ValueIs("value = @v COLLATE NOCASE OR value LIKE @vq ESCAPE '\\'"));
                break;
            default:
                conds.Add(ValueContains(value, p));
                break;
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }
        return "WHERE " + string.Join(" AND ", conds);
    }

    private string SuffixWhere(PathQuery path, ScopeFilter scope, Dictionary<string, object?> p)
    {
        var conds = new List<string>();
        PathCondition(path, p, conds);
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        return "WHERE " + string.Join(" AND ", conds);
    }

    private void PathCondition(PathQuery path, Dictionary<string, object?> p, List<string> conds)
    {
        if (path.Contains is { Count: > 0 } has)
            conds.Add(PathContainsClause(has, p));

        if (path.Selector is { } sel)
        {
            conds.Add(EntryKeyClause(sel, p));
            path = path with { Text = sel.BaseText };
        }

        if (path.Exact)
        {
            if (path.Text.Contains("[]", StringComparison.Ordinal))
            {
                p["@path"] = PathLike(path.Text);
                conds.Add(PathIs($"path LIKE @path ESCAPE '\\' COLLATE NOCASE"));
            }
            else
            {
                p["@path"] = path.Text;
                conds.Add(PathIs("path = @path COLLATE NOCASE"));
            }
        }
        else if (path.IndexTolerant)
        {
            var any = new List<string>();
            var dots = path.Text.Count(c => c == '.');
            for (var mask = 0; mask < 1 << (dots + 1); mask++)
            {
                var sb = new System.Text.StringBuilder("%");
                var at = 0;
                foreach (var ch in path.Text)
                {
                    if (ch == '.')
                    {
                        if ((mask & (1 << at)) != 0) sb.Append("[%]");
                        at++;
                    }
                    sb.Append(Escape(ch.ToString()));
                }
                if ((mask & (1 << dots)) != 0) sb.Append("[%]");
                p[$"@ip{mask}"] = sb.ToString();
                any.Add($"path LIKE @ip{mask} ESCAPE '\\'");
            }
            conds.Add(PathIs(string.Join(" OR ", any)));
        }
        else if (path.Text.Contains('.') || path.Text.Contains('['))
        {
            p["@path"] = PathLike(path.Text);
            p["@pathTail"] = "%." + PathLike(path.Text);
            conds.Add(PathIs($"path LIKE @path ESCAPE '\\' OR path LIKE @pathTail ESCAPE '\\'"));
        }
        else
        {
            p["@leaf"] = NoiseFilter.Leaf(path.Text);
            conds.Add(LeafIs("leaf = @leaf COLLATE NOCASE"));
        }
    }

    private string EntryKeyClause(Search.EntrySelector sel, Dictionary<string, object?> p)
    {
        var pairs = _entryPairs.TryGetValue((sel.After, sel.Key), out var cached)
            ? cached
            : _entryPairs[(sel.After, sel.Key)] = EntryPairs(sel.After, sel.Key);
        if (pairs.Count == 0) return "0";

        p["@ekv"] = sel.Value;
        p["@ekvq"] = "%." + Escape(sel.Value);
        var keyIds = string.Join(",", pairs.Select(x => x.Key).Distinct());
        var toValue = "CASE k.path_id " + string.Join(" ", pairs.Select(x => $"WHEN {x.Key} THEN {x.Value}")) + " END";
        return $"(fv.def_id, fv.path_id) IN (SELECT k.def_id, {toValue} FROM field_values k " +
               $"WHERE k.path_id IN ({keyIds}) AND k.value_id IN (SELECT id FROM field_value_values " +
               "WHERE value = @ekv COLLATE NOCASE OR value LIKE @ekvq ESCAPE '\\'))";
    }

    private readonly Dictionary<(string After, string Key), List<KeyValuePair<long, long>>> _entryPairs = [];

    private List<KeyValuePair<long, long>> EntryPairs(string after, string key)
    {
        var keys = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var rd = Query("SELECT id, path FROM field_value_paths WHERE path LIKE @k ESCAPE '\\'",
                              new Dictionary<string, object?> { ["@k"] = "%]." + Escape(key) }))
            while (rd.Read()) keys.TryAdd(rd.GetString(1), rd.GetInt64(0));

        var depth = after.Count(c => c == '[');
        var pairs = new List<KeyValuePair<long, long>>();
        using var rows = Query("SELECT id, path FROM field_value_paths WHERE path LIKE @c ESCAPE '\\'",
                               new Dictionary<string, object?> { ["@c"] = "%[%]" + PathLike(after) });
        while (rows.Read())
        {
            var path = rows.GetString(1);
            var end = path.Length;
            for (var i = 0; i <= depth && end > 0; i++) end = path.LastIndexOf(']', end - 1);
            if (end < 0) continue;
            if (keys.TryGetValue(path[..(end + 1)] + "." + key, out var keyId))
                pairs.Add(new(keyId, rows.GetInt64(0)));
        }
        return pairs;
    }

    public IReadOnlyList<(string KeyShape, int Defs)> EntriesKeyedBy(string value, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?> { ["@v"] = value, ["@ent"] = "%]._%" };
        var conds = new List<string>
        {
            ValueIs("value = @v COLLATE NOCASE"),
            PathIs("path LIKE @ent ESCAPE '\\'"),
        };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);

        var defs = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        using var rd = Query(
            $"SELECT DISTINCT {FvPath}, fv.def_id FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
            $"WHERE {string.Join(" AND ", conds)}", p);
        while (rd.Read())
        {
            var path = rd.GetString(0);
            if (path.LastIndexOf('.') is var dot && dot < 1 || path[dot - 1] != ']') continue;
            var shape = Search.PathSegments.Shape(path);
            if (!defs.TryGetValue(shape, out var set)) defs[shape] = set = [];
            set.Add(rd.GetInt64(1));
        }
        return [.. defs.Select(kv => (kv.Key, kv.Value.Count))
                       .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> EntryLeaves(string entryShape)
    {
        var prefix = PathLike(entryShape) + ".";
        var ids = new SortedDictionary<string, List<long>>(StringComparer.Ordinal);
        using (var rd = Query("SELECT id, path FROM field_value_paths WHERE path LIKE @e ESCAPE '\\'",
                              new Dictionary<string, object?> { ["@e"] = prefix + "%" }))
            while (rd.Read())
            {
                var path = rd.GetString(1);
                var tail = path[(path.LastIndexOf("].", StringComparison.Ordinal) + 2)..];
                if (Search.PathSegments.Shape(path) != entryShape + "." + tail || tail.Contains('.')) continue;
                if (!ids.TryGetValue(tail, out var list)) ids[tail] = list = [];
                list.Add(rd.GetInt64(0));
            }
        return [.. ids.Where(kv => Scalar(
                          $"SELECT EXISTS(SELECT 1 FROM field_values WHERE path_id IN ({string.Join(",", kv.Value)}) " +
                          $"AND is_default <> {Contract.DefaultState.Same})") > 0)
                      .Select(kv => kv.Key)];
    }

    public (IReadOnlyList<(string Path, int Count)> Paths, int PathTotal,
            IReadOnlyList<(string DefType, int Count)> DefTypes, int DefsCovered)
        ValueCoverage(PathQuery path, ScopeFilter scope, int limit, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(path, scope, p);
        if (defType is { Length: > 0 }) { p["@cdt"] = defType; where += " AND d.def_type = @cdt COLLATE NOCASE"; }
        var join = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id";

        var pathTotal = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath} {join} {where})", p);
        var paths = new List<(string, int)>();
        using (var rd = Query($"SELECT {FvPath}, COUNT(*) c {join} {where} GROUP BY {FvPath} ORDER BY c DESC, {FvPath} LIMIT {limit}", p))
            while (rd.Read()) paths.Add((rd.GetString(0), rd.GetInt32(1)));

        var types = new List<(string, int)>();
        using (var rd = Query($"SELECT d.def_type, COUNT(DISTINCT d.id) c {join} {where} GROUP BY d.def_type ORDER BY c DESC, d.def_type LIMIT {limit}", p))
            while (rd.Read()) types.Add((rd.GetString(0), rd.GetInt32(1)));

        var covered = Scalar($"SELECT COUNT(DISTINCT d.id) {join} {where}", p);
        return (paths, pathTotal, types, covered);
    }

    public IReadOnlyDictionary<(string Path, string Value), int> SharedValues(
        string defType, IEnumerable<(string Path, string? Value)> rows)
    {
        var want = new HashSet<(string, string)>();
        foreach (var (path, value) in rows)
            if (value is not null) want.Add((path, value));
        var found = new Dictionary<(string, string), int>();
        if (want.Count == 0) return found;

        using var rd = Query("SELECT path, value, defs FROM shared_values WHERE def_type = @t COLLATE NOCASE",
                             new Dictionary<string, object?> { ["@t"] = defType });
        while (rd.Read())
        {
            var key = (rd.GetString(0), rd.IsDBNull(1) ? "" : rd.GetString(1));
            if (want.Contains(key)) found[key] = rd.GetInt32(2);
        }
        return found;
    }

    public int CountDefsOfType(string defType, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var conds = new List<string> { "d.def_type = @t COLLATE NOCASE" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var sql = $"SELECT COUNT(*) FROM defs d WHERE {string.Join(" AND ", conds)}";
        var key = sql + "\0" + string.Join("\0", p.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        if (_defsOfTypeCount.TryGetValue(key, out var n)) return n;
        return _defsOfTypeCount[key] = Scalar(sql, p);
    }

    private readonly Dictionary<string, int> _defsOfTypeCount = new(StringComparer.Ordinal);

    public TruncationScope TruncatedDefsSharingPath(PathQuery path, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        return TruncatedAmong(SuffixWhere(path, scope, p), scope, p, defType);
    }

    public TruncationScope TruncatedDefsSharingValue(string value, ValueMatch match, ScopeFilter scope,
                                                     string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        return TruncatedAmong(ValueWhere(value, match, scope, p), scope, p, defType);
    }

    private TruncationScope TruncatedAmong(string innerWhere, ScopeFilter scope,
                                           Dictionary<string, object?> p, string? defType = null)
    {
        var conds = new List<string>
        {
            "t.fields_truncated > 0",
            $"t.def_type IN (SELECT DISTINCT d.def_type FROM field_values fv {FvJoin} " +
            $"JOIN defs d ON d.id = fv.def_id {innerWhere})",
        };
        if (scope.SqlPredicate("t.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@tdt"] = defType; conds.Add("t.def_type = @tdt COLLATE NOCASE"); }

        var types = new List<(string, int)>();
        var count = 0;
        using var rd = Query(
            $"SELECT t.def_type, COUNT(*) FROM defs t WHERE {string.Join(" AND ", conds)} " +
            "GROUP BY t.def_type ORDER BY COUNT(*) DESC, t.def_type", p);
        while (rd.Read()) { types.Add((rd.GetString(0), rd.GetInt32(1))); count += rd.GetInt32(1); }
        return new TruncationScope(count, types);
    }

    public TruncationScope TruncatedDefsOfType(string defType)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var n = Scalar("SELECT COUNT(*) FROM defs WHERE fields_truncated > 0 AND def_type = @t COLLATE NOCASE", p);
        return new TruncationScope(n, n > 0 ? [(defType, n)] : []);
    }

    public bool FieldPathExists(PathQuery path, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(path, scope, p);
        return Scalar($"SELECT EXISTS(SELECT 1 FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where})", p) != 0;
    }

    public (IReadOnlyList<(string Path, int Defs)> Samples, int Total, string? TopType) PathsBelow(
        string name, ScopeFilter scope, int limit)
    {
        var e = Escape(name);
        var p = new Dictionary<string, object?>
        {
            ["@b0"] = e + "[%", ["@b1"] = e + ".%", ["@b2"] = "%." + e + "[%", ["@b3"] = "%." + e + ".%",
        };
        var conds = new List<string>
        {
            PathIs($"path LIKE @b0 ESCAPE '\\' COLLATE NOCASE OR path LIKE @b1 ESCAPE '\\' COLLATE NOCASE " +
                   $"OR path LIKE @b2 ESCAPE '\\' COLLATE NOCASE OR path LIKE @b3 ESCAPE '\\' COLLATE NOCASE"),
        };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);
        var join = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id";

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath} {join} {where})", p);
        var rows = new List<(string, int)>();
        string? topType = null;
        if (total > 0)
        {
            using (var rd = Query($"SELECT {FvPath}, COUNT(DISTINCT d.id) c {join} {where} " +
                                  $"GROUP BY {FvPath} ORDER BY c DESC, {FvPath} LIMIT {limit}", p))
                while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
            using (var rd = Query($"SELECT d.def_type {join} {where} " +
                                  "GROUP BY d.def_type ORDER BY COUNT(DISTINCT d.id) DESC, d.def_type LIMIT 1", p))
                if (rd.Read()) topType = rd.GetString(0);
        }
        return (rows, total, topType);
    }

    public (IReadOnlyList<(string Path, string DefType, int Defs, int DefsExact, string Sample)> Rows,
            int Total, int Exact, int Defs)
        PathsWithValue(string value, ScopeFilter scope, int limit, ValueMatch match = ValueMatch.Substring, int offset = 0,
                       string? defType = null, IReadOnlyList<string>? pathContains = null)
    {
        var p = new Dictionary<string, object?>();
        var where = ValueWhere(value, match, scope, p, defType);
        if (pathContains is { Count: > 0 } has) where += $" AND {PathContainsClause(has, p)}";
        var join = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id";

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath}, d.def_type {join} {where})", p);
        p["@ev"] = value;
        var exactDefs = match == ValueMatch.Substring
            ? $", COUNT(DISTINCT CASE WHEN {FvValue} = @ev COLLATE NOCASE THEN d.id END)"
            : ", COUNT(DISTINCT d.id)";
        var rows = new List<(string, string, int, int, string)>();
        using var rd = Query(
            $"SELECT {FvPath}, d.def_type, COUNT(DISTINCT d.id) c{exactDefs}, MIN({FvValue}) {join} {where} " +
            $"GROUP BY {FvPath}, d.def_type ORDER BY c DESC, {FvPath} LIMIT {limit} OFFSET {offset}", p);
        while (rd.Read())
            rows.Add((rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3),
                      rd.IsDBNull(4) ? "" : rd.GetString(4)));

        var exact = total;
        if (match == ValueMatch.Substring && total > 0)
        {
            var ep = new Dictionary<string, object?>();
            var ew = ValueWhere(value, ValueMatch.Exact, scope, ep, defType);
            if (pathContains is { Count: > 0 } only) ew += $" AND {PathContainsClause(only, ep)}";
            exact = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath}, d.def_type {join} {ew})", ep);
        }

        var defs = total == 0 ? 0 : Scalar($"SELECT COUNT(DISTINCT d.id) {join} {where}", p);
        return (rows, total, exact, defs);
    }

    public (IReadOnlyList<(string Shape, int Defs, string Types)> Shown, int OtherShapes,
            int CrossTypeShapes, IReadOnlyCollection<string> Types)? ValueElsewhere(
        PathQuery own, string value, ValueMatch match, ScopeFilter scope)
    {
        var op = new Dictionary<string, object?>();
        var oc = new List<string>();
        PathCondition(own, op, oc);
        oc.Add(match == ValueMatch.Exact ? ValueEquals(value, op) : ValueContains(value, op));
        if (scope.SqlPredicate("d.source_mod", op) is { } osc) oc.Add(osc);

        var ownShapes = new HashSet<(string Shape, string Type)>();
        var defTypes = new HashSet<string>(StringComparer.Ordinal);
        using (var rd = Query(
            $"SELECT DISTINCT {FvPath}, d.def_type FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
            $"WHERE {string.Join(" AND ", oc)}", op))
            while (rd.Read())
            {
                ownShapes.Add((Search.PathSegments.Shape(rd.GetString(0)), rd.GetString(1)));
                defTypes.Add(rd.GetString(1));
            }
        if (defTypes.Count == 0) return null;

        var p = new Dictionary<string, object?>();
        var where = ValueWhere(value, match, scope, p);

        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rough = new Dictionary<string, int>(StringComparer.Ordinal);
        var types = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        using (var rd = Query(
            $"SELECT {FvPath}, d.def_type, COUNT(DISTINCT d.id) FROM field_values fv {FvJoin} " +
            $"JOIN defs d ON d.id = fv.def_id {where} GROUP BY {FvPath}, d.def_type", p))
            while (rd.Read())
            {
                var shape = Search.PathSegments.Shape(rd.GetString(0));
                var type = rd.GetString(1);
                if (ownShapes.Contains((shape, type))) continue;
                (paths.TryGetValue(shape, out var list) ? list : paths[shape] = []).Add(rd.GetString(0));
                rough[shape] = rough.GetValueOrDefault(shape) + rd.GetInt32(2);
                (types.TryGetValue(shape, out var ts) ? ts : types[shape] = new(StringComparer.Ordinal)).Add(type);
            }
        if (paths.Count == 0) return null;

        var crossType = types.Count(kv => kv.Value.Any(t => !defTypes.Contains(t)));

        var order = rough.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        var take = Cli.Limits.MaxSuggestions;
        while (take < order.Count && take < Cli.Limits.MaxShownShapes
               && order[take].Value == order[take - 1].Value) take++;
        if (order.Count == take + 1) take = order.Count;
        var top = order.Take(take).Select(x => x.Key).ToList();

        var shown = new List<(string Shape, int Defs, string Types)>();
        foreach (var shape in top)
        {
            var pp = new Dictionary<string, object?>();
            var pw = ValueWhere(value, match, scope, pp);
            var pk = new List<string>();
            foreach (var path in paths[shape].Distinct(StringComparer.Ordinal))
            { var k = $"@p{pk.Count}"; pp[k] = path; pk.Add(k); }
            shown.Add((shape, Scalar(
                $"SELECT COUNT(DISTINCT d.id) FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
                $"{pw} AND " + PathIs($"path IN ({string.Join(",", pk)})"), pp),
                string.Join("/", types[shape])));
        }
        return (shown.OrderByDescending(s => s.Defs).ThenBy(s => s.Shape, StringComparer.Ordinal).ToList(),
                paths.Count, crossType, defTypes);
    }

    public IReadOnlyList<string> AuthoredSiblings(IEnumerable<(long DefId, string Path)> shown)
    {
        var rows = shown.ToList();
        var already = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, path) in rows) already.Add(id + "\u0001" + path);

        var authored = AuthoredFieldsOf(rows.Select(r => r.DefId));

        var names = new List<string>();
        foreach (var batch in rows.Chunk(BatchedOrTerms)) SiblingsOfBatch(batch, authored, already, names);
        return names;
    }

    private const int BatchedOrTerms = 200;

    private Dictionary<long, List<(long RowId, string Path, string Leaf)>> AuthoredFieldsOf(IEnumerable<long> defIds)
    {
        var map = new Dictionary<long, List<(long RowId, string Path, string Leaf)>>();
        foreach (var chunk in defIds.Distinct().Chunk(BatchedOrTerms))
        {
            var p = new Dictionary<string, object?>();
            var keys = new List<string>();
            for (var i = 0; i < chunk.Length; i++) { p["@d" + i] = chunk[i]; keys.Add("@d" + i); }
            using var rd = Query(
                $"SELECT fv.def_id, fv.rowid, {FvPath}, {FvLeaf} FROM field_values fv {FvJoin} " +
                $"WHERE fv.def_id IN ({string.Join(",", keys)}) " +
                $"AND fv.is_default <> {Contract.DefaultState.Same} ORDER BY fv.rowid", p);
            while (rd.Read())
            {
                var id = rd.GetInt64(0);
                if (!map.TryGetValue(id, out var list)) map[id] = list = [];
                list.Add((rd.GetInt64(1), rd.GetString(2), rd.GetString(3)));
            }
        }
        return map;
    }

    private static bool StartsWithLike(string s, string prefix)
    {
        if (s.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            var a = s[i];
            var b = prefix[i];
            if (a == b) continue;
            if (a is >= 'A' and <= 'Z') a = (char)(a + 32);
            if (b is >= 'A' and <= 'Z') b = (char)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

    private static void SiblingsOfBatch((long DefId, string Path)[] batch,
                                        Dictionary<long, List<(long RowId, string Path, string Leaf)>> authored,
                                        HashSet<string> already, List<string> names)
    {
        var seen = new HashSet<long>();
        var hits = new List<(long RowId, long DefId, string Path, string Leaf)>();

        foreach (var (id, path) in batch)
        {
            if (Search.PathSegments.ContainerPrefix(path) is not { } prefix) continue;
            if (!authored.TryGetValue(id, out var fields)) continue;
            foreach (var f in fields)
                if (StartsWithLike(f.Path, prefix) && seen.Add(f.RowId))
                    hits.Add((f.RowId, id, f.Path, f.Leaf));
        }
        if (hits.Count == 0) return;

        hits.Sort((a, b) => a.RowId.CompareTo(b.RowId));

        foreach (var h in hits)
        {
            if (already.Contains(h.DefId + "\u0001" + h.Path)) continue;
            if (!names.Contains(h.Leaf, StringComparer.Ordinal)) names.Add(h.Leaf);
        }
    }

    public (IReadOnlyList<(string Value, int Count)> Rows, int Total) DistinctValues(
        PathQuery path, ScopeFilter scope, int limit, string? defType = null, int offset = 0)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(path, scope, p);
        if (defType is { Length: > 0 })
        {
            p["@dt"] = defType;
            where += " AND d.def_type = @dt COLLATE NOCASE";
        }

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvValue} FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where})", p);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT {FvValue}, COUNT(*) c FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where} " +
            $"GROUP BY {FvValue} ORDER BY c DESC, {FvValue} LIMIT {limit} OFFSET {offset}", p);
        while (rd.Read()) rows.Add((rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetInt32(1)));
        return (rows, total);
    }

    public IReadOnlyList<TranslationRow> Translations(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        var rows = new List<TranslationRow>();
        using var rd = Query(
            "SELECT def_name, def_type, path, translated, original, language, source_mod, origin, " +
            "source_file, source_file_count, key, key_state, applied FROM translations " +
            "WHERE def_name = @n COLLATE NOCASE ORDER BY origin, path", p);
        while (rd.Read())
            rows.Add(new TranslationRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
                rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetString(8),
                rd.IsDBNull(9) ? null : rd.GetInt32(9),
                rd.IsDBNull(10) ? null : rd.GetString(10),
                rd.IsDBNull(11) ? null : rd.GetString(11),
                rd.IsDBNull(12) ? null : rd.GetInt32(12) != 0));
        return ExactFirst(rows, r => r.DefName == defName);
    }

    public int KeyedCount() => Scalar("SELECT COUNT(*) FROM keyed");

    public bool HasXmlWrittenRows => _xwRows ??= Scalar("SELECT EXISTS(SELECT 1 FROM xml_written)") != 0;
    private bool? _xwRows;
    public bool HasInjectionKeyRows => _ikRows ??= Scalar("SELECT EXISTS(SELECT 1 FROM injection_keys)") != 0;
    private bool? _ikRows;
    public bool HasXmlWrittenText => _xwText ??= Scalar("SELECT EXISTS(SELECT 1 FROM xml_written WHERE inner_text IS NOT NULL)") != 0;
    private bool? _xwText;
    public bool HasTypeFieldRows
    {
        get
        {
            if (_tfRows is { } v) return v;
            var (_, from, _) = TypeDeclaredSource();
            return (_tfRows = Scalar($"SELECT EXISTS(SELECT 1 FROM {from})") != 0).Value;
        }
    }
    private bool? _tfRows;
    public bool HasInjectionApplied => _applied ??= Scalar(
        "SELECT NOT EXISTS(SELECT 1 FROM translations WHERE source_file IS NULL) "
        + "OR EXISTS(SELECT 1 FROM translations WHERE applied IS NOT NULL)") != 0;
    private bool? _applied;

    private const string KeyedColumns =
        "key, translated, original, language, source_file, source_line, source_mod, placeholder, origin, "
        + "source_file_count";

    private const string KeyedColumnsPrefixed =
        "k.key, k.translated, k.original, k.language, k.source_file, k.source_line, k.source_mod, " +
        "k.placeholder, k.origin, k.source_file_count";

    private IReadOnlyList<KeyedRow> ReadKeyed(string sql, Dictionary<string, object?>? p = null)
    {
        var rows = new List<KeyedRow>();
        using var rd = Query(sql, p);
        while (rd.Read())
            rows.Add(new KeyedRow(
                rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? 0 : rd.GetInt32(5),
                rd.IsDBNull(6) ? null : rd.GetString(6),
                !rd.IsDBNull(7) && rd.GetInt32(7) != 0,
                rd.GetString(8),
                rd.FieldCount > 9 && !rd.IsDBNull(9) ? rd.GetInt32(9) : null));
        return rows;
    }

    public IReadOnlyList<KeyedRow> KeyedByKey(string key)
        => ReadKeyed($"SELECT {KeyedColumns} FROM keyed WHERE key = @k COLLATE NOCASE " +
                     $"ORDER BY (origin <> '{TranslationOrigin.Runtime}'), origin, source_mod",
                     new Dictionary<string, object?> { ["@k"] = key });

    public (IReadOnlyList<string> Keys, int Total) KeyedPrefixSiblings(string key, int limit)
    {
        var p = new Dictionary<string, object?>
        {
            ["@p"] = key.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%",
            ["@k"] = key,
        };
        const string where = "FROM keyed WHERE key LIKE @p ESCAPE '\\' AND key <> @k COLLATE NOCASE";
        var total = Scalar($"SELECT COUNT(DISTINCT key) {where}", p);
        var keys = new List<string>();
        if (total > 0)
        {
            using var rd = Query($"SELECT DISTINCT key {where} ORDER BY LENGTH(key), key LIMIT {limit}", p);
            while (rd.Read()) keys.Add(rd.GetString(0));
        }
        return (keys, total);
    }

    public (IReadOnlyList<KeyedRow> Rows, int Total, int MatchedTotal, int DistinctKeys) KeyedSearch(
        string query, int limit, int offset = 0, bool placeholdersOnly = false,
        ScopeFilter? scope = null, bool noMod = false)
    {
        var p = new Dictionary<string, object?>
        {
            ["@m"] = FtsText.BuildMatchQuery(query),
            ["@q"] = query,
        };
        var from = "FROM keyed_fts f JOIN keyed k ON k.id = f.rowid WHERE keyed_fts MATCH @m" +
                   KeyedModClause("k.source_mod", scope, noMod, p);
        var filtered = placeholdersOnly ? from + " AND k.placeholder = 1" : from;
        var total = Scalar($"SELECT COUNT(*) {filtered}", p);
        var matchedTotal = placeholdersOnly ? Scalar($"SELECT COUNT(*) {from}", p) : total;
        var distinctKeys = total == 0 ? 0 : Scalar($"SELECT COUNT(DISTINCT k.key) {filtered}", p);
        var order = "ORDER BY (k.key = @q COLLATE NOCASE) DESC, (k.origin = 'runtime') DESC, " +
                    "bm25(keyed_fts, 10.0, 3.0, 3.0), LENGTH(k.key), k.key";
        var rows = ReadKeyed($"SELECT {KeyedColumnsPrefixed} {filtered} {order} LIMIT {limit} OFFSET {offset}", p);
        return (rows, total, matchedTotal, distinctKeys);
    }

    public (IReadOnlyList<KeyedRow> Rows, int Total, int LayerTotal) KeyedAll(
        int limit, int offset = 0, bool placeholdersOnly = false, ScopeFilter? scope = null, bool noMod = false)
    {
        var p = new Dictionary<string, object?>();
        var where = " WHERE 1" + (placeholdersOnly ? " AND placeholder = 1" : "") +
                    KeyedModClause("source_mod", scope, noMod, p);
        var layerTotal = KeyedCount();
        var total = Scalar($"SELECT COUNT(*) FROM keyed{where}", p);
        var rows = ReadKeyed(
            $"SELECT {KeyedColumns} FROM keyed{where} " +
            $"ORDER BY key COLLATE NOCASE, (origin <> '{TranslationOrigin.Runtime}'), origin, source_mod " +
            $"LIMIT {limit} OFFSET {offset}", p);
        return (rows, total, layerTotal);
    }

    private static string KeyedModClause(string column, ScopeFilter? scope, bool noMod, Dictionary<string, object?> p)
        => noMod ? $" AND {column} IS NULL"
            : scope?.SqlPredicate(column, p) is { } sc ? " AND " + sc : "";

    public IReadOnlyDictionary<string, KeyedRow> KeyedInEffect(IEnumerable<string> keys)
    {
        var list = keys.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, KeyedRow>(StringComparer.Ordinal);
        if (list.Count == 0) return result;

        const int batch = 500;
        for (var start = 0; start < list.Count; start += batch)
        {
            var p = new Dictionary<string, object?> { ["@o"] = TranslationOrigin.Runtime };
            var names = new List<string>();
            for (var i = start; i < Math.Min(start + batch, list.Count); i++)
            {
                p["@k" + i] = list[i];
                names.Add("@k" + i);
            }
            foreach (var row in ReadKeyed(
                $"SELECT {KeyedColumns} FROM keyed WHERE origin = @o AND key IN ({string.Join(",", names)})", p))
                result[row.Key] = row;
        }
        return result;
    }

    public IReadOnlyList<string> AllKeyedKeys()
    {
        var keys = new List<string>();
        using var rd = Query("SELECT DISTINCT key FROM keyed ORDER BY key");
        while (rd.Read()) keys.Add(rd.GetString(0));
        return keys;
    }

    public int EconomyCount() => Scalar("SELECT COUNT(*) FROM economy");

    private const string EconomyColumns =
        "id, def_name, label, category, mod, market_value, producible, made_from_stuff, is_weapon, " +
        "is_apparel, market_value_defined, calc_state, calculated_market_value, cost_to_make, profit, " +
        "profit_rate, work_to_produce, cost_list, cost_difficulty_var, cost_difficulty_inverted, " +
        "chain_end_share, cost_deep, profit_deep";

    private IReadOnlyList<EconomyRow> ReadEconomy(string sql, Dictionary<string, object?>? p = null)
    {
        var rows = new List<EconomyRow>();
        using var rd = Query(sql, p);
        while (rd.Read())
            rows.Add(new EconomyRow(
                rd.GetInt64(0),
                rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetDouble(5),
                rd.GetInt32(6) != 0,
                rd.GetInt32(7) != 0,
                rd.GetInt32(8) != 0,
                rd.GetInt32(9) != 0,
                rd.GetInt32(10) != 0,
                rd.GetString(11),
                rd.IsDBNull(12) ? null : rd.GetDouble(12),
                rd.IsDBNull(13) ? null : rd.GetDouble(13),
                rd.IsDBNull(14) ? null : rd.GetDouble(14),
                rd.IsDBNull(15) ? null : rd.GetDouble(15),
                rd.IsDBNull(16) ? null : rd.GetDouble(16),
                rd.IsDBNull(17) ? null : rd.GetString(17),
                rd.IsDBNull(18) ? null : rd.GetString(18),
                rd.GetInt32(19) != 0,
                rd.IsDBNull(20) ? null : rd.GetDouble(20),
                rd.IsDBNull(21) ? null : rd.GetDouble(21),
                rd.IsDBNull(22) ? null : rd.GetDouble(22)));
        return rows;
    }

    public IReadOnlyList<EconomyRow> EconomyByName(string defName)
        => ExactFirst(ReadEconomy($"SELECT {EconomyColumns} FROM economy WHERE def_name = @n COLLATE NOCASE",
                                  new Dictionary<string, object?> { ["@n"] = defName }),
                      r => r.DefName == defName);

    public (IReadOnlyList<EconomyRow> Rows, int Total) EconomyAll(
        ScopeFilter scope, string? category, string? calcState, bool producibleOnly,
        string orderBy, int limit, int offset)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();
        if (scope.SqlPredicate("mod", p) is { } modWhere) conds.Add(modWhere);
        if (category is not null) { conds.Add("category = @cat COLLATE NOCASE"); p["@cat"] = category; }
        if (string.Equals(calcState, Commands.EconomyCommand.CalcEmptySum, StringComparison.OrdinalIgnoreCase))
        {
            conds.Add("calc_state = @cs COLLATE NOCASE AND COALESCE(calculated_market_value, 0) <= 0");
            p["@cs"] = IntermediateFormat.EconomyCalcOk;
        }
        else if (string.Equals(calcState, IntermediateFormat.EconomyCalcOk, StringComparison.OrdinalIgnoreCase))
        {
            conds.Add("calc_state = @cs COLLATE NOCASE AND calculated_market_value > 0");
            p["@cs"] = calcState;
        }
        else if (calcState is not null) { conds.Add("calc_state = @cs COLLATE NOCASE"); p["@cs"] = calcState; }
        if (producibleOnly) conds.Add("producible = 1");
        var where = conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);

        var total = Scalar($"SELECT COUNT(*) FROM economy{where}", p);

        var rows = ReadEconomy(
            $"SELECT {EconomyColumns} FROM economy{where} " +
            $"ORDER BY ({orderBy} IS NULL), {orderBy} DESC, def_name LIMIT @lim OFFSET @off",
            new Dictionary<string, object?>(p) { ["@lim"] = limit, ["@off"] = offset });
        return (rows, total);
    }

    public IReadOnlyList<ShaderSourceRow> ShaderSources(string defName)
    {
        var rows = new List<ShaderSourceRow>();
        using var rd = Query(
            "SELECT stage, file, resolved_path, unresolved FROM shader_sources WHERE def_name = @n " +
            "ORDER BY stage DESC",
            new Dictionary<string, object?> { ["@n"] = defName });
        while (rd.Read())
            rows.Add(new ShaderSourceRow(rd.GetString(0), rd.GetString(1),
                                         rd.IsDBNull(2) ? null : rd.GetString(2),
                                         rd.IsDBNull(3) ? null : rd.GetString(3)));
        return rows;
    }

    public IReadOnlyList<EconomyChainRow> EconomyChain(long economyId)
    {
        var rows = new List<EconomyChainRow>();
        using var rd = Query(
            "SELECT thing_def, count, unit_value, chain_end FROM economy_cost_chain " +
            "WHERE economy_id = @id ORDER BY ordinal",
            new Dictionary<string, object?> { ["@id"] = economyId });
        while (rd.Read())
            rows.Add(new EconomyChainRow(rd.GetString(0), rd.GetInt32(1),
                                         rd.IsDBNull(2) ? null : rd.GetDouble(2), rd.GetInt32(3) != 0));
        return rows;
    }

    public IReadOnlyList<EconomyRecipeRow> EconomyRecipes(long economyId)
    {
        var rows = new List<EconomyRecipeRow>();
        using var rd = Query(
            "SELECT def_name, product_count, work_amount, self_referential FROM economy_recipes " +
            "WHERE economy_id = @id ORDER BY ordinal",
            new Dictionary<string, object?> { ["@id"] = economyId });
        while (rd.Read())
            rows.Add(new EconomyRecipeRow(rd.GetString(0), rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                                          rd.IsDBNull(2) ? null : rd.GetDouble(2), rd.GetInt32(3) != 0));
        return rows;
    }

    public IReadOnlyList<string> AllEconomyNames()
    {
        var names = new List<string>();
        using var rd = Query("SELECT def_name FROM economy ORDER BY def_name");
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
    }

    public int XmlNodeCount() => Scalar("SELECT COUNT(*) FROM xml_nodes");

    public IReadOnlyList<XmlNodeRow> NodesNamed(string name)
        => ExactFirst(ReadNodes(NodesNamedWhere, new Dictionary<string, object?> { ["@n"] = name }),
                      n => n.Name == name || n.DefName == name);

    public const string NodesNamedWhere =
        "WHERE rowid IN (SELECT rowid FROM xml_nodes WHERE name = @n COLLATE NOCASE " +
        "UNION SELECT rowid FROM xml_nodes WHERE def_name = @n COLLATE NOCASE)";

    public const string NodeWithNameWhere = "WHERE name = @n COLLATE NOCASE";

    public XmlNodeRow? NodeWithName(string name)
    {
        if (_nodeWithName.TryGetValue(name, out var hit)) return hit;
        return _nodeWithName[name] =
            ExactFirst(ReadNodes(NodeWithNameWhere, new Dictionary<string, object?> { ["@n"] = name }),
                       n => n.Name == name)
            .FirstOrDefault();
    }

    private readonly Dictionary<string, XmlNodeRow?> _nodeWithName = new(StringComparer.Ordinal);

    private sealed class AsciiNoCase : IEqualityComparer<string>
    {
        public static readonly AsciiNoCase Instance = new();
        private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        public bool Equals(string? x, string? y)
        {
            if (x is null || y is null) return x == y;
            if (x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++) if (Fold(x[i]) != Fold(y[i])) return false;
            return true;
        }
        public int GetHashCode(string s)
        {
            var h = new HashCode();
            foreach (var c in s) h.Add(Fold(c));
            return h.ToHashCode();
        }
    }

    public IReadOnlyList<XmlNodeRow> NodesInheritingFrom(string parentName)
        => ReadNodes(NodesInheritingFromWhere, new Dictionary<string, object?> { ["@p"] = parentName });

    public static readonly string NodesInheritingFromWhere =
        "WHERE parent_name = @p COLLATE NOCASE AND (" + ParentResolvesNoCase("xml_nodes.parent_name", "@p") + ")";

    private static string ParentResolvesNoCase(string parent, string name)
        => $"{parent} = {name} OR NOT EXISTS (SELECT 1 FROM xml_nodes y WHERE y.name = {parent})";

    public (int Descendants, int WithPath, int SameValue, int Truncated) Witnesses(
        string ancestorName, string pathFilter, string? value, (string DefName, string DefType)? exclude,
        bool exactPath = false)
    {
        var p = new Dictionary<string, object?>();
        var cte = KinCte(ancestorName, pathFilter, exclude, p, exactPath);

        var same = value is null
            ? "0"
            : $"(SELECT COUNT(DISTINCT fv.def_id) FROM field_values fv {FvJoin} JOIN kin k ON k.id = fv.def_id " +
              "  WHERE " + PathIs($"path LIKE @f ESCAPE '\\'")
              + " AND " + ValueIs("value = @v COLLATE NOCASE") + ")";
        if (value is not null) p["@v"] = value;

        using var rd = Query(
            cte + "SELECT (SELECT COUNT(*) FROM kin), " +
            $"(SELECT COUNT(DISTINCT fv.def_id) FROM field_values fv {FvJoin} JOIN kin k ON k.id = fv.def_id " +
            " WHERE " + PathIs($"path LIKE @f ESCAPE '\\'") + $"), {same}, " +
            "(SELECT COUNT(*) FROM kin k JOIN defs d ON d.id = k.id WHERE d.fields_truncated > 0)", p);
        return rd.Read()
            ? (rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3))
            : (0, 0, 0, 0);
    }

    public int XmlLinesWritten(string defType, string? name, string? defName, string pathFilter, bool exactPath,
                               IReadOnlyCollection<string>? labelPaths = null)
    {
        static string AnyIndex(string path)
            => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+]", "[]");
        var filter = AnyIndex(pathFilter);
        bool Labelled(string path)
        {
            if (labelPaths is null) return false;
            foreach (var l in labelPaths)
                if (path.StartsWith(l, StringComparison.OrdinalIgnoreCase)
                    && (path.Length == l.Length || path[l.Length] == '.'))
                    return true;
            return false;
        }

        var keys = new List<(string Key, int IsName)>();
        if (name is { Length: > 0 }) keys.Add((name, 1));
        if (defName is { Length: > 0 }) keys.Add((defName, 0));
        var count = 0;
        foreach (var (key, isName) in keys)
        {
            using var rd = Query(
                "SELECT path FROM xml_written WHERE node_key = @k AND key_is_name = @n AND def_type = @t COLLATE NOCASE",
                new Dictionary<string, object?> { ["@k"] = key, ["@n"] = isName, ["@t"] = defType });
            while (rd.Read())
            {
                var path = rd.GetString(0);
                if (PathFilterMatches(AnyIndex(path), [filter], exactPath) || Labelled(path)) count++;
            }
        }
        return count;
    }

    public (string? Value, int Defs, int Distinct) DominantValue(string ancestorName, string pathFilter,
                                                                 bool exactPath = false)
    {
        var p = new Dictionary<string, object?>();
        var cte = KinCte(ancestorName, pathFilter, null, p, exactPath);

        using var rd = Query(
            cte +
            "SELECT " + FvValue + ", COUNT(DISTINCT fv.def_id) AS c, " +
            "  (SELECT COUNT(*) FROM (SELECT DISTINCT fvv2.value COLLATE NOCASE "
            + "FROM field_values fv2 " +
            "JOIN field_value_paths fvp2 ON fvp2.id = fv2.path_id " +
            "LEFT JOIN field_value_values fvv2 ON fvv2.id = fv2.value_id " +
            "     JOIN kin k2 ON k2.id = fv2.def_id WHERE fvp2.path LIKE @f ESCAPE '\\')) " +
            $"FROM field_values fv {FvJoin} JOIN kin k ON k.id = fv.def_id " +
            "WHERE " + PathIs($"path LIKE @f ESCAPE '\\'") + " " +
            "GROUP BY " + FvValue + " COLLATE NOCASE ORDER BY c DESC, " + FvValue + " LIMIT 1", p);
        return rd.Read()
            ? (rd.IsDBNull(0) ? null : rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2))
            : (null, 0, 0);
    }

    private static string KinCte(string ancestorName, string pathFilter,
                                 (string DefName, string DefType)? exclude, Dictionary<string, object?> p,
                                 bool exactPath = false)
    {
        p["@root"] = ancestorName;
        p["@f"] = PathFilterLike(pathFilter, exactPath);

        var cte =
            "WITH RECURSIVE anc(name) AS ( " +
            "  SELECT @root " +
            "  UNION " +
            "  SELECT n.name FROM xml_nodes n JOIN anc a ON n.parent_name = a.name COLLATE NOCASE " +
            $"   WHERE n.name IS NOT NULL AND n.name <> '' AND ({ParentResolvesNoCase("n.parent_name", "a.name")}) " +
            "), kin(id) AS ( " +
            "  SELECT DISTINCT d.id FROM xml_nodes x " +
            "    JOIN defs d ON d.def_name = x.def_name " +
            "   WHERE x.def_name IS NOT NULL AND x.def_name <> '' " +
            "     AND x.parent_name COLLATE NOCASE IN (SELECT name FROM anc) " +
            "     AND (x.parent_name IN (SELECT name FROM anc) " +
            "          OR NOT EXISTS (SELECT 1 FROM xml_nodes y WHERE y.name = x.parent_name)) " +
            "     AND (d.def_type = x.def_type COLLATE NOCASE " +
            "          OR (SELECT COUNT(*) FROM defs d2 WHERE d2.def_name = x.def_name) = 1) ";
        if (exclude is { } ex)
        {
            p["@xn"] = ex.DefName; p["@xt"] = ex.DefType;
            cte += "     AND NOT (d.def_name = @xn AND d.def_type = @xt) ";
        }
        return cte + ") ";
    }

    public IReadOnlyList<string> AllXmlNodeNames()
    {
        var names = new List<string>();
        using var rd = Query("SELECT DISTINCT name FROM xml_nodes WHERE name IS NOT NULL AND name <> '' ORDER BY name");
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
    }

    private List<XmlNodeRow> ReadNodes(string where, IDictionary<string, object?> p)
    {
        const string cols = "def_type, name, parent_name, abstract, def_name, label, source_mod, source_file, patch_ops, patch_ops_defname, patch_ops_label";
        var rows = new List<XmlNodeRow>();
        using var rd = Query(
            $"SELECT {cols} FROM xml_nodes {where} ORDER BY abstract DESC, name, def_name", p);
        while (rd.Read())
            rows.Add(new XmlNodeRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.GetInt32(3) != 0,
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.GetInt32(8), rd.GetInt32(9), rd.GetInt32(10)));
        return rows;
    }

    public Dictionary<string, string> XmlWrittenMarks(string defType, string defName)
        => XmlWrittenMarks(defType, defName, out _, out _, out _);

    public Dictionary<string, string> XmlWrittenMarks(string defType, string defName,
                                                      out HashSet<string> containers)
        => XmlWrittenMarks(defType, defName, out containers, out _, out _);

    public Dictionary<string, string> XmlWrittenMarks(string defType, string defName,
                                                      out HashSet<string> containers,
                                                      out Dictionary<string, string> texts,
                                                      out HashSet<string>? patchedPaths)
        => XmlWrittenMarks(defType, defName, out containers, out texts, out patchedPaths, out _, out _);

    public Dictionary<string, string> XmlWrittenMarks(string defType, string defName,
                                                      out HashSet<string> containers,
                                                      out Dictionary<string, string> texts,
                                                      out HashSet<string>? patchedPaths,
                                                      out HashSet<string> parentListPrefixes)
        => XmlWrittenMarks(defType, defName, out containers, out texts, out patchedPaths, out parentListPrefixes, out _);

    public Dictionary<string, string> XmlWrittenMarks(string defType, string defName,
                                                      out HashSet<string> containers,
                                                      out Dictionary<string, string> texts,
                                                      out HashSet<string>? patchedPaths,
                                                      out HashSet<string> parentListPrefixes,
                                                      out Dictionary<string, List<XmlOccurrence>> occurrences)
    {
        containers = new HashSet<string>(StringComparer.Ordinal);
        parentListPrefixes = new HashSet<string>(StringComparer.Ordinal);

        var hereKeys = new List<(string Key, bool IsName)> { (defName, false) };

        var named = ExactFirst(NodesNamed(defName).Where(n => string.Equals(n.DefName, defName, StringComparison.OrdinalIgnoreCase)),
                               n => n.DefName == defName);
        var xmlNode = named.FirstOrDefault(n => DefTypes.Same(n.DefType, defType) && !n.Abstract)
                   ?? named.FirstOrDefault(n => DefTypes.Same(n.DefType, defType))
                   ?? (named.Count == 1 && GetDefsNamed(defName).Count == 1 ? named[0] : null);
        if (xmlNode?.Name is { Length: > 0 } ownName && !string.Equals(ownName, defName, StringComparison.Ordinal))
            hereKeys.Add((ownName, true));

        var ancestors = new List<List<(string Key, bool IsName)>>();
        var cursor = xmlNode?.ParentName;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (cursor is { Length: > 0 } && seen.Add(cursor))
        {
            var node = NodeWithName(cursor);
            var keys = new List<(string Key, bool IsName)> { (cursor, true) };
            if (node is { Abstract: false, DefName: { Length: > 0 } ancestorDefName }) keys.Add((ancestorDefName, false));
            ancestors.Add(keys);
            cursor = node?.ParentName;
        }

        var xmlType = xmlNode?.DefType ?? defType;
        var withPatched = Meta.IndexesPostPatchXml;

        (List<XmlLine> Lines, List<string> NoInherit) Read(IEnumerable<(string Key, bool IsName)> keys)
        {
            var lines = new List<XmlLine>();
            var noInherit = new List<string>();
            foreach (var (key, isName) in keys)
            {
                if (_xmlLinesCache.TryGetValue((key, isName, defType, xmlType), out var hit))
                {
                    lines.AddRange(hit.Lines);
                    noInherit.AddRange(hit.NoInherit);
                    continue;
                }
                var keyLines = new List<XmlLine>();
                var keyNoInherit = new List<string>();
                var p = new Dictionary<string, object?>
                {
                    ["@k"] = key, ["@n"] = isName ? 1 : 0, ["@t"] = defType, ["@x"] = xmlType,
                };
                if (_xmlPreload is { } pre)
                {
                    bool TypeOk(string t) => AsciiNoCase.Instance.Equals(t, defType) || AsciiNoCase.Instance.Equals(t, xmlType);
                    foreach (var (t, line) in pre.Lines.GetValueOrDefault((key, isName)) ?? [])
                        if (TypeOk(t)) keyLines.Add(line);
                    foreach (var (t, path) in pre.NoInherit.GetValueOrDefault((key, isName)) ?? [])
                        if (TypeOk(t)) keyNoInherit.Add(path);
                }
                else
                {
                    const string match = "WHERE node_key = @k AND key_is_name = @n " +
                                         "AND (def_type = @t COLLATE NOCASE OR def_type = @x COLLATE NOCASE)";
                    using (var rd = Query("SELECT path, inner_text, patched FROM xml_written " + match, p))
                        while (rd.Read())
                            keyLines.Add(new XmlLine(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1),
                                                     !rd.IsDBNull(2) && rd.GetInt64(2) != 0));
                    using (var rd = Query("SELECT path FROM xml_no_inherit " + match, p))
                        while (rd.Read()) keyNoInherit.Add(rd.GetString(0));
                }
                _xmlLinesCache[(key, isName, defType, xmlType)] = (keyLines, keyNoInherit);
                lines.AddRange(keyLines);
                noInherit.AddRange(keyNoInherit);
            }
            return (lines, noInherit);
        }

        var chain = new List<(List<XmlLine> Lines, string Mark)>();
        var cuts = new List<List<string>>();
        void Push((List<XmlLine> Lines, List<string> NoInherit) read, string mark)
        {
            chain.Add((read.Lines, mark));
            cuts.Add(read.NoInherit);
        }
        for (var i = ancestors.Count - 1; i >= 0; i--) Push(Read(ancestors[i]), "parent");
        Push(Read(hereKeys), "here");

        var cut = new List<string>();
        for (var n = chain.Count - 1; n >= 0; n--)
        {
            if (cut.Count > 0) chain[n].Lines.RemoveAll(l => cut.Exists(c => CutBy(l.Path, c)));
            cut.AddRange(cuts[n]);
        }

        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        for (var n = 0; n < chain.Count; n++)
            foreach (var line in chain[n].Lines)
                if (FirstIndex(line.Path) is { } fi)
                {
                    if (!counts.TryGetValue(fi.List, out var per)) counts[fi.List] = per = new int[chain.Count];
                    per[n] = Math.Max(per[n], fi.Index + 1);
                }

        Dictionary<string, int>? fieldCounts = null;
        Dictionary<string, string?>? fieldValues = null;
        var offsets = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (list, per) in counts)
        {
            if (per.Count(c => c > 0) < 2) continue;
            fieldCounts ??= FieldListCounts(defType, defName);
            var have = fieldCounts.GetValueOrDefault(list);
            var off = new int[chain.Count];
            for (int n = 1; n < chain.Count; n++) off[n] = off[n - 1] + per[n - 1];
            if (have != per.Sum() && !(have > per.Sum() && TypeLinesAgree(list, off, per)))
            {
                parentListPrefixes.Add(list + "[");
                continue;
            }
            offsets[list] = off;
        }

        bool TypeLinesAgree(string list, int[] off, int[] per)
        {
            fieldValues ??= FieldValues(defType, defName);
            for (var n = 0; n < chain.Count; n++)
            {
                if (per[n] == 0) continue;
                var checkedAny = false;
                foreach (var line in chain[n].Lines)
                {
                    if (line.Text is not { Length: > 0 } text || FirstIndex(line.Path) is not { } fi || fi.List != list) continue;
                    var leaf = line.Path[(line.Path.LastIndexOf('.') + 1)..];
                    if (leaf is not ("Class" or "compClass")) continue;
                    var shifted = list + "[" + (fi.Index + off[n]).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                  + line.Path[fi.Close..];
                    if (!fieldValues.TryGetValue(shifted, out var v) || v is null
                        || !(v == text.Trim() || v.EndsWith("." + text.Trim(), StringComparison.Ordinal))) return false;
                    checkedAny = true;
                }
                if (!checkedAny) return false;
            }
            return true;
        }

        var marks = new Dictionary<string, string>(StringComparer.Ordinal);
        var textsLocal = new Dictionary<string, string>(StringComparer.Ordinal);
        texts = textsLocal;
        var patchedLocal = withPatched ? new HashSet<string>(StringComparer.Ordinal) : null;
        patchedPaths = patchedLocal;
        var occurrencesLocal = new Dictionary<string, List<XmlOccurrence>>(StringComparer.Ordinal);
        occurrences = occurrencesLocal;

        string Shifted(string path, int n)
        {
            var fi = FirstIndex(path);
            return fi is { } f && offsets.TryGetValue(f.List, out var off) && off[n] > 0
                ? f.List + "[" + (f.Index + off[n]).ToString(System.Globalization.CultureInfo.InvariantCulture)
                  + path[f.Close..]
                : path;
        }

        var ancestorText = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var n = 0; n < chain.Count - 1; n++)
            foreach (var line in chain[n].Lines)
                ancestorText[Shifted(line.Path, n)] = line.Text;
        string HereMark(string path, string? text) =>
            !ancestorText.TryGetValue(path, out var a) ? "here"
            : a is not null && text is not null && a.Trim() == text.Trim() ? "here=" : "here>";

        for (var n = chain.Count - 1; n >= 0; n--)
        {
            var (lines, chainMark) = chain[n];
            foreach (var line in lines)
            {
                var path = Shifted(line.Path, n);
                var fi = FirstIndex(path);
                var mark = n == chain.Count - 1 ? HereMark(path, line.Text) : chainMark;

                var attr = path.IndexOf(".@", StringComparison.Ordinal);
                var scan = attr < 0 ? path : path[..attr];
                for (var dot = scan.IndexOf('.'); dot > 0; dot = scan.IndexOf('.', dot + 1))
                    containers.Add(scan[..dot]);
                if (fi is { } g && parentListPrefixes.Contains(g.List + "["))
                {
                    containers.Add(g.List);
                    continue;
                }
                if (attr >= 0)
                {
                    marks.TryAdd(scan + ".@", mark);
                    continue;
                }
                if (line.Text is not null)
                {
                    if (!occurrencesLocal.TryGetValue(path, out var occ)) occurrencesLocal[path] = occ = [];
                    occ.Add(new XmlOccurrence(line.Text, mark));
                }
                if (marks.ContainsKey(path)) continue;
                marks[path] = mark;
                if (line.Text is not null) textsLocal[path] = line.Text;
                if (patchedLocal is not null && line.Patched) patchedLocal.Add(path);
            }
        }
        return marks;
    }

    private readonly record struct XmlLine(string Path, string? Text, bool Patched);

    private readonly Dictionary<(string Key, bool IsName, string DefType, string XmlType),
                                (List<XmlLine> Lines, List<string> NoInherit)> _xmlLinesCache = [];

    private (Dictionary<(string, bool), List<(string DefType, XmlLine Line)>> Lines,
             Dictionary<(string, bool), List<(string DefType, string Path)>> NoInherit)? _xmlPreload;

    public void PreloadXmlWritten()
    {
        if (_xmlPreload is not null) return;
        var lines = new Dictionary<(string, bool), List<(string, XmlLine)>>();
        using (var rd = Query("SELECT def_type, node_key, key_is_name, path, inner_text, patched FROM xml_written ORDER BY rowid"))
            while (rd.Read())
            {
                var k = (rd.GetString(1), rd.GetInt64(2) != 0);
                if (!lines.TryGetValue(k, out var l)) lines[k] = l = [];
                l.Add((rd.GetString(0), new XmlLine(rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                                                    !rd.IsDBNull(5) && rd.GetInt64(5) != 0)));
            }
        var noInherit = new Dictionary<(string, bool), List<(string, string)>>();
        using (var rd = Query("SELECT def_type, node_key, key_is_name, path FROM xml_no_inherit ORDER BY rowid"))
            while (rd.Read())
            {
                var k = (rd.GetString(1), rd.GetInt64(2) != 0);
                if (!noInherit.TryGetValue(k, out var l)) noInherit[k] = l = [];
                l.Add((rd.GetString(0), rd.GetString(3)));
            }
        _xmlPreload = (lines, noInherit);
    }

    private static bool CutBy(string path, string cut)
    {
        if (cut.Length == 0) return true;
        if (!path.StartsWith(cut, StringComparison.Ordinal)) return false;
        if (path.Length == cut.Length) return true;
        var next = path[cut.Length];
        if (next == '[') return true;
        return next == '.' && !path.AsSpan(cut.Length + 1).SequenceEqual("Class");
    }

    private static (string List, int Index, int Close)? FirstIndex(string path)
    {
        var open = path.IndexOf('[');
        if (open <= 0) return null;
        var close = path.IndexOf(']', open + 1);
        if (close < 0 || !int.TryParse(path.AsSpan(open + 1, close - open - 1), System.Globalization.NumberStyles.None,
                                       System.Globalization.CultureInfo.InvariantCulture, out var index)) return null;
        return (path[..open], index, close);
    }

    private Dictionary<string, string?> FieldValues(string defType, string defName)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType, ["@n"] = defName };
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var rd = Query(
            $"SELECT {FvPath}, {FvValue} FROM field_values fv {FvJoin} " +
            "WHERE fv.def_id IN (SELECT id FROM defs WHERE def_type = @t AND def_name = @n)", p);
        while (rd.Read()) d.TryAdd(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1));
        return d;
    }

    private Dictionary<string, int> FieldListCounts(string defType, string defName)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType, ["@n"] = defName };
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        using var rd = Query(
            $"SELECT DISTINCT {FvPath} FROM field_values fv {FvJoin} " +
            "WHERE fv.def_id IN (SELECT id FROM defs WHERE def_type = @t AND def_name = @n)", p);
        while (rd.Read())
            if (FirstIndex(rd.GetString(0)) is { } fi)
                d[fi.List] = Math.Max(d.GetValueOrDefault(fi.List), fi.Index + 1);
        return d;
    }

    public IReadOnlyList<FieldRow> AllFieldCells(long defId, string? pathPrefix = null)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        var prefix = "";
        if (pathPrefix is not null)
        {
            p["@pre"] = pathPrefix; p["@plen"] = pathPrefix.Length;
            prefix = $" AND substr({FvPath}, 1, @plen) = @pre";
        }
        var rows = new List<FieldRow>();
        using var rd = Query(
            $"SELECT {FvPath}, {FvLeaf}, {FvValue}, fv.is_default FROM field_values fv {FvJoin} "
            + $"WHERE fv.def_id = @id{prefix} ORDER BY {FvPath}, fv.rowid", p);
        while (rd.Read())
            rows.Add(new FieldRow(rd.GetString(0), rd.GetString(1),
                                  rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetInt32(3)));
        return rows;
    }

    public const string TypeDeclaredPathsWhere = "WHERE n.name = @t COLLATE NOCASE";

    private const string FvJoin =
        "JOIN field_value_paths fvp ON fvp.id = fv.path_id "
        + "LEFT JOIN field_value_values fvv ON fvv.id = fv.value_id";

    private const string FvPath = "fvp.path";

    private const string FvLeaf = "fvp.leaf";

    private static string LeafIs(string cond) => PathIs(cond);

    private const string FvValue = "fvv.value";

    private string ValueEquals(string value, Dictionary<string, object?> p)
    {
        p["@v"] = value;
        var canonical = ValueText.Canonical(value);
        if (string.Equals(canonical, value, StringComparison.Ordinal))
            return ValueIs("value = @v COLLATE NOCASE");
        p["@vc"] = canonical;
        return ValueIs("value = @v COLLATE NOCASE OR value = @vc COLLATE NOCASE");
    }

    private string ValueContains(string value, Dictionary<string, object?> p)
    {
        p["@v"] = "%" + Escape(value) + "%";
        var canonical = ValueText.Canonical(value);
        if (string.Equals(canonical, value, StringComparison.Ordinal))
            return ValueIs("value LIKE @v ESCAPE '\\'");
        p["@vc"] = "%" + Escape(canonical) + "%";
        return ValueIs("value LIKE @v ESCAPE '\\' OR value LIKE @vc ESCAPE '\\'");
    }

    private static string ValueIs(string cond)
        => $"fv.value_id IN (SELECT id FROM field_value_values WHERE {cond})";

    private static string PathIs(string cond)
        => $"fv.path_id IN (SELECT id FROM field_value_paths WHERE {cond})";

    public readonly record struct TruncationSpread(int LostPaths, int ValuesOnly);

    public TruncationSpread TruncatedDefSpread()
    {
        const string missing = "truncated_by_cap + truncated_by_depth + truncated_by_items + truncated_by_error";
        return new TruncationSpread(
            Scalar($"SELECT COUNT(*) FROM defs WHERE {missing} > 0"),
            Scalar($"SELECT COUNT(*) FROM defs WHERE fields_truncated > 0 AND {missing} = 0"));
    }

    public TruncationCauses? TruncationCausesFor(long defId)
    {
        using var rd = Query(
            "SELECT truncated_by_cap, truncated_by_length, truncated_by_depth, truncated_by_items, truncated_by_error "
            + "FROM defs WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = defId });
        if (!rd.Read()) return null;
        return new TruncationCauses(rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4));
    }

    private static (string PathExpr, string From, string Where) TypeDeclaredSource()
        => ("d.path",
            "type_names n "
            + "JOIN type_subtrees ts ON ts.type_id = n.id "
            + "JOIN subtree_paths sp ON sp.subtree_id = ts.subtree_id "
            + "JOIN type_field_paths d ON d.id = sp.path_id",
            TypeDeclaredPathsWhere);

    public int? TypeDeclaredPathMaxSegments(string defType)
    {
        var (pathExpr, from, where) = TypeDeclaredSource();
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        using var rd = Query(
            $"SELECT MAX(LENGTH({pathExpr}) - LENGTH(REPLACE({pathExpr}, '.', '')) + "
            + $"LENGTH({pathExpr}) - LENGTH(REPLACE({pathExpr}, '[', '')) + 1) "
            + $"FROM {from} {where}", p);
        if (!rd.Read() || rd.IsDBNull(0)) return null;
        return rd.GetInt32(0);
    }

    public IReadOnlyList<string> TypeDeclaredPaths(string defType, IReadOnlyList<string>? pathFilters = null,
                                                   bool exactPath = false)
    {
        var (pathExpr, from, where) = TypeDeclaredSource();
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var filters = (pathFilters ?? []).Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (filters.Count > 0)
        {
            var ors = new List<string>();
            for (var i = 0; i < filters.Count; i++)
            {
                p["@f" + i] = PathFilterLike(filters[i], exactPath);
                ors.Add($"{pathExpr} LIKE @f{i} ESCAPE '\\'");
            }
            where += " AND (" + string.Join(" OR ", ors) + ")";
        }

        var rows = new List<string>();
        using var rd = Query($"SELECT {pathExpr} FROM {from} {where} ORDER BY {pathExpr}", p);
        while (rd.Read()) rows.Add(rd.GetString(0));
        return rows;
    }

    public IReadOnlyList<(string Path, IReadOnlyList<string> Types)> DeclaredPathsEndingIn(string leaf)
    {
        var p = new Dictionary<string, object?> { ["@l"] = leaf };
        var rows = new List<(string Path, IReadOnlyList<string> Types)>();
        using var rd = Query(
            "SELECT d.path, group_concat(n.name, char(9)) FROM type_field_paths d "
            + "JOIN subtree_paths sp ON sp.path_id = d.id "
            + "JOIN type_subtrees ts ON ts.subtree_id = sp.subtree_id "
            + "JOIN type_names n ON n.id = ts.type_id "
            + "WHERE d.leaf = @l COLLATE NOCASE GROUP BY d.id ORDER BY d.path", p);
        while (rd.Read())
            rows.Add((rd.GetString(0),
                      [.. rd.GetString(1).Split('\t').Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
        return rows;
    }

    private const string DefColumns =
        "d.id, d.def_type, d.def_name, d.label, d.description, d.source_mod, d.source_file, d.generated, d.class, d.fields_truncated";

    private static DefRow ReadDefRow(SqliteDataReader rd) => new(
        rd.GetInt64(0), rd.GetString(1), rd.GetString(2),
        rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
        rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
        rd.GetInt32(7) != 0, rd.IsDBNull(8) ? null : rd.GetString(8), rd.GetInt32(9));

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
