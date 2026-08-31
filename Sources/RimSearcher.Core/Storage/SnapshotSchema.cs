using Microsoft.Data.Sqlite;

namespace RimSearcher.Storage;

/// <summary>
/// 快照库 schema。**不兼容上游 db** —— 自立 schema_version,读到无 meta 或版本不符的库
/// 就拒读并指导重导(错误消息不含本机路径,留发布缝)。
/// </summary>
public static class SnapshotSchema
{
    /// <summary>schema 版本。表结构变化时 +1。</summary>
    /// <remarks>
    /// 3:加了 xml_nodes 继承层。
    /// 4:field_values 加了 is_default —— 一条值与 C# 声明默认值的关系。
    /// 5:加了 keyed 表 —— 界面文案那一层译文。
    /// 6:加了 shared_values —— 一条值在同类型里有多普遍。
    /// 7:加了 harvested_roots —— 磁盘那一层**量没量过**,见下。
    /// 8:加了 economy 三表 + economy_state —— 经济面,以及它**量没量成**,见下。
    ///
    /// 0.5.0 起 xml_nodes 多了 patch_ops_defname / patch_ops_label,并加 xml_written
    /// 与 type_fields 两张表。不涨这一档:精确相等的 schema 检查会让磁盘上的旧库
    /// 整份打不开,而缺的那一层由导出器版本上的能力位说话(同 content_fingerprint
    /// 那条缝)。新导入的库有这些列/表;旧库没有,查询侧能力位为假时不去碰它们。
    /// </remarks>
    public const int Version = 8;

    public const string MetaKeySchemaVersion = "schema_version";
    public const string MetaKeyRaw = "export_meta_json";
    public const string MetaKeyFingerprint = "fingerprint";
    public const string MetaKeyImportedAtUtc = "imported_at_utc";
    public const string MetaKeyDefCount = "def_count";
    public const string MetaKeySourcePath = "source_file";

    /// <summary>
    /// 这次导入扫了几个 mod 根目录去收割磁盘上的语言文件。<c>0</c> 就是**一个都没扫**。
    ///
    /// 「磁盘那一层一行都没有」有两个成因:根本没量过(下一步重导),和量过了确实没有。
    /// 收割虽是默认行为,但可被 <c>--no-harvest-translations</c> 关掉,也可能因没配
    /// <c>mod_roots</c> 而没得扫,所以要记下来。
    /// </summary>
    public const string MetaKeyHarvestedRoots = "harvested_roots";

    /// <summary>
    /// 导出那一刻各 mod 的 Defs/Patches 指纹(<see cref="Snapshot.ContentScan"/> 的 JSON)。
    ///
    /// **缺席是有意义的一态**,不是坏库:这个键是后加的,先前建的库里没有它,
    /// 而那些库对「mod 的 XML 后来改没改」没有资格回答。缺席时这条判据整个不说话 ——
    /// 没量过与量过了没变必须分得开(同 <see cref="MetaKeyHarvestedRoots"/> 那条缝)。
    /// 所以它没有涨 schema_version:旧库照旧能读,只是少一条判据。
    /// </summary>
    public const string MetaKeyContent = "content_fingerprint";

    /// <summary>
    /// 经济面这一次到底量没量成:<c>ok</c> / <c>skipped</c> / <c>unavailable</c>,
    /// 原样取自导出尾行(<see cref="Contract.IntermediateFormat.KeyEconomyState"/>)。
    ///
    /// **缺席是第四态**,和上面两条同一道缝:这个键不在 = 这份库建于经济面进导出之前,
    /// 它对「游戏说这东西值多少钱」没有资格回答。所以查询侧的判据是这个键,
    /// **不是 <c>SELECT COUNT(*) FROM economy</c>** —— 计数把四种成因压成同一个零,
    /// 而其中一种(量过了、这个名单下确实没有可生产物)是完整的肯定回答。
    /// </summary>
    public const string MetaKeyEconomyState = "economy_state";

    /// <summary>
    /// <c>unavailable</c> 时点名缺了什么(vanilla 的哪个签名)。原样端给用户 ——
    /// 这一层不回退到自写实现,所以这句话是唯一的下一步。
    /// </summary>
    public const string MetaKeyEconomyError = "economy_error";

    public const string Ddl = """
        PRAGMA journal_mode = OFF;
        PRAGMA synchronous  = OFF;

        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE defs (
            id               INTEGER PRIMARY KEY,
            def_type         TEXT NOT NULL,
            def_name         TEXT NOT NULL,
            label            TEXT,
            description      TEXT,
            source_mod       TEXT,
            source_file      TEXT,
            generated        INTEGER NOT NULL DEFAULT 0,
            class            TEXT,
            fields_truncated INTEGER NOT NULL DEFAULT 0
        );

        -- is_default:这一行与「这个类型刚 new 出来时」的关系,取值见 IntermediateFormat.DefaultState
        -- (0 一定被改过 / 1 与代码默认值无从区分 / 2 没法比)。存原样而不是存 bool ——
        -- 「没法比」并进任何一边都会让呈现侧说出一句它证不了的话(R1)。
        CREATE TABLE field_values (
            def_id     INTEGER NOT NULL,
            path       TEXT NOT NULL,
            leaf       TEXT NOT NULL,
            value      TEXT,
            is_default INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE translations (
            def_id     INTEGER,
            def_type   TEXT,
            def_name   TEXT NOT NULL,
            path       TEXT NOT NULL,
            translated TEXT,
            original   TEXT,
            language   TEXT,
            source_mod TEXT,
            origin     TEXT NOT NULL
        );

        -- Keyed 译文 —— 界面文案。**这张表里一行都不属于任何 def**:key 是
        -- `"SomeKey".Translate()` 里那个 SomeKey,不带点、没有类型维、与 defName 无关。
        -- 所以它不能挤进 translations(那张表的主键形状是 def_name + path),也没有 def_id。
        --
        -- placeholder:语言包里有这个 key 但值是占位 —— 它实际显示的是英文,而在表里
        -- 与真译文同形。不带出来的话,「没译」就与「没有这个 key」分不开了。
        -- 覆盖冲突只存赢家(用户裁决):source_file/source_line 说清最终生效的那一句出自哪里。
        CREATE TABLE keyed (
            id          INTEGER PRIMARY KEY,
            key         TEXT NOT NULL,
            translated  TEXT,
            original    TEXT,
            language    TEXT,
            source_file TEXT,
            source_line INTEGER NOT NULL DEFAULT 0,
            source_mod  TEXT,
            placeholder INTEGER NOT NULL DEFAULT 0,
            origin      TEXT NOT NULL
        );

        -- 继承层。**唯一一张不是「游戏内存里的对象」的表** —— 它是打补丁之前的 XML 原文,
        -- 因为「谁继承谁」在导出时点已经被 XmlInheritance.Clear() 抹掉了。
        -- patch_ops 让这份时间差逐条可见,而不是靠一句总的免责声明糊过去。
        CREATE TABLE xml_nodes (
            id          INTEGER PRIMARY KEY,
            def_type    TEXT NOT NULL,
            name        TEXT,
            parent_name TEXT,
            abstract    INTEGER NOT NULL DEFAULT 0,
            def_name    TEXT,
            label       TEXT,
            source_mod  TEXT,
            source_file TEXT,
            patch_ops   INTEGER NOT NULL DEFAULT 0,
            -- 0.5.0 起才有值。旧库没有这两列,查询侧靠能力位决定读不读,不许把缺列当 0。
            patch_ops_defname INTEGER NOT NULL DEFAULT 0,
            patch_ops_label   INTEGER NOT NULL DEFAULT 0
        );

        -- 每个 XML 节点(含不参与继承的普通 def)实际写出来的字段路径。
        -- patch 之前的原文,用来回答 Replace 还是 Add。
        CREATE TABLE xml_written (
            def_type    TEXT NOT NULL,
            node_key    TEXT NOT NULL,
            key_is_name INTEGER NOT NULL DEFAULT 0,
            path        TEXT NOT NULL
        );

        -- 一个 def 类型能有的字段路径全集,与值无关。用来把「全是 null」和「没有这个字段」分开。
        CREATE TABLE type_fields (
            def_type TEXT NOT NULL,
            path     TEXT NOT NULL
        );

        -- 一条「与新实例不同」的值,在同类型的 def 里有多普遍。
        --
        -- code_default 只证得了「与刚 new 出来的实例不同」,而 ResolveReferences 会给
        -- 同类型的每个 def 都塞上同一个值(如 ThingDef.soundImpactDefault)—— 那种行读起来
        -- 与「有人专门给这个 def 挑了这个值」一模一样。
        --
        -- 分不清「XML 写的」与「引擎事后填的」:那要在 ResolveReferences 前后各取一次值,
        -- 而导出跑在 StaticConstructorOnStartup、resolve 早已做完,插进去只能上 Harmony,
        -- 而 DataMod 刻意无依赖。所以不猜成因,只报可核对的事实:同类型里有多少个 def
        -- 也是这个值。
        --
        -- 只收「过半且不少于 8 个」的组 —— 类型只有三五个 def 时「大多数」不成话。
        CREATE TABLE shared_values (
            def_type TEXT NOT NULL,
            path     TEXT NOT NULL,
            value    TEXT,
            defs     INTEGER NOT NULL
        );

        -- 经济面。外延与 vanilla DebugOutputsEconomy.ItemAndBuildingAcquisition 的 where
        -- 子句逐字一致(有市场价的物品 + 玩家可建或可小型化的建筑),偏离它会让分布分位数
        -- 无法与游戏内那张表对照。
        --
        -- 不与 defs 建外键:这一层的消费方(分布统计、对照池切分)全程只用 def_name 与 mod,
        -- 而 translations 那套「按名字找回 def id」在这里是纯开销。
        --
        -- **列一律可空、不设 DEFAULT 0** —— NULL 与 0 在这张表里处处是两件事:
        -- 工时为 0 的利润率、没有成本链的链尾占比、vanilla 自己都算不出的推算价,
        -- 给一个 0 就等于给了一个会被统计进分布的数。
        --
        -- REAL 而不是 TEXT:定点两位是**中间格式**那一侧的规则(防科学计数法与 locale
        -- 小数点),进了库还存字符串只会让排序与分位数查询变成字符串比较。
        CREATE TABLE economy (
            id                      INTEGER PRIMARY KEY,
            def_name                TEXT NOT NULL,
            label                   TEXT,
            category                TEXT,
            mod                     TEXT,
            market_value            REAL,
            producible              INTEGER NOT NULL,
            made_from_stuff         INTEGER NOT NULL,
            is_weapon               INTEGER NOT NULL,
            is_apparel              INTEGER NOT NULL,
            market_value_defined    INTEGER NOT NULL,
            -- 四态:not_producible / used / recipe / ok。前两态的 calculated_market_value
            -- 为 NULL —— 两种「空」混成一个,消费侧就会把它们统计进分布、拉低整段分位。
            calc_state              TEXT NOT NULL,
            calculated_market_value REAL,
            cost_to_make            REAL,
            profit                  REAL,
            profit_rate             REAL,
            work_to_produce         REAL,
            cost_list               TEXT,
            -- 成本表有一支由难度开关决定的变体时,这里是那个开关名;NULL = 没有变体。
            -- 导出跑在没有 storyteller 的那一刻,而 CostListForDifficulty.Applies 在那时
            -- 无条件为假 —— 于是上面几列**永远是非变体那支**,而变体存不存在在数字上看不出来。
            -- inverted 为真时变体在开关关着时生效(vanilla 的 Turret_Mortar),那正是绝大多数
            -- 存档的状态,于是这一行印出来的数是玩家基本见不到的那一支。
            cost_difficulty_var     TEXT,
            cost_difficulty_inverted INTEGER NOT NULL DEFAULT 0,
            -- 1.0 = 造价全部来自链尾物**手填的**市场价,该行 profit 不反映真实生产消耗。
            chain_end_share         REAL,
            -- 自有指标,不是 vanilla 的量。命名与 cost_to_make 分开是硬要求。
            cost_deep               REAL,
            profit_deep             REAL
        );

        -- 成本链逐项。拆表而不是在 economy 上塞一列 JSON:「谁的成本链里有 Steel」是会被
        -- 问到的反查方向,而 JSON 列上问它只能全表扫 + 字符串匹配。
        CREATE TABLE economy_cost_chain (
            economy_id INTEGER NOT NULL,
            ordinal    INTEGER NOT NULL,
            thing_def  TEXT NOT NULL,
            count      INTEGER NOT NULL,
            unit_value REAL,
            -- 这一项自己没有 recipeMaker —— vanilla CostToMake 的递归在它身上停住。
            chain_end  INTEGER NOT NULL
        );

        -- 能产出同一个物的全部配方。行数 > 1 即表示该物的 calculated_market_value 有
        -- **加载顺序依赖**:CalculableRecipe 返回 DefDatabase 里第一个匹配。
        CREATE TABLE economy_recipes (
            economy_id       INTEGER NOT NULL,
            ordinal          INTEGER NOT NULL,
            def_name         TEXT NOT NULL,
            product_count    INTEGER,
            work_amount      REAL,
            self_referential INTEGER NOT NULL
        );

        CREATE TABLE mods (
            ordinal    INTEGER PRIMARY KEY,
            package_id TEXT NOT NULL,
            name       TEXT,
            version    TEXT
        );

        CREATE VIRTUAL TABLE defs_fts USING fts5(
            def_name, label, description, translated,
            content = '', prefix = '2 3', tokenize = 'unicode61'
        );

        -- keyed 自己的 FTS。**不能并进 defs_fts**:那张表的 rowid 是 def 的 id,
        -- 而 keyed 的行没有 def —— 借用别人的 rowid 空间会让两边的命中互相冒充。
        CREATE VIRTUAL TABLE keyed_fts USING fts5(
            key, translated, original,
            content = '', prefix = '2 3', tokenize = 'unicode61'
        );
        """;

    /// <summary>索引在批量插入之后才建 —— 导入是一次性写,先建索引会显著变慢。</summary>
    public const string Indexes = """
        CREATE INDEX idx_defs_name  ON defs(def_name);
        CREATE INDEX idx_defs_type  ON defs(def_type);
        CREATE INDEX idx_defs_mod   ON defs(source_mod);
        CREATE INDEX idx_fv_def     ON field_values(def_id);
        CREATE INDEX idx_fv_leaf    ON field_values(leaf);
        CREATE INDEX idx_fv_value   ON field_values(value);
        -- 查这两列一律带 COLLATE NOCASE(见 SnapshotDb 的 PathCondition / ValueWhere),
        -- 而上面两条是 BINARY 的:collation 不匹配时 SQLite 不用索引,于是每条谓词都全表扫。
        -- 加一对 NOCASE 的而不是改上面两条 —— DistinctValues 的 DISTINCT/GROUP BY fv.value
        -- 是 BINARY,改掉就轮到它失去索引。
        CREATE INDEX idx_fv_leaf_nc  ON field_values(leaf COLLATE NOCASE);
        CREATE INDEX idx_fv_value_nc ON field_values(value COLLATE NOCASE);
        CREATE INDEX idx_tr_defname ON translations(def_name);
        CREATE INDEX idx_keyed_key   ON keyed(key);
        CREATE INDEX idx_xn_name    ON xml_nodes(name);
        CREATE INDEX idx_xn_parent  ON xml_nodes(parent_name);
        CREATE INDEX idx_xn_defname ON xml_nodes(def_name);
        CREATE INDEX idx_xw_key     ON xml_written(def_type, node_key);
        CREATE INDEX idx_xw_path    ON xml_written(path);
        CREATE INDEX idx_tf_type    ON type_fields(def_type);
        CREATE INDEX idx_tf_path    ON type_fields(path);
        CREATE INDEX idx_sv_type    ON shared_values(def_type);
        CREATE INDEX idx_econ_name  ON economy(def_name);
        CREATE INDEX idx_econ_mod   ON economy(mod);
        CREATE INDEX idx_econ_chain ON economy_cost_chain(economy_id);
        -- 反查方向:哪些物的成本链里有这个原料。
        CREATE INDEX idx_econ_chain_thing ON economy_cost_chain(thing_def);
        CREATE INDEX idx_econ_recipes ON economy_recipes(economy_id);
        """;

    public static void Create(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public static void CreateIndexes(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = Indexes;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>库不可读时抛这个 —— 消息面向调用方,指出下一步做什么。</summary>
public sealed class SnapshotFormatException(string message) : Exception(message);
