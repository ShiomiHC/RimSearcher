// 中间格式契约 —— 产地唯一。
//
// 这个文件被两个程序集编译:游戏侧 RimSearcher.DataMod(net472,写)与 CLI 侧
// RimSearcher.Core(net10.0,读)。B 案把「一次付清的设计期风险」压在这里,所以它必须
// 保持 net472 可编译:不用 record / init / 可空引用注解语义 / System.Text.Json。
//
// 格式:gzip 压缩的 JSONL 单文件。
//   第 1 行            kind=meta    —— 快照身份(指纹)与上限参数
//   第 2..N-1 行       kind=def     —— 每 def 一行
//                      kind=definj  —— 运行时 defInjection 一条一行(游戏语言为英文时无此类行)
//                      kind=keyed   —— Keyed 译文一条一行(界面文案;与 def 无关,key 不带点)
//                      kind=xmlnode —— 继承层:XML 里一个带 Name/ParentName/Abstract 的节点一行
//   第 N 行(尾行)     kind=end     —— 记录数标记,完整性自证
//
// 尾行缺失 = 游戏中途崩溃或被杀,import 拒收(02-6 原子性的游戏侧一半)。

namespace RimSearcher.Contract
{
    public static class IntermediateFormat
    {
        /// <summary>格式版本。中间格式契约变化时 +1;import 侧不认识就拒收。</summary>
        /// <remarks>
        /// 2:加了 kind=xmlnode 继承层。旧文件被拒收是对的 —— 拿它建出来的库
        /// 会在「谁继承谁」上一律零结果,而零结果与「确实没有父节点」长得一模一样。
        ///
        /// 3:fields 从二元组变三元组,第三位是 <see cref="DefaultState"/>。同理必须拒收 v2:
        /// 那些文件里每一行都「没说自己是不是代码默认值」,而 <c>get</c> 会把它们全归进
        /// 「不是默认值」那一栏 —— 与一个真的处处被作者改过的 def 逐字同形。
        ///
        /// 4:加了 kind=keyed(界面文案译文)。拒收 v3 是**用户明确裁决**的,而理由与上面两条
        /// 同形:v3 文件里 keyed 段整个不存在,于是 <c>keyed</c> 查询在它上面一律零结果 ——
        /// 与「这个 key 真的没有译文」逐字同形。备选方案(meta 记一个 has_keyed 标记、
        /// 查询时说破)被否决:那等于要求调用方永久记住一条例外,而记不住的那一次
        /// 恰好就是它下错结论的那一次。代价(重进游戏导一次,分钟级)一次付清。
        /// </remarks>
        public const int FormatVersion = 4;

        /// <summary>导出文件的推荐扩展名。</summary>
        public const string FileExtension = ".rsx.jsonl.gz";

        /// <summary>无人值守导出的命令行开关(GenCommandLine.TryGetCommandLineArg 读取)。</summary>
        public const string CommandLineSwitch = "rimsearcher-export";

        /// <summary>
        /// 导出器自己的 packageId。它必须与 About.xml 一致 —— 两侧都要认这个名字:
        /// 游戏侧靠它被启用才跑得起来,CLI 侧靠它判断「这份 mod 列表能不能用来导出」。
        /// </summary>
        public const string ExporterPackageId = "rimsearcher.datamod";

        // 行类型标记
        public const string KindMeta = "meta";
        public const string KindDef = "def";
        public const string KindDefInjection = "definj";
        public const string KindKeyed = "keyed";
        public const string KindXmlNode = "xmlnode";
        public const string KindEnd = "end";

        // 字段名(两侧共用,防手写漂移)
        public const string KeyKind = "kind";
        public const string KeyFormatVersion = "format_version";
        public const string KeyExporterVersion = "exporter_version";
        public const string KeyExportedAtUtc = "exported_at_utc";
        public const string KeyGameVersion = "game_version";
        public const string KeyLanguage = "language";
        public const string KeyMods = "mods";
        public const string KeyLimits = "limits";
        public const string KeyModSettingsHash = "mod_settings_hash";

        public const string KeyPackageId = "package_id";
        public const string KeyName = "name";
        public const string KeyVersion = "version";

        public const string KeyDefType = "def_type";
        public const string KeyDefName = "def_name";
        public const string KeyLabel = "label";
        public const string KeyDescription = "description";
        public const string KeySourceMod = "source_mod";
        public const string KeySourceFile = "source_file";
        public const string KeyGenerated = "generated";
        public const string KeyClass = "class";
        /// <summary>字段表:<c>[["path","value",默认态],…]</c>,默认态见 <see cref="DefaultState"/>。</summary>
        public const string KeyFields = "fields";
        public const string KeyFieldsTruncated = "fields_truncated";

        public const string KeyPath = "path";
        public const string KeyTranslated = "translated";
        public const string KeyOriginal = "original";

        // Keyed 层(kind=keyed)。translated / original / source_mod 与上面那批共用。
        //
        // 与 definj 的形状差只有一处,但它是本层全部麻烦的来源:**KeyedReplacement 不带
        // replacedString**。def 注入那种「译文在 def 对象上、原文在注入记录里,两者同时在场」
        // 的便宜在这里不存在,英文那一侧只能另取(见 DefExporter.BuildKeyedLines)。
        /// <summary>Keyed 的 key。不带点,也与任何 def 无关 —— 它是 <c>"X".Translate()</c> 里那个 X。</summary>
        public const string KeyKeyedKey = "key";
        /// <summary>译文出自哪个文件的哪一行(<c>KeyedReplacement.fileSourceLine</c>)。</summary>
        public const string KeySourceLine = "source_line";
        /// <summary>
        /// 占位译文(<c>isPlaceholder</c>)—— 语言包里有这个 key、但值是 TODO 占位。
        /// 必须随行带出:占位与真译文在表里同形,而它实际显示的是英文。
        /// </summary>
        public const string KeyPlaceholder = "placeholder";

        // 继承层(kind=xmlnode)。def_type / def_name / source_mod / source_file / name 共用上面那批。
        /// <summary>ParentName= 的值。空 = 这个节点不继承任何东西。</summary>
        public const string KeyParentName = "parent_name";
        /// <summary>Abstract="True" —— 抽象节点不进 def 数据库,只在这一层里存在。</summary>
        public const string KeyAbstract = "abstract";
        /// <summary>
        /// 有多少条 PatchOperation 的 xpath 点名了这个 Name=。这一层是**打补丁之前**的 XML,
        /// 而这个数把「偏差」从一句含糊的免责声明变成逐条、有数的申报。
        /// </summary>
        public const string KeyPatchOps = "patch_ops";

        public const string KeyRecords = "records";
        public const string KeyDefs = "defs";
        public const string KeyInjections = "injections";
        public const string KeyKeyedCount = "keyed";
        public const string KeyXmlNodes = "xml_nodes";

        /// <summary>ImpliedDefs 批次在 source_file 上留的事实值(03 甲:来源标记是字符串而非文件)。</summary>
        public const string ImpliedDefsSourceFile = "ImpliedDefs";

        // ---- 进度回报(编排侧判「卡住了」的唯一硬判据)----
        //
        // 起因:无头跑时游戏若在加载定义**之前**弹一个对话框(缺前置、循环依赖、版本警告),
        // 它既看不见也点不掉,进程就活着不动。从编排侧看,这与「正在慢慢加载」长得一模一样。
        // 拿 CPU 占用去猜是**代理指标**,而代理会撒谎 —— 一个真的很慢的 I/O 段会被判成卡死。
        //
        // 所以改由游戏侧自己报到哪一步了。判据从「猜它在不在干活」变成
        // 「它说自己到了哪一步,以及这一步停了多久」。

        /// <summary>进度文件的后缀,贴着导出目标放 —— 编排侧本来就知道那个路径。</summary>
        public const string ProgressFileSuffix = ".progress";

        /// <summary>Mod 子类构造完成。此时程序集已加载,但**定义还没开始读**。</summary>
        public const string StageModClasses = "mod-classes";

        /// <summary>定义全部就位,导出开始。到这一步之后再慢就是真在写数据了。</summary>
        public const string StageExporting = "exporting";
    }

    /// <summary>
    /// 一条字段值与「这个类型刚 new 出来时它是什么」的关系。
    ///
    /// 起因是 R1:XML 里作者亲手写的值、C# 字段声明里的初始值、以及 <c>ResolveReferences</c>
    /// 填的兜底值,在快照里长成一模一样的行。四个错结论全由这张表直接生成,而且每次错的
    /// 那一行都恰好是「字段名与提问一字不差」的那一行。
    ///
    /// 判据只有一条、也只能有一条:**把这个对象的运行时类型新 new 一个,同一个字段读出来
    /// 一样吗**。这问的是 C# 声明默认值,不是「作者写没写」—— 后者要重放继承与补丁才知道,
    /// 而重放一份必然与游戏分家。所以这里只声明证得出来的那一半:
    /// <see cref="Same"/> = 「与代码默认值无从区分」,<see cref="Differs"/> = 「一定不是代码默认值」
    /// (XML 写的、补丁改的、ResolveReferences 填的,都落这一栏)。
    ///
    /// 证不出来的那些进 <see cref="Unknown"/> 而不是并进任何一边 —— 呈现侧据此把它们
    /// **照常显示**,于是「新不出来这个类型」最坏只是少省一点篇幅,不会让一行值凭空消失。
    /// </summary>
    public static class DefaultState
    {
        /// <summary>与新 new 的实例不同 —— 一定有人改过(XML / 补丁 / ResolveReferences)。</summary>
        public const int Differs = 0;

        /// <summary>与新 new 的实例相同 —— 与 C# 声明默认值无从区分。</summary>
        public const int Same = 1;

        /// <summary>没法比 —— 这个类型 new 不出来。照常显示,不许并进上面任何一栏。</summary>
        public const int Unknown = 2;
    }

    /// <summary>导出侧攒好的一条字段值。net472 可编译,故不是 record。</summary>
    public struct ExportedField
    {
        public string Path;
        public string Value;
        public int Default;

        public ExportedField(string path, string value, int defaultState)
        {
            Path = path;
            Value = value;
            Default = defaultState;
        }
    }

    /// <summary>
    /// 导出上限。数值是可调参数,但**每 def 被截条数随行带出**(fields_truncated),
    /// 「字段被截」与「没有该字段」永远可区分(02-3)。
    /// </summary>
    public sealed class ExportLimits
    {
        /// <summary>字段递归深度上限。叶子不占深度(03 乙的换算口径),故 6 比旧世系 3 深得多。</summary>
        public int MaxFieldDepth = 6;

        /// <summary>单 def 的 field_values 条数上限。</summary>
        public int MaxFieldValuesPerDef = 5000;

        /// <summary>单个字段值的字符数上限,超出截断并计入 fields_truncated。</summary>
        public int MaxValueLength = 400;

        /// <summary>列表/字典枚举的元素数上限。</summary>
        public int MaxCollectionItems = 200;
    }
}
