using System;
using System.Collections;
using System.Collections.Generic;
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
    /// <summary>
    /// 游戏侧导出器。**只做反射遍历 + 写中间格式**,不建库、不过滤噪声、不分词。
    /// 这样它是纯托管的几十 KB,不需要 SQLite.Interop,也不需要在游戏进程里做 LoadLibrary。
    ///
    /// 过滤策略归 import 侧单一产地:策略变了重跑 import 就行,不必再进一次游戏。
    /// </summary>
    public static class DefExporter
    {
        /// <summary>
        /// 读取侧靠这个数分辨「这份快照里没人引用它」与「这份快照根本没量过这件事」——
        /// 两者在结果上逐字同形,而那正是本项目唯一不许留的形状。
        ///
        /// 0.2.0 起列表元素的运行时类型进索引(见 <see cref="Emit"/> 里 <c>.Class</c> 那一段);
        /// 0.3.0 起 keyed 那一层(界面文案 key → 显示文字)进导出,且基类声明的私有字段不再
        /// 整条消失(<see cref="FieldWalk.InstanceFields"/> 自己走基类链)。keyed 的在场判定
        /// 是数据驱动的(<c>KeyedCount()==0</c>),不配版本能力位。
        /// </summary>
        public const string ExporterVersion = "0.3.0";

        public static ExportLimits Limits = new ExportLimits();

        public static string Export(string targetPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 原子性的游戏侧一半:先写临时文件,完成后 rename —— 中途崩不会毁掉已有的那一份。
            var temp = targetPath + ".partial";
            if (File.Exists(temp)) File.Delete(temp);

            long records = 0;
            var defs = 0;
            var injections = 0;
            var keyed = 0;
            var xmlNodes = 0;

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

                    foreach (var def in all)
                    {
                        if (def == null) continue;
                        writer.WriteLine(BuildDefLine(def, defType));
                        records++;
                        defs++;
                    }
                }

                foreach (var line in BuildInjectionLines())
                {
                    writer.WriteLine(line);
                    records++;
                    injections++;
                }

                foreach (var line in BuildKeyedLines())
                {
                    writer.WriteLine(line);
                    records++;
                    keyed++;
                }

                // 继承层从 XML 原文再读一遍:到这个时点「谁继承谁」在内存里已经被
                // XmlInheritance.Clear() 抹掉了(详见 XmlNodeExporter)。
                foreach (var line in XmlNodeExporter.BuildLines())
                {
                    writer.WriteLine(line);
                    records++;
                    xmlNodes++;
                }

                // 尾行记录数标记 —— 完整性自证。游戏中途崩 = 这一行不在,import 拒收。
                records++;
                writer.WriteLine(new JsonLine()
                    .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
                    .Int(IntermediateFormat.KeyRecords, records)
                    .Int(IntermediateFormat.KeyDefs, defs)
                    .Int(IntermediateFormat.KeyInjections, injections)
                    .Int(IntermediateFormat.KeyKeyedCount, keyed)
                    .Int(IntermediateFormat.KeyXmlNodes, xmlNodes)
                    .ToString());

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
                    .Str(IntermediateFormat.KeyPackageId, pack.PackageId)
                    .Str(IntermediateFormat.KeyName, pack.Name)
                    .Str(IntermediateFormat.KeyVersion, meta == null ? "" : SafeModVersion(meta))
                    .ToString());
            }
            mods.Append(']');

            var limits = new JsonLine()
                .Int("max_field_depth", Limits.MaxFieldDepth)
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
                .ToString();
        }

        private static string SafeModVersion(ModMetaData meta)
        {
            try { return meta.ModVersion ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// mod 设置会改 patch 结果,所以它属于数据身份的一部分。目前只存哈希留缝,不参与寻址比对。
        /// </summary>
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

        private static string BuildDefLine(Def def, Type defType)
        {
            var fields = new List<ExportedField>();
            var state = new WalkState();
            Walk(def, "", 0, fields, state);

            var pack = def.modContentPack;
            return new JsonLine()
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
                .Int(IntermediateFormat.KeyFieldsTruncated, state.Truncated)
                .ToString();
        }

        /// <summary>
        /// ImpliedDefs 那一批由代码生成,没有 XML 文件 —— 这里记一个占位来源,由呈现侧说清。
        /// </summary>
        private static string ResolveSourceFile(Def def)
        {
            if (!string.IsNullOrEmpty(def.fileName)) return def.fileName;
            return def.generated ? IntermediateFormat.ImpliedDefsSourceFile : "";
        }

        private sealed class WalkState
        {
            public int Emitted;
            public int Truncated;
            public readonly HashSet<object> Seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        /// <summary>
        /// 绑定口径照抄游戏自己的 def 遍历(`DirectXmlSaver` / `DefInjectionUtility` 都是
        /// `Instance | Public | NonPublic`)。只绑 Public 会漏掉一整类**能从 XML 写进去、
        /// 却是私有字段**的数据 —— 1.6 的 `ThingDef.verbs` 与 `ProjectileProperties.damageAmountBase`
        /// 都是私有的。
        ///
        /// 两类要滤掉:编译器生成的自动属性后备字段(名字里带尖括号,不是数据),
        /// 以及游戏标了 `[Unsaved]` 的运行期字段 —— 沿用 `DirectXmlSaver.XElementFromField`
        /// 的判据,不另立一套。
        /// </summary>
        private static void Walk(object obj, string prefix, int depth,
                                 List<ExportedField> output, WalkState state)
        {
            var type = obj.GetType();
            // 比较基准:同一个运行时类型刚 new 出来的样子,答的是「C# 声明里这个字段初始是
            // 什么」。集合元素同理 —— 走到 comps[0] 时基准换成新 new 的那个 CompProperties
            // 子类,于是 props.energyMax 比的是它自己的初始值,不是「ThingDef 上有没有 comps」。
            var pristine = Pristine(type);
            foreach (var field in FieldWalk.InstanceFields(type))
            {
                if (Attribute.IsDefined(field, typeof(UnsavedAttribute))) continue;
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
                Emit(value, path, depth, output, state, known, baseline);
            }
        }

        /// <summary>
        /// 一个类型新 new 出来的样子,按类型缓存(失败也缓存,免得同一个坏类型被反复试)。
        ///
        /// 用 nonPublic:true 是因为数据类里私有/保护无参构造并不罕见;构造函数可能有副作用,
        /// 所以整段包在 try 里 —— 新不出来那一路的字段进 <see cref="DefaultState.Unknown"/>。
        /// </summary>
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

        /// <summary>
        /// 叶子不占深度 —— 沿用上游 ExtractFieldValuesRecursive 的语义:只有「往下钻进一个
        /// 复合对象」才消耗深度预算,所以同一个数值下覆盖比按节点计深要深得多。
        /// </summary>
        private static void Emit(object value, string path, int depth,
                                 List<ExportedField> output, WalkState state,
                                 bool baselineKnown, object baseline)
        {
            if (value == null) return;

            if (state.Emitted >= Limits.MaxFieldValuesPerDef) { state.Truncated++; return; }

            string leaf;
            if (TryLeaf(value, out leaf))
            {
                // 判默认态要在截断**之前**:两个长值前 400 字符相同、后面不同,截完就分不出来了。
                var defaultState = !baselineKnown
                    ? DefaultState.Unknown
                    : SameAsBaseline(leaf, baseline) ? DefaultState.Same : DefaultState.Differs;

                if (leaf.Length > Limits.MaxValueLength)
                {
                    leaf = leaf.Substring(0, Limits.MaxValueLength);
                    state.Truncated++;
                }
                output.Add(new ExportedField(path, leaf, defaultState));
                state.Emitted++;
                return;
            }

            if (depth >= Limits.MaxFieldDepth) { state.Truncated++; return; }
            if (!value.GetType().IsValueType && !state.Seen.Add(value)) return;

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                // 基准里对应位置的元素。基准列表是 null 或更短 = 这一项代码默认里根本没有,
                // 于是 baseline 为 null 而 known 仍为 true —— 那是一句判得出来的话,
                // 不该退化成「没法比」。
                var baselineItems = baselineKnown ? AsList(baseline) : null;
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= Limits.MaxCollectionItems) { state.Truncated++; break; }
                    var itemBaseline = baselineItems != null && i < baselineItems.Count ? baselineItems[i] : null;
                    Emit(item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1,
                         output, state, baselineKnown, itemBaseline);
                    i++;
                }
                return;
            }

            // 列表元素的**运行时类型**单发一条 `<path>.Class`,否则 `<li Class="JobGiver_
            // AnimalFlee">` 这类多态子对象的类型一条也不进索引(类型不是字段),而「哪些
            // ThinkTreeDef 挂了这个节点」是 def 侧最常见的反查之一。
            //
            // 只发给列表元素(路径以 ] 收尾):`<li Class=>` 只出现在列表里,而给每个嵌套
            // 对象都加一行会把 MaxFieldValuesPerDef 提前撞穿,换来更多 def 被截。
            if (path.Length > 0 && path[path.Length - 1] == ']')
            {
                if (state.Emitted >= Limits.MaxFieldValuesPerDef) { state.Truncated++; return; }
                var typeName = value.GetType().FullName;
                // 与基准同不同的判据跟别处一致:基准里同一位置是同一个类型 = 无从区分,
                // 类型不同或基准里根本没有这一项 = 作者写了 Class=。
                var classState = !baselineKnown ? DefaultState.Unknown
                    : baseline != null && baseline.GetType().FullName == typeName ? DefaultState.Same
                    : DefaultState.Differs;
                output.Add(new ExportedField(path + ".Class", typeName, classState));
                state.Emitted++;
            }

            // 钻进复合对象时基准换成**它自己类型**新 new 的一个,不沿用外层传下来的那个:
            // comps[0].props.energyMax 问的是「CompProperties_Shield 声明里 energyMax 是多少」,
            // 与「ThingDef 默认有没有 comps」是两个问题。
            Walk(value, path, depth + 1, output, state);
        }

        /// <summary>
        /// 与基准同不同。比的是**渲染后的叶子文本**,因为进快照的就是那一份 ——
        /// 拿对象 Equals 比会在 Def 引用、Type、结构体这几类上与快照里存的东西对不上。
        /// </summary>
        private static bool SameAsBaseline(string leaf, object baseline)
        {
            if (baseline == null) return false;
            string baselineLeaf;
            if (!TryLeaf(baseline, out baselineLeaf)) return false;
            return string.Equals(leaf, baselineLeaf, StringComparison.Ordinal);
        }

        private static IList<object> AsList(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string) return null;
            var list = new List<object>();
            try
            {
                // 只取到导出上限为止 —— 后面的元素反正不会被导出,而基准对象理论上可以是
                // 一个无穷序列,枚举到底会把整次导出挂死。
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
            if (type.IsPrimitive || type == typeof(decimal))
            {
                text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return true;
            }

            // Def 引用记 defName —— 这一条把「哪些 def 用了它」从文本匹配变成精确反查。
            var def = value as Def;
            if (def != null) { text = def.defName; return true; }

            var asType = value as Type;
            if (asType != null) { text = asType.FullName; return true; }

            // ModContentPack 等大对象不展开:整棵 mod 内容树挂在每个 def 上,展开一次就是几万条噪声。
            if (value is ModContentPack) { text = ((ModContentPack)value).PackageId; return true; }

            if (type.IsValueType && type.Namespace != null &&
                (type.Namespace.StartsWith("UnityEngine") || type.Namespace == "Verse"))
            {
                // IntVec3 / Vector3 / IntRange 这类小结构体,ToString 比展开成三个分量有用
                if (!type.IsEnum && type.IsLayoutSequential || type.IsExplicitLayout || IsSimpleStruct(type))
                {
                    text = value.ToString();
                    return true;
                }
            }

            text = null;
            return false;
        }

        private static bool IsSimpleStruct(Type type)
        {
            if (!type.IsValueType) return false;
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length == 0 || fields.Length > 4) return false;
            foreach (var f in fields)
                if (!f.FieldType.IsPrimitive && f.FieldType != typeof(string)) return false;
            return true;
        }

        /// <summary>
        /// defInjections 倾倒。导出时刻**译文已经在 def 对象上**,而**被替换的原文留在注入记录的
        /// replacedString 里** —— 两者同时在场,一次导出就能拿到双语。
        /// 游戏语言为英文时这一节自然为空,不需要分支。
        /// </summary>
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
                        .ToString();
                }
            }
        }

        /// <summary>
        /// Keyed 译文倾倒 —— 界面文案那一层(<c>"SomeKey".Translate()</c> 里的 SomeKey)。
        ///
        /// <c>KeyedReplacement</c> 没有 <c>replacedString</c>,所以 defInjections 那个「译文与原文
        /// 同时在场」的便宜在这里不存在:英文侧只能从 <c>defaultLanguage</c> 另取,官方自己
        /// 就这么干(<c>LanguageReportGenerator.SaveTranslationReport</c> 显式 <c>LoadData()</c>
        /// 两份语言,再用 <c>defaultLanguage.TryGetTextFromKey</c> 对照)。
        ///
        /// 与 defInjections 两处**有意的**不同:
        ///
        /// 1. **英文环境下这一节不为空** —— keyed 的数据源就是英文语言文件,于是 translated
        ///    是英文串本身,original 留空。
        /// 2. **占位译文照样导出**,带 placeholder 标记:「语言包里有这个 key 但没译」正是
        ///    汉化对账要问的东西,丢掉它就与「没有这个 key」同形了。
        ///
        /// 覆盖冲突**只记赢家**:keyedReplacements 是全部 mod 合并后的最终值,后加载的盖掉
        /// 先加载的,而这个工具答的是「游戏最终用的是哪一句」。<c>fileSource</c> 说清赢家出处。
        /// </summary>
        private static IEnumerable<string> BuildKeyedLines()
        {
            var lang = LanguageDatabase.activeLanguage;
            if (lang == null || lang.keyedReplacements == null) yield break;

            // 英文侧。TryGetTextFromKey 自己也会兜底 LoadData,显式调用是为了让「这里会多读
            // 一份语言数据」在代码里看得见。
            var english = LanguageDatabase.defaultLanguage;
            if (english != null && english != lang)
            {
                try { english.LoadData(); }
                catch (Exception ex)
                {
                    // 英文侧取不到不该让整份导出失败,但降级要说出口 —— 否则「这份快照的
                    // keyed 没有英文」会被当成数据本来如此。
                    Log.Warning("[RimSearcher] could not load English keyed translations for the original-text " +
                                "column; keyed rows will carry no original: " + ex.Message);
                    english = null;
                }
            }

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
                    .Bool(IntermediateFormat.KeyPlaceholder, rep.isPlaceholder)
                    .ToString();
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
