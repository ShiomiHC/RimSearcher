using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using RimSearcher.Contract;
using RimWorld;
using Verse;

namespace RimSearcher.DataMod
{
    public static class DefExporter
    {
        public const string ExporterVersion = "0.27.0";

        public static ExportLimits Limits = new ExportLimits();

        public static string Export(string targetPath) => Export(targetPath, false);

        public static string Export(string targetPath, bool skipEconomy)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = targetPath + ".partial";
            if (File.Exists(temp)) File.Delete(temp);

            Warned.Clear();

            long records = 0;
            var swTotal = Stopwatch.StartNew();
            var swEconomy = new Stopwatch();
            var swDefs = new Stopwatch();
            var swTypeFields = new Stopwatch();
            var swInjections = new Stopwatch();
            var swInjKeys = new Stopwatch();
            var swInjKeysEmit = new Stopwatch();
            var swKeyed = new Stopwatch();
            var swXmlNodes = new Stopwatch();
            var defs = 0;
            var injections = 0;
            var keyed = 0;
            var xmlNodes = 0;
            var injKeys = 0;

            List<string> economyLines = null;
            var economyState = IntermediateFormat.EconomyStateSkipped;
            string economyError = null;

            if (!skipEconomy)
            {
                swEconomy.Start();
                try
                {
                    economyError = EconomyExporter.ResolveReflection();
                    if (economyError == null)
                    {
                        economyLines = EconomyExporter.Build();
                        economyState = IntermediateFormat.EconomyStateOk;
                    }
                    else
                    {
                        economyState = IntermediateFormat.EconomyStateUnavailable;
                    }
                }
                catch (Exception ex)
                {
                    economyLines = null;
                    economyState = IntermediateFormat.EconomyStateUnavailable;
                    economyError = "The economy extraction threw partway through and its rows were discarded: "
                                 + ex.GetType().Name + ": " + ex.Message
                                 + ". The rest of this snapshot is complete and usable.";
                }

                swEconomy.Stop();
                if (economyState == IntermediateFormat.EconomyStateUnavailable)
                    Log.Warning("[RimSearcher] economy layer unavailable: " + economyError);
            }

            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";

                writer.WriteLine(BuildMetaLine());
                records++;

                foreach (var defType in GenDefDatabase.AllDefTypesWithDatabases())
                {
                    IEnumerable<Def> all;
                    try { all = GenDefDatabase.GetAllDefsInDatabaseForDef(defType); }
                    catch (Exception ex)
                    {
                        Log.Warning("[RimSearcher] skipping def type " + defType.Name + ": " + ex.Message);
                        continue;
                    }

                    swDefs.Start();
                    foreach (var def in all)
                    {
                        if (def == null) continue;
                        writer.WriteLine(BuildDefLine(def, defType));
                        records++;
                        defs++;
                    }
                    swDefs.Stop();

                    swTypeFields.Start();
                    writer.WriteLine(BuildTypeFieldsLine(defType));
                    records++;
                    swTypeFields.Stop();
                }

                swInjections.Start();
                foreach (var line in BuildInjectionLines())
                {
                    writer.WriteLine(line);
                    records++;
                    injections++;
                }
                swInjections.Stop();

                swInjKeys.Start();
                ForEachInjectionKeyLine(swInjKeysEmit, line =>
                {
                    writer.WriteLine(line);
                    records++;
                    injKeys++;
                });
                swInjKeys.Stop();

                swKeyed.Start();
                foreach (var line in BuildKeyedLines())
                {
                    writer.WriteLine(line);
                    records++;
                    keyed++;
                }
                swKeyed.Stop();

                swXmlNodes.Start();
                foreach (var line in XmlNodeExporter.BuildLines())
                {
                    writer.WriteLine(line);
                    records++;
                    xmlNodes++;
                }
                swXmlNodes.Stop();

                var economy = 0;
                if (economyLines != null)
                {
                    foreach (var line in economyLines)
                    {
                        writer.WriteLine(line);
                        records++;
                        economy++;
                    }
                }

                foreach (var line in ShaderSourceExporter.BuildLines())
                {
                    writer.WriteLine(line);
                    records++;
                }

                records++;
                var end = new JsonLine()
                    .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
                    .Int(IntermediateFormat.KeyRecords, records)
                    .Int(IntermediateFormat.KeyDefs, defs)
                    .Int(IntermediateFormat.KeyInjections, injections)
                    .Int(IntermediateFormat.KeyInjKeys, injKeys)
                    .Int(IntermediateFormat.KeyKeyedCount, keyed)
                    .Int(IntermediateFormat.KeyXmlNodes, xmlNodes)
                    .Int(IntermediateFormat.KeyEconomyRows, economy)
                    .Str(IntermediateFormat.KeyEconomyState, economyState);
                if (economyError != null) end.Str(IntermediateFormat.KeyEconomyError, economyError);
                end.Raw(IntermediateFormat.KeyTimingsMs, new JsonLine()
                    .Int(IntermediateFormat.TimingKeys.Economy, swEconomy.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.Defs, swDefs.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.TypeFields, swTypeFields.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.Injections, swInjections.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.InjKeys, swInjKeys.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.InjKeysEmit, swInjKeysEmit.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.Keyed, swKeyed.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.XmlNodes, swXmlNodes.ElapsedMilliseconds)
                    .Int(IntermediateFormat.TimingKeys.Total, swTotal.ElapsedMilliseconds)
                    .ToString());
                writer.WriteLine(end.ToString());

                writer.Flush();
            }

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(temp, targetPath);
            return targetPath;
        }

        private static string BuildMetaLine()
        {
            var mods = new StringBuilder("[");
            var first = true;
            foreach (var pack in LoadedModManager.RunningModsListForReading)
            {
                if (!first) mods.Append(',');
                first = false;
                var meta = ModLister.GetModWithIdentifier(pack.PackageId);
                mods.Append(new JsonLine()
                    .Str(IntermediateFormat.KeyPackageId,
                        IntermediateFormat.IsExporterPackageId(pack.PackageId)
                            ? IntermediateFormat.ExporterPackageId
                            : pack.PackageId)
                    .Str(IntermediateFormat.KeyName, pack.Name)
                    .Str(IntermediateFormat.KeyVersion, meta == null ? "" : SafeModVersion(meta))
                    .Raw(IntermediateFormat.KeyAssemblies, LoadedAssemblies(pack))
                    .ToString());
            }
            mods.Append(']');

            var limits = new JsonLine()
                .Int("max_field_depth", Limits.MaxFieldDepth)
                .Int("max_instance_field_depth", Limits.MaxInstanceFieldDepth)
                .Int("max_field_values_per_def", Limits.MaxFieldValuesPerDef)
                .Int("max_value_length", Limits.MaxValueLength)
                .Int("max_collection_items", Limits.MaxCollectionItems)
                .ToString();

            return new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
                .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
                .Str(IntermediateFormat.KeyExporterVersion, ExporterVersion)
                .Str(IntermediateFormat.KeyExportedAtUtc, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Str(IntermediateFormat.KeyGameVersion, VersionControl.CurrentVersionStringWithRev)
                .Str(IntermediateFormat.KeyLanguage, LanguageDatabase.activeLanguage == null
                    ? "English" : LanguageDatabase.activeLanguage.folderName)
                .Raw(IntermediateFormat.KeyMods, mods.ToString())
                .Raw(IntermediateFormat.KeyLimits, limits)
                .Str(IntermediateFormat.KeyModSettingsHash, ModSettingsHash())
                .Str(IntermediateFormat.KeyPatchRoute, PatchedXml.Route)
                .ToString();
        }

        private static string LoadedAssemblies(ModContentPack pack)
        {
            try
            {
                var sb = new StringBuilder("[");
                var first = true;
                foreach (var asm in pack.assemblies.loadedAssemblies)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(new JsonLine()
                        .Str(IntermediateFormat.KeyName, asm.GetName().Name)
                        .Str(IntermediateFormat.KeyMvid, asm.ManifestModule.ModuleVersionId.ToString())
                        .ToString());
                }
                return sb.Append(']').ToString();
            }
            catch { return "null"; }
        }

        private static string SafeModVersion(ModMetaData meta)
        {
            try { return meta.ModVersion ?? ""; }
            catch { return ""; }
        }

        private static string ModSettingsHash()
        {
            try
            {
                var dir = GenFilePaths.ConfigFolderPath;
                if (!Directory.Exists(dir)) return "";
                var sb = new StringBuilder();
                var files = Directory.GetFiles(dir, "Mod_*.xml");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var f in files)
                    sb.Append(Path.GetFileName(f)).Append(':').Append(new FileInfo(f).Length).Append(';');
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))
                                       .Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static string BuildTypeFieldsLine(Type defType)
        {
            var paths = TypeFieldWalk.Collect(defType, Limits.MaxFieldDepth,
                                              f => Attribute.IsDefined(f, typeof(UnsavedAttribute))
                                                   || IsOpaque(f.DeclaringType),
                                              IsLeafType);
            return new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindTypeFields)
                .Str(IntermediateFormat.KeyDefType, defType.Name)
                .Strs(IntermediateFormat.KeyPaths, paths)
                .ToString();
        }

        private static bool IsOpaque(Type type)
            => typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type);

        private static bool IsLeafType(Type type)
        {
            if (TypeFieldWalk.DefaultIsLeaf(type)) return true;
            if (typeof(Def).IsAssignableFrom(type)) return true;
            if (typeof(ModContentPack).IsAssignableFrom(type)) return true;
            if (typeof(MemberInfo).IsAssignableFrom(type)) return true;
            return WrittenAsText(type);
        }

        private static bool WrittenAsText(Type type)
            => !IsOpaque(type) && ParseHelper.HandlesType(type);

        private static string BuildDefLine(Def def, Type defType)
        {
            var fields = new List<ExportedField>();
            var state = new WalkState();
            Walk(def, "", 0, fields, state);

            var pack = def.modContentPack;
            var line = new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDef)
                .Str(IntermediateFormat.KeyDefType, defType.Name)
                .Str(IntermediateFormat.KeyDefName, def.defName ?? "")
                .Str(IntermediateFormat.KeyLabel, def.label ?? "")
                .Str(IntermediateFormat.KeyDescription, def.description ?? "")
                .Str(IntermediateFormat.KeySourceMod, pack == null ? "" : pack.PackageId)
                .Str(IntermediateFormat.KeySourceFile, ResolveSourceFile(def))
                .Bool(IntermediateFormat.KeyGenerated, def.generated)
                .Str(IntermediateFormat.KeyClass, def.GetType().FullName)
                .Fields(IntermediateFormat.KeyFields, fields)
                .Int(IntermediateFormat.KeyFieldsTruncated, state.Truncated);

            if (state.Truncated > 0)
            {
                line.Int(IntermediateFormat.KeyTruncatedByCap, state.TruncCount)
                    .Int(IntermediateFormat.KeyTruncatedByLength, state.TruncLen)
                    .Int(IntermediateFormat.KeyTruncatedByDepth, state.TruncDepth)
                    .Int(IntermediateFormat.KeyTruncatedByItems, state.TruncItems);
                if (state.TruncError > 0)
                    line.Int(IntermediateFormat.KeyTruncatedByError, state.TruncError);
            }

            return line.ToString();
        }

        private static string ResolveSourceFile(Def def)
        {
            if (!string.IsNullOrEmpty(def.fileName)) return def.fileName;
            return def.generated ? IntermediateFormat.ImpliedDefsSourceFile : "";
        }

        private sealed class WalkState
        {
            public int Emitted;

            public int TruncCount, TruncLen, TruncDepth, TruncItems;

            public int TruncError;

            public int Truncated { get { return TruncCount + TruncLen + TruncDepth + TruncItems + TruncError; } }
            public readonly HashSet<object> Seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        private static void Walk(object obj, string prefix, int depth,
                                 List<ExportedField> output, WalkState state)
        {
            var type = obj.GetType();
            var pristine = Pristine(type);
            var noisy = ConstructorRandomFields(type);
            var randomBodyType = prefix.Length == 0 && IsHarRandomBodyType(obj);
            foreach (var field in FieldWalk.InstanceFields(type))
            {
                if (Attribute.IsDefined(field, typeof(UnsavedAttribute))) continue;
                if (noisy.Contains(field.Name)) continue;
                if (randomBodyType && field.Name == "bodyTypeGlobal") continue;
                object value;
                try { value = field.GetValue(obj); }
                catch { continue; }
                var path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
                object baseline = null;
                var known = pristine != null;
                if (known)
                {
                    try { baseline = field.GetValue(pristine); }
                    catch { known = false; }
                }
                try { Emit(value, path, depth, output, state, known, baseline, field.FieldType); }
                catch (Exception ex)
                {
                    state.TruncError++;
                    WarnOnce(type.FullName + "." + field.Name, "reading field " + type.FullName + "." + field.Name, ex);
                }
            }
        }

        private static void WarnOnce(string key, string what, Exception ex)
        {
            if (!Warned.Add(key)) return;
            Log.Warning("[RimSearcher] " + what + " threw " + ex.GetType().Name + ": " + ex.Message
                        + "; values like it are left out of the field table and counted as truncated_by_error.");
        }

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static object Pristine(Type type)
        {
            object cached;
            if (PristineCache.TryGetValue(type, out cached)) return cached;

            object made = null;
            if (!type.IsAbstract && !type.IsInterface && type != typeof(string))
            {
                try { made = Activator.CreateInstance(type, true); }
                catch { made = null; }
            }
            PristineCache[type] = made;
            return made;
        }

        private static readonly Dictionary<Type, object> PristineCache = new Dictionary<Type, object>();

        private static HashSet<string> ConstructorRandomFields(Type type)
        {
            HashSet<string> cached;
            if (RandomFieldsCache.TryGetValue(type, out cached)) return cached;

            var first = Pristine(type);
            object second = null;
            if (first != null)
            {
                try { second = Activator.CreateInstance(type, true); }
                catch { second = null; }
            }
            var built = RuntimeValues.ConstructorRandomFields(first, second, FieldWalk.InstanceFields(type), TryLeaf);
            RandomFieldsCache[type] = built;
            return built;
        }

        private static readonly Dictionary<Type, HashSet<string>> RandomFieldsCache = new Dictionary<Type, HashSet<string>>();

        private static bool IsHarRandomBodyType(object def)
        {
            if (HarRandomBodyType == null)
            {
                HarRandomBodyType = new HashSet<object>(ReferenceEqualityComparer.Instance);
                try
                {
                    var type = GenTypes.GetTypeInAnyAssembly("AlienRace.AlienBackstoryDef");
                    var field = type == null ? null
                        : type.GetField("checkBodyType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var set = field == null ? null : field.GetValue(null) as IEnumerable;
                    if (set != null)
                        foreach (var d in set) HarRandomBodyType.Add(d);
                }
                catch { }
            }
            return HarRandomBodyType.Contains(def);
        }

        private static HashSet<object> HarRandomBodyType;

        private static void Emit(object value, string path, int depth,
                                 List<ExportedField> output, WalkState state,
                                 bool baselineKnown, object baseline, Type declared)
        {
            if (value == null || IsOpaque(value.GetType()) || RuntimeValues.IsHandle(value.GetType())) return;

            if (state.Emitted >= Limits.MaxFieldValuesPerDef) { state.TruncCount++; return; }

            string leaf;
            Exception error;
            var read = RuntimeValues.ReadLeaf(value, TryLeaf, out leaf, out error);
            if (read == RuntimeValues.LeafRead.Unreadable)
            {
                state.TruncError++;
                WarnOnce(value.GetType().FullName, "rendering a " + value.GetType().FullName, error);
                return;
            }
            if (read == RuntimeValues.LeafRead.Leaf)
            {
                if (leaf == null) return;

                var defaultState = !baselineKnown ? DefaultState.Unknown : AgainstBaseline(leaf, baseline);

                if (leaf.Length > Limits.MaxValueLength)
                {
                    leaf = leaf.Substring(0, Limits.MaxValueLength);
                    state.TruncLen++;
                }
                output.Add(new ExportedField(path, leaf, defaultState));
                state.Emitted++;
                return;
            }

            if (depth >= Limits.MaxInstanceFieldDepth) { state.TruncDepth++; return; }
            if (!value.GetType().IsValueType && !state.Seen.Add(value)) return;

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                var baselineItems = baselineKnown ? AsList(baseline) : null;
                var elementType = NestedClass.ElementType(value.GetType()) ?? NestedClass.ElementType(declared);
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= Limits.MaxCollectionItems) { state.TruncItems++; break; }
                    var itemBaseline = baselineItems != null && i < baselineItems.Count ? baselineItems[i] : null;
                    Emit(item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1,
                         output, state, baselineKnown, itemBaseline, elementType);
                    i++;
                }
                return;
            }

            if (NestedClass.ShouldEmit(value.GetType(), declared))
            {
                if (state.Emitted >= Limits.MaxFieldValuesPerDef) { state.TruncCount++; return; }
                var typeName = value.GetType().FullName;
                var classState = !baselineKnown ? DefaultState.Unknown
                    : baseline != null && baseline.GetType().FullName == typeName ? DefaultState.Same
                    : DefaultState.Differs;
                output.Add(new ExportedField(path + ".Class", typeName, classState));
                state.Emitted++;
            }

            Walk(value, path, depth + 1, output, state);
        }

        private static int AgainstBaseline(string leaf, object baseline)
        {
            if (baseline == null) return DefaultState.Differs;
            string baselineLeaf;
            switch (RuntimeValues.ReadLeaf(baseline, TryLeaf, out baselineLeaf))
            {
                case RuntimeValues.LeafRead.Unreadable: return DefaultState.Unknown;
                case RuntimeValues.LeafRead.NotLeaf: return DefaultState.Differs;
                default:
                    return string.Equals(leaf, baselineLeaf, StringComparison.Ordinal)
                        ? DefaultState.Same : DefaultState.Differs;
            }
        }

        private static IList<object> AsList(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string) return null;
            var list = new List<object>();
            try
            {
                foreach (var item in enumerable)
                {
                    if (list.Count >= Limits.MaxCollectionItems) break;
                    list.Add(item);
                }
            }
            catch { return null; }
            return list;
        }

        private static bool TryLeaf(object value, out string text)
        {
            var type = value.GetType();

            if (type == typeof(string)) { text = (string)value; return true; }
            if (type.IsEnum) { text = value.ToString(); return true; }
            if (value is bool flag) { text = ValueText.Bool(flag); return true; }
            if (type.IsPrimitive || type == typeof(decimal))
            {
                text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return true;
            }

            var def = value as Def;
            if (def != null) { text = def.defName; return true; }

            var asType = value as Type;
            if (asType != null) { text = asType.FullName; return true; }

            if (RuntimeValues.TryMemberText(value, out text)) return true;

            if (value is ModContentPack) { text = ((ModContentPack)value).PackageId; return true; }

            if (value is CurvePoint p) { text = p.Loc.ToString(); return true; }

            if (value is FloatRange range) { text = ValueText.Range(range.min, range.max); return true; }
            if (value is UnityEngine.Color color)
            {
                text = ValueText.Tuple(new[] { color.r, color.g, color.b, color.a });
                return true;
            }
            if (value is Rot4 rot) { text = ValueText.Rotation(rot.AsInt, 4); return true; }
            if (TryRot8(value, out text)) return true;

            if (WrittenAsText(type))
            {
                text = type.IsValueType ? ValueText.Canonical(value.ToString()) : value.ToString();
                return true;
            }

            text = null;
            return false;
        }

        private static bool TryRot8(object value, out string text)
        {
            text = null;
            var type = value.GetType();
            if (type.FullName != "SmashTools.Rot8") return false;
            if (Rot8Field == null || Rot8Field.DeclaringType != type)
                Rot8Field = type.GetField("rotInt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (Rot8Field == null) return false;
            text = ValueText.Rotation(Convert.ToInt32(Rot8Field.GetValue(value), CultureInfo.InvariantCulture), 8);
            return true;
        }

        private static FieldInfo Rot8Field;

        private static IEnumerable<string> BuildInjectionLines()
        {
            var lang = LanguageDatabase.activeLanguage;
            if (lang == null) yield break;

            foreach (var package in lang.defInjections)
            {
                if (package == null || package.injections == null) continue;
                var typeName = package.defType == null ? "" : package.defType.Name;

                foreach (var pair in package.injections)
                {
                    var inj = pair.Value;
                    if (inj == null || inj.isPlaceholder) continue;

                    var path = inj.path ?? pair.Key;
                    var dot = path.IndexOf('.');
                    if (dot <= 0) continue;

                    var translated = inj.injection;
                    if (translated == null && inj.fullListInjection != null)
                        translated = string.Join(" | ", inj.fullListInjection.ToArray());
                    if (string.IsNullOrEmpty(translated)) continue;

                    var original = inj.replacedString;
                    if (string.IsNullOrEmpty(original) && inj.replacedList != null)
                    {
                        var parts = new List<string>();
                        foreach (var s in inj.replacedList) parts.Add(s);
                        original = string.Join(" | ", parts.ToArray());
                    }

                    yield return new JsonLine()
                        .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDefInjection)
                        .Str(IntermediateFormat.KeyDefType, typeName)
                        .Str(IntermediateFormat.KeyDefName, path.Substring(0, dot))
                        .Str(IntermediateFormat.KeyPath, path.Substring(dot + 1))
                        .Str(IntermediateFormat.KeyTranslated, translated)
                        .Str(IntermediateFormat.KeyOriginal, original ?? "")
                        .Bool(IntermediateFormat.KeyInjected, inj.injected)
                        .Str(IntermediateFormat.KeySourceFile, inj.fileSource ?? "")
                        .ToString();
                }
            }
        }

        private static void ForEachInjectionKeyLine(Stopwatch swEmit, Action<string> emit)
        {
            foreach (var defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                var typeName = defType.Name;
                try
                {
                    DefInjectionUtility.ForEachPossibleDefInjection(defType,
                        (suggestedPath, normalizedPath, isCollection, currentValue,
                         currentValueCollection, translationAllowed, fullListTranslationAllowed,
                         fieldInfo, def) =>
                        {
                            if (def == null || normalizedPath == null) return;

                            var prefix = def.defName + ".";
                            if (!normalizedPath.StartsWith(prefix, StringComparison.Ordinal)) return;

                            var suggested = suggestedPath;
                            if (suggested != null && suggested.StartsWith(prefix, StringComparison.Ordinal))
                                suggested = suggested.Substring(prefix.Length);

                            swEmit.Start();
                            emit(new JsonLine()
                                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindInjKey)
                                .Str(IntermediateFormat.KeyDefType, typeName)
                                .Str(IntermediateFormat.KeyDefName, def.defName)
                                .Str(IntermediateFormat.KeyPath, normalizedPath.Substring(prefix.Length))
                                .Str(IntermediateFormat.KeySuggestedPath, suggested ?? "")
                                .Bool(IntermediateFormat.KeyIsCollection, isCollection)
                                .Bool(IntermediateFormat.KeyTranslationAllowed, translationAllowed)
                                .Bool(IntermediateFormat.KeyFullListTranslationAllowed,
                                      fullListTranslationAllowed)
                                .ToString());
                            swEmit.Stop();
                        });
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimSearcher] skipping injection keys for def type " + typeName
                                + ": " + ex.Message);
                }
            }
        }

        private static IEnumerable<string> BuildKeyedLines()
        {
            var lang = LanguageDatabase.activeLanguage;
            if (lang == null || lang.keyedReplacements == null) yield break;

            var english = LanguageDatabase.defaultLanguage;
            if (english != null && english != lang)
            {
                try { english.LoadData(); }
                catch (Exception ex)
                {
                    Log.Warning("[RimSearcher] could not load English keyed translations for the original-text " +
                                "column; keyed rows will carry no original: " + ex.Message);
                    english = null;
                }
            }

            var roots = new List<KeyValuePair<string, string>>();
            foreach (var pack in LoadedModManager.RunningModsListForReading)
                if (!string.IsNullOrEmpty(pack.RootDir))
                    roots.Add(new KeyValuePair<string, string>(NormalizeDir(pack.RootDir), pack.PackageId));
            roots.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));

            foreach (var pair in lang.keyedReplacements)
            {
                var rep = pair.Value;
                if (rep == null) continue;

                var key = rep.key;
                if (string.IsNullOrEmpty(key)) key = pair.Key;
                if (string.IsNullOrEmpty(key)) continue;

                var translated = rep.value;
                if (translated == null) translated = "";

                var original = "";
                if (english != null && english != lang)
                {
                    TaggedString eng;
                    if (english.TryGetTextFromKey(key, out eng) && eng.RawText != null)
                        original = eng.RawText;
                }

                yield return new JsonLine()
                    .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindKeyed)
                    .Str(IntermediateFormat.KeyKeyedKey, key)
                    .Str(IntermediateFormat.KeyTranslated, translated)
                    .Str(IntermediateFormat.KeyOriginal, original)
                    .Str(IntermediateFormat.KeySourceFile, rep.fileSource ?? "")
                    .Int(IntermediateFormat.KeySourceLine, rep.fileSourceLine)
                    .Str(IntermediateFormat.KeySourceMod, ModOf(rep.fileSourceFullPath, roots))
                    .Bool(IntermediateFormat.KeyPlaceholder, rep.isPlaceholder)
                    .ToString();
            }
        }

        private static string NormalizeDir(string dir)
        {
            var d = dir.Replace('\\', '/');
            return d.EndsWith("/") ? d : d + "/";
        }

        private static string ModOf(string fullPath, List<KeyValuePair<string, string>> roots)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            var p = fullPath.Replace('\\', '/');
            foreach (var r in roots)
                if (p.StartsWith(r.Key, StringComparison.OrdinalIgnoreCase)) return r.Value;
            return "";
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
