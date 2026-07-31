using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimSearcher.Contract;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

public sealed record ImportStats(
    int Defs, int FieldValues, int NoiseDropped, int RuntimeTranslations,
    int HarvestedTranslations, int KeyedInEffect, int KeyedHarvested,
    int TruncatedDefs, int XmlNodes, ExportMeta Meta, string DbPath);

/// <summary>
/// 中间格式 → SQLite。建库整个在这一侧:产地唯一由进程边界保证,策略变化免重导。
///
/// 原子性的 import 侧一半:先写 temp db,建完 rename 替换。游戏侧那一半是尾行记录数标记 ——
/// 这里读到尾标记才认账。
/// </summary>
public sealed class SnapshotImporter
{
    /// <summary>静态收割翻译时要扫的 mod 根目录(环境外 advisory 层)。空则跳过收割。</summary>
    public IReadOnlyList<string> ModRoots { get; init; } = [];

    /// <summary>
    /// 参考侧 XML 指纹要用的环境。<c>null</c> 就不记那一层 —— 于是建出来的库对
    /// 「mod 的 Defs 后来改没改」不作答(<see cref="SnapshotSchema.MetaKeyContent"/>)。
    ///
    /// 与 <see cref="ModRoots"/> 分开一个字段,是因为 <c>--no-harvest-translations</c>
    /// 会把那个清空,而关掉翻译收割不该顺手把过期判据也关掉。
    /// </summary>
    public Config.RimConfig? Environment { get; init; }

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
        var xmlNodes = 0; var keyedInEffect = 0;
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
            using var insertFv = Prepare(db, "INSERT INTO field_values (def_id, path, leaf, value, is_default) VALUES ($id,$p,$lf,$v,$def)");
            using var insertFts = Prepare(db, "INSERT INTO defs_fts (rowid, def_name, label, description, translated) VALUES ($id,$n,$l,$d,$tr)");
            using var insertTr = Prepare(db, """
                INSERT INTO translations (def_id, def_type, def_name, path, translated, original, language, source_mod, origin)
                VALUES ($id,$t,$n,$p,$tr,$o,$lang,$sm,$origin)
                """);
            using var insertXn = Prepare(db, """
                INSERT INTO xml_nodes (def_type, name, parent_name, abstract, def_name, label,
                                       source_mod, source_file, patch_ops)
                VALUES ($t,$n,$pn,$a,$dn,$l,$sm,$sf,$po)
                """);
            using var insertKeyed = Prepare(db, """
                INSERT INTO keyed (id, key, translated, original, language, source_file, source_line,
                                   source_mod, placeholder, origin)
                VALUES ($id,$k,$tr,$o,$lang,$sf,$sl,$sm,$ph,$origin)
                """);
            using var insertKeyedFts = Prepare(db,
                "INSERT INTO keyed_fts (rowid, key, translated, original) VALUES ($id,$k,$tr,$o)");

            // 一个 defName 下可能挂着**几个** def(同名跨 def 类型是 RimWorld 常态),
            // 所以是 name → 列表:取单个 id 会让归属取决于导出顺序。
            var idsByName = new Dictionary<string, List<(long Id, string? Type)>>(StringComparer.Ordinal);
            var ftsExtra = new Dictionary<long, List<string>>();
            var pendingInjections = new List<(string defName, string defType, string path, string translated, string original)>();
            long nextId = 1;
            // keyed 自己的 id 序列。显式维护而不是问 last_insert_rowid():FTS 那一行要用同一个
            // rowid,而两条 INSERT 之间夹着别的语句。
            long nextKeyedId = 1;

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
                    var defTypeHere = Str(root, IntermediateFormat.KeyDefType);
                    (idsByName.TryGetValue(defName, out var sameName)
                        ? sameName
                        : idsByName[defName] = []).Add((id, defTypeHere));

                    var truncated = root.TryGetProperty(IntermediateFormat.KeyFieldsTruncated, out var ftEl)
                        ? ftEl.GetInt32() : 0;
                    if (truncated > 0) truncatedDefs++;

                    Bind(insertDef, "$id", id);
                    Bind(insertDef, "$t", defTypeHere);
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
                        foreach (var triple in fields.EnumerateArray())
                        {
                            if (triple.GetArrayLength() < 3) continue;
                            var path = triple[0].GetString() ?? "";
                            if (NoiseFilter.IsNoise(path)) { noise++; continue; }
                            Bind(insertFv, "$id", id);
                            Bind(insertFv, "$p", path);
                            Bind(insertFv, "$lf", NoiseFilter.Leaf(path));
                            Bind(insertFv, "$v", triple[1].GetString());
                            Bind(insertFv, "$def", triple[2].GetInt32());
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

                if (kind == IntermediateFormat.KindXmlNode)
                {
                    Bind(insertXn, "$t", Str(root, IntermediateFormat.KeyDefType));
                    Bind(insertXn, "$n", Str(root, IntermediateFormat.KeyName));
                    Bind(insertXn, "$pn", Str(root, IntermediateFormat.KeyParentName));
                    Bind(insertXn, "$a", root.TryGetProperty(IntermediateFormat.KeyAbstract, out var aEl) && aEl.GetBoolean() ? 1 : 0);
                    Bind(insertXn, "$dn", Str(root, IntermediateFormat.KeyDefName));
                    Bind(insertXn, "$l", Str(root, IntermediateFormat.KeyLabel));
                    Bind(insertXn, "$sm", Str(root, IntermediateFormat.KeySourceMod));
                    Bind(insertXn, "$sf", Str(root, IntermediateFormat.KeySourceFile));
                    Bind(insertXn, "$po", root.TryGetProperty(IntermediateFormat.KeyPatchOps, out var poEl) ? poEl.GetInt32() : 0);
                    insertXn.ExecuteNonQuery();
                    xmlNodes++;
                    continue;
                }

                // Keyed 行不依赖任何 def,所以不必像 definj 那样攒着等 id 表建完 —— 直接入库。
                if (kind == IntermediateFormat.KindKeyed)
                {
                    var kid = nextKeyedId++;
                    var key = Str(root, IntermediateFormat.KeyKeyedKey) ?? "";
                    if (key.Length == 0) continue;
                    var translated = Str(root, IntermediateFormat.KeyTranslated);
                    var original = Str(root, IntermediateFormat.KeyOriginal);

                    Bind(insertKeyed, "$id", kid);
                    Bind(insertKeyed, "$k", key);
                    Bind(insertKeyed, "$tr", translated);
                    Bind(insertKeyed, "$o", string.IsNullOrEmpty(original) ? null : original);
                    Bind(insertKeyed, "$lang", meta.Language);
                    Bind(insertKeyed, "$sf", Str(root, IntermediateFormat.KeySourceFile));
                    Bind(insertKeyed, "$sl", root.TryGetProperty(IntermediateFormat.KeySourceLine, out var slEl)
                        ? slEl.GetInt32() : 0);
                    Bind(insertKeyed, "$sm", null);
                    Bind(insertKeyed, "$ph", root.TryGetProperty(IntermediateFormat.KeyPlaceholder, out var phEl)
                                            && phEl.GetBoolean() ? 1 : 0);
                    Bind(insertKeyed, "$origin", TranslationOrigin.Runtime);
                    insertKeyed.ExecuteNonQuery();

                    // key 走标识符分词(CamelCase 拆开),译文与原文按自然文本 —— 与 defs_fts
                    // 同一个产地。
                    Bind(insertKeyedFts, "$id", kid);
                    Bind(insertKeyedFts, "$k", FtsText.ForIndex(key, identifier: true));
                    Bind(insertKeyedFts, "$tr", FtsText.ForIndex(translated));
                    Bind(insertKeyedFts, "$o", FtsText.ForIndex(original));
                    insertKeyedFts.ExecuteNonQuery();
                    keyedInEffect++;
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
                var candidates = Candidates(idsByName, inj.defName);
                var owner = Owner(candidates, inj.defType);
                Bind(insertTr, "$id", owner);
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
                Recall(ftsExtra, owner, candidates, inj.translated);
            }

            var (harvested, keyedHarvested) = HarvestStaticTranslations(
                insertTr, insertKeyed, insertKeyedFts, ref nextKeyedId, idsByName, meta, ftsExtra);

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
                Put(SnapshotSchema.MetaKeyHarvestedRoots, ModRoots.Count.ToString());

                // 扫盘发生在游戏已经退出之后,所以这一份指纹严格说是「导出结束那一刻」的磁盘,
                // 不是「游戏读 XML 那一刻」的。中间这几十秒里有人改了文件的话,这一层会把它
                // 记成基线 —— 少报一次,不会多报。
                if (Environment is { } env)
                {
                    var scan = ContentFingerprint.Scan(env, meta.Mods.Select(m => m.PackageId), meta.GameVersion);
                    if (scan is not null) Put(SnapshotSchema.MetaKeyContent, scan.ToJson());
                }
            }

            tx.Commit();

            // shared_values 的一次扫。放在 commit 之后、建索引之前:GROUP BY 全表在事务里做
            // 会把 journal 撑大一圈。
            //
            // 「不少于 8 个」是「大多数」这个词成不成话的下限 —— 类型只有三五个 def 时,
            // 「其中两个也是这个值」不构成任何提示。过半是同一个词的另一半。
            using (var fill = db.CreateCommand())
            {
                fill.CommandText =
                    "INSERT INTO shared_values (def_type, path, value, defs) " +
                    "SELECT d.def_type, fv.path, fv.value, COUNT(DISTINCT fv.def_id) n " +
                    "  FROM field_values fv JOIN defs d ON d.id = fv.def_id " +
                    $" WHERE fv.is_default <> {Contract.DefaultState.Same} " +
                    " GROUP BY d.def_type, fv.path, fv.value " +
                    "HAVING n >= 8 " +
                    "   AND n * 2 > (SELECT COUNT(*) FROM defs d2 WHERE d2.def_type = d.def_type)";
                fill.ExecuteNonQuery();
            }

            SnapshotSchema.CreateIndexes(db);
            using (var vac = db.CreateCommand()) { vac.CommandText = "PRAGMA optimize;"; vac.ExecuteNonQuery(); }

            db.Close();
            SqliteConnection.ClearAllPools();

            if (File.Exists(dbPath)) File.Delete(dbPath);
            File.Move(tempDb, dbPath);

            return new ImportStats(defs, fieldValues, noise, runtimeTr, harvested,
                                   keyedInEffect, keyedHarvested, truncatedDefs, xmlNodes, meta, dbPath);
        }
    }

    /// <summary>这个 defName 下的全部 def。没有就是空表,调用点不必分两种写法。</summary>
    private static IReadOnlyList<(long Id, string? Type)> Candidates(
        Dictionary<string, List<(long Id, string? Type)>> idsByName, string defName)
        => idsByName.TryGetValue(defName, out var list) ? list : [];

    /// <summary>
    /// 这条译文归哪个 def。**判不出来就写 null**,不挑一个:游戏自己也是按 defName 注入的,
    /// 语言文件的 key 里根本没有类型这一维。
    /// </summary>
    private static long? Owner(IReadOnlyList<(long Id, string? Type)> candidates, string? defType)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Id;
        var typed = candidates.Where(c => DefTypes.Same(c.Type, defType)).ToList();
        return typed.Count == 1 ? typed[0].Id : null;
    }

    /// <summary>
    /// 译文进双语 FTS。归属判不出来时**每个同名 def 都收**:这一列是召回用的,漏掉一个
    /// 就「用中文名搜不到那个 def」,比多召回一个同名 def 贵得多。
    /// </summary>
    private static void Recall(Dictionary<long, List<string>> ftsExtra, long? owner,
                               IReadOnlyList<(long Id, string? Type)> candidates, string text)
    {
        var targets = owner is { } id ? [id] : candidates.Select(c => c.Id);
        foreach (var target in targets)
            (ftsExtra.TryGetValue(target, out var l) ? l : ftsExtra[target] = []).Add(text);
    }

    private (int DefInjected, int Keyed) HarvestStaticTranslations(
        SqliteCommand insertTr, SqliteCommand insertKeyed, SqliteCommand insertKeyedFts,
        ref long nextKeyedId,
        Dictionary<string, List<(long Id, string? Type)>> idsByName,
        ExportMeta meta, Dictionary<long, List<string>> ftsExtra)
    {
        if (ModRoots.Count == 0) return (0, 0);
        var count = 0;
        var keyedCount = 0;
        var runtimeMods = meta.Mods.Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ModRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var modDir in SafeDirs(root))
            {
                var packageId = ReadPackageId(modDir) ?? Path.GetFileName(modDir);

                // Keyed 那一半。同 key 多来源时**不去重、不挑一个**:这一层的语义是
                // 「磁盘上存在这些译文」,不是「哪一句会生效」—— 后者由运行时那一层回答
                // (keyedReplacements 本身已经是合并后的最终值)。
                foreach (var keyedDir in FindLanguageSubdirs(modDir, meta.Language, "Keyed"))
                {
                    foreach (var xml in SafeFiles(keyedDir, "*.xml"))
                    {
                        foreach (var (key, text) in ReadLanguageFile(xml))
                        {
                            var kid = nextKeyedId++;
                            Bind(insertKeyed, "$id", kid);
                            Bind(insertKeyed, "$k", key);
                            Bind(insertKeyed, "$tr", text);
                            Bind(insertKeyed, "$o", null);
                            Bind(insertKeyed, "$lang", meta.Language);
                            Bind(insertKeyed, "$sf", Path.GetFileName(xml));
                            Bind(insertKeyed, "$sl", 0);
                            Bind(insertKeyed, "$sm", packageId);
                            Bind(insertKeyed, "$ph", 0);
                            Bind(insertKeyed, "$origin", runtimeMods.Contains(packageId)
                                ? TranslationOrigin.Harvested
                                : TranslationOrigin.HarvestedOutside);
                            insertKeyed.ExecuteNonQuery();

                            Bind(insertKeyedFts, "$id", kid);
                            Bind(insertKeyedFts, "$k", FtsText.ForIndex(key, identifier: true));
                            Bind(insertKeyedFts, "$tr", FtsText.ForIndex(text));
                            Bind(insertKeyedFts, "$o", "");
                            insertKeyedFts.ExecuteNonQuery();
                            keyedCount++;
                        }
                    }
                }

                foreach (var injDir in FindLanguageSubdirs(modDir, meta.Language, "DefInjected"))
                {
                    foreach (var xml in SafeFiles(injDir, "*.xml"))
                    {
                        foreach (var (key, text) in ReadLanguageFile(xml))
                        {
                            var dot = key.IndexOf('.');
                            if (dot <= 0) continue;
                            var defName = key[..dot];
                            var path = key[(dot + 1)..];
                            var candidates = Candidates(idsByName, defName);
                            if (candidates.Count == 0) continue;

                            // 收割来的 key 是 `DefName.field`,没有类型信息,所以能判出归属的
                            // 只有「这个名字下只有一个 def」那一种。
                            var owner = Owner(candidates, null);
                            Bind(insertTr, "$id", owner);
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
                            Recall(ftsExtra, owner, candidates, text);
                        }
                    }
                }
            }
        }
        return (count, keyedCount);
    }

    /// <summary>
    /// mod 里 <c>Languages/&lt;语言&gt;/&lt;子目录&gt;</c> 的实际落点。<c>Keyed</c> 与
    /// <c>DefInjected</c> 共用这一条路径规则(两种目录在同一层并列),所以规则只有一个产地。
    ///
    /// **官方 Data 目录不在射程内**:那边的非英文语言包是 .tar 打包的,游戏走 VirtualDirectory
    /// 读它,而这里只认磁盘上的普通目录。官方那一份由运行时导出覆盖。
    /// </summary>
    private static IEnumerable<string> FindLanguageSubdirs(string modDir, string language, string subdir)
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
                var dir = Path.Combine(lr, language, subdir);
                if (Directory.Exists(dir)) yield return dir;
            }
        }
    }

    /// <summary>
    /// 一个 <c>&lt;LanguageData&gt;</c> 文件里的条目。Keyed 与 DefInjected 的文件形状相同
    /// (根元素下每个子元素一条),差别只在 key 的**读法**:DefInjected 的是
    /// <c>DefName.field</c>,Keyed 的就是 key 本身。所以解析共用,拆分留给调用点。
    /// </summary>
    private static IEnumerable<(string Key, string Text)> ReadLanguageFile(string path)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(path); }
        catch { yield break; }
        if (doc.Root is null) yield break;
        foreach (var el in doc.Root.Elements())
        {
            var text = el.Value;
            if (string.IsNullOrWhiteSpace(text)) continue;
            // 游戏读这两种文件时都会把字面 `\n` 换成真换行(Keyed 走 DirectXmlLoaderSimple、
            // DefInjected 走 DefInjectionPackage,两处都只换 `\n`)。不跟着换,收割层与运行时层
            // 就会为**同一句译文**存下两个不同的字符串,「两层不一致」这个信号里就混进纯表示差异。
            yield return (el.Name.LocalName, text.Replace("\\n", "\n"));
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
