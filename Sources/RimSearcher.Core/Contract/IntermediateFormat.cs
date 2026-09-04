// 中间格式契约 —— 产地唯一。
//
// 这个文件被两个程序集编译:游戏侧 RimSearcher.DataMod(net472,写)与 CLI 侧
// RimSearcher.Core(net10.0,读),故必须保持 net472 可编译:
// 不用 record / init / 可空引用注解语义 / System.Text.Json。
//
// 格式:gzip 压缩的 JSONL 单文件。
//   第 1 行            kind=meta    —— 快照身份(指纹)与上限参数
//   第 2..N-1 行       kind=def     —— 每 def 一行
//                      kind=definj  —— 运行时 defInjection 一条一行(游戏语言为英文时无此类行)
//                      kind=keyed   —— Keyed 译文一条一行(界面文案;与 def 无关,key 不带点)
//                      kind=xmlnode —— 继承层:XML 里一个带 Name/ParentName/Abstract 的节点一行
//                      kind=xmlwritten —— 一个 XML 节点实际写出来的字段路径(含普通 def)
//                      kind=typefields —— 一个 def 类型能有的字段路径全集
//                      kind=economy —— 经济面:一条可生产物的市场价/造价/工时/成本链
//   第 N 行(尾行)     kind=end     —— 记录数标记,完整性自证
//
// 尾行缺失 = 游戏中途崩溃或被杀,import 拒收。

namespace RimSearcher.Contract
{
    public static class IntermediateFormat
    {
        /// <summary>格式版本。中间格式契约变化时 +1;import 侧不认识就拒收。</summary>
        /// <remarks>
        /// 2:加了 kind=xmlnode 继承层。
        /// 3:fields 从二元组变三元组,第三位是 <see cref="DefaultState"/>。
        /// 4:加了 kind=keyed(界面文案译文)。
        /// 5:加了 kind=economy(经济面)与尾行的 economy_state 三态。
        ///
        /// 0.5.0 起多了 kind=xmlwritten / kind=typefields,以及 xmlnode 上的
        /// patch_ops_defname / patch_ops_label。0.6.0 起 xmlwritten 另带与 paths
        /// 同序的 texts。0.8.0 起多了 kind=injkey(注入键层)。都不涨这一档:缺的那一层由导出器版本上的能力位说话
        /// (与 IndexesNestedClass 同一套),旧文件仍能导入、旧库仍能打开;
        /// 涨了就会把磁盘上的旧导出整批拒收,而「看不见 ≠ 不存在」要的是宣布缺层,
        /// 不是把整份快照关掉。旧 CLI 读到不认识的 kind 会静默落空,但它本来就没有
        /// 对应列,不会把空说成「量过了、是零」。旧 CLI 读到不认识的键同样落空。
        ///
        /// 每一档都必须拒收前一档,而不是降级读:缺的那一层在查询结果里与
        /// 「事实上就没有」逐字同形,库里无从区分。
        ///
        /// 5 这一档尤其不能省:import 侧对**不认识的 kind 是静默落空**(没有 else 分支),
        /// 而 records 计数按行数走、尾行声明数也照样对得上 —— 不涨版本时,一个带经济行的
        /// 新文件被旧 CLI 读的结果是导入成功、退出码 0、经济面整个不存在,没有任何迹象。
        /// </remarks>
        public const int FormatVersion = 5;

        /// <summary>导出文件的推荐扩展名。</summary>
        public const string FileExtension = ".rsx.jsonl.gz";

        /// <summary>无人值守导出的命令行开关(GenCommandLine.TryGetCommandLineArg 读取)。</summary>
        public const string CommandLineSwitch = "rimsearcher-export";

        /// <summary>
        /// 跳过经济面抽取。**布尔开关走 <c>GenCommandLine.CommandLineArgPassed</c>**,
        /// 不走 <c>TryGetCommandLineArg</c> —— 后者只认 <c>key=value</c> 形式
        /// (它先判 <c>Contains('=')</c>),拿它做布尔开关会静默地永远为假。
        /// </summary>
        public const string SkipEconomySwitch = "rimsearcher-no-economy";

        /// <summary>
        /// 导出器自己的 packageId,必须与 About.xml 一致:游戏侧靠它被启用,
        /// CLI 侧靠它判断「这份 mod 列表能不能用来导出」。
        /// </summary>
        public const string ExporterPackageId = "rimsearcher.datamod";

        // 行类型标记
        public const string KindMeta = "meta";
        public const string KindDef = "def";
        public const string KindDefInjection = "definj";
        public const string KindKeyed = "keyed";
        public const string KindXmlNode = "xmlnode";
        public const string KindEconomy = "economy";
        /// <summary>一个 XML 节点实际写出来的字段路径(patch 前);0.6.0 起另带同序文本。</summary>
        public const string KindXmlWritten = "xmlwritten";
        /// <summary>一个 def 类型能有的字段路径全集,与值无关。</summary>
        public const string KindTypeFields = "typefields";
        /// <summary>一个 def 上「可以被注入译文」的槽位一行,与译文在不在无关。</summary>
        public const string KindInjKey = "injkey";
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
        // 与 definj 的形状差只有一处:**KeyedReplacement 不带 replacedString**,
        // 英文那一侧只能另取(见 DefExporter.BuildKeyedLines)。
        /// <summary>Keyed 的 key。不带点,也与任何 def 无关 —— 它是 <c>"X".Translate()</c> 里那个 X。</summary>
        public const string KeyKeyedKey = "key";
        /// <summary>译文出自哪个文件的哪一行(<c>KeyedReplacement.fileSourceLine</c>)。</summary>
        public const string KeySourceLine = "source_line";
        /// <summary>
        /// 占位译文(<c>isPlaceholder</c>)—— 语言包里有这个 key、但值是 TODO 占位。
        /// 必须随行带出:占位与真译文在表里同形,而它实际显示的是英文。
        /// </summary>
        public const string KeyPlaceholder = "placeholder";

        // ---- 注入键层(kind=injkey)。def_type / def_name / path 共用上面那批 ----
        //
        // 产地是 vanilla 的 `DefInjectionUtility.ForEachPossibleDefInjection`,它对每个槽位
        // **同时**给出两个键串:normalizedPath 用下标(`stages.0.label`),suggestedPath 用
        // 把手(`stages.observed_corpse.label`,把手为空时退回下标)。语言文件里两种都合法、
        // 都真的注入得上,于是同一个槽位在不同 mod 的译文里长成两个不同的键 —— 库里若只存
        // 译者写的那一串,`--path` 就得让调用方先猜对是哪一种。
        //
        // **只发带信息的行**,判据两条:
        //   1. 两个键串不同 —— 这一条是把手 ↔ 下标的对照表本身,少一条就是一个键归一不了。
        //   2. translation_allowed 为假**且这个槽位当下有文本** —— 「谁都没译」与「这个字段
        //      不许译」在译文表里同形(都是没有行),而出路一个是去译、一个是别白费劲。
        //      有文本这个附加条件不是省事:空槽位不会被当成「谁都没译」,那个问题根本不会
        //      发生;而不加它,每个 def 的内部缓存字段(Unsaved 那批)都要各占一行。
        // 两条都不满足的槽位(可译、键串同形)不发:它在字段表里已经在场,重复一遍就是几十万行。
        //
        // 限度:把手取自导出时刻的 label(`GetBestHandleWithIndexForListElement`),而译者
        // 手上那份语言文件可能是作者改 label **之前**写的。那种键在这张表里查不到,导入侧
        // 必须把它单独标出来,不许挑一个近似的当成对上了。

        /// <summary>把手式键串(去掉 defName. 前缀)。TKey 替换掉整串时不带前缀,原样落下。</summary>
        public const string KeySuggestedPath = "suggested_path";

        /// <summary>
        /// 这个槽位允许注入译文吗。假 = 字段带 <c>NoTranslate</c> 或 <c>Unsaved</c>,或它的
        /// 某一级祖先带 —— 该标记沿递归下传,所以假不一定长在这个字段自己身上。
        /// </summary>
        public const string KeyTranslationAllowed = "translation_allowed";

        /// <summary>
        /// 字符串集合字段允许**换条数**吗(<c>TranslationCanChangeCount</c>)。
        /// 与 <see cref="KeyTranslationAllowed"/> 分开带:后者管「能不能译」,这个管
        /// 「译文的条数必须与原文一样吗」,而条数不符是译文整条静默失效的常见成因。
        /// </summary>
        public const string KeyFullListTranslationAllowed = "full_list_translation_allowed";

        /// <summary>这个槽位是字符串集合而不是单个字符串。</summary>
        public const string KeyIsCollection = "is_collection";

        // 继承层(kind=xmlnode)。def_type / def_name / source_mod / source_file / name 共用上面那批。
        /// <summary>ParentName= 的值。空 = 这个节点不继承任何东西。</summary>
        public const string KeyParentName = "parent_name";
        /// <summary>Abstract="True" —— 抽象节点不进 def 数据库,只在这一层里存在。</summary>
        public const string KeyAbstract = "abstract";
        /// <summary>
        /// 有多少条 PatchOperation 的 xpath 点名了这个 Name=。这一层是**打补丁之前**的 XML,
        /// 这个数申报了偏差的规模。
        /// </summary>
        public const string KeyPatchOps = "patch_ops";
        /// <summary>有多少条 xpath 用 <c>defName="…"</c> 点了这个节点的 defName。</summary>
        public const string KeyPatchOpsDefName = "patch_ops_defname";
        /// <summary>有多少条 xpath 用 <c>label="…"</c> 点了这个节点的 label。</summary>
        public const string KeyPatchOpsLabel = "patch_ops_label";

        /// <summary>
        /// xml_written / type_fields 共用的路径数组。<c>["defName","projectile.speed",…]</c>。
        /// </summary>
        public const string KeyPaths = "paths";
        /// <summary>
        /// xml_written 的行内文本数组,与 <see cref="KeyPaths"/> 同序同长。
        /// 叶子是 InnerText;<c>path.Class</c> 那一行是 Class= 属性值。
        /// 导入侧必须校验等长 —— 错位后每一格的文本都指向别的路径,输出仍然像对的。
        /// </summary>
        public const string KeyTexts = "texts";
        /// <summary>
        /// xml_written 的「这一行是补丁加的」标记,与 <see cref="KeyPaths"/> 同序同长。
        /// 真 = 打完补丁的文档里有这一行,而磁盘上的原文没有。导入侧同样必须校验等长。
        /// </summary>
        public const string KeyPatched = "patched";
        /// <summary>
        /// 元数据行:这次的 xml_written 是怎么拿到打完补丁的文档的。
        /// <see cref="PatchRouteHarmony"/> / <see cref="PatchRouteReplay"/> / <see cref="PatchRouteNone"/>。
        /// 两条路线的结果若有差异,快照消费方得知道自己手上这份是哪一种。
        /// </summary>
        public const string KeyPatchRoute = "patch_route";
        /// <summary>抄了游戏真正用过的那份文档(Harmony Postfix)。</summary>
        public const string PatchRouteHarmony = "harmony";
        /// <summary>自己重放了一遍 LoadModXML / CombineIntoUnifiedXML / ApplyPatches。</summary>
        public const string PatchRouteReplay = "replay";
        /// <summary>两条都没走成 —— 路径全集仍是打补丁**之前**的原文。</summary>
        public const string PatchRouteNone = "none";
        /// <summary>xml_written 的节点键:有 defName 就用 defName,否则用 Name=。</summary>
        public const string KeyNodeKey = "node_key";
        /// <summary>node_key 取自 Name= 而非 defName 时为真。</summary>
        public const string KeyKeyIsName = "key_is_name";

        // ---- 经济面(kind=economy)。def_name / label / source_mod 不共用上面那批,见下 ----
        //
        // 外延与 vanilla `DebugOutputsEconomy.ItemAndBuildingAcquisition` 的 where 子句逐字一致:
        // 有市场价的物品(category==Item && BaseMarketValue > 0.01),加上玩家可建或可小型化的
        // 建筑。偏离这个筛子会让分布分位数无法与游戏内那张表对照。
        //
        // 数字一律定点两位(见 JsonLine.Num):JSON 数字侧禁科学计数法,且恒用 InvariantCulture。
        // **NULL 与 0 在这一层处处是两件事**,逐条见下面各字段的注释。

        /// <summary>
        /// 这一行属于哪个 mod。<c>modContentPack?.PackageId ?? "unknown"</c> ——
        /// 兜底值与 def 行的 <see cref="KeySourceMod"/>(空串)**有意不同**:
        /// Vethara 的 `/economy` 端点用的是 "unknown",而那个端点是这次迁移唯一的正确性闸
        /// (逐字段交叉校验),让这一格逐字可比比库内命名整齐值钱。
        /// 两种写法在真正的理由(Patch 新增的顶层 def 拿不到 modContentPack,丢掉该行会让
        /// 对照池静默缺项)上等价。
        /// </summary>
        public const string KeyEconomyMod = "mod";

        public const string KeyEconomyCategory = "category";
        public const string KeyEconomyMarketValue = "market_value";
        public const string KeyEconomyProducible = "producible";
        public const string KeyEconomyMadeFromStuff = "made_from_stuff";
        public const string KeyEconomyIsWeapon = "is_weapon";
        public const string KeyEconomyIsApparel = "is_apparel";
        public const string KeyEconomyMarketValueDefined = "market_value_defined";

        /// <summary>
        /// 四态,**不可合并**:<c>not_producible</c>(无配方也不可建)/ <c>used</c>(未声明
        /// MarketValue statBase)/ <c>recipe</c>(CalculableRecipe 非空)/ <c>ok</c>。
        /// 前两态的 <see cref="KeyEconomyCalculatedMarketValue"/> 为 null —— 把两种「空」混成
        /// 一个,消费侧就会把它们统计进分布、拉低整段分位。
        /// </summary>
        public const string KeyEconomyCalcState = "calc_state";

        public const string EconomyCalcNotProducible = "not_producible";
        public const string EconomyCalcUsed = "used";
        public const string EconomyCalcRecipe = "recipe";
        public const string EconomyCalcOk = "ok";

        /// <summary>仅 <c>recipe</c> / <c>ok</c> 两态有值。</summary>
        public const string KeyEconomyCalculatedMarketValue = "calculated_market_value";

        /// <summary>vanilla 的 CostToMake。<c>recipeMaker</c> 为空时 null(游戏那张表印 "-")。</summary>
        public const string KeyEconomyCostToMake = "cost_to_make";

        /// <summary>
        /// <c>market_value − cost_to_make</c>,同样只在 <c>recipeMaker</c> 非空时有值。
        /// **这是与游戏那张表的一处有意偏离**:vanilla 的 profit 列无条件相减,而 CostToMake
        /// 首句 <c>recipeMaker == null → return BaseMarketValue</c>,于是那一格印 0.0 ——
        /// 一个与真的零利润逐字同形的数。
        /// </summary>
        public const string KeyEconomyProfit = "profit";

        /// <summary>
        /// <c>(market_value − cost_to_make) / work × 10000</c>。**work &lt;= 0 时必须给 null**:
        /// vanilla 表在这里无条件除,工时为 −1 时那一格是负数噪声,原样传出去会被当成极端值
        /// 统计进分布。
        /// </summary>
        public const string KeyEconomyProfitRate = "profit_rate";

        /// <summary><c>WorkToProduceBest</c>,<c>&lt;= 0</c> 给 null。</summary>
        public const string KeyEconomyWorkToProduce = "work_to_produce";

        /// <summary><c>CostListString(def, false, false)</c> 的原字符串。</summary>
        public const string KeyEconomyCostList = "cost_list";

        /// <summary>
        /// 这个 def 的成本表有一支**由难度开关决定**的变体时,这里是那个开关名
        /// (<c>CostListForDifficulty.difficultyVar</c>);没有变体时为 null。
        ///
        /// 为什么必须随行带出:<c>BuildableDef.CostList</c> 在变体生效时返回的是另一张表,
        /// 而判据 <c>CostListForDifficulty.Applies</c> 的第一句是
        /// <c>if (Find.Storyteller == null) return false;</c> —— 导出跑在
        /// <c>StaticConstructorOnStartup</c>,那一刻没有 storyteller,**于是它永远取非变体那支**。
        ///
        /// 这不是一句可以省的免责声明:带 <see cref="KeyEconomyCostDifficultyInverted"/> 的 def
        /// (vanilla 的 <c>Turret_Mortar</c> 就是)变体恰恰在开关**关着**时生效,而那是绝大多数
        /// 存档的实际状态 —— 快照给出的那个数是玩家基本见不到的那一支,却与一个正常的数逐字同形。
        ///
        /// 2026-08-15 与 Vethara <c>/economy</c> 端点(跑在 Playing 状态)的逐字段交叉校验里,
        /// 2822 个共有 def 上只有这一处不一致,成因就是它。
        /// </summary>
        public const string KeyEconomyCostDifficultyVar = "cost_difficulty_var";

        /// <summary>
        /// <c>CostListForDifficulty.invert</c> —— 真表示变体在那个开关**关着**时生效。
        /// 与 <see cref="KeyEconomyCostDifficultyVar"/> 分开带:它决定导出取到的是常见的那一支
        /// 还是罕见的那一支,而这正是「小提醒」与「这个数没人会遇到」的分界。
        /// </summary>
        public const string KeyEconomyCostDifficultyInverted = "cost_difficulty_inverted";

        /// <summary>逐项 <c>{thing_def, count, unit_value, chain_end}</c>。</summary>
        public const string KeyEconomyCostChain = "cost_chain";
        public const string KeyEconomyThingDef = "thing_def";
        public const string KeyEconomyCount = "count";
        public const string KeyEconomyUnitValue = "unit_value";
        /// <summary>这一项自己没有 recipeMaker —— CostToMake 的递归在它身上停住。</summary>
        public const string KeyEconomyChainEnd = "chain_end";

        /// <summary>
        /// 链尾项价值占比。**total &lt;= 0 时给 null**,不给 0.00 —— 无 costList 与
        /// 「costList 全是零价物」两种来源都不该长得像「没问题」。
        /// 等于 1.0 时该行的 profit 只是「市场价减去你自己填的另外几个数」。
        /// </summary>
        public const string KeyEconomyChainEndShare = "chain_end_share";

        /// <summary>
        /// **自有指标,不是 vanilla 的量** —— 命名上必须与 <see cref="KeyEconomyCostToMake"/>
        /// 分开。与它的唯一差别是链尾改用 <c>StatWorker_MarketValue.CalculatedBaseMarketValue</c>,
        /// 那同样是 vanilla 自己的算法,且对手写 RecipeDef(而非 &lt;recipeMaker&gt; 节)产出的物
        /// 有效,正好补上 CostToMake 的盲区。两者并列输出,差异本身即信息。
        /// </summary>
        public const string KeyEconomyCostDeep = "cost_deep";

        public const string KeyEconomyProfitDeep = "profit_deep";

        /// <summary>
        /// 逐项 <c>{def_name, product_count, work_amount, self_referential}</c>。
        /// 长度 &gt; 1 即表示该行的 <see cref="KeyEconomyCalculatedMarketValue"/> 有**加载顺序
        /// 依赖**:<c>CalculableRecipe</c> 返回 DefDatabase 里第一个匹配,而等比放大的 bulk
        /// 配方 workAmount 通常不等比,单位成本更低。
        /// </summary>
        public const string KeyEconomyRecipeCandidates = "recipe_candidates";
        public const string KeyEconomyProductCount = "product_count";
        public const string KeyEconomyWorkAmount = "work_amount";
        /// <summary>某个 ingredient 的 filter 放行产物自己 —— 推算价里混进了手填价。</summary>
        public const string KeyEconomySelfReferential = "self_referential";

        // ---- 尾行 ----

        public const string KeyRecords = "records";
        public const string KeyDefs = "defs";
        public const string KeyInjections = "injections";
        /// <summary>kind=injkey 的行数。零 = 这次没发注入键层,不是「一个槽位都没有」。</summary>
        public const string KeyInjKeys = "inj_keys";
        public const string KeyKeyedCount = "keyed";
        public const string KeyXmlNodes = "xml_nodes";
        public const string KeyEconomyRows = "economy";

        /// <summary>
        /// 经济面这一次到底量没量成,三态。**判据不是 <c>COUNT(*)</c>** —— 经济抽取要调
        /// vanilla 的 private 方法,会部分失败,而 keyed 不会;零行有四种成因
        /// (没量过 / 主动跳过 / 签名缺失 / 量过了确实没有),计数把它们压成同一个数。
        ///
        /// 落在**尾行**而不是 meta 行:meta 是第一行,那时抽取还没跑;尾行本来就是「这次导出
        /// 实际产出了什么」的自证行,且尾行缺失一律拒收,所以这几个字段必定伴随一次完整导出。
        /// </summary>
        public const string KeyEconomyState = "economy_state";

        /// <summary>量过了。</summary>
        public const string EconomyStateOk = "ok";
        /// <summary>导出时带了 <see cref="SkipEconomySwitch"/>。</summary>
        public const string EconomyStateSkipped = "skipped";
        /// <summary>vanilla 签名对不上,或抽取中途抛了 —— 详情在 <see cref="KeyEconomyError"/>。</summary>
        public const string EconomyStateUnavailable = "unavailable";

        /// <summary>
        /// <see cref="EconomyStateUnavailable"/> 时点名缺了什么。**不得回退到自写的等价实现**:
        /// 一旦回退,调用方拿到的是与游戏内表格不一致的数字,而输出里没有任何迹象说明口径
        /// 已经换了一套。
        /// </summary>
        public const string KeyEconomyError = "economy_error";

        /// <summary>ImpliedDefs 批次在 source_file 上留的事实值 —— 是来源标记,不是文件路径。</summary>
        public const string ImpliedDefsSourceFile = "ImpliedDefs";

        // ---- 进度回报(编排侧判「卡住了」的唯一硬判据)----
        //
        // 无头跑时游戏若在加载定义**之前**弹一个对话框(缺前置、循环依赖、版本警告),
        // 它既看不见也点不掉,进程就活着不动 —— 从编排侧看与「正在慢慢加载」同形,
        // CPU 占用这类代理指标区分不了。所以改由游戏侧自己报到哪一步了。

        /// <summary>进度文件的后缀,贴着导出目标放 —— 编排侧本来就知道那个路径。</summary>
        public const string ProgressFileSuffix = ".progress";

        /// <summary>Mod 子类构造完成。此时程序集已加载,但**定义还没开始读**。</summary>
        public const string StageModClasses = "mod-classes";

        /// <summary>定义全部就位,导出开始。到这一步之后再慢就是真在写数据了。</summary>
        public const string StageExporting = "exporting";
    }

    /// <summary>
    /// 一条字段值与「这个类型刚 new 出来时它是什么」的关系 —— 不分这一档的话,XML 里作者
    /// 亲手写的值、C# 字段声明里的初始值、<c>ResolveReferences</c> 填的兜底值在快照里同形。
    ///
    /// 判据只有一条:**把这个对象的运行时类型新 new 一个,同一个字段读出来一样吗**。
    /// 这问的是 C# 声明默认值,不是「作者写没写」—— 后者要重放继承与补丁,而重放一份
    /// 必然与游戏分家。所以只声明证得出来的那一半:<see cref="Same"/> = 与代码默认值无从
    /// 区分,<see cref="Differs"/> = 一定不是代码默认值(XML / 补丁 / ResolveReferences 都在此栏)。
    ///
    /// 证不出来的进 <see cref="Unknown"/>,呈现侧据此**照常显示** —— 不许让一行值凭空消失。
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
    /// 「字段被截」与「没有该字段」永远可区分。
    /// </summary>
    public sealed class ExportLimits
    {
        /// <summary>字段递归深度上限。叶子不占深度,所以这个 6 比它读起来要深。</summary>
        public int MaxFieldDepth = 6;

        /// <summary>单 def 的 field_values 条数上限。</summary>
        public int MaxFieldValuesPerDef = 5000;

        /// <summary>单个字段值的字符数上限,超出截断并计入 fields_truncated。</summary>
        public int MaxValueLength = 400;

        /// <summary>列表/字典枚举的元素数上限。</summary>
        public int MaxCollectionItems = 200;
    }
}
