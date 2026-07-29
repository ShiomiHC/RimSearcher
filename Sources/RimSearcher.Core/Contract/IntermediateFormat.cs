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
//                      kind=xmlnode —— 继承层:XML 里一个带 Name/ParentName/Abstract 的节点一行
//   第 N 行(尾行)     kind=end     —— 记录数标记,完整性自证
//
// 尾行缺失 = 游戏中途崩溃或被杀,import 拒收(02-6 原子性的游戏侧一半)。

namespace RimSearcher.Contract
{
    public static class IntermediateFormat
    {
        /// <summary>格式版本。中间格式契约变化时 +1;import 侧不认识就拒收。</summary>
        /// <remarks>2:加了 kind=xmlnode 继承层。旧文件被拒收是对的 —— 拿它建出来的库
        /// 会在「谁继承谁」上一律零结果,而零结果与「确实没有父节点」长得一模一样。</remarks>
        public const int FormatVersion = 2;

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
        public const string KeyFields = "fields";
        public const string KeyFieldsTruncated = "fields_truncated";

        public const string KeyPath = "path";
        public const string KeyTranslated = "translated";
        public const string KeyOriginal = "original";

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
