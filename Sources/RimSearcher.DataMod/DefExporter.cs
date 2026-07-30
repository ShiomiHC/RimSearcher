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
    /// 游戏侧导出器。**只做反射遍历 + 写中间格式**,不建库、不过滤噪声、不分词
    /// (B 案分工,06 层 1)。这样它是纯托管的几十 KB,没有 SQLite.Interop、没有 LoadLibrary
    /// hack,上游 02-8 那一整条问题在这里不存在。
    ///
    /// 不过滤噪声是有意的:过滤策略归 import 侧单一产地(02-2),策略变了重跑 import 就行,
    /// 不必再进一次游戏。
    /// </summary>
    public static class DefExporter
    {
        /// <summary>
        /// 0.2.0 起,列表元素的运行时类型也进索引(见 <see cref="Emit"/> 里 <c>.Class</c> 那一段)。
        /// 读取侧靠这个数分辨「这份快照里没人引用它」与「这份快照根本没量过这件事」——
        /// 两者在结果上逐字同形,而那正是本项目唯一不许留的形状。
        /// </summary>
        public const string ExporterVersion = "0.2.0";

        public static ExportLimits Limits = new ExportLimits();

        public static string Export(string targetPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 原子性的游戏侧一半(02-6):先写临时文件,完成后 rename。
            // 上游是 File.Delete 旧库再从头写,中途崩就一个库都没有。
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

                // 继承层。这一节从 XML 原文再读一遍,理由见 XmlNodeExporter ——
                // 到这个时点「谁继承谁」在内存里已经被 XmlInheritance.Clear() 抹掉了。
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
        /// mod 设置会改 patch 结果(03 甲),所以它属于数据身份的一部分。第一批只存哈希留缝,
        /// 不参与寻址比对 —— 06 开放点记着这条。
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
        /// ImpliedDefs 那一批没有 XML 文件,来源标记是字符串(00 论据 1 / 03 甲)。
        /// 这里如实记下事实,呈现侧再按 R51 在作用块里说清「代码生成,无 XML 源文件」。
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
        /// 都是私有的,漏掉它们意味着「这把枪打什么弹、这颗弹多少伤害」在快照里根本不存在,
        /// 而输出侧无从区分「没这个字段」和「没看见这个字段」—— 缺席会被读成事实。
        ///
        /// 两类要滤掉:编译器生成的自动属性后备字段(名字里带尖括号,不是数据),
        /// 以及游戏亲自标了 `[Unsaved]` 的运行期字段 —— 那是游戏自己声明的「这不是数据」,
        /// `DirectXmlSaver.XElementFromField` 就是照这条跳过的,直接沿用而不是另立一套判据。
        /// </summary>
        private static void Walk(object obj, string prefix, int depth,
                                 List<ExportedField> output, WalkState state)
        {
            var type = obj.GetType();
            // 比较基准:同一个运行时类型刚 new 出来的样子。整个 R1 的判据就这一句 ——
            // 它答的是「C# 声明里这个字段初始是什么」,包括集合元素:走到 comps[0] 时基准
            // 换成新 new 的那个 CompProperties 子类,于是 props.energyMax 比的是它自己的初始值,
            // 而不是「ThingDef 上有没有 comps」。
            var pristine = Pristine(type);
            foreach (var field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.IsStatic) continue;
                if (field.Name.IndexOf('<') >= 0) continue;
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
        /// 用 nonPublic:true 是因为数据类里私有/保护无参构造并不罕见;构造函数有副作用的
        /// 类型理论上存在,所以整段包在 try 里 —— 新不出来就返回 null,那一路的字段进
        /// <see cref="DefaultState.Unknown"/>,呈现侧照常显示。
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
        /// 叶子不占深度 —— 上游 ExtractFieldValuesRecursive 的语义(03 乙的换算口径)。
        /// 只有「往下钻进一个复合对象」才消耗深度预算,所以同一个数值下覆盖比按节点计深要深得多。
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
                // 基准里对应位置的元素。基准列表是 null 或更短 = 这一项代码里根本没有,
                // 于是 baseline 为 null 而 known 仍为 true —— 「代码默认里没这一项」是一句
                // 判得出来的话,不该退化成「没法比」。
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

            // 列表元素的**运行时类型**单发一条 `<path>.Class`(0.2.0 起)。
            //
            // 第七轮 T1:`<li Class="JobGiver_AnimalFlee">` 这一类多态子对象的类型此前一条也
            // 不进索引 —— 类型不是字段,Emit 走到这里就直接钻进去了。于是「哪些 ThinkTreeDef
            // 挂了这个节点」这类反查在工具内**无解**,而这是 def 侧最常见的反查之一:
            // 思维树节点、各种 worker/li 全在这个形状上。一份盲测轨迹为此半题答不出,
            // 另花 6 次调用只为确认「这是边界,不是我不会查」。
            //
            // 只发给列表元素(路径以 ] 收尾),不是每个复合对象都发:`<li Class=>` 只出现在
            // 列表里,而给每个嵌套对象都加一行会把 MaxFieldValuesPerDef 提前撞穿 ——
            // 换来的是更多 def 被截,那是让一层看着「没有」的方向。
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
            // 与「ThingDef 默认有没有 comps」是两个问题,混为一谈正是 R1 的形状。
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

            // Def 引用记 defName —— 这一条把「哪些 def 用了它」从文本匹配变成精确反查(00 论据 4)。
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
        /// defInjections 倾倒。反编译实证:导出时刻**译文已经在 def 对象上**,而**被替换的原文
        /// 留在注入记录的 replacedString 里** —— 两者同时在场,所以一次导出就能拿到双语。
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
        /// **与 defInjections 的形状差只有一处,但它决定了这一节的写法**:
        /// <c>KeyedReplacement</c> 没有 <c>replacedString</c>,所以「译文与被它替换的原文同时在场」
        /// 那个便宜在这里不存在。英文那一侧只能从 <c>defaultLanguage</c> 另取 —— 官方自己就这么干
        /// (<c>LanguageReportGenerator.SaveTranslationReport</c> 显式 <c>LoadData()</c> 两份语言,
        /// 然后 <c>AppendKeyedTranslationsMatchingEnglish</c> 拿 <c>defaultLanguage.TryGetTextFromKey</c>
        /// 对照),这一节照搬那条路。
        ///
        /// 与那一节还有两处**有意的**不同:
        ///
        /// 1. **英文环境下这一节不为空。** defInjections 在英文下自然没有记录(没有东西被替换),
        ///    而 keyed 的数据源**就是**英文语言文件 —— 于是英文环境下 translated 是英文串本身,
        ///    original 留空(它自己就是原文,没有「被替换的那一侧」)。
        /// 2. **占位译文照样导出**,只是带上 placeholder 标记。defInjections 那节把
        ///    <c>isPlaceholder</c> 整条跳过,而在这里「语言包里有这个 key 但没译」正是
        ///    汉化对账要问的东西;丢掉它,「没译」就与「没有这个 key」同形了。
        ///
        /// 覆盖冲突**只记赢家**(用户裁决):keyedReplacements 是全部 mod 合并后的最终值,
        /// 后加载的盖掉先加载的,而这个工具答的是「游戏最终用的是哪一句」。输家不建模,
        /// <c>fileSource</c> 说清赢家出自哪个文件的哪一行就够。
        /// </summary>
        private static IEnumerable<string> BuildKeyedLines()
        {
            var lang = LanguageDatabase.activeLanguage;
            if (lang == null || lang.keyedReplacements == null) yield break;

            // 英文侧。同一个对象时不必取(译文就是原文),否则显式加载 —— TryGetTextFromKey
            // 自己也会兜底 LoadData,但显式调用让「这里会多读一份语言数据」在代码里看得见。
            var english = LanguageDatabase.defaultLanguage;
            if (english != null && english != lang)
            {
                try { english.LoadData(); }
                catch (Exception ex)
                {
                    // 英文侧取不到不该让整份导出失败:少一列 original,而 translated 那一列
                    // 是这一层的主体。降级要说出口,否则「这份快照的 keyed 没有英文」
                    // 会被当成数据本来如此。
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
