using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimTooling.IO;

namespace RimSearcher.Storage;

public sealed record ImportStats(
    int Defs, int FieldValues, int NoiseDropped, int RuntimeTranslations,
    int HarvestedTranslations, int KeyedInEffect, int KeyedHarvested,
    int TruncatedDefs, int XmlNodes, int EconomyRows,
    int InjKeys,
    string? EconomyState,
    ExportMeta Meta, string DbPath);

public sealed class SnapshotImporter
{
    public IReadOnlyList<string> ModRoots { get; init; } = [];

    public bool HarvestRequested { get; init; } = true;

    public Config.RimConfig? Environment { get; init; }

    public ImportStats Import(string exportPath, string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

        ImportStats? stats = null;
        AtomicFile.Write(dbPath, tempDb =>
        {
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
        string? economyState = null;
        string? economyError = null;
        string? exportTimings = null;
        var sawEnd = false;
        var sawInjKey = false;
        var records = 0L;

        var swTotal = Stopwatch.StartNew();
        var swRead = new Stopwatch(); var swInjections = new Stopwatch();
        var swHarvest = new Stopwatch(); var swFts = new Stopwatch();
        var swCommit = new Stopwatch(); var swShared = new Stopwatch();
        var swIndexes = new Stopwatch(); var swOptimize = new Stopwatch();

        using (var tx = db.BeginTransaction())
        {
            using var insertDef = Prepare(db, """
                INSERT INTO defs (id, def_type, def_name, label, description, source_mod, source_file,
                                  generated, class, fields_truncated,
                                  truncated_by_cap, truncated_by_length, truncated_by_depth,
                                  truncated_by_items, truncated_by_error)
                VALUES ($id,$t,$n,$l,$d,$sm,$sf,$g,$c,$ft,$tcap,$tlen,$tdep,$titm,$terr)
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
            using var insertXni = Prepare(db, """
                INSERT INTO xml_no_inherit (def_type, node_key, key_is_name, path)
                VALUES ($t,$k,$kn,$p)
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
                INSERT INTO type_field_paths (id, path, leaf)
                VALUES ($id,$p,$lf)
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
            using var insertShaderSource = Prepare(db, """
                INSERT INTO shader_sources (def_name, stage, file, resolved_path, unresolved)
                VALUES ($n,$st,$f,$p,$u)
                """);
            using var insertEconRecipe = Prepare(db, """
                INSERT INTO economy_recipes (economy_id, ordinal, def_name, product_count, work_amount,
                                             self_referential)
                VALUES ($id,$o,$n,$pc,$wa,$sr)
                """);

            var idsByName = new Dictionary<string, List<(long Id, string? Type)>>(StringComparer.Ordinal);
            var ftsExtra = new Dictionary<long, List<string>>();
            var pendingInjections = new List<(string defName, string defType, string path, string translated,
                                             string original, bool? applied, string? sourceFile)>();
            long nextId = 1;
            long nextKeyedId = 1;
            long nextEconomyId = 1;
            var tfPathIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var typeNameIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var subtreeIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var fvPathIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var fvValueIds = new Dictionary<string, long>(StringComparer.Ordinal);
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
                        "导出文件的 meta 行之前有数据行。须重新导出。");

                if (kind == IntermediateFormat.KindDef)
                {
                    if (sawInjKey)
                        throw new SnapshotFormatError(
                            "导出文件的注入键行之后还有 def 行，行的顺序不符合格式。须重新导出。");

                    var id = nextId++;
                    var defName = Str(root, IntermediateFormat.KeyDefName) ?? "";
                    var defTypeHere = Str(root, IntermediateFormat.KeyDefType);
                    (idsByName.TryGetValue(defName, out var sameName)
                        ? sameName
                        : idsByName[defName] = []).Add((id, defTypeHere));

                    var truncated = root.TryGetProperty(IntermediateFormat.KeyFieldsTruncated, out var ftEl)
                        ? ftEl.GetInt32() : 0;
                    if (truncated > 0) truncatedDefs++;

                    int Cause(string key)
                        => root.TryGetProperty(key, out var el) ? el.GetInt32() : 0;

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
                    Bind(insertDef, "$tcap", Cause(IntermediateFormat.KeyTruncatedByCap));
                    Bind(insertDef, "$tlen", Cause(IntermediateFormat.KeyTruncatedByLength));
                    Bind(insertDef, "$tdep", Cause(IntermediateFormat.KeyTruncatedByDepth));
                    Bind(insertDef, "$titm", Cause(IntermediateFormat.KeyTruncatedByItems));
                    Bind(insertDef, "$terr", Cause(IntermediateFormat.KeyTruncatedByError));
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
                                Bind(insertFvPath, "$lf", NoiseFilter.Leaf(path));
                                insertFvPath.ExecuteNonQuery();
                            }
                            Bind(insertFv, "$id", id);
                            Bind(insertFv, "$pid", fvPathId);
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
                                $"{defTypeW} '{nodeKey}' 的 xmlwritten 记录里，texts 数组与 paths 不逐项对应。须重新导出。");
                        if (wtexts.GetArrayLength() != wpaths.GetArrayLength())
                            throw new SnapshotFormatError(
                                $"{defTypeW} '{nodeKey}' 的 xmlwritten 记录有 {wpaths.GetArrayLength()} 条 paths、" +
                                $"{wtexts.GetArrayLength()} 条 texts。两个数组须逐项对应。" +
                                "须重新导出。");
                    }
                    var hasPatchedProp = root.TryGetProperty(IntermediateFormat.KeyPatched, out var wpatched);
                    if (hasPatchedProp)
                    {
                        if (!hasPaths || wpatched.ValueKind != JsonValueKind.Array)
                            throw new SnapshotFormatError(
                                $"{defTypeW} '{nodeKey}' 的 xmlwritten 记录里，patched 数组与 paths 不逐项对应。须重新导出。");
                        if (wpatched.GetArrayLength() != wpaths.GetArrayLength())
                            throw new SnapshotFormatError(
                                $"{defTypeW} '{nodeKey}' 的 xmlwritten 记录有 {wpaths.GetArrayLength()} 条 paths、" +
                                $"{wpatched.GetArrayLength()} 个 patched 标记。两个数组须逐项对应。" +
                                "须重新导出。");
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
                    if (root.TryGetProperty(IntermediateFormat.KeyNoInherit, out var wni)
                        && wni.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in wni.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.String) continue;
                            Bind(insertXni, "$t", defTypeW);
                            Bind(insertXni, "$k", nodeKey);
                            Bind(insertXni, "$kn", keyIsName);
                            Bind(insertXni, "$p", e.GetString() ?? "");
                            insertXni.ExecuteNonQuery();
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
                        var byHead = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                        foreach (var p in tpaths.EnumerateArray())
                        {
                            var path = p.GetString();
                            if (string.IsNullOrEmpty(path)) continue;
                            if (!tfPathIds.TryGetValue(path, out var pathId))
                            {
                                pathId = tfPathIds.Count + 1;
                                tfPathIds[path] = pathId;
                                Bind(insertTfPath, "$id", pathId);
                                Bind(insertTfPath, "$p", path);
                                Bind(insertTfPath, "$lf", NoiseFilter.Leaf(path));
                                insertTfPath.ExecuteNonQuery();
                            }
                            var head = HeadSegment(path);
                            if (!byHead.TryGetValue(head, out var bucket))
                                byHead[head] = bucket = [];
                            bucket.Add(pathId);
                        }

                        if (byHead.Count > 0)
                        {
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
                    Bind(insertKeyed, "$sm", Str(root, IntermediateFormat.KeySourceMod) is { Length: > 0 } km ? km : null);
                    Bind(insertKeyed, "$ph", root.TryGetProperty(IntermediateFormat.KeyPlaceholder, out var phEl)
                                            && phEl.GetBoolean() ? 1 : 0);
                    Bind(insertKeyed, "$origin", TranslationOrigin.Runtime);
                    insertKeyed.ExecuteNonQuery();

                    Bind(insertKeyedFts, "$id", kid);
                    Bind(insertKeyedFts, "$k", FtsText.ForIndex(key, identifier: true));
                    Bind(insertKeyedFts, "$tr", FtsText.ForIndex(translated));
                    Bind(insertKeyedFts, "$o", FtsText.ForIndex(original));
                    insertKeyedFts.ExecuteNonQuery();
                    keyedInEffect++;
                    continue;
                }

                if (kind == IntermediateFormat.KindEconomy)
                {
                    var eid = nextEconomyId++;
                    Bind(insertEcon, "$id", eid);
                    Bind(insertEcon, "$n", Str(root, IntermediateFormat.KeyDefName) ?? "");
                    Bind(insertEcon, "$l", Str(root, IntermediateFormat.KeyLabel));
                    Bind(insertEcon, "$cat", Str(root, IntermediateFormat.KeyEconomyCategory));
                    Bind(insertEcon, "$mod", Str(root, IntermediateFormat.KeyEconomyMod));
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

                if (kind == IntermediateFormat.KindShaderSource)
                {
                    Bind(insertShaderSource, "$n", Str(root, IntermediateFormat.KeyDefName) ?? "");
                    Bind(insertShaderSource, "$st", Str(root, IntermediateFormat.KeyShaderStage) ?? "");
                    Bind(insertShaderSource, "$f", Str(root, IntermediateFormat.KeyShaderFile) ?? "");
                    Bind(insertShaderSource, "$p", Str(root, IntermediateFormat.KeyShaderResolvedPath));
                    Bind(insertShaderSource, "$u", Str(root, IntermediateFormat.KeyShaderUnresolved));
                    insertShaderSource.ExecuteNonQuery();
                    continue;
                }

                if (kind == IntermediateFormat.KindInjKey)
                {
                    sawInjKey = true;
                    var ikName = Str(root, IntermediateFormat.KeyDefName) ?? "";
                    var ikType = Str(root, IntermediateFormat.KeyDefType) ?? "";
                    Bind(insertIk, "$id", Owner(Candidates(idsByName, ikName), ikType));
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
                throw new SnapshotFormatError("导出文件没有 meta 行，无法识别。须重新导出。");

            if (!sawEnd)
                throw new SnapshotFormatError(
                    "导出文件没有结束标记，内容不完整。须重新导出。");

            if (declaredRecords is { } dr && dr != records)
                throw new SnapshotFormatError(
                    $"导出文件声明 {dr} 条记录，实际读到 {records} 条。文件已损坏；须重新导出。");

            var ikTypeById = ikTypeIds.ToDictionary(kv => kv.Value, kv => kv.Key);
            var ikPathById = ikPathIds.ToDictionary(kv => kv.Value, kv => kv.Key);
            long IkId(Dictionary<string, long> ids, string? s)
                => s is not null && ids.TryGetValue(s, out var id) ? id : -1;

            using var probeSlot = Prepare(db,
                "SELECT def_type_id, path_id, translation_allowed FROM injection_keys " +
                "WHERE def_name_id = $n AND (path_id = $k OR suggested_path_id = $k) " +
                "AND ($t IS NULL OR def_type_id = $t) LIMIT 2");

            var slotSeen = new Dictionary<(string? Type, string Name, string Key),
                                          (string? Type, string Path, string State)>();

            (string Path, string? Key, string? Type, string? State) Resolve(
                string? defType, string defName, string key)
            {
                var probe = defType is { Length: > 0 } ? defType : null;
                if (!slotSeen.TryGetValue((probe, defName, key), out var hit))
                {
                    Bind(probeSlot, "$n", IkId(ikNameIds, defName));
                    Bind(probeSlot, "$k", IkId(ikPathIds, key));
                    Bind(probeSlot, "$t", probe is null ? null : IkId(ikTypeIds, probe));
                    using var rd = probeSlot.ExecuteReader();
                    if (!rd.Read())
                        hit = (probe, InjectionKey.ToFieldPath(key), InjectionKey.State.NoSlot);
                    else
                    {
                        var foundType = rd.IsDBNull(0) ? null : ikTypeById[rd.GetInt64(0)];
                        var indexPath = ikPathById[rd.GetInt64(1)];
                        var allowed = rd.GetInt64(2) != 0;
                        if (probe is null && rd.Read()) foundType = null;
                        hit = (probe ?? foundType, InjectionKey.ToFieldPath(indexPath),
                               allowed ? InjectionKey.State.Resolved : InjectionKey.State.Refused);
                    }
                    slotSeen[(probe, defName, key)] = hit;
                }
                return (hit.Path, key, hit.Type, hit.State);
            }

            swInjections.Start();
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
                Put(SnapshotSchema.MetaKeyRaw, meta.RawJson);
                Put(SnapshotSchema.MetaKeyFingerprint, meta.Fingerprint);
                Put(SnapshotSchema.MetaKeyImportedAtUtc, DateTime.UtcNow.ToString("O"));
                Put(SnapshotSchema.MetaKeyDefCount, defs.ToString());
                Put(SnapshotSchema.MetaKeySourcePath, Path.GetFileName(exportPath));
                Put(SnapshotSchema.MetaKeyHarvestedRoots, ModRoots.Count.ToString());
                Put(SnapshotSchema.MetaKeyTranslationsHarvest,
                    ModRoots.Count > 0 ? SnapshotSchema.TranslationsHarvestOn
                    : HarvestRequested ? SnapshotSchema.TranslationsHarvestNoRoots
                    : SnapshotSchema.TranslationsHarvestOff);

                if (economyState is not null)
                {
                    Put(SnapshotSchema.MetaKeyEconomyState, economyState);
                    if (economyError is not null) Put(SnapshotSchema.MetaKeyEconomyError, economyError);
                }

                if (exportTimings is not null) Put(SnapshotSchema.MetaKeyExportTimings, exportTimings);

                if (Environment is { } env)
                {
                    var loaded = meta.Mods.Where(m => m.Assemblies is not null)
                                     .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                                     .ToDictionary(g => g.Key, g => g.First().Assemblies!, StringComparer.OrdinalIgnoreCase);
                    var scan = ContentFingerprint.Scan(env, meta.Mods.Select(m => m.PackageId), meta.GameVersion,
                                                       meta.Language, loaded);
                    if (scan is not null) Put(SnapshotSchema.MetaKeyContent, scan.ToJson());
                }
            }

            swCommit.Start();
            tx.Commit();
            swCommit.Stop();

            swShared.Start();
            using (var fill = db.CreateCommand())
            {
                fill.CommandText =
                    "CREATE TEMP TABLE type_defs AS SELECT def_type, COUNT(*) n FROM defs GROUP BY def_type; " +
                    "INSERT INTO shared_values (def_type, path, value, defs) " +
                    "SELECT d.def_type, fp.path, fvv.value, COUNT(DISTINCT fv.def_id) n " +
                    "  FROM field_values fv JOIN defs d ON d.id = fv.def_id " +
                    "       JOIN field_value_paths fp ON fp.id = fv.path_id " +
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
                    ("total", swTotal.ElapsedMilliseconds),
                };
                t2.CommandText = "INSERT INTO meta (key, value) VALUES ($k,$v)";
                Bind(t2, "$k", SnapshotSchema.MetaKeyImportTimings);
                Bind(t2, "$v", JsonTree.Write(phases.ToDictionary(x => x.Name, x => (object?)x.Ms), indented: false));
                t2.ExecuteNonQuery();
            }

            stats = new ImportStats(defs, fieldValues, noise, runtimeTr, harvested,
                                    keyedInEffect, keyedHarvested, truncatedDefs, xmlNodes,
                                    economyRows, injKeys, economyState, meta, dbPath);
            }

            db.Close();
        });

        return stats!;
    }

    private static IReadOnlyList<(long Id, string? Type)> Candidates(
        Dictionary<string, List<(long Id, string? Type)>> idsByName, string defName)
        => idsByName.TryGetValue(defName, out var list) ? list : [];

    private static long? Owner(IReadOnlyList<(long Id, string? Type)> candidates, string? defType)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Id;
        var typed = candidates.Where(c => DefTypes.Same(c.Type, defType)).ToList();
        return typed.Count == 1 ? typed[0].Id : null;
    }

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
        var defTypes = idsByName.Values.SelectMany(v => v).Select(v => v.Type)
                                .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ModRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var modDir in SafeDirs(root))
            {
                var packageId = ReadPackageId(modDir) ?? Path.GetFileName(modDir);

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

                var folded = new Dictionary<(string DefName, string? DefType, string Path, string Text),
                                            (string FirstFile, int Files)>();
                foreach (var (injDir, injRel) in FindLanguageSubdirs(modDir, meta.Language, "DefInjected"))
                {
                    foreach (var typeDir in SafeDirs(injDir))
                    {
                        var declaredType = ResolveInjectedType(Path.GetFileName(typeDir), defTypes);
                        foreach (var xml in SafeFiles(typeDir, "*.xml"))
                        {
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
                foreach (var name in new[] { language, ContentFingerprint.LegacyFolderName(language) }
                                         .Distinct(StringComparer.Ordinal))
                {
                    var langDir = Path.Combine(lr, name);
                    if (!Directory.Exists(langDir)) continue;
                    var dir = Path.Combine(langDir, subdir);
                    if (Directory.Exists(dir))
                        yield return (dir, Path.GetRelativePath(modDir, dir).Replace('\\', '/'));
                    break;
                }
            }
        }
    }

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
            catch {  }
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

    private static double? Num(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int Flag(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True ? 1 : 0;

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
    public const string Runtime = "runtime";
    public const string Harvested = "harvested";
    public const string HarvestedOutside = "harvested_outside";
}
