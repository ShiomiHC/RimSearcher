using Microsoft.Data.Sqlite;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

/// <summary>四种上限各自的截断。措辞不在这里,在 <c>ExportCap</c>。</summary>
public enum TruncationCause { Cap, Length, Depth, Items }

/// <summary>
/// 一个 def 的截断成因,四类分开。产地是导出器的 WalkState —— 那四个数在 0.13.0 之前
/// 共用一个计数器,于是「被截了」答得出、「为什么」答不出,而四种的出路完全不同。
///
/// **四个数的统计单元互不相同**,加起来那个总数因此不是任何一样东西的条数:
/// 条数上限是每碰一格加一(真的是「丢了几条」);值长度那一类**一条路径都没丢**,
/// 丢的是那一格值的后半截;深度与集合各是「一整棵没走的子树 / 一条没走完的列表」
/// 算一,而那底下有多少条谁都没数。<see cref="Missing"/> 是「没进索引的条数」的下界,
/// 它把值长度那一类排除在外。
/// </summary>
public sealed record TruncationCauses(int Cap, int Length, int Depth, int Items)
{
    public int Total => Cap + Length + Depth + Items;

    /// <summary>
    /// 至少有多少条路径**没进索引**。值长度不算 —— 那条路径就在表里,只是值不全,
    /// 把它加进「这个 def 一共有几条字段路径」会把一个已经数过的东西再数一遍。
    /// 深度与集合各只算一条,而它们底下的子树没数过,所以这是下界不是估计。
    /// </summary>
    public int Missing => Cap + Depth + Items;

    /// <summary>非零的那几类,按条数从多到少。全零时为空 —— 调用方据此闭嘴。</summary>
    public IEnumerable<(TruncationCause Cause, int Count)> Ranked()
        => new[]
           {
               (TruncationCause.Cap, Cap),
               (TruncationCause.Length, Length),
               (TruncationCause.Depth, Depth),
               (TruncationCause.Items, Items),
           }
           .Where(x => x.Item2 > 0)
           .OrderByDescending(x => x.Item2);
}

public sealed record DefRow(long Id, string DefType, string DefName, string? Label, string? Description,
                            string? SourceMod, string? SourceFile, bool Generated, string? Class,
                            int FieldsTruncated);

/// <summary>
/// 一条字段值。<paramref name="Default"/> 取 <see cref="Contract.DefaultState"/> 三值之一,
/// 不摊平成 bool —— 「没法比」并进任一边都会说出证不了的话。
/// </summary>
public sealed record FieldRow(string Path, string Leaf, string? Value, int Default);

/// <summary>
/// 完整性尾注要说的两件事:与本次结果同类型的 def 里被砍过的**有几个**,以及**是哪几个类型**。
///
/// 只回一个数不够 —— 尾注末尾那条续页命令得带上收窄开关,否则它指向的是全库,
/// 而尾注刚说的是其中某几个类型的一小批。
/// </summary>
/// <remarks>
/// 计数**按类型分开留着**,不是先加总再报一个数:声明印成一张表之后,每个类型一行
/// 各带自己的数,而合成一个总数就得靠句子把名单和数拼回去 —— 那句话的单复数、
/// 名单只有一项时该不该说「between them」之类,全是拼装出来的伪问题。
/// </remarks>
public sealed record TruncationScope(int Count, IReadOnlyList<(string Type, int Defs)> ByType)
{
    public IReadOnlyList<string> Types => [.. ByType.Select(t => t.Type)];
}

/// <summary>
/// <see cref="SnapshotDb.PathsWithValue"/> 怎么算「取到过这个值」。
/// </summary>
public enum ValueMatch
{
    /// <summary>值里含这段文本(<c>where</c> 的默认)。</summary>
    Substring,
    /// <summary>整个值与它相等(<c>where --exact</c>)。</summary>
    Exact,
    /// <summary>
    /// 值**就是**这个标识符,或者是它的限定形态(<c>RimWorld.CompShield</c> 之于
    /// <c>CompShield</c>)。
    ///
    /// 不用子串:<c>ludeon.rimworld</c> 会命中 <c>ludeon.rimworld.royalty</c>。
    /// 只有落空成因分流(<see cref="Commands.NameLookup"/>)用它 —— 那里要问的是
    /// 「这个名字就是它」,不是「这个名字出现在它里面」。
    /// </summary>
    Identifier,
}

/// <summary>
/// 一个字段路径怎么比。
///
/// 默认是**后缀**,按整段对齐:<c>graphicData.shaderType</c> 命中每一条以这两段结尾的
/// 路径,但够不着 <c>swimmingGraphicData.shaderType</c>。<paramref name="Exact"/> 把它
/// 钉成整条路径 —— 两者仍是两件事,后缀不限它前面还有几段。
///
/// 「不在 <c>.</c> 上对齐」是 2026-09-09 之前的语义(承上游 <c>find</c>),那时单段走
/// leaf 列等值(段对齐)而多段走裸 <c>%text</c>(不对齐),同一个缺省下两套判据。
///
/// 精确态里 <c>[]</c> 是任意下标的通配 —— 「这批命中横跨几种路径形状」那句印出来的
/// 就是带 <c>[]</c> 的形状,而它是读的人手上唯一一条现成的收窄依据,得能原样粘回来。
///
/// <paramref name="IndexTolerant"/> 让每个 <c>.</c> 前面**以及整条路径的末尾**可以有下标,
/// 于是 <c>statBases.stat</c> 够得着 <c>statBases[0].stat</c>,
/// <c>stuffProps.categories</c> 够得着 <c>stuffProps.categories[0]</c>(标量列表)。
/// 末尾那一处得单列:两种形状在真数据里都常见,只放中间那处的话后者照旧空。这一档**只在查空之后**开
/// (见 <c>where</c> 的救援分支):无条件放开等于把后缀匹配再放宽一级,而放宽会把
/// 原本各自成立的两条路径悄悄并成一张表。
/// </summary>
public readonly record struct PathQuery(string Text, bool Exact = false, bool IndexTolerant = false,
                                        IReadOnlyList<string>? Contains = null)
{
    public static implicit operator PathQuery(string text) => new(text);

    /// <summary>
    /// 这条路径值不值得试「少写了下标」。
    ///
    /// 自己写了下标(<c>[0]</c> 或 <c>[]</c>)的不试 —— 那是明确表态。点数上限是四:
    /// 每个点一个可选位、末尾再一个,组合数是 2^(N+1),而 N 再大下去这次救援自己
    /// 就比它救的查询贵了(32 条 LIKE 封顶)。语料里点分路径最长三段。
    /// </summary>
    public bool CanTolerateIndex =>
        !Exact && !IndexTolerant &&
        !Text.Contains('[', StringComparison.Ordinal) &&
        Text.Count(c => c == '.') is > 0 and <= 4;
}

/// <summary>
/// 一条译文。<paramref name="DefType"/> 为 null 表示**归属判不出来**:注入 key 只有
/// <c>DefName.field</c>,而这条又没有能认出类型的目录名(<c>DefInjected/&lt;类型&gt;/</c>),
/// 于是同名跨 def 类型时它归谁在数据源里就是不确定的。
///
/// <paramref name="SourceFileCount"/> 是这个 mod 里有几个语言文件写着这条同样的译文 ——
/// 版本目录(<c>1.4/</c>~<c>1.6/</c>)各铺一份是常态,入库时折成一行,只有这个数留着痕。
/// 两者对**运行时那一层恒为 null**(数据源是游戏内存,不是文件),对旧库也为 null。
/// </summary>
public sealed record TranslationRow(string DefName, string? DefType, string Path, string? Translated,
                                   string? Original, string? Language, string? SourceMod, string Origin,
                                   string? SourceFile = null, int? SourceFileCount = null,
                                   string? Key = null, string? KeyState = null, bool? Applied = null);

/// <summary>
/// 一个「可以被注入译文」的槽位。<paramref name="Path"/> 是下标式键串(<c>stages.0.label</c>),
/// <paramref name="SuggestedPath"/> 是把手式(<c>stages.observed_corpse.label</c>)——
/// 语言文件里两种都合法、都真注入得上,所以译者写的那一串得先归一到一种。
///
/// <paramref name="TranslationAllowed"/> 为假 = 这个字段不许注入译文
/// (<c>NoTranslate</c> / <c>Unsaved</c>,或它的某一级祖先带)。这一格是「谁都没译」与
/// 「白译也没用」的分界,而两者在译文表里同形 —— 都是没有行。
/// </summary>
public sealed record InjectionKeyRow(string DefName, string? DefType, string Path, string SuggestedPath,
                                     bool IsCollection, bool TranslationAllowed,
                                     bool FullListTranslationAllowed);

/// <summary>
/// 一条界面文案译文。<paramref name="Key"/> 是 <c>"X".Translate()</c> 里那个 X ——
/// 与任何 def 无关,所以这张表没有 def_id、也没有 def_type。
///
/// <paramref name="Placeholder"/> = 语言包里有这个 key 但值是占位:它实际显示的是英文,
/// 而在表里与真译文同形。<paramref name="Origin"/> 分层同 translations —— runtime 那一层
/// 是游戏最终用的那一句(覆盖冲突的赢家),harvest 两层只说「磁盘上存在」。
/// </summary>
public sealed record KeyedRow(string Key, string? Translated, string? Original, string? Language,
                              string? SourceFile, int SourceLine, string? SourceMod,
                              bool Placeholder, string Origin, int? SourceFileCount = null);

/// <summary>
/// 继承层的一行:XML 里一个带 <c>Name=</c> / <c>ParentName=</c> / <c>Abstract=</c> 的节点。
/// <paramref name="PatchOps"/> 是有多少条 PatchOperation 的 xpath 点了这个 Name —— 这一层
/// 是打补丁**之前**的原文。
/// </summary>
public sealed record XmlNodeRow(string DefType, string? Name, string? ParentName, bool Abstract,
                                string? DefName, string? Label, string? SourceMod, string? SourceFile,
                                int PatchOps,
                                int PatchOpsDefName = 0,
                                int PatchOpsLabel = 0);

/// <summary>
/// 经济面的一行。**每个可空的数都是「算不出」而不是「算出来是零」** —— 两者在这一层处处
/// 并存,合并任何一对都会让消费侧把算不出的那些统计进分布。
/// </summary>
public sealed record EconomyRow(long Id, string DefName, string? Label, string? Category, string? Mod,
                                double? MarketValue, bool Producible, bool MadeFromStuff,
                                bool IsWeapon, bool IsApparel, bool MarketValueDefined,
                                string CalcState, double? CalculatedMarketValue,
                                double? CostToMake, double? Profit, double? ProfitRate,
                                double? WorkToProduce, string? CostList,
                                /// <summary>
                                /// 成本表那一支由难度开关决定的变体的开关名,<c>null</c> = 没有变体。
                                /// 有值就意味着**上面那几个成本数是非变体那支** —— 导出时没有
                                /// storyteller,那个条件判不了。
                                /// </summary>
                                string? CostDifficultyVar, bool CostDifficultyInverted,
                                double? ChainEndShare, double? CostDeep, double? ProfitDeep);

/// <summary>成本链的一项。<paramref name="ChainEnd"/> = 它自己没有 recipeMaker。</summary>
public sealed record EconomyChainRow(string ThingDef, int Count, double? UnitValue, bool ChainEnd);

/// <summary>能产出同一个物的一个配方。同一个物有多行 = 推算价有加载顺序依赖。</summary>
public sealed record EconomyRecipeRow(string DefName, int ProductCount, double? WorkAmount,
                                      bool SelfReferential);

/// <summary>
/// 快照库的只读查询面。所有带上限的查询都同时回传总数 —— 三态文法要求调用方能区分
/// 「就这么多」与「被截了」(上游全 CLI 返回裸数组,LIMIT 命中与否不可区分)。
/// </summary>
public sealed class SnapshotDb : IDisposable
{
    private readonly SqliteConnection _db;

    public string Path { get; }
    public ExportMeta Meta { get; }
    public IReadOnlyList<ModRef> Mods { get; }

    /// <summary>
    /// 建这份快照时扫了几个 mod 根目录去收割磁盘上的语言文件。<c>false</c> 就是一个都没扫,
    /// 于是这份库里「磁盘上没有这一句」这句话根本说不出口 —— 见
    /// <see cref="SnapshotSchema.MetaKeyHarvestedRoots"/>。
    /// </summary>
    public bool Harvested { get; }

    /// <summary>
    /// <see cref="Harvested"/> 为假时的成因(<see cref="SnapshotSchema.MetaKeyTranslationsHarvest"/> 的三个值);
    /// <c>null</c> = 老库没记,那时成因只能按现机配置猜。
    /// </summary>
    public string? TranslationsHarvest { get; }

    /// <summary>建库时读的导出文件名(不带目录);<c>null</c> = 别的工具写的库。重导入的命令拿它填参数。</summary>
    public string? SourceFile { get; }

    /// <summary>
    /// 导出那一刻各 mod 的 Defs/Patches 指纹。<c>null</c> = **这份快照没量过** ——
    /// 见 <see cref="SnapshotSchema.MetaKeyContent"/>,那是一条判据的缺席,不是「没变」。
    /// </summary>
    public ContentScan? Content { get; }

    /// <summary>
    /// 经济面量没量成:<c>ok</c> / <c>skipped</c> / <c>unavailable</c>,而 <c>null</c> 是
    /// **第四态** —— 这份库建于经济面进导出之前,它对这件事没有资格回答。
    ///
    /// 这一条是经济面的在场判据,<c>SELECT COUNT(*) FROM economy</c> 不是:计数把四种成因
    /// 压成同一个零,而其中一种(量过了、这个名单下确实没有可生产物)是完整的肯定回答。
    /// </summary>
    public string? EconomyState { get; }

    /// <summary><see cref="EconomyState"/> 为 unavailable 时点名缺了什么,原样端出。</summary>
    public string? EconomyError { get; }

    /// <summary>
    /// 导出时每一层各花了多少毫秒,层名 → 毫秒。<c>null</c> = 那次导出早于计时
    /// (0.11.0),**不是「零毫秒」**。层名见 <see cref="IntermediateFormat.TimingKeys"/>。
    /// </summary>
    public IReadOnlyDictionary<string, long>? ExportTimings { get; }

    /// <summary>
    /// 建库时每一段各花了多少毫秒,段名 -> 毫秒。<c>null</c> = 那次导入早于计时,不是零。
    /// 与 <see cref="ExportTimings"/> 分开摆,因为两侧的量级实测差一个数量级。
    /// </summary>
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

    /// <summary>
    /// 「这个路径上没有库」的措辞,产地唯一。校验先于开库发生(见
    /// <see cref="Snapshot.SnapshotCatalog.ValidateExplicit"/>),两处各写一句就会漂。
    /// </summary>
    public static string NoDatabaseAt(string path)
        => $"No snapshot database at '{path}'. Run 'rimsearcher snapshot list' to see what is registered, " +
           "or 'rimsearcher export' to produce one from the game.";

    /// <summary>
    /// 读侧的连接参数。库是只读的、一次命令用完就关,SQLite 的默认值是按「小库 + 长驻进程」
    /// 定的,两条都不成立:默认 2MB 页缓存放不下 1GB 库的一次全表扫,而 temp_store 的默认
    /// 会把 ORDER BY 的中间结果写到磁盘。
    ///
    /// 失败一律咽掉:这三条**只影响快慢,不影响答案**,而 Open 是所有查询的必经之路 ——
    /// 老库、别的 SQLite 编译选项、只读介质上任一条 PRAGMA 不被接受,都不该让一次查询崩掉。
    /// </summary>
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

        // Pooling = false:池化连接 Dispose 之后**文件仍开着**,而快照文件随后会被
        // 轮转(SnapshotRetention.Install 要 Move/Delete 它),在 Windows 上开着就动不了。
        // 唯一的补救 SqliteConnection.ClearAllPools() 是**进程级**的:它会把别的线程
        // 正在用的连接一起处理掉,于是同进程里并发跑的另一次查询在自己的连接上收到
        // ObjectDisposedException。不入池就没有这两件事 —— 本工具一条命令只开一两个
        // 连接,池本来也省不下什么。
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
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

        // 版本闸在上面,这一格必然写过 —— 读不出来只可能是别的工具伪造的库。
        var harvested = meta.TryGetValue(SnapshotSchema.MetaKeyHarvestedRoots, out var hr) &&
                        int.TryParse(hr, out var roots) && roots > 0;
        meta.TryGetValue(SnapshotSchema.MetaKeyTranslationsHarvest, out var translationsHarvest);
        meta.TryGetValue(SnapshotSchema.MetaKeySourcePath, out var sourceFile);

        var content = meta.TryGetValue(SnapshotSchema.MetaKeyContent, out var cf)
            ? ContentScan.FromJson(cf)
            : null;

        // 缺席照实传 null。给它一个兜底字符串会把「这份库没资格回答」变成一个看着像答案的值。
        meta.TryGetValue(SnapshotSchema.MetaKeyEconomyState, out var econState);
        meta.TryGetValue(SnapshotSchema.MetaKeyEconomyError, out var econError);

        // 同一道缝:没这一格就传 null,不合成一张全零的表。
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

        // 排序:名字整体命中 > 有 label > 名字前缀命中 > bm25 相关度 > 名字短的在前。
        // 列权重让 def_name 压过 description。「有 label」压在前缀之上:带 label 的是玩家
        // 看得见的东西,不带的是 EffecterDef/SoundDef 一类基础设施,而前缀只说明「名字长得像」。
        var order = "ORDER BY (d.def_name = @q COLLATE NOCASE) DESC, " +
                    "(d.label IS NOT NULL AND d.label != '') DESC, " +
                    "(d.def_name LIKE @qe || '%' ESCAPE '\\' COLLATE NOCASE) DESC, " +
                    "bm25(defs_fts, 10.0, 4.0, 1.0, 3.0), LENGTH(d.def_name), d.def_name";
        var rows = ReadDefs($"SELECT {DefColumns} {from} {order} LIMIT {limit} OFFSET {offset}", p);
        return (rows, total);
    }

    /// <summary>
    /// 名字里含 <paramref name="query"/>、但 FTS **没**匹配上的 def 名。
    ///
    /// FTS 分词按分隔符与驼峰词首切,查询词落在名字中段就漏(`VoidNode` 找不到
    /// `MonolithGleamingVoidNode`)。减法在这里做而不是在调用方按已显示的行去重 ——
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

    /// <summary>
    /// 译文**原文那一侧**含这段文本的 def 名。
    ///
    /// FTS 只索引 translated —— 一份中文快照上,每个 def 的英文原名都在 translations.original
    /// 里躺着却一个也搜不到。这条把另一半接上。
    ///
    /// 走 LIKE 不走 FTS:为 original 建索引要改 schema、逼所有人重新导入一次,
    /// 而这条扫描只在**零结果时**才跑。
    /// 连接只按 def_name:译文的 def_type 来自 DefInjected 的目录名(XML 根元素),
    /// 而 defs.def_type 是运行时的桶名,两者对不上是常态,拿它做条件会漏。
    /// </summary>
    public IReadOnlyList<string> NamesByTranslationOriginal(string query, ScopeFilter scope, string? defType)
    {
        var p = new Dictionary<string, object?> { ["@q"] = "%" + Escape(query) + "%" };
        var conds = new List<string> { "t.original LIKE @q ESCAPE '\\'" };
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var names = new List<string>();
        using var rd = Query(
            "SELECT DISTINCT d.def_name FROM translations t JOIN defs d ON d.def_name = t.def_name " +
            $"WHERE {string.Join(" AND ", conds)} ORDER BY LENGTH(d.def_name), d.def_name", p);
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
    }

    /// <summary>
    /// 按名字取行,顺序照传入的名次排(模糊打分的排序不能被 SQL 打乱)。
    ///
    /// 一个 defName 带**几行**是常态:Firefoam 既是 ThingDef 又是 StatDef,mod 覆盖原版时
    /// 同理。同名的几行**都出** —— 只留一行的输出与正确输出逐字同形,读的人无从知道自己
    /// 少看了一个 def。
    ///
    /// <c>Total</c> 数的是行不是名字,而且在截断**之前**数 —— 页脚那句「N of M」的 M
    /// 若按名字算,同名处就会比表里的行还少。
    /// </summary>
    public (IReadOnlyList<DefRow> Rows, int Total) ByNames(IReadOnlyList<string> names, int limit)
    {
        if (names.Count == 0) return ([], 0);
        var p = new Dictionary<string, object?>();
        var keys = new List<string>();
        for (var i = 0; i < names.Count; i++) { p["@n" + i] = names[i]; keys.Add("@n" + i); }
        var where = $"WHERE d.def_name IN ({string.Join(",", keys)})";
        var rows = ReadDefs($"SELECT {DefColumns} FROM defs d {where}", p);

        var byName = rows.GroupBy(r => r.DefName, StringComparer.Ordinal)
                         .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var ordered = new List<DefRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in names)
            if (seen.Add(n) && byName.TryGetValue(n, out var group)) ordered.AddRange(group);

        var total = ordered.Count;
        if (ordered.Count > limit) ordered.RemoveRange(limit, ordered.Count - limit);
        return (ordered, total);
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
    /// 一个 def 类型的全部 def,按 def_name 排。<c>get --type T</c> 不给名字那一路的产地;
    /// 不分页 —— 那一路要的就是整类,挑子集是 <c>list</c> 的活。
    /// </summary>
    public IReadOnlyList<DefRow> DefsOfType(string defType)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        return ReadDefs($"SELECT {DefColumns} FROM defs d WHERE d.def_type = @t COLLATE NOCASE " +
                        "ORDER BY d.def_name, d.id", p);
    }

    /// <summary>
    /// 一个 def 的字段。<paramref name="pathFilter"/> 非空时只留路径含该子串的行。
    /// <c>Matched</c> 是过滤后的总数,<c>Total</c> 是这个 def 的字段总数 —— 两个都给,
    /// 调用方才分得清「过滤掉了多少」和「被 limit 截了多少」。
    /// </summary>
    /// <summary>
    /// <paramref name="includeDefaults"/> 为 false 时,与 C# 声明默认值无从区分的那些行
    /// **不进 Rows**,但照样计进 <c>Defaulted</c>。滤掉的判据只认
    /// <see cref="Contract.DefaultState.Same"/>:「没法比」的一律留下,少省一点篇幅换
    /// 「不会有值凭空消失」。
    /// </summary>
    public (IReadOnlyList<FieldRow> Rows, int Matched, int Total, int Defaulted,
            IReadOnlyList<string> MatchedPaths) Fields(
        long defId, int limit, IReadOnlyList<string>? pathFilters = null, bool includeDefaults = true,
        bool exactPath = false)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        // defName 不是这个 def 的一个字段,是它的身份 —— 值已经在表上方的 def_name 行里,
        // 而它那一行的另两格逐 def 恒定(五个快照 9.7 万行 is_default 全 0;xml 那格要么
        // 与 source 行同一句话,要么与同表每一行同带 +patch)。整列同值的折叠按列走,
        // 够不着单独一行,于是这一行在这里就不算字段。Total 一律不数它:
        // 「the def does have N fields」与「Drop --path-contains to see them」看到的
        // 必须是同一个 N。点名过滤(--path-contains def…)能把它召回来 —— 调用方点了名
        // 的东西不许消失,与 --defaults 同一条规矩。
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

        // 命中的**全部**路径,不受 limit 与 includeDefaults 影响 —— 「其中几条是整段命中」
        // 必须在截断之前数完,否则同一个 --path-contains 换个 --limit 就换一句结论。
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

    /// <summary>
    /// 这个 def 上有几个字段**把这段文本当值**装着。只服务一句话:<c>--path-contains</c> 筛空时,
    /// 「路径里没有它」与「它其实是个值」是两种成因,而后者可以当场算出来。
    /// </summary>
    public int ValueHits(long defId, string text)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId, ["@v"] = "%" + Escape(text) + "%" };
        return Scalar($"SELECT COUNT(*) FROM field_values fv WHERE fv.def_id = @id AND "
                      + ValueIs("value LIKE @v ESCAPE '\\'"), p);
    }

    /// <summary>LIKE 的通配符转义。用户给的过滤串里出现 <c>_</c> 是常事(field_path 之类)。</summary>
    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// 一段路径文本变成 LIKE 模式:先转义,再把 <c>[]</c> 放成下标通配。
    /// **凡是拿路径去比的地方都走这里** —— <c>[]</c> 是 <see cref="Search.PathSegments.Shape"/>
    /// 印出去的规范写法(<c>statBases[7].stat</c> → <c>statBases[].stat</c>),而印出去的形状
    /// 原样粘回哪条命令都得能跑。此前只有位置参数那条路(<c>where</c> / <c>values</c>)认它,
    /// 三条 <c>--path-contains</c> 把它当字面量,于是恒空 —— 而恒空与「这个字段不存在」同形。
    ///
    /// 顺序不能倒:转义在前,路径里真有的 <c>%</c> 才不会被当成通配。
    /// 字面含 <c>[]</c> 的路径实测一条都没有(十五个库的路径字典各 0、值字典各 0、
    /// 类型侧 <c>type_field_paths</c> 也是 0;含 <c>[</c> 的有 2.4 万~15 万),旧行为无损。
    /// 值那一侧不走这里:<c>[]</c> 是路径文法,值里的 <c>[]</c> 该当字面量。
    /// </summary>
    private static string PathLike(string s)
        => Escape(s).Replace("[]", "[%]", StringComparison.Ordinal);

    /// <summary>
    /// <c>--path-contains</c> 那一族的 LIKE 模式,唯一产地。<paramref name="exact"/> 是
    /// <c>--exact-path</c>:整条相等,而不是子串。
    ///
    /// 抽出来是因为这个模式在五处各拼一遍(<see cref="Fields"/> / <see cref="FieldPathsForType"/> /
    /// <see cref="TypeDefsWithPath"/> / <see cref="TypeDeclaredPaths"/> / <see cref="KinCte"/>),
    /// 而它们是五段互不相干的 SQL —— 一个开关接上其中几处、漏掉另几处时,漏掉的那几处
    /// 印出来的数与表里的行对不上账,而两边都不报错。
    ///
    /// 整条那一支仍走 LIKE 而不是 <c>=</c>:<c>[]</c> 是下标通配,<see cref="PathLike"/> 已经
    /// 把它翻成 <c>[%]</c>,换成等值比会让每个带 <c>[]</c> 的写法恒空。
    /// </summary>
    private static string PathFilterLike(string text, bool exact)
        => exact ? PathLike(text) : "%" + PathLike(text) + "%";

    /// <summary>
    /// <c>where</c> / <c>values</c> 的 <c>--path-contains</c> 那一条 —— 与 <c>get</c> 那族
    /// 逐字同一个谓词(路径含这段文本),给多个是并集,措辞也是同一句
    /// 「Repeat it to widen the selection」。
    ///
    /// 参数名带前缀,免得与位置参数那一支的 <c>@path</c> 撞:两者在同一条 WHERE 里共存,
    /// 而字典里同名后写的会把先写的顶掉 —— 顶掉之后两个条件都还在,只是比的是同一段文本。
    /// </summary>
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
        // 筛在 LIMIT **之前**发生 —— 这正是它存在的理由。管道接 grep 是筛在之后,
        // 而那会连同「25 of 167 defs」那句计数一起吃掉,于是「这一页里没有」与
        // 「整个快照里没有」在一个空结果上完全同形。
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

    /// <summary>
    /// 一个 def_type 桶里实际有几种运行时 class。
    ///
    /// 游戏的 <c>GenDefDatabase.AllDefTypesWithDatabases()</c> 只产出「祖先链上没有非抽象 Def」
    /// 的类型,所以 <c>CreepJoinerAggressiveDef</c> 这种继承自具体类的子类型没有自己的库,
    /// 它的 def 全落在 <c>CreepJoinerBaseDef</c> 桶里。def_type 记的是桶,不是运行时类型。
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

    /// <summary>导出时被砍过字段的 def —— 「完整集」这个结论的唯一交叉验证入口。</summary>
    public (IReadOnlyList<(string DefName, string DefType, int Dropped)> Rows, int Total)
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
        if (defName is { Length: > 0 }) { p["@dn"] = defName; conds.Add("d.def_name = @dn COLLATE NOCASE"); }
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
    ///
    /// <c>Total</c> 与 <c>Defs</c> 是**两个数**,不是一个数的两种说法:一行是一个
    /// (def, 路径)对,而同一个 def 可以在多条路径上取到同一个值 ——
    /// <c>where capacity Consciousness</c> 是 155 行、80 个 def(<c>AlcoholHigh</c> 一个
    /// 就占四行)。分页数的是行,而「这个值一共被几个 def 用着」问的是后者;
    /// 拿其中一个去回答另一个,差得下来一倍。
    /// </summary>
    public (IReadOnlyList<(DefRow Def, string Path, string? Value, int Default)> Rows, int Total, int Defs)
        FindByField(PathQuery path, string? value, bool exact, ScopeFilter scope, int limit, int offset = 0,
                    string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            if (exact) { p["@v"] = value; conds.Add(ValueIs("value = @v COLLATE NOCASE")); }
            else { p["@v"] = "%" + Escape(value) + "%"; conds.Add(ValueIs("value LIKE @v ESCAPE '\\'")); }
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        // 条件进 conds 而不是事后滤行:Total 与 Defs 两个计数与行表共用同一个 FROM,
        // 在这里加一条,三个数一起收窄。滤行的话表变了而两个数没变。
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var where = "WHERE " + string.Join(" AND ", conds);
        var from = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where}";
        // 两个计数一次扫出来。分两条 SELECT 时同一个 FROM 要走两遍,而点分路径上的
        // FROM 是一次全表扫(path 列无索引)—— 口径不变,只是不扫第二遍。
        int total = 0, defs = 0;
        using (var cnt = Query($"SELECT COUNT(*), COUNT(DISTINCT d.id) {from}", p))
            if (cnt.Read()) { total = cnt.GetInt32(0); defs = cnt.GetInt32(1); }

        var rows = new List<(DefRow, string, string?, int)>();
        // 一个 def 匹配多条路径时一行一条,所以 def_name 排不完 —— 必须有决胜列。
        // 少了它,行序是「优化器这次选了哪条索引」的副产品:leaf 从大表挪到路径字典之后
        // 驱动索引从 idx_fv_leaf_nc 换成 idx_fv_pathid,同一个 def 的几行顺序就翻了。
        // 而这条查询**带 LIMIT/OFFSET** —— 非确定的序上翻页会漏行,也会重复。
        using var rd = Query(
            $"SELECT {DefColumns}, {FvPath}, {FvValue}, fv.is_default {from} " +
            $"ORDER BY d.def_name, {FvPath}, fv.rowid LIMIT {limit} OFFSET {offset}", p);
        while (rd.Read())
            rows.Add((ReadDefRow(rd), rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetString(11), rd.GetInt32(12)));
        return (rows, total, defs);
    }

    /// <summary>
    /// 同一次 <see cref="FindByField"/> 的命中横跨几种**路径形状**(下标归一后的路径),
    /// 每种几条。数在分页之前数,所以它说的是整个结果集,不是这一页。
    ///
    /// 后缀匹配会把语义不同的路径混进同一个结果集(`where stat Mass` 的上千行里混着一行
    /// <c>statFactors[N].stat</c>,其余是 <c>statBases[N].stat</c>),拿它做集合差时
    /// 那一行是**静默假阴性**。
    ///
    /// 归一到形状而不是列出原始路径:`statBases[0..109].stat` 一百多条各列一遍是噪音,
    /// 而「两种形状」才是做集合运算的人要判的那件事。
    /// </summary>
    public IReadOnlyList<(string Shape, int Count)> FindPathShapes(
        PathQuery path, string? value, bool exact, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            if (exact) { p["@v"] = value; conds.Add(ValueIs("value = @v COLLATE NOCASE")); }
            else { p["@v"] = "%" + Escape(value) + "%"; conds.Add(ValueIs("value LIKE @v ESCAPE '\\'")); }
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

    /// <summary>
    /// 同一次 <see cref="FindByField"/> 的命中里,这些 def 分别由哪些 mod 声明 —— 每个 mod
    /// 各带几个 def。
    ///
    /// 数在分页之前(理由同 <see cref="FindPathShapes"/>):这句话要判的是整个结果集跨了几家,
    /// 而首页二十五行按 def 名排序,常常只落在一两家上。
    ///
    /// 按 <c>DISTINCT d.id</c> 数:同一个 def 在多条路径上命中时按行数会数大。
    /// 清单本身不设上限 —— 快照里 mod 总数是几十的量级,取全了也不贵,而调用方要判的
    /// 「这几家里有没有官方」经不起截断。
    /// </summary>
    public IReadOnlyList<(string Mod, int Defs)> FindModsHolding(
        PathQuery path, string? value, bool exact, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            if (exact) { p["@v"] = value; conds.Add(ValueIs("value = @v COLLATE NOCASE")); }
            else { p["@v"] = "%" + Escape(value) + "%"; conds.Add(ValueIs("value LIKE @v ESCAPE '\\'")); }
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

    /// <summary>
    /// 同一次 <see cref="FindByField"/> 的命中里,有几个 def 是加载期由 C# 造出来的。
    ///
    /// 与 <see cref="FindPathShapes"/> 同理,**数在分页之前** —— 首页二十五行按 def 名
    /// 排序,而 ImpliedDefs 的名字扎堆在 <c>Meat_</c> / <c>Corpse_</c> / <c>Blueprint_</c>
    /// 这几处,首页往往一个都碰不上;不给 <c>--limit</c> 灌进脚本的人拿到的却是全集。
    /// 页内口径会让这句话恰好在最该出声的那次哑火。
    ///
    /// 按 <c>DISTINCT d.id</c> 数:同一个 def 可以在多条路径上命中(后缀匹配一放开就常有),
    /// 按行数会数大。
    /// </summary>
    public (IReadOnlyList<string> Names, int Total) FindGeneratedDefs(
        PathQuery path, string? value, bool exact, ScopeFilter scope, int limit, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string> { "d.generated = 1" };

        PathCondition(path, p, conds);

        if (value is { Length: > 0 })
        {
            if (exact) { p["@v"] = value; conds.Add(ValueIs("value = @v COLLATE NOCASE")); }
            else { p["@v"] = "%" + Escape(value) + "%"; conds.Add(ValueIs("value LIKE @v ESCAPE '\\'")); }
        }
        if (scope.SqlPredicate("d.source_mod", p) is { } sc) conds.Add(sc);
        if (defType is { Length: > 0 }) { p["@dt"] = defType; conds.Add("d.def_type = @dt COLLATE NOCASE"); }

        var where = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id " +
                    $"WHERE {string.Join(" AND ", conds)}";
        var total = Scalar($"SELECT COUNT(DISTINCT d.id) {where}", p);

        var names = new List<string>();
        using var rd = Query(
            $"SELECT DISTINCT d.def_name {where} ORDER BY d.def_name LIMIT {limit}", p);
        while (rd.Read()) names.Add(rd.GetString(0));
        return (names, total);
    }

    /// <summary>
    /// 同类型的 def 里,有几个在含这段文本的路径上有值 —— 以及那摊路径有几条。
    ///
    /// def 数按 <c>DISTINCT def_id</c> 数,不是把每条路径的行数相加:同一个 def 往往在多条
    /// 路径上都有值(<c>ingestible.*</c> 在语料里就有七十几条),相加得到的数会大过这个类型
    /// 的 def 总数。
    /// </summary>
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

    /// <summary>
    /// <c>WholeSegment</c> 是 <c>Total</c> 里有几条把 <paramref name="pathFilter"/> 用作**完整的一段**。
    /// 子串匹配不留痕:不拆开这两档,「你要的那个字段根本不在」与「它在,旁边还有一堆别的」
    /// 逐字同形。数在分页**之前**数。
    /// </summary>
    /// <param name="pathFilters">
    /// 多个一起给是**并集**(声明层的措辞就是 "Repeat it to widen the selection")。
    /// </param>
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
                // 下面六条整段模式全建在 e 上,所以 `[]` 的通配得在这里一次放完 ——
                // 分两次的话「命中」与「整段命中」会按两套文法数。
                var e = PathLike(filters[i]);
                p[$"@f{i}"] = PathFilterLike(filters[i], exactPath);
                any.Add(PathIs($"path LIKE @f{i} ESCAPE '\\'"));

                // 「完整的一段」有六种落法:整条就是它,或者它是开头段 / 中间段 / 结尾段,
                // 后面接 `.` 或 `[`。下标不算段的一部分 —— comps[3] 里那个 comps 就是完整的一段。
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

    /// <summary>
    /// 后缀匹配的 WHERE 子句 —— <c>values</c> 与 <c>ValueCoverage</c> 必须用同一个,
    /// 否则「覆盖面」描述的就不是「值表」实际统计的那批行。
    /// </summary>
    /// <summary>
    /// 「取到过这个值」的谓词。抽出来是因为截断尾注要按**同一批 def** 收窄。
    /// </summary>
    // static 不得:ValueIs 要读这份库的形状(同 PathCondition / SuffixWhere)。
    private string ValueWhere(string value, ValueMatch match, ScopeFilter scope,
                              Dictionary<string, object?> p, string? defType = null)
    {
        var conds = new List<string>();
        switch (match)
        {
            case ValueMatch.Exact:
                p["@v"] = value;
                conds.Add(ValueIs("value = @v COLLATE NOCASE"));
                break;
            case ValueMatch.Identifier:
                p["@v"] = value;
                p["@vq"] = "%." + Escape(value);
                conds.Add(ValueIs("value = @v COLLATE NOCASE OR value LIKE @vq ESCAPE '\\'"));
                break;
            default:
                p["@v"] = "%" + Escape(value) + "%";
                conds.Add(ValueIs("value LIKE @v ESCAPE '\\'"));
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

    /// <summary>
    /// 路径条件的唯一产地。行表与计数表分开建条件的话,同一个 <c>--exact-path</c> 会让
    /// 「这一页几条」与「一共几条」数的是两个集合。
    /// </summary>
    private void PathCondition(PathQuery path, Dictionary<string, object?> p, List<string> conds)
    {
        // 第二个槽,与位置参数按 AND 合:位置参数说「结尾是什么」,这个说「上面某处有什么」。
        // 两个条件正交,一个槽装不下 —— `thingDefs` ∧ `filter` 在 baseline 上是 13 种形状,
        // 中间几段各不相同,多段后缀写不出来。
        //
        // 挂在这里而不是各调用点:PathCondition 有六个调用方(行、计数、覆盖面、截断范围…),
        // 只接上几处的话,「这一页几条」与「一共几条」数的就是两个集合。
        if (path.Contains is { Count: > 0 } has)
            conds.Add(PathContainsClause(has, p));

        if (path.Exact)
        {
            // `[]` 是下标通配(判据在 PathLike)。整条相等那一支要先看有没有 `[]`:
            // 没有就走 `=`,那条走得到索引。
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
            // 每个可选位「有下标」与「没下标」各是一条 LIKE,取并集。LIKE 里没有可选段,
            // 单条模式表达不出「这里可以有下标」,所以只能把组合摊开 —— 位数在
            // CanTolerateIndex 那边卡着(2^5 = 32 条封顶)。
            // 可选位 = 每个点前面一个,加上整条路径的末尾一个(最高位)。
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
            // `[]` 在这一支也是下标通配(判据在 PathLike)。两件事正交:`--exact-path`
            // 管「整条还是后缀」,`[]` 管「下标不限」—— 不写成「含 [] 就当 --exact-path」,
            // 那救不了自己写的一段尾巴(`filter.thingDefs[]` 不是整条路径,加了旗照样空)。
            //
            // 后缀在**段边界**上对齐(2026-09-09):要么整条就是它,要么它前面紧挨着一个 `.`。
            // 此前这里是裸的 `%text`,于是 `graphicData.color` 连
            // `alternateGraphics[0].dessicatedGraphicData.color` 一起收走 —— 49 条里只有
            // 7 条是问的那个字段。而下面单段那一支走 leaf 列等值,**本来就是段对齐的**:
            // 同一个缺省下两套判据,严格程度还是反的 —— 把边界写出来的那一半反而更松。
            //
            // 真实语料 224 种多段写法里 11 种因此换答案(折成调用 72 次)。跨段边界的纯文本
            // 后缀就此没有了:那 11 种没有一种看得出是有意要它,形态全是「我要这一条,
            // 结果多来了几条」。
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

    /// <summary>
    /// 值表的归属面:这批值实际由哪些**完整路径**贡献,落在哪些 def 类型上,盖住多少 def。
    ///
    /// 没有这一层,后缀匹配会静默地把语义不同的路径混成一张表 ——
    /// <c>values damageAmountBase</c> 报出的「-1 / 37 defs」全是
    /// <c>comps[N].damageAmountBase</c>(爆炸物),而问的那条
    /// <c>projectile.damageAmountBase</c> 压根不在表里。
    /// </summary>
    public (IReadOnlyList<(string Path, int Count)> Paths, int PathTotal,
            IReadOnlyList<(string DefType, int Count)> DefTypes, int DefsCovered)
        ValueCoverage(PathQuery path, ScopeFilter scope, int limit, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(path, scope, p);
        // 产地块必须描述**值表实际统计的那批行**。--type 只筛值表而不筛产地块,就会出现
        // 「表里只有 ThingDef,产地却说还有 HediffDef 和 AbilityDef」。
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

    /// <summary>
    /// 这些 (路径, 值) 里,哪些在同类型的 def 上是**大多数都有的那个值**,以及有多少个。
    ///
    /// 答的是 <c>code_default</c> 那一列答不了的问题:<c>no</c> 只证得了「与刚 new 出来的
    /// 实例不同」,读的人却一律读成「有人给这个 def 挑了这个值」,而「谁挑的」在这份快照里
    /// 没有产地(见 shared_values 的建表注释)。
    ///
    /// 走预计算表 —— 现算要 0.5s,而 <c>get</c> 整条是 0.156s。
    /// </summary>
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
    /// 「快照里一共有 N 个 def 被砍过」这个数字挂在每一次反查上,就成了恒真的免责声明 ——
    /// 说了等于没说。收窄到「与本次结果同类型的 def」之后,它不发声时「完整」才是无条件的。
    /// </summary>
    /// <param name="defType">
    /// 调用方自己已经把结果收到这一个类型上时,尾注也得跟着收 —— 否则会在一张与它无关的表
    /// 下面挂一个完整性告警,而**一旦有一句披露被发现是过期的,其余每一句都要被重新审视**。
    /// </param>
    public TruncationScope TruncatedDefsSharingPath(PathQuery path, ScopeFilter scope, string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        return TruncatedAmong(SuffixWhere(path, scope, p), scope, p, defType);
    }

    /// <summary>
    /// 同上,但这批 def 是「取到过某个值」而不是「有某条路径」选出来的。
    ///
    /// <c>where --value</c> 非有它不可:那条路上的结果行是**路径**,而按路径逐条求和
    /// 会把 <c>defName</c> 这种每个 def 类型都有的路径整个放大成全库 —— 报出来的是
    /// **子集计数大于全集**,而它印出来与一个正常计数逐字同形。
    /// </summary>
    public TruncationScope TruncatedDefsSharingValue(string value, ValueMatch match, ScopeFilter scope,
                                                     string? defType = null)
    {
        var p = new Dictionary<string, object?>();
        return TruncatedAmong(ValueWhere(value, match, scope, p), scope, p, defType);
    }

    /// <summary>
    /// 「落在这批 def 类型上、且被砍过」的 def 有几个。
    ///
    /// 收窄的两处(类型 + scope)缺一处,「可能属于这里而没露面」这句话担保的东西就不成立:
    /// 少了 scope,它说的是一批 <c>--scope</c> 明明排除掉的 def。
    /// </summary>
    private TruncationScope TruncatedAmong(string innerWhere, ScopeFilter scope,
                                           Dictionary<string, object?> p, string? defType = null)
    {
        // 外层用 t、内层用 d:同名别名在 SQLite 里靠作用域遮蔽也能跑,但这段 SQL 的全部
        // 意思都在「内外收窄的是不同的东西」上。
        var conds = new List<string>
        {
            "t.fields_truncated > 0",
            $"t.def_type IN (SELECT DISTINCT d.def_type FROM field_values fv {FvJoin} " +
            $"JOIN defs d ON d.id = fv.def_id {innerWhere})",
        };
        if (scope.SqlPredicate("t.source_mod", p) is { } sc) conds.Add(sc);
        // 收窄的第三处。缺它的时候脚注说的是一批调用方**自己已经滤掉**的 def。
        if (defType is { Length: > 0 }) { p["@tdt"] = defType; conds.Add("t.def_type = @tdt COLLATE NOCASE"); }

        // 按类型分组而不是只取一个总数:尾注要把「这批是哪几个类型」说出来。分组同时收得
        // 更紧 —— 只有**真有被砍的 def** 的类型才进名单,而内层那个 DISTINCT 列的是
        // 「用到这条路径的所有类型」。
        var types = new List<(string, int)>();
        var count = 0;
        using var rd = Query(
            $"SELECT t.def_type, COUNT(*) FROM defs t WHERE {string.Join(" AND ", conds)} " +
            "GROUP BY t.def_type ORDER BY COUNT(*) DESC, t.def_type", p);
        while (rd.Read()) { types.Add((rd.GetString(0), rd.GetInt32(1))); count += rd.GetInt32(1); }
        return new TruncationScope(count, types);
    }

    /// <summary>某个 def 类型里有几个 def 在导出时被砍过。</summary>
    public TruncationScope TruncatedDefsOfType(string defType)
    {
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        var n = Scalar("SELECT COUNT(*) FROM defs WHERE fields_truncated > 0 AND def_type = @t COLLATE NOCASE", p);
        return new TruncationScope(n, n > 0 ? [(defType, n)] : []);
    }

    /// <summary>某个字段后缀在快照里到底存不存在 —— find 的零结果要靠它分流成因。</summary>
    public bool FieldPathExists(PathQuery path, ScopeFilter scope)
    {
        var p = new Dictionary<string, object?>();
        var where = SuffixWhere(path, scope, p);
        return Scalar($"SELECT EXISTS(SELECT 1 FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id {where})", p) != 0;
    }

    /// <summary>
    /// 这个名字是不是别的路径的**上一层**。
    ///
    /// 索引只存叶子。<c>List&lt;ThingDefCountRangeClass&gt; statBases</c> 这样的字段自己不落一行,
    /// 值住在 <c>statBases[0].stat</c> 上,于是按后缀问 <c>statBases</c> 恒空 —— 而这是最容易敲的
    /// 那个名字(C# 字段名就长这样)。空结果与「快照里真没有这个字段」逐字同形,分不出来就会
    /// 把 1967 个 def 有值的字段报成空的。
    /// </summary>
    /// <returns>按 def 数排的样本路径、这样的路径一共几条,以及占最多的那个 def 类型
    /// (指路的 <c>fields</c> 要一个 &lt;DefType&gt; 才敲得动,不填就只是个名词)。</returns>
    public (IReadOnlyList<(string Path, int Defs)> Samples, int Total, string? TopType) PathsBelow(
        string name, ScopeFilter scope, int limit)
    {
        // 段边界要认准:`stat` 不能命中 `statBases[0].value`。名字后面只接 `[` 或 `.`,
        // 前面只接开头或 `.` —— 四种组合各一条 LIKE。
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

    /// <summary>
    /// 按值反查字段路径:给一段文本,回答「哪些字段取到过含它的值」。
    ///
    /// 没有它,唯一的出路是猜字段名 —— 猜偏了,<c>--path-contains</c> 会返回一个语法上完全正常、
    /// 语义上完全错误的结果集(<c>fields FactionDef --path-contains texture</c> 只命中
    /// <c>settlementTexturePath</c>,真正管事的 <c>factionIconPath</c> 被整个滤掉)。
    /// </summary>
    /// <remarks>
    /// <c>Exact</c> 是 <c>Total</c> 里有几组**整值就等于**这段文本。子串命中不留痕:
    /// 不拆开这两档,「有一个字段的值就是它」与「有一堆字段的值里碰巧含这几个字母」
    /// 逐字同形。
    /// </remarks>
    public (IReadOnlyList<(string Path, string DefType, int Defs, int DefsExact, string Sample)> Rows,
            int Total, int Exact, int Defs)
        PathsWithValue(string value, ScopeFilter scope, int limit, ValueMatch match = ValueMatch.Substring, int offset = 0,
                       string? defType = null, IReadOnlyList<string>? pathContains = null)
    {
        var p = new Dictionary<string, object?>();
        var where = ValueWhere(value, match, scope, p, defType);
        // 不给字段路径那一支也得认 --path-contains。漏掉这里的话,同一个开关在
        // `where <路径> --path-contains X` 上生效、在 `where --value Y --path-contains X`
        // 上静默无效,而后者的输出与「筛过了,就这些」逐字同形。
        if (pathContains is { Count: > 0 } has) where += $" AND {PathContainsClause(has, p)}";
        var join = $"FROM field_values fv {FvJoin} JOIN defs d ON d.id = fv.def_id";

        var total = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath}, d.def_type {join} {where})", p);
        // 行里的 def 数拆成「值就是它」与其余两半。子串态下这两半是**两个不同的问题**,
        // 合成一个数之后没有任何东西能把它们分开:实测 194 个真实查询值里 55 个存在
        // 一行两态并存(3.9 的 verbProperties.range 上,4 个 def 的值是 3.9、6 个是 23.9,
        // 而那一列印的是 10)。
        //
        // 相加恒等于 Defs —— (def, path) 在库里唯一,一个 def 在一行里只有一个值,
        // 于是它非此即彼。这条实测过:同一批值上「同一个 def 在同一条路径上两态并存」0 例。
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
            exact = Scalar($"SELECT COUNT(*) FROM (SELECT DISTINCT {FvPath}, d.def_type {join} {ew})", ep);
        }

        // 去重后的 def 总数。一个 def 常常在好几条路径上都持有这个值(stuffProps.categories[0]
        // 与 [1]),于是把 Defs 那一列逐行加起来会把它数好几遍 —— 而「一共多少个 def」正是
        // 读者拿这张表要的东西,他们此前只能估(「可能存在重叠,所以约 6 个」)。
        var defs = total == 0 ? 0 : Scalar($"SELECT COUNT(DISTINCT d.id) {join} {where}", p);
        return (rows, total, exact, defs);
    }

    /// <summary>
    /// 同一个值还落在**哪些别的路径形状**上 —— 只看给定的那几个 def_type,并排掉调用方
    /// 自己命中的那些形状。
    ///
    /// <see cref="PathsWithValue"/> 答的是「这个值分布在哪」,这里答的是
    /// 「你点名的那条路径之外还有谁」——**同一份数据的补集视角**,与
    /// <c>ScopeFilter.Complement</c> 那条同源:一次合法查询返回一张干净完整的表,
    /// 而它是不是全集,读的人从表里看不出来。
    ///
    /// **不按 def_type 收窄。** 这里原先收窄,理由写的是「『最大的那个』这个词的正确性前提」——
    /// 而下面那批注释后来推翻了「只报最大的那个」,改成列若干条让读者自己判。收窄活了下来,
    /// 它的理由没有:**一个决定被它当初的目的固化,而那个目的早已撤销**。
    ///
    /// 撤销它的代价实测为负 —— 去掉 <c>def_type IN</c> 后最坏值(baseline 上的 "1",
    /// 1800 条形状)从 0.184s 变 0.179s:<c>idx_fv_value_nc</c> 已经把 value 那一侧走成索引,
    /// 类型过滤是 JOIN 之后的额外判断,不省 IO。
    ///
    /// 收窄挡掉的是**跨类型的那一半**,而那半常常正是答案:消费侧 2026-08-14 的实例里,
    /// 材料 Shard 在起手的 RecipeDef 上只有 3 条形状,全类型有 17 条 —— 82% 在别的类型上,
    /// 且那 82% 才是「谁在消费这个材料」的答案。收窄版给的是个自洽的小数字,
    /// 没有任何一处看得出它不是全集。
    ///
    /// 形状内的 def 数**单独精确数一次**,不靠把 <c>comps[0]</c> 与 <c>comps[4]</c> 的
    /// 计数相加 —— 同一个 def 在两个下标上都带这个值时,相加会多报。排序用相加的近似值
    /// (只影响挑谁,不影响印出来的数),印出来的那个数走第二趟精确查询。
    /// </summary>
    public (IReadOnlyList<(string Shape, int Defs, string Types)> Shown, int OtherShapes,
            int CrossTypeShapes, IReadOnlyCollection<string> Types)? ValueElsewhere(
        PathQuery own, string value, ValueMatch match, ScopeFilter scope)
    {
        // 「调用方这次命中了哪些形状、哪些 def_type」要按**整个结果集**算,不是按这一页 ——
        // 与 FindPathShapes 同一条纪律。只取 DISTINCT 的 (path, def_type),不取行。
        var op = new Dictionary<string, object?>();
        var oc = new List<string>();
        PathCondition(own, op, oc);
        if (match == ValueMatch.Exact) { op["@v"] = value; oc.Add(ValueIs("value = @v COLLATE NOCASE")); }
        else { op["@v"] = "%" + Escape(value) + "%"; oc.Add(ValueIs("value LIKE @v ESCAPE '\\'")); }
        if (scope.SqlPredicate("d.source_mod", op) is { } osc) oc.Add(osc);

        // 「自己命中的形状」要连**类型**一起记。只按形状名排除,在放开 def_type 之后会
        // 误伤:同名形状坐在另一个类型上时(`costList[].thingDef` 在 ThingDef 与 RecipeDef
        // 上都有),那条正是要找的另一半,却因为名字与起手的相同而被当成「自己」排掉。
        // 收窄版看不见这个 bug —— 那时候候选集里压根没有别的类型。
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

        // 形状 → 该形状下的具体路径,以及一个用来排序的近似 def 数。
        // 形状 → 它坐在哪些 def_type 上:放开之后这一列**必须印出来**,否则读者手里
        // 一条 `costList[].thingDef (4)` 看不出它根本不在自己问的那个类型上。
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
                // GROUP BY 多了一列 def_type,同一条 path 会分成几行回来 —— 具体路径要去重,
                // 否则第二趟的 `path IN (...)` 里同一个值出现好几次。
                (paths.TryGetValue(shape, out var list) ? list : paths[shape] = []).Add(rd.GetString(0));
                rough[shape] = rough.GetValueOrDefault(shape) + rd.GetInt32(2);
                (types.TryGetValue(shape, out var ts) ? ts : types[shape] = new(StringComparer.Ordinal)).Add(type);
            }
        if (paths.Count == 0) return null;

        // 「有几条压根不在你问的那个类型上」—— 这个数进主语位。它是可加的(形状计数),
        // 不是 def 数的并集:并集是个看着像答案的错数,下面那段注释说的就是这件事。
        var crossType = types.Count(kv => kv.Value.Any(t => !defTypes.Contains(t)));

        // **只报「最大的那个」是错的**,实测:同一道题从 explosionRadius 起手时最大项是
        // comps[].explosiveRadius(9),正是要找的另一半;从 explosiveRadius 起手时最大项
        // 变成 statBases[].value(3),一个无关字段,而真正的另一半 projectile.explosionRadius
        // 根本没被点名。**「最大」跟「相关」没有关系** —— 而这正是这句话自己说的道理,
        // 却在选样时用了一个与相关性无关的标准。
        // 改成列若干条让读者自己判,与紧邻的跨形状那句同形(那句一直是这么做的)。
        //
        // 恰好只多出一条时全列:「plus 1 path shape not shown」这句话占的字比那一项本身还多,
        // 而藏起来的那一项可能正是相关的那条 —— 用一句更长的话换一次可能的漏掉,不划算。
        //
        // 分界线还**不许落在并列上**。被砍掉的首位与展示末位一样大时,谁进展示位只取决于
        // 同分时的字典序 —— 一个与提问、与数据都无关的量,却印成了看起来有依据的排名。
        // 消费侧量过(真快照,20 个常见值 × def_type):触发省略的 143 组里 54 组(38%)
        // 的边界正落在并列上,值 =1 的 ThingDef 那组两边都是 3414 个 def。
        //
        // 修法是把并列的一并展示,不是加一句「这里是并列」——那句话得报个数,而排序用的是
        // 各下标相加的近似值、印出来的是下面第二趟的精确值,两个数在同一句里对不上。
        // 天花板咬住时分界线又落回并列上,那一小撮(4240 组里 16 组)不再多说,理由同上。
        var order = rough.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        var take = Cli.Limits.MaxSuggestions;
        while (take < order.Count && take < Cli.Limits.MaxShownShapes
               && order[take].Value == order[take - 1].Value) take++;
        if (order.Count == take + 1) take = order.Count;
        var top = order.Take(take).Select(x => x.Key).ToList();

        // 印出来的是**那个形状自己的 def 数**,不是并集。并集试过,是个更坏的东西:
        // 靶题(值 3.9)的并集是 17,而真值是 11 —— 差额是 statBases[].value 与
        // verbs[].minRange 这些语义无关的形状。**哪些形状跟提问的是同一件事,判据不在值里**,
        // CLI 判不出来,所以这个和不能由它来加。一个看着像答案的错数比不给数更坏
        // (见 Docs/17「天花板标签会盖住假话」那条的反面:这里是真天花板,那就说破)。
        //
        // 这个数走第二趟精确查询,不靠把 comps[0] 与 comps[4] 的计数相加 ——
        // 同一个 def 在两个下标上都带这个值时,相加会多报。排序才用相加的近似值。
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

    /// <summary>
    /// 与这些行同处一个带下标容器、而且**有人设过**(<c>is_default &lt;&gt; Same</c>)的兄弟字段。
    ///
    /// 一块 <c>comps[1]</c> 里的字段互相约束:<c>minFuelCost=50</c> 盖掉同块的
    /// <c>fuelPerTile=3</c>,差 16 倍,而只列出后者的输出一个字都没提前者。
    ///
    /// 两道收窄都是有意的。只认带下标的容器:不带下标的层是分类不是实例,兄弟太多且不成组,
    /// 提示会退化成免责声明。只点 <c>code_default=no</c>:声明默认值那一批没人挑过,
    /// 把它们列出来等于把整个类的字段表倒一遍。
    /// </summary>
    /// <remarks>
    /// 回**全部**,不在这里截:截了再交给 NameList,它就以为名单是完整的,于是
    /// 「and N more」一个字都不说。一块 comps[N] 的字段数是个位到几十,不设上限也不会失控。
    ///
    /// 上面那条只管**回多少**;行数本身要分批发,见 <see cref="BatchedOrTerms"/>。
    /// </remarks>
    public IReadOnlyList<string> AuthoredSiblings(IEnumerable<(long DefId, string Path)> shown)
    {
        var rows = shown.ToList();
        // 「已经印出来的那些行」要在**开查之前**全部就位:分批之后,后一批查出来的兄弟里
        // 可能有前一批印过的行,按批填就会把它当成新兄弟点名。
        var already = new HashSet<string>(StringComparer.Ordinal);
        // 分隔符写成转义:它是个不可见字符,而两处拼键的地方必须逐字节同形 ——
        // 字面量那一份在编辑器与 diff 里都看不出来,写错时这份索引会静默地全查不中。
        foreach (var (id, path) in rows) already.Add(id + "\u0001" + path);

        // 「这批行牵动的 def,它们有人设过的字段」一次取完,块与块之间共用。
        // 逐块回库时,每块都要为它的 200 个 `path LIKE 前缀%` 各扫一遍路径字典
        // (带 ESCAPE 的 LIKE 用不上索引),于是**每印一行就全扫一次那 2.6 万行** ——
        // `where stat --type ThingDef` 的 35166 行上是 6 分钟,而主表那边 0.4 秒就算完了。
        // 这里的规模该是 def 数(那一次 4326),而按 def_id 取行顺 idx_fv_def 走。
        var authored = AuthoredFieldsOf(rows.Select(r => r.DefId));

        var names = new List<string>();
        foreach (var batch in rows.Chunk(BatchedOrTerms)) SiblingsOfBatch(batch, authored, already, names);
        return names;
    }

    /// <summary>
    /// 一块几行。分块留着不是为了 SQL 了 —— 兄弟名的**顺序**是块内按 rowid 排、块间按
    /// 行序接,而输出只取前几个,并成一次全局排序会换掉印出来的那几个名字。
    /// </summary>
    private const int BatchedOrTerms = 200;

    /// <summary>
    /// 这批 def 上所有「有人设过」的字段,按 <c>fv.rowid</c>(= 导出器写入顺序)排好。
    /// 前缀匹配挪进内存,SQL 的谓词只剩 <c>def_id IN (…)</c>。
    /// </summary>
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

    /// <summary>
    /// SQLite 的 <c>LIKE '前缀%'</c>:大小写不敏感,但**只折叠 ASCII 的 A–Z**。
    /// .NET 的 OrdinalIgnoreCase 折得比它宽,照搬会让内存这一侧多认几条。
    /// </summary>
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
        // 一块内的几百个 (def, 前缀) 此前是同一条 WHERE 的几百个 OR 项,同一行被两项
        // 同时命中也只出一次 —— 按 rowid 去重把那件事原样留住。
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

        // 按 rowid 排 = 导出器写入的顺序 = 这一块在 XML/类声明里的顺序。
        // 按 path 字典序排会让同一块里语义最近的几个字段散到各处(fuelPerTile 就是
        // 这样被 cooldown* 挤出前三名的)。
        hits.Sort((a, b) => a.RowId.CompareTo(b.RowId));

        foreach (var h in hits)
        {
            // 已经印在表里的那一行不算它自己的兄弟。
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

    /// <summary>
    /// 一个 defName 的全部译文,连 <c>def_type</c> 一起回 —— 同名跨 def 类型时,挑哪些
    /// 归这个 def 的判断要在命令层做,因为那里才知道有没有同名歧义。
    ///
    /// <c>def_type</c> 可能为 null:收割自语言文件的行,注入 key 是 <c>DefName.field</c>,
    /// 不带类型,游戏自己也是按 defName 注入的 —— 那条译文属于哪个同名 def,在数据源里
    /// 就是不确定的。
    /// </summary>
    public IReadOnlyList<TranslationRow> Translations(string defName)
    {
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        var rows = new List<TranslationRow>();
        // 后加的两列靠列名认,不靠 schema_version —— 涨了版本每一份旧库连同 --keep
        // 留下的那些旧代都会拒读,而它们唯一的用途正是 snapshot diff(同 type_fields)。
        var extra = TranslationsHaveSourceFile;
        var keyed = TranslationsHaveKey;
        using var rd = Query(
            "SELECT def_name, def_type, path, translated, original, language, source_mod, origin" +
            (extra ? ", source_file, source_file_count" : "") +
            (keyed ? ", key, key_state, applied" : "") + " FROM translations " +
            "WHERE def_name = @n COLLATE NOCASE ORDER BY origin, path", p);
        while (rd.Read())
            rows.Add(new TranslationRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6),
                rd.GetString(7),
                extra && !rd.IsDBNull(8) ? rd.GetString(8) : null,
                extra && !rd.IsDBNull(9) ? rd.GetInt32(9) : null,
                keyed && !rd.IsDBNull(extra ? 10 : 8) ? rd.GetString(extra ? 10 : 8) : null,
                keyed && !rd.IsDBNull(extra ? 11 : 9) ? rd.GetString(extra ? 11 : 9) : null,
                keyed && !rd.IsDBNull(extra ? 12 : 10) ? rd.GetInt32(extra ? 12 : 10) != 0 : null));
        return rows;
    }

    /// <summary>
    /// 这个 def 的槽位名册。<c>null</c> = 这份快照没量过这一层(导出器早于 0.9.0,**0.8.0
    /// 也算在内** —— 那一版的表不全,见 <see cref="ExportMeta.IndexesInjectionKeys"/>),
    /// 空表 = 量过了、这个 def 一个可注入槽位都没有。同 <see cref="TypeDeclaredPaths"/>
    /// 那条缝:两者合成一个空列表,调用方就会把「没导」说成「没有」。
    ///
    /// 0.9.0 起这是**全集**,所以「这个 def 能译什么」拿它回答是准的。
    /// </summary>
    public IReadOnlyList<InjectionKeyRow>? InjectionKeys(string defName)
    {
        if (!Meta.IndexesInjectionKeys || !HasInjectionKeys) return null;
        var p = new Dictionary<string, object?> { ["@n"] = defName };
        var rows = new List<InjectionKeyRow>();
        // 两种形状:字符串列的旧库,与四列全进字典的新库。同 TypeFieldsAreSubtrees,靠列名认。
        // 谓词走字典的 IN 子查询而不是挂在 JOIN 上 —— 后者会让优化器从主表驱动,
        // 字典化就白做了(路径字典那次实测 1.78s 对 2.08s)。
        var sql = InjectionKeysAreDictionary
            ? "SELECT n.def_name, t.def_type, p.path, q.path, k.is_collection, "
              + "k.translation_allowed, k.full_list_translation_allowed FROM injection_keys k "
              + "JOIN injection_key_names n ON n.id = k.def_name_id "
              + "LEFT JOIN injection_key_types t ON t.id = k.def_type_id "
              + "JOIN injection_key_paths p ON p.id = k.path_id "
              + "JOIN injection_key_paths q ON q.id = k.suggested_path_id "
              + "WHERE k.def_name_id IN (SELECT id FROM injection_key_names "
              + "WHERE def_name = @n COLLATE NOCASE) ORDER BY p.path"
            : "SELECT def_name, def_type, path, suggested_path, is_collection, translation_allowed, "
              + "full_list_translation_allowed FROM injection_keys "
              + "WHERE def_name = @n COLLATE NOCASE ORDER BY path";
        using var rd = Query(sql, p);
        while (rd.Read())
            rows.Add(new InjectionKeyRow(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.GetString(2), rd.GetString(3),
                rd.GetInt32(4) != 0, rd.GetInt32(5) != 0, rd.GetInt32(6) != 0));
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

    // ---------- Keyed(界面文案)----------

    /// <summary>
    /// 这份快照里有没有 keyed 那一层。零 = 导出时游戏没有任何 keyed 译文(理论上不该发生,
    /// 英文环境下英文语言文件自己就是数据源),所以呈现侧拿它区分「这个 key 没有」与
    /// 「这一层整个是空的」—— 后者是数据侧的问题,不是答案。
    /// </summary>
    public int KeyedCount() => Scalar("SELECT COUNT(*) FROM keyed");

    /// <summary>
    /// 这份库的 keyed 记不记得同一句话在这个 mod 里钺了几份。同
    /// <see cref="TranslationsHaveSourceFile"/>,**靠列名认**。
    /// </summary>
    private bool KeyedHasSourceFileCount => _kSfc ??= HasColumn("keyed", "source_file_count");
    private bool? _kSfc;

    /// <summary>
    /// 新列**接在末尾**：前面八个序号于是不动,读取侧只靠 FieldCount 判它在不在。
    /// 插在中间的话每一个 rd.GetXxx(n) 都得跟着改,而改错一个不报错。
    /// </summary>
    private string KeyedColumns =>
        "key, translated, original, language, source_file, source_line, source_mod, placeholder, origin"
        + (KeyedHasSourceFileCount ? ", source_file_count" : "");

    /// <summary>
    /// 同一份列,带表别名。JOIN 到 <c>keyed_fts</c> 时 <c>key</c> / <c>translated</c> /
    /// <c>original</c> 三个名字**两张表都有**,不加前缀是 SQL 歧义;而拿
    /// <c>Replace("key", "k.key")</c> 从上面那份拼会顺手改掉 <c>keyed</c> 里的 key。
    /// </summary>
    private string KeyedColumnsPrefixed =>
        "k.key, k.translated, k.original, k.language, k.source_file, k.source_line, k.source_mod, " +
        "k.placeholder, k.origin" + (KeyedHasSourceFileCount ? ", k.source_file_count" : "");

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

    /// <summary>
    /// 一个 key 的全部行。**可能多于一条**:runtime 那一条是生效值,harvest 层可以另有几条
    /// (磁盘上别的 mod 也译了同一个 key)。挑哪条呈现、怎么说清分层,归命令层 ——
    /// 这里不替它挑,挑了就等于发一张「谁生效」的证书,而 harvest 层证不了这件事。
    /// </summary>
    public IReadOnlyList<KeyedRow> KeyedByKey(string key)
        // 生效的那一条排头:字典序里 harvested < harvested_outside < runtime,只按 origin
        // 排会把唯一权威的那行甩到最后,而一个 key 常有三五条来源。
        => ReadKeyed($"SELECT {KeyedColumns} FROM keyed WHERE key = @k COLLATE NOCASE " +
                     $"ORDER BY (origin <> '{TranslationOrigin.Runtime}'), origin, source_mod",
                     new Dictionary<string, object?> { ["@k"] = key });

    /// <summary>
    /// 以 <paramref name="key"/> 为前缀、但不是它自己的那些 key(去重)。
    ///
    /// 精确命中会把前缀匹配整个关掉 —— <c>keyed CommandSettle</c> 只回一行,而
    /// <c>CommandSettleDesc</c> 就躺在旁边。**少掉的那些与「不存在」逐字同形**,
    /// 于是命令层要拿这个数说破自己收窄过。
    ///
    /// 判据是字面前缀,不走 FTS:FTS 那条同时认译文与英文原文,数出来的是另一个集合,
    /// 而这句话要说的恰恰是「以此为前缀的 key」。
    /// </summary>
    /// <returns><c>Keys</c> 最多 <paramref name="limit"/> 个(字典序),<c>Total</c> 是全部。</returns>
    public (IReadOnlyList<string> Keys, int Total) KeyedPrefixSiblings(string key, int limit)
    {
        // key 里可以合法地出现 _ 与 %(LIKE 的两个通配符),不转义的话
        // `Stat_Foo` 会把 `StatXFoo` 也算成兄弟 —— 一个只会多报、不会少报的错,
        // 但它报出来的数字没有产地。
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

    /// <summary>
    /// 全文检索 keyed 三列(key / 译文 / 英文原文)。中文查得到 key,英文也查得到 ——
    /// 这一层的 original 进了 FTS(与 translations 那张表不同,那边只索引 translated),
    /// 因为「从屏幕上的字往回走」是这一层存在的理由,而屏幕上的字两种语言都可能。
    /// </summary>
    /// <param name="placeholdersOnly">
    /// 只要占位译文。**必须在这里筛,不能等取完页再筛** —— 页内筛出来的
    /// 「这一页没有占位」会被当成「一条占位都没有」说出去,而分母还是全体命中数。
    /// </param>
    /// <returns>
    /// <c>Total</c> 是**过滤之后**的命中数,分页三件事按它算;<c>MatchedTotal</c> 忽略
    /// 占位过滤 —— 「N 条命中里一条占位都没有」那句话要的是它,两个数不能混用一个。
    /// </returns>
    public (IReadOnlyList<KeyedRow> Rows, int Total, int MatchedTotal) KeyedSearch(
        string query, int limit, int offset = 0, bool placeholdersOnly = false)
    {
        var p = new Dictionary<string, object?>
        {
            ["@m"] = FtsText.BuildMatchQuery(query),
            ["@q"] = query,
        };
        var from = "FROM keyed_fts f JOIN keyed k ON k.id = f.rowid WHERE keyed_fts MATCH @m";
        var filtered = placeholdersOnly ? from + " AND k.placeholder = 1" : from;
        var total = Scalar($"SELECT COUNT(*) {filtered}", p);
        var matchedTotal = placeholdersOnly ? Scalar($"SELECT COUNT(*) {from}", p) : total;
        // key 整体命中排最前,然后是生效层压过磁盘层 —— 后者只是「存在」,不是答案。
        var order = "ORDER BY (k.key = @q COLLATE NOCASE) DESC, (k.origin = 'runtime') DESC, " +
                    "bm25(keyed_fts, 10.0, 3.0, 3.0), LENGTH(k.key), k.key";
        var rows = ReadKeyed($"SELECT {KeyedColumnsPrefixed} {filtered} {order} LIMIT {limit} OFFSET {offset}", p);
        return (rows, total, matchedTotal);
    }

    /// <summary>
    /// 整层枚举,不带查询词。<c>keyed</c> 的位置参数是必填的,而 <c>--empty-translation</c> 是
    /// **整层的过滤器**、不是搜索结果上的过滤器 —— 没有这条,「把还没译的全列出来」
    /// 这条意图没有可表达的形式。
    /// </summary>
    /// <param name="placeholdersOnly">
    /// 与 <see cref="KeyedSearch"/> 同一条理由:必须在 SQL 里筛。取完页再筛,
    /// 「这一页没有占位」就会被当成「一条占位都没有」说出去。
    /// </param>
    /// <returns><c>Total</c> 是过滤之后的行数(分页按它算),<c>LayerTotal</c> 是整层的行数
    /// —— 「整层 N 行里一条占位都没有」那句话要的是后者。</returns>
    public (IReadOnlyList<KeyedRow> Rows, int Total, int LayerTotal) KeyedAll(
        int limit, int offset = 0, bool placeholdersOnly = false)
    {
        var where = placeholdersOnly ? " WHERE placeholder = 1" : "";
        var layerTotal = KeyedCount();
        var total = placeholdersOnly ? Scalar($"SELECT COUNT(*) FROM keyed{where}") : layerTotal;
        // 同一个 key 的几条来源挨在一起,生效的那条排头 —— 与 KeyedByKey 同一套次序,
        // 于是从枚举里挑一个 key 再单查它,两处的行序不会打架。
        var rows = ReadKeyed(
            $"SELECT {KeyedColumns} FROM keyed{where} " +
            $"ORDER BY key COLLATE NOCASE, (origin <> '{TranslationOrigin.Runtime}'), origin, source_mod " +
            $"LIMIT {limit} OFFSET {offset}");
        return (rows, total, layerTotal);
    }

    /// <summary>
    /// 一批 key 各自的生效译文。<c>code-search</c> 给命中行附译文时一次问完 ——
    /// 一行一次查询会让一次扫描变成几千次 SQL。只回 runtime 那一层:附在代码行边上的
    /// 那句话必须是游戏真会显示的,磁盘层在这里贴上去就是伪证。
    /// </summary>
    public IReadOnlyDictionary<string, KeyedRow> KeyedInEffect(IEnumerable<string> keys)
    {
        var list = keys.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, KeyedRow>(StringComparer.Ordinal);
        if (list.Count == 0) return result;

        // 分批 —— SQLite 的参数上限是 999,而一次 code-search 的命中可以远超它。
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

    /// <summary>
    /// 全部 key。零结果时的模糊候选池 —— 与 def 侧同一条路子:「名字打错了」与
    /// 「这个环境里真没有」必须分得开。
    /// </summary>
    public IReadOnlyList<string> AllKeyedKeys()
    {
        var keys = new List<string>();
        using var rd = Query("SELECT DISTINCT key FROM keyed ORDER BY key");
        while (rd.Read()) keys.Add(rd.GetString(0));
        return keys;
    }

    // ---------- 经济面 ----------

    /// <summary>
    /// 经济表里有多少行。**这不是在场判据** —— 判据是 <see cref="EconomyState"/>,
    /// 因为零行有四种成因而这个数只有一个。命令层拿它报数,不拿它判有没有。
    /// </summary>
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

    /// <summary>
    /// 按名字取。**可能多于一条**:同名跨 def 类型是 RimWorld 常态,而这一层只筛 ThingDef,
    /// 所以实际上至多一条 —— 仍回列表,免得形状随命中数变。
    /// </summary>
    public IReadOnlyList<EconomyRow> EconomyByName(string defName)
        => ReadEconomy($"SELECT {EconomyColumns} FROM economy WHERE def_name = @n COLLATE NOCASE",
                       new Dictionary<string, object?> { ["@n"] = defName });

    /// <summary>
    /// 整层枚举,带过滤。<paramref name="total"/> 是**过滤之后、分页之前**的数 ——
    /// 分页三件事按它报,而「筛掉了多少」由命令层拿 <see cref="EconomyCount"/> 另说。
    /// </summary>
    public (IReadOnlyList<EconomyRow> Rows, int Total) EconomyAll(
        ScopeFilter scope, string? category, string? calcState, bool producibleOnly,
        string orderBy, int limit, int offset)
    {
        var p = new Dictionary<string, object?>();
        var conds = new List<string>();
        // scope 走全仓同一套(config 里的 mod 组、'-' 排除、vanilla 展开)—— 对照池切分
        // 正是这一层的主要用法,自建一个 --mod 会让同一个词在两条命令里选中不同的集合。
        if (scope.SqlPredicate("mod", p) is { } modWhere) conds.Add(modWhere);
        if (category is not null) { conds.Add("category = @cat COLLATE NOCASE"); p["@cat"] = category; }
        if (calcState is not null) { conds.Add("calc_state = @cs COLLATE NOCASE"); p["@cs"] = calcState; }
        if (producibleOnly) conds.Add("producible = 1");
        var where = conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);

        var total = Scalar($"SELECT COUNT(*) FROM economy{where}", p);

        // 排序列由调用方从一个闭集里挑(见 EconomyCommand),不拼用户输入。
        // NULL 排最后:它们是「算不出」,让它们混在最小值那一头会被读成一串零。
        var rows = ReadEconomy(
            $"SELECT {EconomyColumns} FROM economy{where} " +
            $"ORDER BY ({orderBy} IS NULL), {orderBy} DESC, def_name LIMIT @lim OFFSET @off",
            new Dictionary<string, object?>(p) { ["@lim"] = limit, ["@off"] = offset });
        return (rows, total);
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

    /// <summary>
    /// 全部出现在经济表里的 defName。零结果时的模糊候选池 —— 与 def 侧、keyed 侧同一条路子。
    /// </summary>
    public IReadOnlyList<string> AllEconomyNames()
    {
        var names = new List<string>();
        using var rd = Query("SELECT def_name FROM economy ORDER BY def_name");
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
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
    /// 一个具名节点底下,**别的**后代 def 里有多少条带着某个字段路径、其中多少条的值
    /// 与给定值逐字相同。
    ///
    /// 答的是「这个值是哪一层写的」:<c>get</c> 给的是合并后的值,而抽象节点在快照里
    /// 没有自己的字段表,两条命令拼起来正面答不了「Mass 是 BuildingBase 写的还是这个
    /// def 自己写的」。
    ///
    /// 快照里没有「哪一层声明了它」这条事实(游戏在 <c>LoadAllActiveMods</c> 末尾就把继承
    /// 解完丢了),但它**推得出来**:某一层若真声明了这个字段,它的后代应当**都**带着;
    /// 后代里有一条不带,那一层就没声明。这里只出数,推论留给读的人。
    ///
    /// <paramref name="pathFilter"/> 用子串语义,与 <c>get --path-contains</c> 同一套:同一个词
    /// 在两条命令里选中同一批字段,否则两边的数对不上账而没人看得出为什么。
    /// </summary>
    /// <param name="exclude">排除掉的那个 def(问的就是它,算进分母会把每一层都撑成非零)。</param>
    /// <remarks>
    /// 回传的 <c>Truncated</c> 是分母里有几条的字段表在导出时被截过 —— 那种 def 会
    /// 「没有这条路径」而其实有,正好是让一层被误判成「没声明」的那个方向。整库的截断数
    /// (<see cref="TruncatedDefCount"/>)在这里没用:它恒为非零,而恒真的免责声明会被学着跳过。
    /// </remarks>
    public (int Descendants, int WithPath, int SameValue, int Truncated) Witnesses(
        string ancestorName, string pathFilter, string? value, (string DefName, string DefType)? exclude,
        bool exactPath = false)
    {
        var p = new Dictionary<string, object?>();
        var cte = KinCte(ancestorName, pathFilter, exclude, p, exactPath);

        // 三个数一趟取回:分开跑三趟的话递归 CTE 也跑三趟,而 BuildingBase 这种量级的
        // 后代集是这条命令里最贵的一件事。
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

    /// <summary>
    /// 一层之下**最常见**的那个值,带着它的 def 数,以及一共出现了几种值。
    ///
    /// 抽象节点自己没有值可比,而这张表要分的恰恰是「一个共享值」与「各写各的一份」——
    /// 众数把这件事变回可数的:众数占满带路径的那批就是共享,散开就是各写各的。
    /// 与「问的那个 def 自己装着什么」不是同一个口径,所以调用方必须在表头说破它是哪一种。
    /// </summary>
    public (string? Value, int Defs, int Distinct) DominantValue(string ancestorName, string pathFilter,
                                                                 bool exactPath = false)
    {
        var p = new Dictionary<string, object?>();
        var cte = KinCte(ancestorName, pathFilter, null, p, exactPath);

        // 并列时按值排序定序:名次靠随机决定的话,同一份快照两次运行会给出两个参照值。
        using var rd = Query(
            cte +
            // 内层那个数用的是另一个别名(fv2),接不上 FvJoin/FvValue 的 fv/fvp/fvv,
            // 所以这三处按形状内联。**分组是 NOCASE 的,不能改成按号分组** ——
            // 号与值一一对应,而 NOCASE 会把只差大小写的两个值并成一格,两者不是同一个划分。
            "SELECT " + FvValue + ", COUNT(DISTINCT fv.def_id) AS c, " +
            "  (SELECT COUNT(*) FROM (SELECT DISTINCT "
            + (ValuesAreDictionary ? "fvv2.value" : "fv2.value") + " COLLATE NOCASE "
            + "FROM field_values fv2 " +
            (FieldValuesAreDictionary ? "JOIN field_value_paths fvp2 ON fvp2.id = fv2.path_id " : "") +
            (ValuesAreDictionary ? "LEFT JOIN field_value_values fvv2 ON fvv2.id = fv2.value_id " : "") +
            "     JOIN kin k2 ON k2.id = fv2.def_id WHERE " +
            (FieldValuesAreDictionary ? "fvp2.path" : "fv2.path") + " LIKE @f ESCAPE '\\')) " +
            $"FROM field_values fv {FvJoin} JOIN kin k ON k.id = fv.def_id " +
            "WHERE " + PathIs($"path LIKE @f ESCAPE '\\'") + " " +
            "GROUP BY " + FvValue + " COLLATE NOCASE ORDER BY c DESC, " + FvValue + " LIMIT 1", p);
        return rd.Read()
            ? (rd.IsDBNull(0) ? null : rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2))
            : (null, 0, 0);
    }

    /// <summary>
    /// 一个具名层之下的 def 集(<c>kin</c>)。两条查询共用,免得同一个后代集算出两种。
    /// </summary>
    private static string KinCte(string ancestorName, string pathFilter,
                                 (string DefName, string DefType)? exclude, Dictionary<string, object?> p,
                                 bool exactPath = false)
    {
        p["@root"] = ancestorName;
        p["@f"] = PathFilterLike(pathFilter, exactPath);

        // 自环与 XML 里写得出的环由 UNION(而非 UNION ALL)吃掉:去重之后递归自然收敛。
        var cte =
            "WITH RECURSIVE anc(name) AS ( " +
            "  SELECT @root " +
            "  UNION " +
            "  SELECT n.name FROM xml_nodes n JOIN anc a ON n.parent_name = a.name COLLATE NOCASE " +
            "   WHERE n.name IS NOT NULL AND n.name <> '' " +
            "), kin(id) AS ( " +
            "  SELECT DISTINCT d.id FROM xml_nodes x " +
            "    JOIN defs d ON d.def_name = x.def_name COLLATE NOCASE " +
            "   WHERE x.def_name IS NOT NULL AND x.def_name <> '' " +
            "     AND x.parent_name COLLATE NOCASE IN (SELECT name FROM anc) " +
            // 关联口径与 get 的 inherits_from 同源:`xml_nodes.def_type` 是 XML 根元素名,
            // `defs.def_type` 是桶名,两者会不一致(异构桶)。硬要求相等会把整批异构桶的
            // 后代丢出分母 —— 分母小了,每一层都更容易看着「全都带」。先要相等的,
            // 名字没有歧义时才回退到唯一候选;有歧义又对不上宁可不算。
            "     AND (d.def_type = x.def_type COLLATE NOCASE " +
            "          OR (SELECT COUNT(*) FROM defs d2 WHERE d2.def_name = x.def_name COLLATE NOCASE) = 1) ";
        if (exclude is { } ex)
        {
            p["@xn"] = ex.DefName; p["@xt"] = ex.DefType;
            cte += "     AND NOT (d.def_name = @xn COLLATE NOCASE AND d.def_type = @xt) ";
        }
        return cte + ") ";
    }

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
        var extra = Meta.IndexesPatchOpsByDefNameLabel;
        var cols = extra
            ? "def_type, name, parent_name, abstract, def_name, label, source_mod, source_file, patch_ops, patch_ops_defname, patch_ops_label"
            : "def_type, name, parent_name, abstract, def_name, label, source_mod, source_file, patch_ops";
        var rows = new List<XmlNodeRow>();
        using var rd = Query(
            $"SELECT {cols} FROM xml_nodes {where} ORDER BY abstract DESC, name, def_name", p);
        while (rd.Read())
            rows.Add(new XmlNodeRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.GetInt32(3) != 0,
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.GetInt32(8),
                extra ? rd.GetInt32(9) : 0,
                extra ? rd.GetInt32(10) : 0));
        return rows;
    }

    /// <summary>
    /// 这个 def 的字段路径在 XML 里写在哪一层。<c>null</c> = 这份快照没量过。
    /// 字典的值是 <c>here</c> 或 <c>parent</c>;不在字典里的路径就是两边都没写。
    /// </summary>
    public Dictionary<string, string>? XmlWrittenMarks(string defType, string defName)
        => XmlWrittenMarks(defType, defName, out _, out _, out _);

    /// <param name="containers">
    /// XML 侧写过的每一条路径的各级前缀。索引路径 join 落空、而它的容器在这里时,
    /// 说明两边在描述同一个容器、只是写法对不上 —— 那一格不是 no。
    /// </param>
    public Dictionary<string, string>? XmlWrittenMarks(string defType, string defName,
                                                       out HashSet<string> containers)
        => XmlWrittenMarks(defType, defName, out containers, out _, out _);

    /// <param name="texts">
    /// 每条 XML 叶子路径的行内文本。<c>null</c> = 这份快照没量过(能力位为假),
    /// 查询侧走候选数那条旧路,不许把缺列当空串去比。
    /// </param>
    /// <param name="patchedPaths">
    /// 补丁加进来的路径。<c>null</c> = 这份快照收的是打补丁**之前**的原文,
    /// 「作者写的」与「别的 mod 加的」在它里面分不开,查询侧一律不加后缀。
    /// </param>
    public Dictionary<string, string>? XmlWrittenMarks(string defType, string defName,
                                                       out HashSet<string> containers,
                                                       out Dictionary<string, string>? texts,
                                                       out HashSet<string>? patchedPaths)
    {
        containers = new HashSet<string>(StringComparer.Ordinal);
        texts = null;
        patchedPaths = null;
        if (!Meta.IndexesXmlWritten) return null;

        var hereKeys = new HashSet<string>(StringComparer.Ordinal);
        var parentKeys = new HashSet<string>(StringComparer.Ordinal);
        hereKeys.Add(defName);

        var named = NodesNamed(defName)
            .Where(n => string.Equals(n.DefName, defName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var xmlNode = named.FirstOrDefault(n => DefTypes.Same(n.DefType, defType))
                   ?? (named.Count == 1 ? named[0] : null);
        if (xmlNode?.Name is { Length: > 0 } ownName) hereKeys.Add(ownName);

        var cursor = xmlNode?.ParentName;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (cursor is { Length: > 0 } && seen.Add(cursor))
        {
            parentKeys.Add(cursor);
            var up = NodesNamed(cursor)
                .FirstOrDefault(n => string.Equals(n.Name, cursor, StringComparison.OrdinalIgnoreCase));
            cursor = up?.ParentName;
        }

        var xmlType = xmlNode?.DefType ?? defType;
        var marks = new Dictionary<string, string>(StringComparer.Ordinal);
        var withText = Meta.IndexesXmlWrittenText;
        var textsLocal = withText ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
        texts = textsLocal;
        var withPatched = Meta.IndexesPostPatchXml;
        var patchedLocal = withPatched ? new HashSet<string>(StringComparer.Ordinal) : null;
        patchedPaths = patchedLocal;
        var containersLocal = containers;   // out 参数进不了局部函数,引用同一个集合
        void Load(IEnumerable<string> keys, string mark)
        {
            foreach (var key in keys)
            {
                var p = new Dictionary<string, object?>
                {
                    ["@k"] = key, ["@t"] = defType, ["@x"] = xmlType,
                };
                var cols = withPatched ? "path, inner_text, patched"
                         : withText ? "path, inner_text"
                         : "path";
                using var rd = Query(
                    $"SELECT {cols} FROM xml_written WHERE node_key = @k " +
                    "AND (def_type = @t COLLATE NOCASE OR def_type = @x COLLATE NOCASE)", p);
                while (rd.Read())
                {
                    var path = rd.GetString(0);
                    if (!marks.ContainsKey(path))
                    {
                        marks[path] = mark;
                        if (textsLocal is not null)
                            textsLocal[path] = rd.IsDBNull(1) ? "" : rd.GetString(1);
                        if (patchedLocal is not null && !rd.IsDBNull(2) && rd.GetInt64(2) != 0)
                            patchedLocal.Add(path);
                    }
                    for (var dot = path.IndexOf('.'); dot > 0; dot = path.IndexOf('.', dot + 1))
                        containersLocal.Add(path[..dot]);
                }
            }
        }
        // here 先写,parent 不覆盖 —— 本节点写过的就是 here,哪怕祖先也写了。
        Load(hereKeys, "here");
        Load(parentKeys, "parent");
        return marks;
    }

    /// <summary>
    /// 这个 def 每一格的路径与值。值回连要看同一元素的其它格,必须是全量,
    /// 不受 --limit / --defaults / --path-contains 影响。
    /// </summary>
    public IReadOnlyList<FieldRow> AllFieldCells(long defId)
    {
        var p = new Dictionary<string, object?> { ["@id"] = defId };
        var rows = new List<FieldRow>();
        using var rd = Query(
            // anchor 取的是元素里第一个对上标签的格,所以行序参与输出 —— 不排就靠 rowid,
            // 而那不是承诺。
            $"SELECT {FvPath}, {FvLeaf}, {FvValue}, fv.is_default FROM field_values fv {FvJoin} "
            + $"WHERE fv.def_id = @id ORDER BY {FvPath}, fv.rowid", p);
        while (rd.Read())
            rows.Add(new FieldRow(rd.GetString(0), rd.GetString(1),
                                  rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetInt32(3)));
        return rows;
    }

    /// <summary>
    /// 这个 def 类型声明了哪些字段路径。<c>null</c> = 这份快照没量过。
    /// </summary>
    /// <summary>
    /// 子树分解之后,类型名住在 <c>type_names</c> 的 222 行里,一次全扫,NOCASE 不要索引。
    /// </summary>
    public const string TypeDeclaredPathsWhere = "WHERE n.name = @t COLLATE NOCASE";

    /// <summary>
    /// 拆表之前的那两种形状共用的谓词。与 <c>idx_tf_type_nc</c> 必须是同一种排序 ——
    /// BINARY 的索引配 NOCASE 的谓词等于没有索引,那张表 1373 万行,失配实测 12.2s 对 0.113s。
    /// 新库里既没有这张表也没有那条索引,这个常量只走在旧库上。
    /// </summary>
    public const string TypeDeclaredPathsLegacyWhere = "WHERE t.def_type = @t COLLATE NOCASE";

    /// <summary>
    /// 这份库的声明层是不是拆成子树的那一版。**靠表在不在认,不靠版本号** ——
    /// schema_version 相等的检查一旦为此涨档,磁盘上每一份旧库都会拒读,连同 <c>--keep</c>
    /// 留下的那些旧代,而它们唯一的用途正是拿来 <c>snapshot diff</c>,重导对它们不适用。
    ///
    /// <c>PRAGMA table_info</c> 对不存在的表回零行,于是探一列就够。
    /// </summary>
    private bool TypeFieldsAreSubtrees => _tfSub ??= HasColumn("subtree_paths", "path_id");
    private bool? _tfSub;

    /// <summary>
    /// 拆表之前那两种形状里,较新的一种(路径进了字典)。同 <see cref="TypeFieldsAreSubtrees"/>,
    /// 靠列名认。
    /// </summary>
    private bool TypeFieldsAreDictionary => _tfDict ??= HasColumn("type_fields", "path_id");
    private bool? _tfDict;

    /// <summary>
    /// 这份库的字段路径住在字典表里,还是逐行存在 <c>field_values.path</c> 上。
    /// 同 <see cref="TypeFieldsAreDictionary"/>,**靠列名认** —— 两种形状能出自同一个导出器,
    /// 而 <c>--keep</c> 留下的旧代永远是老形状,那些库唯一的用途正是拿来 diff。
    /// </summary>
    private bool FieldValuesAreDictionary => _fvDict ??= HasColumn("field_values", "path_id");
    private bool? _fvDict;

    /// <summary>
    /// 每一处 <c>FROM field_values fv</c> 后面跟的那一段,以及 SELECT / GROUP BY 里那个路径列。
    /// 字典库把路径接回来,旧库什么都不接。**这一对只管取值,不管筛选** —— 筛选走
    /// <see cref="PathIs"/>。
    /// </summary>
    private string FvJoin =>
        (FieldValuesAreDictionary ? "JOIN field_value_paths fvp ON fvp.id = fv.path_id " : "")
        // 值那一侧必须是 LEFT:value_id 可空(原来的 value 为 NULL),内连会把那些行整个丢掉。
        // 用不到 fvv 的查询里这条 JOIN 不花钱 —— 挂在主键上的、一列都没引用的 LEFT JOIN
        // 会被 SQLite 直接省掉(omit-noop-join)。
        + (ValuesAreDictionary ? "LEFT JOIN field_value_values fvv ON fvv.id = fv.value_id" : "");

    /// <inheritdoc cref="FvJoin"/>
    private string FvPath => FieldValuesAreDictionary ? "fvp.path" : "fv.path";

    /// <summary>
    /// 这份库的 <c>leaf</c> 住在路径字典上,还是逐行存在 <c>field_values</c> 里。
    /// 它是 <c>NoiseFilter.Leaf(path)</c> 的返回值,path 的纯函数,所以挪得动;
    /// 旧库里它还在大表上。同 <see cref="FieldValuesAreDictionary"/>,靠列名认。
    /// </summary>
    private bool LeafLivesOnPaths => _fvLeaf ??= HasColumn("field_value_paths", "leaf");
    private bool? _fvLeaf;

    /// <inheritdoc cref="FvJoin"/>
    private string FvLeaf => LeafLivesOnPaths ? "fvp.leaf" : "fv.leaf";

    /// <summary>
    /// leaf 谓词。<paramref name="cond"/> 写成对裸列名 <c>leaf</c> 的条件。
    /// 新库上它跑在 2.6 万行的路径字典里再回表(同 <see cref="PathIs"/> 那条实测),
    /// 旧库上原样加一层括号 —— 那时 <c>leaf</c> 就在 <c>field_values</c> 上,不会有歧义。
    /// </summary>
    private string LeafIs(string cond) => LeafLivesOnPaths ? PathIs(cond) : $"({cond})";

    /// <summary>
    /// 这份库的值住在字典表里,还是逐行存在 <c>field_values.value</c> 上。同上,靠列名认。
    /// </summary>
    private bool ValuesAreDictionary => _fvVal ??= HasColumn("field_values", "value_id");
    private bool? _fvVal;

    /// <inheritdoc cref="FvJoin"/>
    private string FvValue => ValuesAreDictionary ? "fvv.value" : "fv.value";

    /// <summary>
    /// 值谓词。<paramref name="cond"/> 写成对裸列名 <c>value</c> 的条件。
    ///
    /// 同 <see cref="PathIs"/>:谓词跑在 6.5 万行的字典上再顺 <c>idx_fv_value</c> 回表,
    /// **不要**写成挂在 <see cref="FvJoin"/> 那条 LEFT JOIN 上的 <c>fvv.value LIKE …</c> ——
    /// 那会让优化器从主表驱动,150 万行逐行去字典取值才比。
    ///
    /// NULL 的语义两条路一致:<c>value_id</c> 为空时 IN 不中,与 <c>NULL = @v</c> 同样为假。
    /// </summary>
    private string ValueIs(string cond)
        => ValuesAreDictionary
            ? $"fv.value_id IN (SELECT id FROM field_value_values WHERE {cond})"
            : $"({cond})";

    /// <summary>
    /// 路径谓词。<paramref name="cond"/> 写成对裸列名 <c>path</c> 的条件,由这里决定它跑在哪张表上。
    ///
    /// 字典库上**不能**把它写成 <c>fvp.path LIKE …</c> 挂在 <see cref="FvJoin"/> 那条 JOIN 上:
    /// 实测优化器会从 defs 驱动、按 def_id 取出 150 万行、再逐行按 rowid 去字典表取路径才比 ——
    /// 谓词跑在大表上,字典化白做(改造前后同一条查询 1.78s 对 2.08s,反而更慢)。
    /// 写成对 <c>path_id</c> 的 IN 子查询就把顺序钉死了:先在 2.6 万行的字典上扫出一批号,
    /// 再顺 <c>idx_fv_pathid</c> 回表。
    ///
    /// 旧库上原样加一层括号,与改造前逐字同一条谓词。
    /// </summary>
    private string PathIs(string cond)
        => FieldValuesAreDictionary
            ? $"fv.path_id IN (SELECT id FROM field_value_paths WHERE {cond})"
            : $"({cond})";

    /// <summary>
    /// 这份库的 translations 记不记得译文出自哪个语言文件、同一句在这个 mod 里出现过几次。
    /// 同 <see cref="TypeFieldsAreDictionary"/>,**靠列名认**。
    /// </summary>
    private bool TranslationsHaveSourceFile => _trSf ??= HasColumn("translations", "source_file");
    private bool? _trSf;

    /// <summary>
    /// 这份库有 injection_keys 这张表吗。<c>PRAGMA table_info</c> 对不存在的表回零行,
    /// 于是探一列就够 —— 能力位说的是「导出带没带这一层」,这个探的是「库里建没建」,
    /// 两者都得真才敢读。
    /// </summary>
    /// <summary>
    /// 这份库的 translations 带不带「译者写的那一串」与归一的四态。
    /// 同 <see cref="TranslationsHaveSourceFile"/>,**靠列名认**。
    /// </summary>
    private bool TranslationsHaveKey => _trKey ??= HasColumn("translations", "key_state");
    private bool? _trKey;

    /// <summary>注入键层在场:版本位到了**且**表长成那个形状(0.8.0 有表却答不出它要答的问题)。</summary>
    public bool InjectionKeysIndexed => Meta.IndexesInjectionKeys && HasInjectionKeys;

    private bool HasInjectionKeys => _ik ??= HasColumn("injection_keys", "suggested_path")
                                          || InjectionKeysAreDictionary;
    private bool? _ik;

    /// <summary>
    /// 这份库的名册是不是四列全进字典的那一版。同 <see cref="TypeFieldsAreSubtrees"/>,靠列名认 ——
    /// <c>--keep</c> 留下的旧代永远是老形状,而它们唯一的用途正是拿来 diff。
    /// </summary>
    private bool InjectionKeysAreDictionary
        => _ikDict ??= HasColumn("injection_keys", "suggested_path_id");
    private bool? _ikDict;

    private bool? _hasTruncationBreakdown;

    /// <summary>
    /// 这份库分不分得清「为什么被截」。假 = 0.13.0 之前导的,四种截断在库里只有一个总数 ——
    /// 那种库上呈现侧只说得出总数,**不许把缺席印成四个零**。
    /// </summary>
    public bool DefsHaveTruncationBreakdown
        => _hasTruncationBreakdown ??= HasColumn("defs", "truncated_by_cap");

    /// <summary>
    /// 这份库里**真有**分好类的截断。列在不在与列里有没有数是两件事:0.13.0 之前导出的
    /// 那份文件进了新库,四列拿的是 DEFAULT 0,于是「有那四列」为真而一个成因也答不出来。
    /// 指路句问的是后者 —— 指向一句它印不出来的话,比不指路更糟。
    /// </summary>
    public bool TruncationCausesMeasured
        => _causesMeasured ??= DefsHaveTruncationBreakdown
           && Scalar("SELECT COUNT(*) FROM defs WHERE truncated_by_cap + truncated_by_length "
                         + "+ truncated_by_depth + truncated_by_items > 0") > 0;

    private bool? _causesMeasured;

    /// <summary>被截过的一批 def 分成两拨:真少了路径的,与只有值被切、一条路径没少的。</summary>
    public readonly record struct TruncationSpread(int LostPaths, int ValuesOnly);

    /// <summary>
    /// 把被截过的 def 分成这两拨。null = 这份库没分类过,那时只答得出一个总数。
    ///
    /// 分开数不是为了好看:七个官方快照上「只有值被切」占 27 个里的 22 个,而按总数
    /// 说出去的那句「这些 def 缺了路径」对那 22 个是假的 —— 它们的行都在表里。
    /// </summary>
    public TruncationSpread? TruncatedDefSpread()
    {
        if (!TruncationCausesMeasured) return null;
        const string missing = "truncated_by_cap + truncated_by_depth + truncated_by_items";
        return new TruncationSpread(
            Scalar($"SELECT COUNT(*) FROM defs WHERE {missing} > 0"),
            Scalar($"SELECT COUNT(*) FROM defs WHERE fields_truncated > 0 AND {missing} = 0"));
    }

    /// <summary>
    /// 一个 def 的截断成因。null = 这份库没分类过(不是四类都为零)。
    ///
    /// 单独一次查而不是挂在 <see cref="DefRow"/> 上:问这件事的只有「这一个 def 被截了」
    /// 那一句,而 DefColumns 的列序被几处硬编码的下标依赖着,往里加列会静默错位。
    /// </summary>
    public TruncationCauses? TruncationCausesFor(long defId)
    {
        if (!DefsHaveTruncationBreakdown) return null;
        using var rd = Query(
            "SELECT truncated_by_cap, truncated_by_length, truncated_by_depth, truncated_by_items "
            + "FROM defs WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = defId });
        if (!rd.Read()) return null;
        return new TruncationCauses(rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3));
    }

    private bool HasColumn(string table, string column)
    {
        using var rd = Query($"PRAGMA table_info({table})", new Dictionary<string, object?>());
        while (rd.Read())
            if (string.Equals(rd.GetString(1), column, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 磁盘上有三种形状,三条路给的是同一份东西(见 SnapshotSchema 的 type_names 那段)。
    /// 子树那条不必去重:每条路径恰属一个首段,所以同一类型下两棵子树的路径集不相交。
    /// </summary>
    private (string PathExpr, string From, string Where) TypeDeclaredSource()
    {
        if (TypeFieldsAreSubtrees)
            return ("d.path",
                    "type_names n "
                    + "JOIN type_subtrees ts ON ts.type_id = n.id "
                    + "JOIN subtree_paths sp ON sp.subtree_id = ts.subtree_id "
                    + "JOIN type_field_paths d ON d.id = sp.path_id",
                    TypeDeclaredPathsWhere);

        var dict = TypeFieldsAreDictionary;
        return (dict ? "d.path" : "t.path",
                dict ? "type_fields t JOIN type_field_paths d ON d.id = t.path_id" : "type_fields t",
                TypeDeclaredPathsLegacyWhere);
    }

    /// <summary>
    /// 这个类型的声明路径里最深的那条有几段(点与下标各算一段)。
    ///
    /// 「这个类型不声明这样的字段」是一句**确定的否定**,而它的依据是一张按递归深度收的表:
    /// 类型图不是树也不是 DAG(实测有一个 471 个类型的强连通分量),摊平成路径不存在
    /// 「展开完」这回事。所以那句话必须带上自己的量程 —— 缺了它,「嵌套过深所以没测到」
    /// 与「这个类型真没这个字段」在输出里完全同形。产地 Docs/22 第 9.3 / 12.2 节。
    ///
    /// 只在落空那条路上算一次:那条路本来就是贵的一条,而它正是最需要这个数的地方。
    /// </summary>
    public int? TypeDeclaredPathMaxSegments(string defType)
    {
        if (!Meta.IndexesTypeFields) return null;
        var (pathExpr, from, where) = TypeDeclaredSource();
        var p = new Dictionary<string, object?> { ["@t"] = defType };
        using var rd = Query(
            $"SELECT MAX(LENGTH({pathExpr}) - LENGTH(REPLACE({pathExpr}, '.', '')) + "
            + $"LENGTH({pathExpr}) - LENGTH(REPLACE({pathExpr}, '[', '')) + 1) "
            + $"FROM {from} {where}", p);
        if (!rd.Read() || rd.IsDBNull(0)) return null;
        return rd.GetInt32(0);
    }

    public IReadOnlyList<string>? TypeDeclaredPaths(string defType, IReadOnlyList<string>? pathFilters = null,
                                                    bool exactPath = false)
    {
        if (!Meta.IndexesTypeFields) return null;

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
