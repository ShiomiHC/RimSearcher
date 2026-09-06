using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimSearcher.Contract;
using RimSearcher.Snapshot;

namespace RimSearcher.Storage;

public sealed record ImportStats(
    int Defs, int FieldValues, int NoiseDropped, int RuntimeTranslations,
    int HarvestedTranslations, int KeyedInEffect, int KeyedHarvested,
    int TruncatedDefs, int XmlNodes, int EconomyRows,
    /// <summary>
    /// 槽位名册的行数。**零不是「一个可注入槽位都没有」** —— 0.9.0 之前的导出没有这一层
    /// (或者有而不全)。两种成因靠导出器版本上的能力位分,不靠这个数。
    /// </summary>
    int InjKeys,
    /// <summary>
    /// 经济面的三态,<c>null</c> 是第四态(这份导出建于经济面之前)。
    /// <see cref="EconomyRows"/> 单独看不出这四种成因的分别。
    /// </summary>
    string? EconomyState,
    ExportMeta Meta, string DbPath);

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

        // Pooling = false 的成因写在 SnapshotDb.Open 那里:池化会让关掉的连接继续占着
        // 文件,而这个方法最后一步正是把 tempDb 移到 dbPath 上。
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = tempDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        db.Open();
        SnapshotSchema.Create(db);

        ExportMeta? meta = null;
        var defs = 0; var fieldValues = 0; var noise = 0; var runtimeTr = 0; var truncatedDefs = 0;
        var xmlNodes = 0; var keyedInEffect = 0; var economyRows = 0; var injKeys = 0;
        long? declaredRecords = null;
        // 经济面的三态从尾行来。**null 是第四态** —— 尾行没这个字段 = 这份导出建于经济面
        // 进中间格式之前。四态在 meta 表里必须分得开,见 SnapshotSchema.MetaKeyEconomyState。
        string? economyState = null;
        string? economyError = null;
        // 尾行的分层耗时。null = 那次导出早于 0.11.0,不是「零毫秒」。
        string? exportTimings = null;
        var sawEnd = false;
        // 注入键行边读边落地,靠的是 def 行全在它们前面。见 KindDef 那一支的判据。
        var sawInjKey = false;
        var records = 0L;

        // 导入这一侧的分层耗时。加它的由头是一次实测:游戏那边 2 分 22 秒就写完了导出文件,
        // 这边建库花了十几分钟 —— 只量导出那侧会把「慢」整个归错地方。
        var swTotal = Stopwatch.StartNew();
        var swRead = new Stopwatch(); var swInjections = new Stopwatch();
        var swHarvest = new Stopwatch(); var swFts = new Stopwatch();
        var swCommit = new Stopwatch(); var swShared = new Stopwatch();
        var swIndexes = new Stopwatch(); var swOptimize = new Stopwatch();

        using (var tx = db.BeginTransaction())
        {
            using var insertDef = Prepare(db, """
                INSERT INTO defs (id, def_type, def_name, label, description, source_mod, source_file,
                                  generated, class, fields_truncated)
                VALUES ($id,$t,$n,$l,$d,$sm,$sf,$g,$c,$ft)
                """);
            using var insertFv = Prepare(db,
                "INSERT INTO field_values (def_id, path_id, value_id, is_default) VALUES ($id,$pid,$v,$def)");
            using var insertFvValue = Prepare(db,
                "INSERT INTO field_value_values (id, value) VALUES ($id,$v)");
            using var insertFvPath = Prepare(db,
                "INSERT INTO field_value_paths (id, path, leaf) VALUES ($id,$p,$lf)");
            using var insertFts = Prepare(db, "INSERT INTO defs_fts (rowid, def_name, label, description, translated) VALUES ($id,$n,$l,$d,$tr)");
            using var insertTr = Prepare(db, """
                INSERT INTO translations (def_id, def_type, def_name, path, key, key_state, applied, translated, original, language, source_mod, source_file, source_file_count, origin)
                VALUES ($id,$t,$n,$p,$key,$state,$applied,$tr,$o,$lang,$sm,$sf,$sfc,$origin)
                """);
            using var insertIk = Prepare(db, """
                INSERT INTO injection_keys (def_id, def_type_id, def_name_id, path_id, suggested_path_id,
                                            is_collection, translation_allowed, full_list_translation_allowed)
                VALUES ($id,$t,$n,$p,$sp,$col,$ta,$fl)
                """);
            using var insertIkType = Prepare(db,
                "INSERT INTO injection_key_types (id, def_type) VALUES ($id,$v)");
            using var insertIkName = Prepare(db,
                "INSERT INTO injection_key_names (id, def_name) VALUES ($id,$v)");
            using var insertIkPath = Prepare(db,
                "INSERT INTO injection_key_paths (id, path) VALUES ($id,$v)");
            using var insertXn = Prepare(db, """
                INSERT INTO xml_nodes (def_type, name, parent_name, abstract, def_name, label,
                                       source_mod, source_file, patch_ops, patch_ops_defname, patch_ops_label)
                VALUES ($t,$n,$pn,$a,$dn,$l,$sm,$sf,$po,$pod,$pol)
                """);
            using var insertXw = Prepare(db, """
                INSERT INTO xml_written (def_type, node_key, key_is_name, path, inner_text, patched)
                VALUES ($t,$k,$kn,$p,$x,$pa)
                """);
            using var insertTypeName = Prepare(db, """
                INSERT INTO type_names (id, name)
                VALUES ($id,$n)
                """);
            using var insertTypeSubtree = Prepare(db, """
                INSERT OR IGNORE INTO type_subtrees (type_id, subtree_id)
                VALUES ($t,$s)
                """);
            using var insertSubtreePath = Prepare(db, """
                INSERT INTO subtree_paths (subtree_id, path_id)
                VALUES ($s,$p)
                """);
            using var insertTfPath = Prepare(db, """
                INSERT INTO type_field_paths (id, path)
                VALUES ($id,$p)
                """);
            using var insertKeyed = Prepare(db, """
                INSERT INTO keyed (id, key, translated, original, language, source_file,
                                   source_file_count, source_line, source_mod, placeholder, origin)
                VALUES ($id,$k,$tr,$o,$lang,$sf,$sfc,$sl,$sm,$ph,$origin)
                """);
            using var insertKeyedFts = Prepare(db,
                "INSERT INTO keyed_fts (rowid, key, translated, original) VALUES ($id,$k,$tr,$o)");
            using var insertEcon = Prepare(db, """
                INSERT INTO economy (id, def_name, label, category, mod, market_value, producible,
                                     made_from_stuff, is_weapon, is_apparel, market_value_defined,
                                     calc_state, calculated_market_value, cost_to_make, profit,
                                     profit_rate, work_to_produce, cost_list, cost_difficulty_var,
                                     cost_difficulty_inverted, chain_end_share, cost_deep, profit_deep)
                VALUES ($id,$n,$l,$cat,$mod,$mv,$prod,$stuff,$wep,$app,$mvd,$cs,$cmv,$ctm,$pf,
                        $pr,$work,$cl,$cdv,$cdi,$ces,$cd,$pd)
                """);
            using var insertEconChain = Prepare(db, """
                INSERT INTO economy_cost_chain (economy_id, ordinal, thing_def, count, unit_value, chain_end)
                VALUES ($id,$o,$td,$c,$uv,$ce)
                """);
            using var insertEconRecipe = Prepare(db, """
                INSERT INTO economy_recipes (economy_id, ordinal, def_name, product_count, work_amount,
                                             self_referential)
                VALUES ($id,$o,$n,$pc,$wa,$sr)
                """);

            // 一个 defName 下可能挂着**几个** def(同名跨 def 类型是 RimWorld 常态),
            // 所以是 name → 列表:取单个 id 会让归属取决于导出顺序。
            var idsByName = new Dictionary<string, List<(long Id, string? Type)>>(StringComparer.Ordinal);
            var ftsExtra = new Dictionary<long, List<string>>();
            var pendingInjections = new List<(string defName, string defType, string path, string translated,
                                             string original, bool? applied, string? sourceFile)>();
            long nextId = 1;
            // keyed 自己的 id 序列。显式维护而不是问 last_insert_rowid():FTS 那一行要用同一个
            // rowid,而两条 INSERT 之间夹着别的语句。
            long nextKeyedId = 1;
            long nextEconomyId = 1;
            // 声明层的路径字典,id 就是它进来的次序(Count + 1)。
            var tfPathIds = new Dictionary<string, long>(StringComparer.Ordinal);
            // def 类型名 → 号。纯官方 222 个。
            var typeNameIds = new Dictionary<string, long>(StringComparer.Ordinal);
            // 子树 → 号。键是**排序后的 path_id 串**,内容相同即同一棵 —— 不靠哈希碰运气,
            // 那 1374 万行的冗余整个压在「相不相等」这一个判断上。纯官方 2884 棵,键合计约 4M。
            var subtreeIds = new Dictionary<string, long>(StringComparer.Ordinal);
            // 字段路径的字典。同 tfPathIds:第一次见到就发号,行里只存号。
            // baseline 上 150 万行摊到 2.6 万条不同路径,这张表因此只有 838K。
            var fvPathIds = new Dictionary<string, long>(StringComparer.Ordinal);
            // 值的字典。150 万行摊到 6.5 万个不同值(races 311 万摊到 11.1 万),内存约 5M。
            var fvValueIds = new Dictionary<string, long>(StringComparer.Ordinal);
            // 名册那四列的字典。**导入期那次自查直接用这三个**,不回库拿字符串找号 ——
            // 于是字典表自己不需要中途建索引(见 SnapshotSchema.InjectionKeyIndexes)。
            var ikTypeIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var ikNameIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var ikPathIds = new Dictionary<string, long>(StringComparer.Ordinal);

            long Intern(Dictionary<string, long> ids, SqliteCommand insert, string value)
            {
                if (ids.TryGetValue(value, out var id)) return id;
                id = ids.Count + 1;
                ids[value] = id;
                Bind(insert, "$id", id);
                Bind(insert, "$v", value);
                insert.ExecuteNonQuery();
                return id;
            }

            swRead.Start();
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
                    economyState = Str(root, IntermediateFormat.KeyEconomyState);
                    economyError = Str(root, IntermediateFormat.KeyEconomyError);
                    exportTimings = root.TryGetProperty(IntermediateFormat.KeyTimingsMs, out var tm)
                        ? tm.GetRawText() : null;
                    continue;
                }

                if (meta is null)
                    throw new SnapshotFormatError(
                        "The export file has data lines before its meta line. Re-run the export.");

                if (kind == IntermediateFormat.KindDef)
                {
                    // 判的是**顺序**而不是「def 行有没有」:后者会把「一个 def 都没有的
                    // 导出」误判成损坏,而那种文件里 def_id 本来就该是 null。
                    if (sawInjKey)
                        throw new SnapshotFormatError(
                            "The export file has def lines after its injection-key lines, so those keys were " +
                            "attached to a def table that was still incomplete. Re-run the export.");

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
                            var value = triple[1].GetString();
                            if (NoiseFilter.IsNoise(path, value)) { noise++; continue; }
                            if (!fvPathIds.TryGetValue(path, out var fvPathId))
                            {
                                fvPathId = fvPathIds.Count + 1;
                                fvPathIds[path] = fvPathId;
                                Bind(insertFvPath, "$id", fvPathId);
                                Bind(insertFvPath, "$p", path);
                                // leaf 跟着 path 走一次,不跟着行走 150 万次 ——
                                // 它是 path 的纯函数,同一条 path 底下必然同一个值。
                                Bind(insertFvPath, "$lf", NoiseFilter.Leaf(path));
                                insertFvPath.ExecuteNonQuery();
                            }
                            Bind(insertFv, "$id", id);
                            Bind(insertFv, "$pid", fvPathId);
                            // NULL 不发号 —— 发了的话「这个字段是空的」与「值是空串」同形。
                            Bind(insertFv, "$v", value is null
                                ? null : Intern(fvValueIds, insertFvValue, value));
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
                    Bind(insertXn, "$pod", root.TryGetProperty(IntermediateFormat.KeyPatchOpsDefName, out var podEl) ? podEl.GetInt32() : 0);
                    Bind(insertXn, "$pol", root.TryGetProperty(IntermediateFormat.KeyPatchOpsLabel, out var polEl) ? polEl.GetInt32() : 0);
                    insertXn.ExecuteNonQuery();
                    xmlNodes++;
                    continue;
                }

                if (kind == IntermediateFormat.KindXmlWritten)
                {
                    var nodeKey = Str(root, IntermediateFormat.KeyNodeKey) ?? "";
                    if (nodeKey.Length == 0) continue;
                    var defTypeW = Str(root, IntermediateFormat.KeyDefType) ?? "";
                    var keyIsName = root.TryGetProperty(IntermediateFormat.KeyKeyIsName, out var knEl)
                                    && knEl.GetBoolean() ? 1 : 0;
                    var hasPaths = root.TryGetProperty(IntermediateFormat.KeyPaths, out var wpaths)
                                   && wpaths.ValueKind == JsonValueKind.Array;
                    var hasTextsProp = root.TryGetProperty(IntermediateFormat.KeyTexts, out var wtexts);
                    if (hasTextsProp)
                    {
                        if (!hasPaths || wtexts.ValueKind != JsonValueKind.Array)
                            throw new SnapshotFormatError(
                                $"An xmlwritten record for {defTypeW} '{nodeKey}' has a texts array that is not " +
                                "parallel to paths. Re-run the export.");
                        if (wtexts.GetArrayLength() != wpaths.GetArrayLength())
                            throw new SnapshotFormatError(
                                $"An xmlwritten record for {defTypeW} '{nodeKey}' has {wpaths.GetArrayLength()} " +
                                $"paths and {wtexts.GetArrayLength()} texts. Those arrays are parallel, and a " +
                                "length mismatch would silently attach every text to the wrong path. Re-run the export.");
                    }
                    var hasPatchedProp = root.TryGetProperty(IntermediateFormat.KeyPatched, out var wpatched);
                    if (hasPatchedProp)
                    {
                        if (!hasPaths || wpatched.ValueKind != JsonValueKind.Array)
                            throw new SnapshotFormatError(
                                $"An xmlwritten record for {defTypeW} '{nodeKey}' has a patched array that is not " +
                                "parallel to paths. Re-run the export.");
                        if (wpatched.GetArrayLength() != wpaths.GetArrayLength())
                            throw new SnapshotFormatError(
                                $"An xmlwritten record for {defTypeW} '{nodeKey}' has {wpaths.GetArrayLength()} " +
                                $"paths and {wpatched.GetArrayLength()} patched flags. Those arrays are parallel, " +
                                "and a length mismatch would silently mark the wrong lines as patch-added. " +
                                "Re-run the export.");
                    }
                    if (hasPaths)
                    {
                        var n = wpaths.GetArrayLength();
                        for (var i = 0; i < n; i++)
                        {
                            var path = wpaths[i].GetString();
                            if (string.IsNullOrEmpty(path)) continue;
                            Bind(insertXw, "$t", defTypeW);
                            Bind(insertXw, "$k", nodeKey);
                            Bind(insertXw, "$kn", keyIsName);
                            Bind(insertXw, "$p", path);
                            Bind(insertXw, "$x", hasTextsProp
                                ? (wtexts[i].ValueKind == JsonValueKind.String ? wtexts[i].GetString() ?? "" : "")
                                : null);
                            Bind(insertXw, "$pa", hasPatchedProp
                                ? (object)(wpatched[i].ValueKind == JsonValueKind.True ? 1 : 0)
                                : null);
                            insertXw.ExecuteNonQuery();
                        }
                    }
                    continue;
                }

                if (kind == IntermediateFormat.KindTypeFields)
                {
                    var defTypeF = Str(root, IntermediateFormat.KeyDefType) ?? "";
                    if (defTypeF.Length == 0) continue;
                    if (root.TryGetProperty(IntermediateFormat.KeyPaths, out var tpaths)
                        && tpaths.ValueKind == JsonValueKind.Array)
                    {
                        // 这个类型的路径按首段分组 —— 每一组就是一棵子树。一条记录里装着
                        // 一个类型的全部路径,所以分组不必跨记录攒。
                        var byHead = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                        foreach (var p in tpaths.EnumerateArray())
                        {
                            var path = p.GetString();
                            if (string.IsNullOrEmpty(path)) continue;
                            // 字典在内存里攒:52 万条(纯官方)约 60M,换掉每行一次 SELECT。
                            // 跨 def 类型共用 —— 冗余正出在「所有 Def 子类共享基类那棵树」上。
                            if (!tfPathIds.TryGetValue(path, out var pathId))
                            {
                                pathId = tfPathIds.Count + 1;
                                tfPathIds[path] = pathId;
                                Bind(insertTfPath, "$id", pathId);
                                Bind(insertTfPath, "$p", path);
                                insertTfPath.ExecuteNonQuery();
                            }
                            var head = HeadSegment(path);
                            if (!byHead.TryGetValue(head, out var bucket))
                                byHead[head] = bucket = [];
                            bucket.Add(pathId);
                        }

                        if (byHead.Count > 0)
                        {
                            // 同一个类型出现在两条记录里时接着往下挂,不另发一个号 ——
                            // 那会让它在 type_names 里有两行,而查询按名字找,只会撞见头一行。
                            if (!typeNameIds.TryGetValue(defTypeF, out var typeId))
                            {
                                typeId = typeNameIds.Count + 1;
                                typeNameIds[defTypeF] = typeId;
                                Bind(insertTypeName, "$id", typeId);
                                Bind(insertTypeName, "$n", defTypeF);
                                insertTypeName.ExecuteNonQuery();
                            }

                            foreach (var bucket in byHead.Values)
                            {
                                bucket.Sort();
                                var key = string.Join(',', bucket);
                                if (!subtreeIds.TryGetValue(key, out var subtreeId))
                                {
                                    subtreeId = subtreeIds.Count + 1;
                                    subtreeIds[key] = subtreeId;
                                    foreach (var pathId in bucket)
                                    {
                                        Bind(insertSubtreePath, "$s", subtreeId);
                                        Bind(insertSubtreePath, "$p", pathId);
                                        insertSubtreePath.ExecuteNonQuery();
                                    }
                                }
                                Bind(insertTypeSubtree, "$t", typeId);
                                Bind(insertTypeSubtree, "$s", subtreeId);
                                insertTypeSubtree.ExecuteNonQuery();
                            }
                        }
                    }
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
                    Bind(insertKeyed, "$sfc", null);
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

                // 经济行同样不依赖任何 def:它按 def_name 与 mod 被消费,不必找回 def id。
                if (kind == IntermediateFormat.KindEconomy)
                {
                    var eid = nextEconomyId++;
                    Bind(insertEcon, "$id", eid);
                    Bind(insertEcon, "$n", Str(root, IntermediateFormat.KeyDefName) ?? "");
                    Bind(insertEcon, "$l", Str(root, IntermediateFormat.KeyLabel));
                    Bind(insertEcon, "$cat", Str(root, IntermediateFormat.KeyEconomyCategory));
                    Bind(insertEcon, "$mod", Str(root, IntermediateFormat.KeyEconomyMod));
                    // Num() 把 JSON 的 null 原样带成 SQL NULL。写成 0 的话,「工时为零所以
                    // 利润率算不出」就与「利润率正好是零」在库里同形了。
                    Bind(insertEcon, "$mv", Num(root, IntermediateFormat.KeyEconomyMarketValue));
                    Bind(insertEcon, "$prod", Flag(root, IntermediateFormat.KeyEconomyProducible));
                    Bind(insertEcon, "$stuff", Flag(root, IntermediateFormat.KeyEconomyMadeFromStuff));
                    Bind(insertEcon, "$wep", Flag(root, IntermediateFormat.KeyEconomyIsWeapon));
                    Bind(insertEcon, "$app", Flag(root, IntermediateFormat.KeyEconomyIsApparel));
                    Bind(insertEcon, "$mvd", Flag(root, IntermediateFormat.KeyEconomyMarketValueDefined));
                    Bind(insertEcon, "$cs", Str(root, IntermediateFormat.KeyEconomyCalcState) ?? "");
                    Bind(insertEcon, "$cmv", Num(root, IntermediateFormat.KeyEconomyCalculatedMarketValue));
                    Bind(insertEcon, "$ctm", Num(root, IntermediateFormat.KeyEconomyCostToMake));
                    Bind(insertEcon, "$pf", Num(root, IntermediateFormat.KeyEconomyProfit));
                    Bind(insertEcon, "$pr", Num(root, IntermediateFormat.KeyEconomyProfitRate));
                    Bind(insertEcon, "$work", Num(root, IntermediateFormat.KeyEconomyWorkToProduce));
                    Bind(insertEcon, "$cl", Str(root, IntermediateFormat.KeyEconomyCostList));
                    Bind(insertEcon, "$cdv", Str(root, IntermediateFormat.KeyEconomyCostDifficultyVar));
                    Bind(insertEcon, "$cdi", Flag(root, IntermediateFormat.KeyEconomyCostDifficultyInverted));
                    Bind(insertEcon, "$ces", Num(root, IntermediateFormat.KeyEconomyChainEndShare));
                    Bind(insertEcon, "$cd", Num(root, IntermediateFormat.KeyEconomyCostDeep));
                    Bind(insertEcon, "$pd", Num(root, IntermediateFormat.KeyEconomyProfitDeep));
                    insertEcon.ExecuteNonQuery();
                    economyRows++;

                    if (root.TryGetProperty(IntermediateFormat.KeyEconomyCostChain, out var chain) &&
                        chain.ValueKind == JsonValueKind.Array)
                    {
                        var ord = 0;
                        foreach (var part in chain.EnumerateArray())
                        {
                            Bind(insertEconChain, "$id", eid);
                            Bind(insertEconChain, "$o", ord++);
                            Bind(insertEconChain, "$td", Str(part, IntermediateFormat.KeyEconomyThingDef) ?? "");
                            Bind(insertEconChain, "$c", part.TryGetProperty(IntermediateFormat.KeyEconomyCount, out var cEl)
                                ? cEl.GetInt32() : 0);
                            Bind(insertEconChain, "$uv", Num(part, IntermediateFormat.KeyEconomyUnitValue));
                            Bind(insertEconChain, "$ce", Flag(part, IntermediateFormat.KeyEconomyChainEnd));
                            insertEconChain.ExecuteNonQuery();
                        }
                    }

                    if (root.TryGetProperty(IntermediateFormat.KeyEconomyRecipeCandidates, out var cands) &&
                        cands.ValueKind == JsonValueKind.Array)
                    {
                        var ord = 0;
                        foreach (var cand in cands.EnumerateArray())
                        {
                            Bind(insertEconRecipe, "$id", eid);
                            Bind(insertEconRecipe, "$o", ord++);
                            Bind(insertEconRecipe, "$n", Str(cand, IntermediateFormat.KeyDefName) ?? "");
                            Bind(insertEconRecipe, "$pc", cand.TryGetProperty(IntermediateFormat.KeyEconomyProductCount, out var pcEl)
                                ? pcEl.GetInt32() : 0);
                            Bind(insertEconRecipe, "$wa", Num(cand, IntermediateFormat.KeyEconomyWorkAmount));
                            Bind(insertEconRecipe, "$sr", Flag(cand, IntermediateFormat.KeyEconomySelfReferential));
                            insertEconRecipe.ExecuteNonQuery();
                        }
                    }
                    continue;
                }

                // 0.9.0 起这一层是**槽位名册**,一个槽位一行,于是它跟 def 一样大到不能缓在
                // 内存里(0.8.0 那版只发带信息的行,25 万条,缓着无所谓)。边读边落地,靠的是
                // 导出器把 def 全写完才写 injkey —— 那个顺序是本方法唯一的依赖,所以显式判一次:
                // 反过来的话 def_id 会静默错挂,而错挂的行与正确的行同形。
                if (kind == IntermediateFormat.KindInjKey)
                {
                    sawInjKey = true;
                    var ikName = Str(root, IntermediateFormat.KeyDefName) ?? "";
                    var ikType = Str(root, IntermediateFormat.KeyDefType) ?? "";
                    Bind(insertIk, "$id", Owner(Candidates(idsByName, ikName), ikType));
                    // 空的 def_type 落成 NULL,不发号 —— 那一列本来就可空,而给空串发个号
                    // 会让「没有类型」与「类型是空串」在库里同形。
                    Bind(insertIk, "$t", ikType.Length == 0
                        ? null : Intern(ikTypeIds, insertIkType, ikType));
                    Bind(insertIk, "$n", Intern(ikNameIds, insertIkName, ikName));
                    Bind(insertIk, "$p", Intern(ikPathIds, insertIkPath,
                        Str(root, IntermediateFormat.KeyPath) ?? ""));
                    Bind(insertIk, "$sp", Intern(ikPathIds, insertIkPath,
                        Str(root, IntermediateFormat.KeySuggestedPath) ?? ""));
                    Bind(insertIk, "$col", Flag(root, IntermediateFormat.KeyIsCollection));
                    Bind(insertIk, "$ta", Flag(root, IntermediateFormat.KeyTranslationAllowed));
                    Bind(insertIk, "$fl", Flag(root, IntermediateFormat.KeyFullListTranslationAllowed));
                    insertIk.ExecuteNonQuery();
                    injKeys++;
                }

                if (kind == IntermediateFormat.KindDefInjection)
                {
                    pendingInjections.Add((
                        Str(root, IntermediateFormat.KeyDefName) ?? "",
                        Str(root, IntermediateFormat.KeyDefType) ?? "",
                        Str(root, IntermediateFormat.KeyPath) ?? "",
                        Str(root, IntermediateFormat.KeyTranslated) ?? "",
                        Str(root, IntermediateFormat.KeyOriginal) ?? "",
                        root.TryGetProperty(IntermediateFormat.KeyInjected, out var injEl)
                            ? injEl.GetBoolean() : (bool?)null,
                        Str(root, IntermediateFormat.KeySourceFile)));
                }
            }

            swRead.Stop();

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

            // 译者写的那一串 → 槽位。名册在库里(上面边读边落的),这里按键串反查:
            // 下标式与把手式两种拼法都合法,所以两列都问,取先中的那一条。
            //
            // 查库而不是在内存里再攒一份对照表:名册是一个槽位一行,大到与 def 同量级。
            // 译文行只有几万条,而 (def_name, path) / (def_name, suggested_path) 两个索引都在。
            //
            // 没有 def 类型的行(从语言文件收割来的,键串里本来就不带类型)按 defName 反查 ——
            // 名册这一侧带着类型,于是**顺便把类型也认了回来**,同名跨类型时才认不出。
            //
            // 名册字典化之后,进出这条查询的都是号:上面发号用的三个字典还在手上,
            // 于是不必让 SQL 拿字符串再找一遍(那样字典表就得中途建索引)。
            // 字典里没有的字符串给 -1 —— 号从 1 起,-1 一行都匹配不上,与旧写法上
            // 「这个名字/键串根本不在册」落到同一处(no rows → NoSlot)。
            var ikTypeById = ikTypeIds.ToDictionary(kv => kv.Value, kv => kv.Key);
            var ikPathById = ikPathIds.ToDictionary(kv => kv.Value, kv => kv.Key);
            long IkId(Dictionary<string, long> ids, string? s)
                => s is not null && ids.TryGetValue(s, out var id) ? id : -1;

            using var probeSlot = Prepare(db,
                "SELECT def_type_id, path_id, translation_allowed FROM injection_keys " +
                "WHERE def_name_id = $n AND (path_id = $k OR suggested_path_id = $k) " +
                "AND ($t IS NULL OR def_type_id = $t) LIMIT 2");

            var rostered = meta.IndexesInjectionKeys;
            var slotSeen = new Dictionary<(string? Type, string Name, string Key),
                                          (string? Type, string Path, string State)>();

            (string Path, string? Key, string? Type, string? State) Resolve(
                string? defType, string defName, string key)
            {
                // 没有名册就不判:老快照照旧存数据源原样,两列写 null。**0.8.0 也走这条** ——
                // 那一版的名册答不出「在不在册」(见 ExportMeta.IndexesInjectionKeys)。
                if (!rostered) return (key, null, defType, null);

                var probe = defType is { Length: > 0 } ? defType : null;
                if (!slotSeen.TryGetValue((probe, defName, key), out var hit))
                {
                    Bind(probeSlot, "$n", IkId(ikNameIds, defName));
                    Bind(probeSlot, "$k", IkId(ikPathIds, key));
                    // **只有 probe 为 null 才是「不限类型」。** 类型给了但不在字典里要一行都
                    // 匹配不上(旧写法上就是 `def_type = 一个没有的值`),所以那时给 -1 而不是
                    // NULL —— 两者在这条 SQL 上是放行与全不中,差一个反的结论。
                    Bind(probeSlot, "$t", probe is null ? null : IkId(ikTypeIds, probe));
                    using var rd = probeSlot.ExecuteReader();
                    if (!rd.Read())
                        hit = (probe, InjectionKey.ToFieldPath(key), InjectionKey.State.NoSlot);
                    else
                    {
                        var foundType = rd.IsDBNull(0) ? null : ikTypeById[rd.GetInt64(0)];
                        var indexPath = ikPathById[rd.GetInt64(1)];
                        var allowed = rd.GetInt64(2) != 0;
                        // 两条以上时类型认不出来(同名跨 def 类型),但**在不在册是认得出的** ——
                        // 那一问才是这一列要答的,所以只把类型留空,不退回 unknown。
                        if (probe is null && rd.Read()) foundType = null;
                        hit = (probe ?? foundType, InjectionKey.ToFieldPath(indexPath),
                               allowed ? InjectionKey.State.Resolved : InjectionKey.State.Refused);
                    }
                    slotSeen[(probe, defName, key)] = hit;
                }
                return (hit.Path, key, hit.Type, hit.State);
            }

            swInjections.Start();
            // 名册这时才建索引:下面每条译文都要按键串回头查它,而它有五十万行。
            // 计入 injections 这一段,因为它是为这一段建的。
            SnapshotSchema.CreateInjectionKeyIndexes(db);
            foreach (var inj in pendingInjections)
            {
                var candidates = Candidates(idsByName, inj.defName);
                var owner = Owner(candidates, inj.defType);
                var slot = Resolve(inj.defType, inj.defName, inj.path);
                Bind(insertTr, "$id", owner);
                Bind(insertTr, "$t", slot.Type);
                Bind(insertTr, "$n", inj.defName);
                Bind(insertTr, "$p", slot.Path);
                Bind(insertTr, "$key", slot.Key);
                Bind(insertTr, "$state", slot.State);
                // 游戏自己的判决,原样落下。没这一位的老导出写 null —— 不许补成 1,
                // 那等于替游戏担保一件没测过的事。
                Bind(insertTr, "$applied", inj.applied is { } ap ? (ap ? 1 : 0) : null);
                Bind(insertTr, "$tr", inj.translated);
                Bind(insertTr, "$o", inj.original);
                Bind(insertTr, "$lang", meta.Language);
                Bind(insertTr, "$sm", null);
                Bind(insertTr, "$sf", inj.sourceFile is { Length: > 0 } sfv ? sfv : null);
                Bind(insertTr, "$sfc", null);
                Bind(insertTr, "$origin", TranslationOrigin.Runtime);
                insertTr.ExecuteNonQuery();
                runtimeTr++;
                Recall(ftsExtra, owner, candidates, inj.translated);
            }

            swInjections.Stop();

            swHarvest.Start();
            var (harvested, keyedHarvested) = HarvestStaticTranslations(
                insertTr, insertKeyed, insertKeyedFts, ref nextKeyedId, idsByName, meta, ftsExtra,
                Resolve);
            swHarvest.Stop();

            // 翻译文本回填进 FTS 的 translated 列(双语索引)
            swFts.Start();
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
            swFts.Stop();

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

                // 尾行没这个字段就一个字也不写 —— **缺席是有意义的一态**(这份导出建于经济面
                // 之前),和 content_fingerprint 同一道缝。写个 "unknown" 进去会把「没资格回答」
                // 变成一个看着像答案的值。
                if (economyState is not null)
                {
                    Put(SnapshotSchema.MetaKeyEconomyState, economyState);
                    if (economyError is not null) Put(SnapshotSchema.MetaKeyEconomyError, economyError);
                }

                // 同一道缝:没这个字段就一个字也不写。
                if (exportTimings is not null) Put(SnapshotSchema.MetaKeyExportTimings, exportTimings);

                // 扫盘发生在游戏已经退出之后,所以这一份指纹严格说是「导出结束那一刻」的磁盘,
                // 不是「游戏读 XML 那一刻」的。中间这几十秒里有人改了文件的话,这一层会把它
                // 记成基线 —— 少报一次,不会多报。
                if (Environment is { } env)
                {
                    var scan = ContentFingerprint.Scan(env, meta.Mods.Select(m => m.PackageId), meta.GameVersion);
                    if (scan is not null) Put(SnapshotSchema.MetaKeyContent, scan.ToJson());
                }
            }

            swCommit.Start();
            tx.Commit();
            swCommit.Stop();

            // shared_values 的一次扫。放在 commit 之后、建索引之前:GROUP BY 全表在事务里做
            // 会把 journal 撑大一圈。
            //
            // 「不少于 8 个」是「大多数」这个词成不成话的下限 —— 类型只有三五个 def 时,
            // 「其中两个也是这个值」不构成任何提示。过半是同一个词的另一半。
            swShared.Start();
            using (var fill = db.CreateCommand())
            {
                // 每类型的 def 数先算成一张临时表。写成相关子查询的话它**每组算一次**,
                // 而这时 defs 上还没有 def_type 的索引(索引在下一步才建)—— 于是每组一次
                // 1.6 万行全扫。同一份导出实测这一段 26.5s → 0.5s,两侧产出的 314 行逐行相同。
                // 与名册索引那一处同一种形状:导入自己要查的东西,不能等最后建索引。
                fill.CommandText =
                    "CREATE TEMP TABLE type_defs AS SELECT def_type, COUNT(*) n FROM defs GROUP BY def_type; " +
                    "INSERT INTO shared_values (def_type, path, value, defs) " +
                    "SELECT d.def_type, fp.path, fvv.value, COUNT(DISTINCT fv.def_id) n " +
                    "  FROM field_values fv JOIN defs d ON d.id = fv.def_id " +
                    "       JOIN field_value_paths fp ON fp.id = fv.path_id " +
                    // LEFT:value_id 可空,内连会把值为空的那批行整个丢出这张表。
                    "       LEFT JOIN field_value_values fvv ON fvv.id = fv.value_id " +
                    $" WHERE fv.is_default <> {Contract.DefaultState.Same} " +
                    " GROUP BY d.def_type, fp.path, fv.value_id " +
                    "HAVING n >= 8 " +
                    "   AND n * 2 > (SELECT n FROM type_defs t WHERE t.def_type = d.def_type); " +
                    "DROP TABLE type_defs;";
                fill.ExecuteNonQuery();
            }

            swShared.Stop();

            swIndexes.Start();
            SnapshotSchema.CreateIndexes(db);
            swIndexes.Stop();

            swOptimize.Start();
            using (var vac = db.CreateCommand()) { vac.CommandText = "PRAGMA optimize;"; vac.ExecuteNonQuery(); }
            swOptimize.Stop();

            // 这一条只能在**建完索引之后**写:commit / shared_values / indexes / optimize
            // 四段都发生在上面那个 meta 表填完之后,写早了那四格永远是零。
            using (var t2 = db.CreateCommand())
            {
                var phases = new (string Name, long Ms)[]
                {
                    ("read", swRead.ElapsedMilliseconds),
                    ("injections", swInjections.ElapsedMilliseconds),
                    ("harvest", swHarvest.ElapsedMilliseconds),
                    ("fts", swFts.ElapsedMilliseconds),
                    ("commit", swCommit.ElapsedMilliseconds),
                    ("shared_values", swShared.ElapsedMilliseconds),
                    ("indexes", swIndexes.ElapsedMilliseconds),
                    ("optimize", swOptimize.ElapsedMilliseconds),
                    // total 在这一行写之前读,所以它差着这条 INSERT 与最后那次改名的时间。
                    ("total", swTotal.ElapsedMilliseconds),
                };
                t2.CommandText = "INSERT INTO meta (key, value) VALUES ($k,$v)";
                Bind(t2, "$k", SnapshotSchema.MetaKeyImportTimings);
                Bind(t2, "$v", "{" + string.Join(",",
                    phases.Select(x => JsonSerializer.Serialize(x.Name) + ":" + x.Ms)) + "}");
                t2.ExecuteNonQuery();
            }

            // 连接不入池(见 Pooling = false),所以 Close 就是真关文件 —— 下一行的
            // Delete/Move 立刻做得成,不必再去清全进程的连接池。
            db.Close();

            if (File.Exists(dbPath)) File.Delete(dbPath);
            File.Move(tempDb, dbPath);

            return new ImportStats(defs, fieldValues, noise, runtimeTr, harvested,
                                   keyedInEffect, keyedHarvested, truncatedDefs, xmlNodes,
                                   economyRows, injKeys, economyState, meta, dbPath);
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
        ExportMeta meta, Dictionary<long, List<string>> ftsExtra,
        Func<string?, string, string, (string Path, string? Key, string? Type, string? State)> resolve)
    {
        if (ModRoots.Count == 0) return (0, 0);
        var count = 0;
        var keyedCount = 0;
        var runtimeMods = meta.Mods.Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 这份快照里到底有哪些 def 类型 —— `DefInjected/<这一级>/` 的目录名要拿它来认。
        var defTypes = idsByName.Values.SelectMany(v => v).Select(v => v.Type)
                                .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ModRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var modDir in SafeDirs(root))
            {
                var packageId = ReadPackageId(modDir) ?? Path.GetFileName(modDir);

                // Keyed 那一半。同 key 多来源时**不挑一个**:这一层的语义是
                // 「磁盘上存在这些译文」,不是「哪一句会生效」—— 后者由运行时那一层回答
                // (keyedReplacements 本身已经是合并后的最终值)。
                //
                // 但同一个 mod 里**逐列全同**的几行不是几种说法,是磁盘布局:
                // 版本目录（1.4/ 1.5/ 1.6/）各钺一份。往下折成一行，折掉的份数留在
                // source_file_count 里 —— 同 DefInjected 那一半的判据。跳 mod 的同名 key 照旧各留一行。
                var foldedKeyed = new Dictionary<(string Key, string Text), (string FirstFile, int Files)>();
                foreach (var (keyedDir, keyedRel) in FindLanguageSubdirs(modDir, meta.Language, "Keyed"))
                {
                    foreach (var xml in SafeFiles(keyedDir, "*.xml"))
                    {
                        var rel = keyedRel + "/" + Path.GetFileName(xml);
                        foreach (var (key, text) in ReadLanguageFile(xml))
                            foldedKeyed[(key, text)] = foldedKeyed.TryGetValue((key, text), out var seen)
                                ? (seen.FirstFile, seen.Files + 1) : (rel, 1);
                    }
                }

                foreach (var ((key, text), (firstKeyedFile, keyedFiles)) in foldedKeyed)
                {
                    var kid = nextKeyedId++;
                    Bind(insertKeyed, "$id", kid);
                    Bind(insertKeyed, "$k", key);
                    Bind(insertKeyed, "$tr", text);
                    Bind(insertKeyed, "$o", null);
                    Bind(insertKeyed, "$lang", meta.Language);
                    Bind(insertKeyed, "$sf", firstKeyedFile);
                    Bind(insertKeyed, "$sfc", keyedFiles);
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

                // 一个 mod 的收割结果先在内存里归并再落库。**折叠的单元是 mod,不是文件**:
                // 版本目录(1.4/ 1.5/ 1.6/)是同一份译文的几个副本,而跨 mod 的同名 key
                // 是真有几种说法,那个照旧各留一行。
                var folded = new Dictionary<(string DefName, string? DefType, string Path, string Text),
                                            (string FirstFile, int Files)>();
                foreach (var (injDir, injRel) in FindLanguageSubdirs(modDir, meta.Language, "DefInjected"))
                {
                    // DefInjected 底下那一级恒是类型名 —— 注入 key 里没有类型这一维,
                    // 目录名是它唯一的产地。带命名空间的写法(CombatExtended.AmmoSetDef)
                    // 是常见形态,取最后一段。
                    foreach (var typeDir in SafeDirs(injDir))
                    {
                        var declaredType = ResolveInjectedType(Path.GetFileName(typeDir), defTypes);
                        foreach (var xml in SafeFiles(typeDir, "*.xml"))
                        {
                            // SafeFiles 递归,所以文件可能还在 <Type>/ 底下更深处 ——
                            // 取相对 typeDir 的那一段,别只留文件名。
                            var rel = (injRel + "/" + Path.GetFileName(typeDir) + "/"
                                       + Path.GetRelativePath(typeDir, xml)).Replace('\\', '/');
                            foreach (var (key, text) in ReadLanguageFile(xml))
                            {
                                var dot = key.IndexOf('.');
                                if (dot <= 0) continue;
                                var defName = key[..dot];
                                var path = key[(dot + 1)..];
                                if (Candidates(idsByName, defName).Count == 0) continue;

                                var slot = (defName, declaredType, path, text);
                                folded[slot] = folded.TryGetValue(slot, out var seen)
                                    ? (seen.FirstFile, seen.Files + 1)
                                    : (rel, 1);
                            }
                        }
                    }
                }

                foreach (var ((defName, declaredType, path, text), (firstFile, files)) in folded)
                {
                    var candidates = Candidates(idsByName, defName);
                    var owner = Owner(candidates, declaredType);
                    var slot = resolve(declaredType, defName, path);
                    Bind(insertTr, "$id", owner);
                    Bind(insertTr, "$t", slot.Type);
                    Bind(insertTr, "$n", defName);
                    Bind(insertTr, "$p", slot.Path);
                    Bind(insertTr, "$key", slot.Key);
                    Bind(insertTr, "$state", slot.State);
                    // 收割来的行游戏根本没读过,所以「注没注进去」这一问对它们不存在。
                    Bind(insertTr, "$applied", null);
                    Bind(insertTr, "$tr", text);
                    Bind(insertTr, "$o", null);
                    Bind(insertTr, "$lang", meta.Language);
                    Bind(insertTr, "$sm", packageId);
                    Bind(insertTr, "$sf", firstFile);
                    Bind(insertTr, "$sfc", files);
                    Bind(insertTr, "$origin", runtimeMods.Contains(packageId)
                        ? TranslationOrigin.Harvested
                        : TranslationOrigin.HarvestedOutside);
                    insertTr.ExecuteNonQuery();
                    count++;
                    Recall(ftsExtra, owner, candidates, text);
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
    /// <remarks>
    /// 第二项是该目录**相对 mod 目录**的路径。带版本目录的那条(<c>1.6/Languages/…</c>)
    /// 与不带的那条在这里都要能分辨出来 —— 只留文件名的话,同一句话铺在几套版本目录里
    /// 就无从说明它为什么出现了几次。
    /// </remarks>
    private static IEnumerable<(string Dir, string Rel)> FindLanguageSubdirs(
        string modDir, string language, string subdir)
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
                if (Directory.Exists(dir))
                    yield return (dir, Path.GetRelativePath(modDir, dir).Replace('\\', '/'));
            }
        }
    }

    /// <summary>
    /// <c>DefInjected/&lt;这一级&gt;/</c> 的目录名对应哪个 def 类型。**认不出来就返回 null**,
    /// 不猜:归属判不出来时留空,后面那条「按 defName 匹配」的免责句才有落点。
    ///
    /// 认得出的三种写法,前两种照抄游戏(<c>LoadedLanguage.LoadData</c>):裸类型名
    /// (<c>ThingDef</c>);**去掉末尾一个字符**再试一次(<c>ThingDefs</c> 这类复数目录名,
    /// 游戏的原话是 <c>name.Substring(0, name.Length - 1)</c>,只在长度大于 3 时试);
    /// 以及带命名空间的(<c>CombatExtended.AmmoSetDef</c>),取最后一段再比 —— 游戏那边
    /// 靠 <c>GenTypes.GetTypeInAnyAssembly</c> 直接解析全限定名,而这里手上只有短名。
    ///
    /// 实扫本机三个 mod 根 14153 个文件,裸名直接对上的占 89.3%,余下绝大多数是带命名空间的。
    ///
    /// **认不出来时不跳过这条,只留空类型**,与游戏有意不同:游戏认得所有已加载的类型,
    /// 认不出就是真的不存在;而这里的名单只有**这份快照里**的类型,一个没启用的 mod
    /// 自造的类型在这里注定认不出来,可它的译文正是 harvested_outside 那一层要召回的东西。
    /// 留空之后由「按 defName 匹配」那条免责句接手。
    /// </summary>
    private static string? ResolveInjectedType(string dirName, IReadOnlyCollection<string> defTypes)
    {
        if (string.IsNullOrEmpty(dirName)) return null;
        foreach (var name in Variants(dirName))
        {
            var hit = defTypes.FirstOrDefault(t => DefTypes.Same(t, name));
            if (hit != null) return hit;
        }
        return null;

        static IEnumerable<string> Variants(string dirName)
        {
            yield return dirName;
            if (dirName.Length > 3) yield return dirName[..^1];
            var dot = dirName.LastIndexOf('.');
            if (dot <= 0 || dot == dirName.Length - 1) yield break;
            var shortName = dirName[(dot + 1)..];
            yield return shortName;
            if (shortName.Length > 3) yield return shortName[..^1];
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

    /// <summary>
    /// 数,或 <c>null</c>。JSON 里的 <c>null</c> 与字段缺席都回 <c>null</c>,由 <see cref="Bind"/>
    /// 落成 SQL NULL —— 经济面上「算不出」与「算出来是零」处处是两件事,把前者写成 0
    /// 就等于给了一个会被统计进分布的数。
    /// </summary>
    private static double? Num(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>布尔落成 0/1。缺席作假 —— 这几列都是 NOT NULL,没有第三态可表达。</summary>
    private static int Flag(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True ? 1 : 0;

    /// <summary>
    /// 一条字段路径的首段:<c>comps[0].props.x</c> → <c>comps</c>,<c>defName</c> → 自己。
    /// 声明层按它切子树(见 <see cref="SnapshotSchema"/> 的 type_names 那段)。
    ///
    /// 切在**第一个** <c>.</c> 或 <c>[</c> 上,两者都算:列表字段的首段后面直接跟下标。
    /// 这个切法保证每条路径恰属一个首段 —— 分组因而是一个划分,复原时取并集即原样。
    /// </summary>
    internal static string HeadSegment(string path)
    {
        var i = path.AsSpan().IndexOfAny('.', '[');
        return i < 0 ? path : path[..i];
    }

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
