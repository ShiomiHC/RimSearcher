using Microsoft.Data.Sqlite;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

public sealed record DefRow(long Id, string DefType, string DefName, string? Label, string? Description,
                            string? SourceMod, string? SourceFile, bool Generated, string? Class,
                            int FieldsTruncated);

public sealed record FieldRow(string Path, string Leaf, string? Value);

/// <summary>
/// 一条译文。<paramref name="DefType"/> 为 null 表示这条是从语言文件收割的,注入 key
/// 只有 <c>DefName.field</c> —— 同名跨 def 类型时它归谁在数据源里就是不确定的。
/// </summary>
public sealed record TranslationRow(string DefName, string? DefType, string Path, string? Translated,
                                   string? Original, string? Language, string? SourceMod, string Origin);

/// <summary>
/// 继承层的一行:XML 里一个带 <c>Name=</c> / <c>ParentName=</c> / <c>Abstract=</c> 的节点。
/// <paramref name="PatchOps"/> 是有多少条 PatchOperation 的 xpath 点了这个 Name —— 这一层
/// 是打补丁**之前**的原文,那个数就是这份时间差的逐条申报。
/// </summary>
public sealed record XmlNodeRow(string DefType, string? Name, string? ParentName, bool Abstract,
                                string? DefName, string? Label, string? SourceMod, string? SourceFile,
                                int PatchOps);

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
        //
        // 这一档必须压在**名字前缀**之上。反过来排过一版,结果是 `search shield` 把没有 label 的
        // Shield_Break(EffecterDef)排到了 Apparel_ShieldBelt 前面 —— 只因为它的名字以
        // shield 开头。前缀命中说明的是「名字长得像」,有没有 label 说明的是「这东西给人看」,
        // 后者才是提问的人真正在筛的东西。
        var order = "ORDER BY (d.def_name = @q COLLATE NOCASE) DESC, " +
                    "(d.label IS NOT NULL AND d.label != '') DESC, " +
                    "(d.def_name LIKE @q || '%' COLLATE NOCASE) DESC, " +
                    "bm25(defs_fts, 10.0, 4.0, 1.0, 3.0), LENGTH(d.def_name), d.def_name";
        var rows = ReadDefs($"SELECT {DefColumns} {from} {order} LIMIT {limit}", p);
        return (rows, total);
    }

    /// <summary>
    /// 名字里含 <paramref name="query"/>、但 FTS **没**匹配上的 def 名。
    ///
    /// FTS 分词按分隔符与驼峰词首切,查询词落在名字中段就漏(`VoidNode` 找不到
    /// `MonolithGleamingVoidNode`)。补扫必须在这里做减法而不是在调用方按已显示的行去重 ——
    /// 那样 `--limit` 一小,没显示出来的 FTS 命中就会被当成新增重复计进总数。
    /// </summary>
    public IReadOnlyList<string> NamesContainingUnmatched(string query, ScopeFilter scope, string? defType)
    {
        var p = new Dictionary<string, object?> { ["@m"] = FtsText.BuildMatchQuery(query), ["@q"] = "%" + Escape(query) + "%" };
        var conds = new List<string> { "d.def_name LIKE @q ESCAPE '\\'", "d.id NOT IN (SELECT rowid FROM defs_fts WHERE defs_fts MATCH @m)" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var names = new List<string>();
        using var rd = Query($"SELECT d.def_name FROM defs d WHERE {string.Join(" AND ", conds)} ORDER BY LENGTH(d.def_name), d.def_name", p);
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
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

    /// <summary>
    /// 一个 def 的字段。<paramref name="pathFilter"/> 非空时只留路径含该子串的行 ——
    /// 没有它,调用方拿一个 295 字段的 def 找 statBases 只能把整份输出 grep 一遍,
    /// 而「别拿文本匹配 def」正是这套工具存在的理由,自己逼出这个动作是自相矛盾。
    /// <c>Matched</c> 是过滤后的总数,<c>Total</c> 是这个 def 的字段总数 —— 两个都给,
    /// 调用方才分得清「过滤掉了多少」和「被 limit 截了多少」。
    /// </summary>
    public (IReadOnlyList<FieldRow> Rows, int Matched, int Total) Fields(
        long defId, int limit, IReadOnlyList<string>? pathFilters = null)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        var total = Scalar("SELECT COUNT(*) FROM field_values WHERE def_id = @id", p);

        var filters = (pathFilters ?? []).Where(f => !string.IsNullOrEmpty(f)).ToList();
        var where = "WHERE def_id = @id";
        if (filters.Count > 0)
        {
            var ors = new List<string>();
            for (var i = 0; i < filters.Count; i++)
            {
                p["@f" + i] = "%" + Escape(filters[i]) + "%";
                ors.Add($"path LIKE @f{i} ESCAPE '\\'");
            }
            where += " AND (" + string.Join(" OR ", ors) + ")";
        }

        var matched = filters.Count == 0
            ? total
            : Scalar($"SELECT COUNT(*) FROM field_values {where}", p);
        var rows = new List<FieldRow>();
        using var rd = Query($"SELECT path, leaf, value FROM field_values {where} ORDER BY rowid LIMIT {limit}", p);
        while (rd.Read()) rows.Add(new FieldRow(rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        return (rows, matched, total);
    }

    /// <summary>LIKE 的通配符转义。用户给的过滤串里出现 <c>_</c> 是常事(field_path 之类)。</summary>
    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public (IReadOnlyList<DefRow> Rows, int Total) ListByType(
        string defType, ScopeFilter scope, int limit, int offset, string? className = null)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var conds = new List<string> { "d.def_type = @t COLLATE NOCASE" };
        if (className is { Length: > 0 })
        {
            p["@c"] = className;
            conds.Add("(d.class = @c COLLATE NOCASE OR d.class LIKE '%.' || @c COLLATE NOCASE)");
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);
        var total = Scalar($"SELECT COUNT(*) FROM defs d {where}", p);
        var rows = ReadDefs($"SELECT {DefColumns} FROM defs d {where} ORDER BY d.def_name LIMIT {limit} OFFSET {offset}", p);
        return (rows, total);
    }

    /// <summary>
    /// 一个 def_type 桶里实际有几种运行时 class。
    ///
    /// 游戏的 <c>GenDefDatabase.AllDefTypesWithDatabases()</c> 只产出「祖先链上没有非抽象 Def」
    /// 的类型,所以 <c>CreepJoinerAggressiveDef</c> 这种继承自具体类的子类型没有自己的库,
    /// 它的 def 全落在 <c>CreepJoinerBaseDef</c> 桶里。def_type 记的是桶,不是运行时类型 ——
    /// 桶异构时不把 class 摆出来,「列出所有 CreepJoinerAggressiveDef」就会得到
    /// 「这个类型不存在」,而缺席会被读成事实。
    /// </summary>
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

    /// <summary>名字不是 def_type 时的反查:有没有 def 的运行时 class 恰是它,在哪个桶下。</summary>
    public IReadOnlyList<(string DefType, int Count)> TypesHoldingClass(string className, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?> { ["@c"] = className };
        var conds = new List<string> { "(d.class = @c COLLATE NOCASE OR d.class LIKE '%.' || @c COLLATE NOCASE)" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT d.def_type, COUNT(*) c FROM defs d WHERE {string.Join(" AND ", conds)} " +
            "GROUP BY d.def_type ORDER BY c DESC, d.def_type", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
        return rows;
    }

    /// <summary>导出时被砍过字段的 def —— 「完整集」这个结论的唯一交叉验证入口。</summary>
    public (IReadOnlyList<(string DefName, string DefType, int Dropped)> Rows, int Total)
        TruncatedDefs(ScopeFilter scope, int limit)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string> { "d.fields_truncated > 0" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);
        var total = Scalar($"SELECT COUNT(*) FROM defs d {where}", p);
        var rows = new List<(string, string, int)>();
        using var rd = Query(
            $"SELECT d.def_name, d.def_type, d.fields_truncated FROM defs d {where} " +
            $"ORDER BY d.fields_truncated DESC, d.def_name LIMIT {limit}", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetString(1), rd.GetInt32(2)));
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
            rows.Add((ReadDefRow(rd), rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetString(11)));
        return (rows, total);
    }

    public (IReadOnlyList<(string Path, int Count)> Rows, int Total) FieldPathsForType(
        string defType, int limit, string? pathFilter = null)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var where = "WHERE d.def_type = @t COLLATE NOCASE";
        if (!string.IsNullOrEmpty(pathFilter))
        {
            p["@f"] = "%" + Escape(pathFilter!) + "%";
            where += " AND fv.path LIKE @f ESCAPE '\\'";
        }
        var total = Scalar(
            $"SELECT COUNT(*) FROM (SELECT DISTINCT fv.path FROM field_values fv JOIN defs d ON d.id = fv.def_id {where})", p);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT fv.path, COUNT(*) c FROM field_values fv JOIN defs d ON d.id = fv.def_id {where} " +
            $"GROUP BY fv.path ORDER BY c DESC, fv.path LIMIT {limit}", p);
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetInt32(1)));
        return (rows, total);
    }

    /// <summary>
    /// 后缀匹配的 WHERE 子句 —— <c>values</c> 与 <c>ValueCoverage</c> 必须用同一个,
    /// 否则「覆盖面」描述的就不是「值表」实际统计的那批行,而这正是最容易骗人的一种不一致。
    /// </summary>
    private static string SuffixWhere(string pathSuffix, ScopeFilter scope, Dictionary<string, object?> p)
    {
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
        return "WHERE " + string.Join(" AND ", conds);
    }

    /// <summary>
    /// 值表的归属面:这批值实际由哪些**完整路径**贡献,落在哪些 def 类型上,盖住多少 def。
    ///
    /// 没有这一层,后缀匹配会静默地把语义不同的路径混成一张表 —— 实测里
    /// <c>values damageAmountBase</c> 报出「-1 / 37 defs」,读起来像「到处都是 -1」,
    /// 实际上 37 条全是 <c>comps[N].damageAmountBase</c>(爆炸物),而问的那条
    /// <c>projectile.damageAmountBase</c> 压根不在表里。值本身没错,错的是省掉了它的产地。
    /// </summary>
    public (IReadOnlyList<(string Path, int Count)> Paths, int PathTotal,
            IReadOnlyList<(string DefType, int Count)> DefTypes, int DefsCovered)
        ValueCoverage(string pathSuffix, ScopeFilter scope, int limit, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(pathSuffix, scope, p);
        // 产地块必须描述**值表实际统计的那批行**。--type 只筛值表而不筛产地块,就会出现
        // 「表里只有 ThingDef,产地却说还有 HediffDef 和 AbilityDef」—— 那是最容易骗人的一种不一致。
        if (defType is { Length: > 0 }) { p["@cdt"] = defType; where += " AND d.def_type = @cdt COLLATE NOCASE"; }
        const string join = "FROM field_values fv JOIN defs d ON d.id = fv.def_id";

        var pathTotal = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT fv.path {join} {where})", p);
        var paths = new List<(string, int)>();
        using (var rd = Query($"SELECT fv.path, COUNT(*) c {join} {where} GROUP BY fv.path ORDER BY c DESC, fv.path LIMIT {limit}", p))
            while (rd.Read()) paths.Add((rd.GetString(0), rd.GetInt32(1)));

        var types = new List<(string, int)>();
        using (var rd = Query($"SELECT d.def_type, COUNT(DISTINCT d.id) c {join} {where} GROUP BY d.def_type ORDER BY c DESC, d.def_type LIMIT {limit}", p))
            while (rd.Read()) types.Add((rd.GetString(0), rd.GetInt32(1)));

        var covered = Scalar($"SELECT COUNT(DISTINCT d.id) {join} {where}", p);
        return (paths, pathTotal, types, covered);
    }

    /// <summary>某个 def 类型在本作用域下的 def 总数 —— 覆盖率的分母。</summary>
    public int CountDefsOfType(string defType, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var conds = new List<string> { "d.def_type = @t COLLATE NOCASE" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        return Scalar($"SELECT COUNT(*) FROM defs d WHERE {string.Join(" AND ", conds)}", p);
    }

    /// <summary>
    /// 用到某字段路径的那些 def 类型里,有几个 def 在导出时被砍过。
    ///
    /// 「快照里一共有 N 个 def 被砍过」这个数字挂在每一次反查上,就成了 00 论据 3 淘汰掉的
    /// 那种每次返回都带的免责声明 —— 说了等于没说,读的人只能把它抄进答案里当保留意见。
    /// 收窄到「与本次结果同类型的 def」之后,它不发声时「完整」才是无条件的。
    /// </summary>
    public int TruncatedDefsSharingPath(string pathSuffix, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(pathSuffix, scope, p);
        return Scalar(
            "SELECT COUNT(*) FROM defs d WHERE d.fields_truncated > 0 AND d.def_type IN (" +
            $"SELECT DISTINCT d.def_type FROM field_values fv JOIN defs d ON d.id = fv.def_id {where})", p);
    }

    /// <summary>某个 def 类型里有几个 def 在导出时被砍过。</summary>
    public int TruncatedDefsOfType(string defType)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        return Scalar("SELECT COUNT(*) FROM defs WHERE fields_truncated > 0 AND def_type = @t COLLATE NOCASE", p);
    }

    /// <summary>某个字段后缀在快照里到底存不存在 —— find 的零结果要靠它分流成因。</summary>
    public bool FieldPathExists(string pathSuffix, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(pathSuffix, scope, p);
        return Scalar($"SELECT EXISTS(SELECT 1 FROM field_values fv JOIN defs d ON d.id = fv.def_id {where})", p) != 0;
    }

    /// <summary>
    /// 按值反查字段路径:给一段文本,回答「哪些字段取到过含它的值」。
    ///
    /// 「别再 grep XML」拿走了一种能力,就必须给回等价的一种,否则唯一的出路是猜字段名 ——
    /// 而猜偏了,<c>--path</c> 会返回一个语法上完全正常、语义上完全错误的结果集。实测里
    /// 有人用 <c>fields FactionDef --path texture</c> 拿到唯一命中 <c>settlementTexturePath</c>
    /// 并准备据此下结论,真正管事的 <c>factionIconPath</c> 因为名字里没有 "texture" 被整个滤掉。
    /// </summary>
    public (IReadOnlyList<(string Path, string DefType, int Defs, string Sample)> Rows, int Total)
        PathsWithValue(string valueSubstring, ScopeFilter scope, int limit, bool exact = false)
    {
        // R11:`--exact` 原先在这条路上被接受、被忽略、输出与不加时一字不差 —— 三轮唯一一处
        // 既成的静默吞掉。它在这里是有意义的(整值相等 vs 含子串),所以实现它,而不是拒绝它:
        // 少一条要记的例外,对拼命令行的调用方就少一次踩空。
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();
        if (exact) { p["@v"] = valueSubstring; conds.Add("fv.value = @v COLLATE NOCASE"); }
        else { p["@v"] = "%" + Escape(valueSubstring) + "%"; conds.Add("fv.value LIKE @v ESCAPE '\\'"); }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        var where = "WHERE " + string.Join(" AND ", conds);
        const string join = "FROM field_values fv JOIN defs d ON d.id = fv.def_id";

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT fv.path, d.def_type {join} {where})", p);
        var rows = new List<(string, string, int, string)>();
        using var rd = Query(
            $"SELECT fv.path, d.def_type, COUNT(DISTINCT d.id) c, MIN(fv.value) {join} {where} " +
            $"GROUP BY fv.path, d.def_type ORDER BY c DESC, fv.path LIMIT {limit}", p);
        while (rd.Read())
            rows.Add((rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.IsDBNull(3) ? "" : rd.GetString(3)));
        return (rows, total);
    }

    public (IReadOnlyList<(string Value, int Count)> Rows, int Total) DistinctValues(
        string pathSuffix, ScopeFilter scope, int limit, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(pathSuffix, scope, p);
        if (defType is { Length: > 0 })
        {
            p["@dt"] = defType;
            where += " AND d.def_type = @dt COLLATE NOCASE";
        }

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT fv.value FROM field_values fv JOIN defs d ON d.id = fv.def_id {where})", p);
        var rows = new List<(string, int)>();
        using var rd = Query(
            $"SELECT fv.value, COUNT(*) c FROM field_values fv JOIN defs d ON d.id = fv.def_id {where} " +
            $"GROUP BY fv.value ORDER BY c DESC, fv.value LIMIT {limit}", p);
        while (rd.Read()) rows.Add((rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetInt32(1)));
        return (rows, total);
    }

    /// <summary>
    /// 一个 defName 的全部译文,连 <c>def_type</c> 一起回 —— 同名跨 def 类型时,挑哪些
    /// 归这个 def 的判断要在命令层做,因为那里才知道有没有同名歧义(R2)。
    ///
    /// <c>def_type</c> 可能为 null:收割自语言文件的行,注入 key 是 <c>DefName.field</c>,
    /// 不带类型,游戏自己也是按 defName 注入的 —— 那条译文属于哪个同名 def,在数据源里
    /// 就是不确定的,不是这里丢了信息。
    /// </summary>
    public IReadOnlyList<TranslationRow> Translations(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        var rows = new List<TranslationRow>();
        using var rd = Query(
            "SELECT def_name, def_type, path, translated, original, language, source_mod, origin FROM translations " +
            "WHERE def_name = @n COLLATE NOCASE ORDER BY origin, path", p);
        while (rd.Read())
            rows.Add(new TranslationRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
                rd.GetString(7)));
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

    // ---------- 继承层 ----------

    public int XmlNodeCount() => Scalar("SELECT COUNT(*) FROM xml_nodes");

    /// <summary>
    /// 一个名字在继承层里的全部落点。一个字符串可能同时是具名节点的 <c>Name=</c> 和
    /// 某个 def 的 <c>defName</c>(RimWorld 里常见:抽象基与同名成品),两边都要回,
    /// 否则「查不到」就掩盖了「查的是另一半」。
    /// </summary>
    public IReadOnlyList<XmlNodeRow> NodesNamed(string name)
        => ReadNodes("WHERE name = @n COLLATE NOCASE OR def_name = @n COLLATE NOCASE",
                     new Dictionary<string, object?> { ["@n"] = name });

    /// <summary>直接子节点 —— <c>ParentName=</c> 指向这个名字的。</summary>
    public IReadOnlyList<XmlNodeRow> NodesInheritingFrom(string parentName)
        => ReadNodes("WHERE parent_name = @p COLLATE NOCASE",
                     new Dictionary<string, object?> { ["@p"] = parentName });

    /// <summary>
    /// 具名节点的模糊候选池。零结果时用它分流:名字打错了,还是这个环境里真没有。
    /// </summary>
    public IReadOnlyList<string> AllXmlNodeNames()
    {
        var names = new List<string>();
        using var rd = Query("SELECT DISTINCT name FROM xml_nodes WHERE name IS NOT NULL AND name <> '' ORDER BY name");
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
    }

    private List<XmlNodeRow> ReadNodes(string where, IDictionary<string, object?> p)
    {
        var rows = new List<XmlNodeRow>();
        using var rd = Query(
            "SELECT def_type, name, parent_name, abstract, def_name, label, source_mod, source_file, patch_ops " +
            $"FROM xml_nodes {where} ORDER BY abstract DESC, name, def_name", p);
        while (rd.Read())
            rows.Add(new XmlNodeRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.GetInt32(3) != 0,
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.GetInt32(8)));
        return rows;
    }

    // ---------- 底层 ----------

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
