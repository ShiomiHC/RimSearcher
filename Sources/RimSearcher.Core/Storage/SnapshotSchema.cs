using Microsoft.Data.Sqlite;

namespace RimSearcher.Storage;

/// <summary>
/// 快照库 schema。**不兼容上游 db** —— 自立 schema_version,读到无 meta 或版本不符的库
/// 就拒读并指导重导(错误消息不含本机路径,留发布缝)。
/// </summary>
public static class SnapshotSchema
{
    /// <summary>schema 版本。表结构变化时 +1。</summary>
    public const int Version = 1;

    public const string MetaKeySchemaVersion = "schema_version";
    public const string MetaKeyRaw = "export_meta_json";
    public const string MetaKeyFingerprint = "fingerprint";
    public const string MetaKeyImportedAtUtc = "imported_at_utc";
    public const string MetaKeyDefCount = "def_count";
    public const string MetaKeySourcePath = "source_file";

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
            parent           TEXT,
            fields_truncated INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE field_values (
            def_id INTEGER NOT NULL,
            path   TEXT NOT NULL,
            leaf   TEXT NOT NULL,
            value  TEXT
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
        """;

    /// <summary>索引在批量插入之后才建 —— 导入是一次性写,先建索引会显著变慢。</summary>
    public const string Indexes = """
        CREATE INDEX idx_defs_name  ON defs(def_name);
        CREATE INDEX idx_defs_type  ON defs(def_type);
        CREATE INDEX idx_defs_mod   ON defs(source_mod);
        CREATE INDEX idx_fv_def     ON field_values(def_id);
        CREATE INDEX idx_fv_leaf    ON field_values(leaf);
        CREATE INDEX idx_fv_value   ON field_values(value);
        CREATE INDEX idx_tr_defname ON translations(def_name);
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
