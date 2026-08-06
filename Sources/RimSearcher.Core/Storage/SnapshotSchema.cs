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
    /// </remarks>
    public const int Version = 7;

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
            patch_ops   INTEGER NOT NULL DEFAULT 0
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
        CREATE INDEX idx_sv_type    ON shared_values(def_type);
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
