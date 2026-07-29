using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimSearcher.Contract;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

public sealed record ImportStats(
    int Defs, int FieldValues, int NoiseDropped, int RuntimeTranslations,
    int HarvestedTranslations, int TruncatedDefs, ExportMeta Meta, string DbPath);

/// <summary>
/// 中间格式 → SQLite。B 案把建库整个搬到这一侧,收益在 06「分工」一节记过:产地唯一由
/// 进程边界保证、策略变化免重导、建库逻辑进得了测试闸。
///
/// 原子性(02-6)的 import 侧一半:先写 temp db,建完 rename 替换。游戏侧那一半是尾行
/// 记录数标记 —— 这里读到尾标记才认账。
/// </summary>
public sealed class SnapshotImporter
{
    /// <summary>静态收割翻译时要扫的 mod 根目录(环境外 advisory 层)。空则跳过收割。</summary>
    public IReadOnlyList<string> ModRoots { get; init; } = [];

    public ImportStats Import(string exportPath, string dbPath)
    {
        var tempDb = dbPath + ".tmp";
        if (File.Exists(tempDb)) File.Delete(tempDb);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = tempDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        db.Open();
        SnapshotSchema.Create(db);

        ExportMeta? meta = null;
        var defs = 0; var fieldValues = 0; var noise = 0; var runtimeTr = 0; var truncatedDefs = 0;
        long? declaredRecords = null;
        var sawEnd = false;
        var records = 0L;

        using (var tx = db.BeginTransaction())
        {
            using var insertDef = Prepare(db, """
                INSERT INTO defs (id, def_type, def_name, label, description, source_mod, source_file,
                                  generated, class, fields_truncated)
                VALUES ($id,$t,$n,$l,$d,$sm,$sf,$g,$c,$ft)
                """);
            using var insertFv = Prepare(db, "INSERT INTO field_values (def_id, path, leaf, value) VALUES ($id,$p,$lf,$v)");
            using var insertFts = Prepare(db, "INSERT INTO defs_fts (rowid, def_name, label, description, translated) VALUES ($id,$n,$l,$d,$tr)");
            using var insertTr = Prepare(db, """
                INSERT INTO translations (def_id, def_type, def_name, path, translated, original, language, source_mod, origin)
                VALUES ($id,$t,$n,$p,$tr,$o,$lang,$sm,$origin)
                """);

            var idByName = new Dictionary<string, long>(StringComparer.Ordinal);
            var ftsExtra = new Dictionary<long, List<string>>();
            var pendingInjections = new List<(string defName, string defType, string path, string translated, string original)>();
            long nextId = 1;

            foreach (var line in ReadLines(exportPath))
            {
                records++;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var kind = root.GetProperty(IntermediateFormat.KeyKind).GetString();

                if (kind == IntermediateFormat.KindMeta)
                {
                    meta = ExportMeta.Parse(line);
                    continue;
                }

                if (kind == IntermediateFormat.KindEnd)
                {
                    sawEnd = true;
                    declaredRecords = root.TryGetProperty(IntermediateFormat.KeyRecords, out var r) ? r.GetInt64() : null;
                    continue;
                }

                if (meta is null)
                    throw new SnapshotFormatError(
                        "The export file has data lines before its meta line. Re-run the export.");

                if (kind == IntermediateFormat.KindDef)
                {
                    var id = nextId++;
                    var defName = Str(root, IntermediateFormat.KeyDefName) ?? "";
                    idByName[defName] = id;

                    var truncated = root.TryGetProperty(IntermediateFormat.KeyFieldsTruncated, out var ftEl)
                        ? ftEl.GetInt32() : 0;
                    if (truncated > 0) truncatedDefs++;

                    Bind(insertDef, "$id", id);
                    Bind(insertDef, "$t", Str(root, IntermediateFormat.KeyDefType));
                    Bind(insertDef, "$n", defName);
                    Bind(insertDef, "$l", Str(root, IntermediateFormat.KeyLabel));
                    Bind(insertDef, "$d", Str(root, IntermediateFormat.KeyDescription));
                    Bind(insertDef, "$sm", Str(root, IntermediateFormat.KeySourceMod));
                    Bind(insertDef, "$sf", Str(root, IntermediateFormat.KeySourceFile));
                    Bind(insertDef, "$g", root.TryGetProperty(IntermediateFormat.KeyGenerated, out var gEl) && gEl.GetBoolean() ? 1 : 0);
                    Bind(insertDef, "$c", Str(root, IntermediateFormat.KeyClass));
                    Bind(insertDef, "$ft", truncated);
                    insertDef.ExecuteNonQuery();
                    defs++;

                    if (root.TryGetProperty(IntermediateFormat.KeyFields, out var fields) &&
                        fields.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pair in fields.EnumerateArray())
                        {
                            if (pair.GetArrayLength() < 2) continue;
                            var path = pair[0].GetString() ?? "";
                            if (NoiseFilter.IsNoise(path)) { noise++; continue; }
                            Bind(insertFv, "$id", id);
                            Bind(insertFv, "$p", path);
                            Bind(insertFv, "$lf", NoiseFilter.Leaf(path));
                            Bind(insertFv, "$v", pair[1].GetString());
                            insertFv.ExecuteNonQuery();
                            fieldValues++;
                        }
                    }

                    Bind(insertFts, "$id", id);
                    Bind(insertFts, "$n", FtsText.ForIndex(defName, identifier: true));
                    Bind(insertFts, "$l", FtsText.ForIndex(Str(root, IntermediateFormat.KeyLabel)));
                    Bind(insertFts, "$d", FtsText.ForIndex(Str(root, IntermediateFormat.KeyDescription)));
                    Bind(insertFts, "$tr", "");
                    insertFts.ExecuteNonQuery();
                    continue;
                }

                if (kind == IntermediateFormat.KindDefInjection)
                {
                    pendingInjections.Add((
                        Str(root, IntermediateFormat.KeyDefName) ?? "",
                        Str(root, IntermediateFormat.KeyDefType) ?? "",
                        Str(root, IntermediateFormat.KeyPath) ?? "",
                        Str(root, IntermediateFormat.KeyTranslated) ?? "",
                        Str(root, IntermediateFormat.KeyOriginal) ?? ""));
                }
            }

            if (meta is null)
                throw new SnapshotFormatError("The export file has no meta line; it cannot be identified. Re-run the export.");

            if (!sawEnd)
                throw new SnapshotFormatError(
                    "The export file has no end marker, which means the game did not finish writing it " +
                    "(a crash or a forced exit mid-export). Run the export again; a partial file is refused " +
                    "rather than imported silently.");

            if (declaredRecords is { } dr && dr != records)
                throw new SnapshotFormatError(
                    $"The export file declares {dr} records but {records} were read. The file is damaged; re-run the export.");

            foreach (var inj in pendingInjections)
            {
                idByName.TryGetValue(inj.defName, out var defId);
                Bind(insertTr, "$id", defId == 0 ? null : defId);
                Bind(insertTr, "$t", inj.defType);
                Bind(insertTr, "$n", inj.defName);
                Bind(insertTr, "$p", inj.path);
                Bind(insertTr, "$tr", inj.translated);
                Bind(insertTr, "$o", inj.original);
                Bind(insertTr, "$lang", meta.Language);
                Bind(insertTr, "$sm", null);
                Bind(insertTr, "$origin", TranslationOrigin.Runtime);
                insertTr.ExecuteNonQuery();
                runtimeTr++;
                if (defId != 0)
                    (ftsExtra.TryGetValue(defId, out var l) ? l : ftsExtra[defId] = []).Add(inj.translated);
            }

            var harvested = HarvestStaticTranslations(insertTr, idByName, meta, ftsExtra);

            // 翻译文本回填进 FTS 的 translated 列(双语索引)
            using (var updFts = Prepare(db, "INSERT INTO defs_fts (defs_fts, rowid, def_name, label, description, translated) VALUES ('delete',$id,$n0,$l0,$d0,$t0)"))
            using (var read = Prepare(db, "SELECT def_name, label, description FROM defs WHERE id = $id"))
            using (var reIns = Prepare(db, "INSERT INTO defs_fts (rowid, def_name, label, description, translated) VALUES ($id,$n,$l,$d,$tr)"))
            {
                foreach (var (defId, texts) in ftsExtra)
                {
                    Bind(read, "$id", defId);
                    using var rd = read.ExecuteReader();
                    if (!rd.Read()) continue;
                    var n = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    var l = rd.IsDBNull(1) ? "" : rd.GetString(1);
                    var d = rd.IsDBNull(2) ? "" : rd.GetString(2);
                    rd.Close();

                    Bind(updFts, "$id", defId);
                    Bind(updFts, "$n0", FtsText.ForIndex(n, identifier: true));
                    Bind(updFts, "$l0", FtsText.ForIndex(l));
                    Bind(updFts, "$d0", FtsText.ForIndex(d));
                    Bind(updFts, "$t0", "");
                    updFts.ExecuteNonQuery();

                    Bind(reIns, "$id", defId);
                    Bind(reIns, "$n", FtsText.ForIndex(n, identifier: true));
                    Bind(reIns, "$l", FtsText.ForIndex(l));
                    Bind(reIns, "$d", FtsText.ForIndex(d));
                    Bind(reIns, "$tr", FtsText.ForIndex(string.Join(" ", texts.Distinct())));
                    reIns.ExecuteNonQuery();
                }
            }

            using (var insertMod = Prepare(db, "INSERT INTO mods (ordinal, package_id, name, version) VALUES ($o,$p,$n,$v)"))
            {
                var ord = 0;
                foreach (var m in meta.Mods)
                {
                    Bind(insertMod, "$o", ord++);
                    Bind(insertMod, "$p", m.PackageId);
                    Bind(insertMod, "$n", m.Name);
                    Bind(insertMod, "$v", m.Version);
                    insertMod.ExecuteNonQuery();
                }
            }

            using (var insertMeta = Prepare(db, "INSERT INTO meta (key, value) VALUES ($k,$v)"))
            {
                void Put(string k, string? v) { Bind(insertMeta, "$k", k); Bind(insertMeta, "$v", v); insertMeta.ExecuteNonQuery(); }
                Put(SnapshotSchema.MetaKeySchemaVersion, SnapshotSchema.Version.ToString());
                Put(SnapshotSchema.MetaKeyRaw, meta.RawJson);
                Put(SnapshotSchema.MetaKeyFingerprint, meta.Fingerprint);
                Put(SnapshotSchema.MetaKeyImportedAtUtc, DateTime.UtcNow.ToString("O"));
                Put(SnapshotSchema.MetaKeyDefCount, defs.ToString());
                Put(SnapshotSchema.MetaKeySourcePath, Path.GetFileName(exportPath));
            }

            tx.Commit();

            SnapshotSchema.CreateIndexes(db);
            using (var vac = db.CreateCommand()) { vac.CommandText = "PRAGMA optimize;"; vac.ExecuteNonQuery(); }

            db.Close();
            SqliteConnection.ClearAllPools();

            if (File.Exists(dbPath)) File.Delete(dbPath);
            File.Move(tempDb, dbPath);

            return new ImportStats(defs, fieldValues, noise, runtimeTr, harvested, truncatedDefs, meta, dbPath);
        }
    }

    /// <summary>
    /// 静态收割(第二轮裁决 8 的 advisory 层):扫**所有已装 mod** 的
    /// <c>Languages/&lt;快照语言&gt;/DefInjected/</c>,只留 defName 命中快照内 def 的条目。
    ///
    /// 要点:不判「这是不是翻译 mod」——没有判据,也不需要。目标 mod 自带的翻译与第三方
    /// 汉化包一视同仁,不相干的条目被 defName 过滤自然掉出去。**不替换任何字段值**,
    /// 纯检索召回,所以同路径多译文并存不构成冲突。
    /// </summary>
    private int HarvestStaticTranslations(SqliteCommand insertTr, Dictionary<string, long> idByName,
                                          ExportMeta meta, Dictionary<long, List<string>> ftsExtra)
    {
        if (ModRoots.Count == 0) return 0;
        var count = 0;
        var runtimeMods = meta.Mods.Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ModRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var modDir in SafeDirs(root))
            {
                var packageId = ReadPackageId(modDir) ?? Path.GetFileName(modDir);
                foreach (var injDir in FindDefInjectedDirs(modDir, meta.Language))
                {
                    foreach (var xml in SafeFiles(injDir, "*.xml"))
                    {
                        foreach (var (key, text) in ReadInjectionFile(xml))
                        {
                            var dot = key.IndexOf('.');
                            if (dot <= 0) continue;
                            var defName = key[..dot];
                            var path = key[(dot + 1)..];
                            if (!idByName.TryGetValue(defName, out var defId)) continue;

                            Bind(insertTr, "$id", defId);
                            Bind(insertTr, "$t", null);
                            Bind(insertTr, "$n", defName);
                            Bind(insertTr, "$p", path);
                            Bind(insertTr, "$tr", text);
                            Bind(insertTr, "$o", null);
                            Bind(insertTr, "$lang", meta.Language);
                            Bind(insertTr, "$sm", packageId);
                            Bind(insertTr, "$origin", runtimeMods.Contains(packageId)
                                ? TranslationOrigin.Harvested
                                : TranslationOrigin.HarvestedOutside);
                            insertTr.ExecuteNonQuery();
                            count++;
                            (ftsExtra.TryGetValue(defId, out var l) ? l : ftsExtra[defId] = []).Add(text);
                        }
                    }
                }
            }
        }
        return count;
    }

    private static IEnumerable<string> FindDefInjectedDirs(string modDir, string language)
    {
        foreach (var pattern in new[] { "Languages", "*/Languages" })
        {
            IEnumerable<string> langRoots;
            try
            {
                langRoots = pattern == "Languages"
                    ? (Directory.Exists(Path.Combine(modDir, "Languages")) ? [Path.Combine(modDir, "Languages")] : Array.Empty<string>())
                    : Directory.EnumerateDirectories(modDir).Select(d => Path.Combine(d, "Languages")).Where(Directory.Exists);
            }
            catch { continue; }

            foreach (var lr in langRoots)
            {
                var dir = Path.Combine(lr, language, "DefInjected");
                if (Directory.Exists(dir)) yield return dir;
            }
        }
    }

    private static IEnumerable<(string Key, string Text)> ReadInjectionFile(string path)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(path); }
        catch { yield break; }
        if (doc.Root is null) yield break;
        foreach (var el in doc.Root.Elements())
        {
            var text = el.Value;
            if (string.IsNullOrWhiteSpace(text)) continue;
            yield return (el.Name.LocalName, text);
        }
    }

    private static string? ReadPackageId(string modDir)
    {
        foreach (var candidate in new[] { Path.Combine(modDir, "About", "About.xml") })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(candidate);
                var id = doc.Root?.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "packageId", StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            }
            catch { /* About.xml 坏了不该让整次 import 失败 */ }
        }
        return null;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { return []; }
    }

    public static IEnumerable<string> ReadLines(string exportPath)
    {
        using var fs = File.OpenRead(exportPath);
        Stream stream = exportPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(fs, CompressionMode.Decompress)
            : fs;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0)
                yield return line;
    }

    private static string? Str(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static SqliteCommand Prepare(SqliteConnection db, string sql)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, string name, object? value)
    {
        if (cmd.Parameters.Contains(name)) cmd.Parameters[name].Value = value ?? DBNull.Value;
        else cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

public static class TranslationOrigin
{
    /// <summary>运行时 defInjection —— 快照环境内权威。</summary>
    public const string Runtime = "runtime";
    /// <summary>快照内 mod 的静态 DefInjected 文件。</summary>
    public const string Harvested = "harvested";
    /// <summary>快照**之外**已装 mod 的静态 DefInjected —— 仅供检索召回,不代表环境内会生效。</summary>
    public const string HarvestedOutside = "harvested_outside";
}
