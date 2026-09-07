using Microsoft.Data.Sqlite;

namespace RimSearcher.Storage;

/// <summary>
/// 快照库 schema。**不兼容上游 db** —— 自立 schema_version,读到无 meta 或版本不符的库
/// 就拒读并指导重导(错误消息不含本机路径,留发布缝)。
/// </summary>
public static class SnapshotSchema
{
    /// <summary>schema 版本。表结构变化时 +1。</summary>
    /// <remarks>
    /// 3:加了 xml_nodes 继承层。
    /// 4:field_values 加了 is_default —— 一条值与 C# 声明默认值的关系。
    /// 5:加了 keyed 表 —— 界面文案那一层译文。
    /// 6:加了 shared_values —— 一条值在同类型里有多普遍。
    /// 7:加了 harvested_roots —— 磁盘那一层**量没量过**,见下。
    /// 8:加了 economy 三表 + economy_state —— 经济面,以及它**量没量成**,见下。
    ///
    /// 0.5.0 起 xml_nodes 多了 patch_ops_defname / patch_ops_label,并加 xml_written
    /// 与 type_fields 两张表。0.6.0 起 xml_written 多了 inner_text,0.7.0 多了 patched。
    /// 0.8.0 起加 injection_keys 表,translations 多了 source_file / source_file_count。
    /// 都不涨这一档:
    /// 精确相等的 schema 检查会让磁盘上的旧库整份打不开,而缺的那一层由导出器版本上
    /// 的能力位说话(同 content_fingerprint 那条缝)。新导入的库有这些列/表;旧库没有,
    /// 查询侧能力位为假时不去碰它们。
    ///
    /// type_fields 后来把 path 抽成 type_field_paths 字典(1373 万行里只有 52 万条不同
    /// 路径,平均 116 字符),再后来整张表拆成 type_subtrees + subtree_paths(那 1373 万行
    /// 是一次 JOIN 的展开结果,两个因子合起来只有 3.85%)。这两条与上面几条不同 ——
    /// 它们改的是**既有表的形状**,于是声明层在磁盘上有三种样子。
    ///
    /// 同样不涨版本,同样的理由,而且拆表这一次理由更硬:磁盘上现有十九份库,其中十二份是
    /// --keep 留下的旧代,它们**永远不会**被重导,而重导正是拒读消息唯一能指的出路。
    /// 区分不靠导出器版本(三种形状能出自同一个导出器),靠 SnapshotDb 探表/列在不在 ——
    /// 那比版本号更精确:版本号说的是「这份库建于哪一档」,探到的是「它现在长什么样」。
    /// </remarks>
    public const int Version = 8;

    public const string MetaKeySchemaVersion = "schema_version";
    public const string MetaKeyRaw = "export_meta_json";
    public const string MetaKeyFingerprint = "fingerprint";
    public const string MetaKeyImportedAtUtc = "imported_at_utc";
    public const string MetaKeyDefCount = "def_count";
    public const string MetaKeySourcePath = "source_file";

    /// <summary>
    /// 这次导入扫了几个 mod 根目录去收割磁盘上的语言文件。<c>0</c> 就是**一个都没扫**。
    ///
    /// 「磁盘那一层一行都没有」有两个成因:根本没量过(下一步重导),和量过了确实没有。
    /// 收割虽是默认行为,但可被 <c>--no-harvest-translations</c> 关掉,也可能因没配
    /// <c>mod_roots</c> 而没得扫,所以要记下来。
    /// </summary>
    public const string MetaKeyHarvestedRoots = "harvested_roots";

    /// <summary>
    /// 导出那一刻各 mod 的 Defs/Patches 指纹(<see cref="Snapshot.ContentScan"/> 的 JSON)。
    ///
    /// **缺席是有意义的一态**,不是坏库:这个键是后加的,先前建的库里没有它,
    /// 而那些库对「mod 的 XML 后来改没改」没有资格回答。缺席时这条判据整个不说话 ——
    /// 没量过与量过了没变必须分得开(同 <see cref="MetaKeyHarvestedRoots"/> 那条缝)。
    /// 所以它没有涨 schema_version:旧库照旧能读,只是少一条判据。
    /// </summary>
    public const string MetaKeyContent = "content_fingerprint";

    /// <summary>
    /// 经济面这一次到底量没量成:<c>ok</c> / <c>skipped</c> / <c>unavailable</c>,
    /// 原样取自导出尾行(<see cref="Contract.IntermediateFormat.KeyEconomyState"/>)。
    ///
    /// **缺席是第四态**,和上面两条同一道缝:这个键不在 = 这份库建于经济面进导出之前,
    /// 它对「游戏说这东西值多少钱」没有资格回答。所以查询侧的判据是这个键,
    /// **不是 <c>SELECT COUNT(*) FROM economy</c>** —— 计数把四种成因压成同一个零,
    /// 而其中一种(量过了、这个名单下确实没有可生产物)是完整的肯定回答。
    /// </summary>
    public const string MetaKeyEconomyState = "economy_state";

    /// <summary>
    /// 导出时每一层各花了多少毫秒,原样存尾行给的那个 JSON 对象(导出器 0.11.0 起)。
    ///
    /// **缺席是一态**:这份快照建于计时之前,不是「零毫秒」。存进库里而不只是印一次,
    /// 是因为「上一代慢在哪」要能事后翻得出来 —— 重导之后那次运行的输出就没了。
    /// </summary>
    public const string MetaKeyExportTimings = "export_timings_ms";

    /// <summary>
    /// 导入时每一段各花了多少毫秒。与 <see cref="MetaKeyExportTimings"/> 同一个用途,
    /// 分开存是因为**两侧不是一回事** —— 实测游戏那侧两分钟出文件,这侧十几分钟建库,
    /// 只印导出那张表会把「慢」归错地方。缺席同样是「那一版没量」,不是零毫秒。
    /// </summary>
    public const string MetaKeyImportTimings = "import_timings_ms";

    /// <summary>
    /// <c>unavailable</c> 时点名缺了什么(vanilla 的哪个签名)。原样端给用户 ——
    /// 这一层不回退到自写实现,所以这句话是唯一的下一步。
    /// </summary>
    public const string MetaKeyEconomyError = "economy_error";

    public const string Ddl = """
        PRAGMA journal_mode = OFF;
        PRAGMA synchronous  = OFF;

        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE defs (
            id               INTEGER PRIMARY KEY,
            def_type         TEXT NOT NULL,
            def_name         TEXT NOT NULL,
            label            TEXT,
            description      TEXT,
            source_mod       TEXT,
            source_file      TEXT,
            generated        INTEGER NOT NULL DEFAULT 0,
            class            TEXT,
            fields_truncated INTEGER NOT NULL DEFAULT 0,

            -- fields_truncated 的四个成分(0.13.0 起)。总数答得出「被截了」,答不出
            -- 「为什么被截」,而四种的出路完全不同 —— 深度要放开、条数上限要抬、
            -- 值长度是展示取舍、集合是宽度问题。一次实测里那个总数的大头被连猜错两次
            -- (依据是「最大列表下标正好 199」,那是相关性;真正撞的是单 def 条数上限)。
            --
            -- **不涨 schema_version**,理由与 type_fields 拆表那次逐字相同:精确相等的
            -- 检查会让磁盘上每一份旧库拒读,连同 --keep 留下的旧代。旧库上这四列整个不在,
            -- 呈现侧靠 SnapshotDb.DefsHaveTruncationBreakdown 探列名,不当成四个零 ——
            -- 「没分类过」与「四类都是零」在读者那儿是两句不同的话。
            truncated_by_cap    INTEGER NOT NULL DEFAULT 0,
            truncated_by_length INTEGER NOT NULL DEFAULT 0,
            truncated_by_depth  INTEGER NOT NULL DEFAULT 0,
            truncated_by_items  INTEGER NOT NULL DEFAULT 0
        );

        -- is_default:这一行与「这个类型刚 new 出来时」的关系,取值见 IntermediateFormat.DefaultState
        -- (0 一定被改过 / 1 与代码默认值无从区分 / 2 没法比)。存原样而不是存 bool ——
        -- 「没法比」并进任何一边都会让呈现侧说出一句它证不了的话(R1)。
        -- 路径提进字典表,同 type_field_paths 那一手。baseline(1.21G)实测:150 万行里
        -- 只有 2.6 万条不同 path,行数 57:1、字节 39:1(32.7M -> 838K)。省下的空间是次要的,
        -- 主要收益在**谓词跑在哪张表上**:后缀匹配写成 `path LIKE '%x'`,前导通配让任何
        -- B-tree 都用不上,于是它此前是一次 150 万行全表扫(热 0.42s);跑在 2.6 万行的
        -- 字典上再按 path_id 回表,同一条谓词 0.008s。
        --
        -- 表名沿用,列换成 path_id。**旧库的这张表是带 path 的**,靠列名分辨
        -- (SnapshotDb.FieldValuesAreDictionary)。不涨 schema_version,理由与 type_fields
        -- 那处逐字相同:精确相等的检查会让磁盘上每一份旧库拒读,连同 --keep 留下的旧代。
        -- leaf 住在这儿而不是 field_values 上:它是 NoiseFilter.Leaf(path) 的返回值,
        -- **path 的纯函数**(单一算处,就在导入那一行),于是每条 path 底下它是常量。
        -- 逐行存等于把 2.6 万个答案抄 150 万遍 —— 22.4M 字符,而不同取值只有 5392 个。
        --
        -- 收益的大头不是那 22.4M,是**两条索引整个消失**:谓词一律是
        -- `leaf = @x COLLATE NOCASE`,原来要在 150 万行上配一条 NOCASE 索引,
        -- 而 BINARY 那条(DISTINCT/GROUP BY 用)得一并留着,两条共 71.0M。
        -- 现在这一问落在 2.6 万行的字典上,回表顺已有的 idx_fv_pathid 走。
        CREATE TABLE field_value_paths (
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            leaf TEXT NOT NULL
        );

        -- 值也进字典。七份快照上冗余稳定在 23~28 倍(最重的 races:311 万行 11.1 万个
        -- 不同值),字符 10.5M → 1.5M。同 leaf,大头在索引:按值反查一律带 COLLATE NOCASE,
        -- 而 DISTINCT/GROUP BY 是 BINARY,于是这一列此前也得配一对索引(共 49.4M)。
        -- 换成号之后主表只剩一条整数索引,大小写那一问落在 6.5 万行的字典上。
        --
        -- **可空**:value_id 为 NULL 就是原来的 value 为 NULL。不给 NULL 发号 ——
        -- 发了的话「这个字段是空的」与「这个字段的值是空串」就在库里同形了。
        CREATE TABLE field_value_values (
            id    INTEGER PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE field_values (
            def_id     INTEGER NOT NULL,
            path_id    INTEGER NOT NULL,
            value_id   INTEGER,
            is_default INTEGER NOT NULL DEFAULT 0
        );

        -- source_file / source_file_count 只对收割行有值(运行时那一层的数据源是游戏内存,
        -- 不是某个文件)。**计数不是冗余**:一个 mod 常同时铺 1.4/ 1.5/ 1.6/ 三套 Languages,
        -- 同一句话逐列全同地入库三次;折成一行之后,「这句话在这个 mod 里出现过几次」
        -- 就只剩这一列说得出。缺了它,折叠会把「三份同文」印成「一份」而不留痕。
        --
        -- path 归一到**字段表那一侧的文法**(stages[0].label),key 留数据源给的那一串
        -- (stages.0.label 或 stages.observed_corpse.label,要写语言文件的人需要它)。
        -- 不归一的话 `--path-contains stages[0]` 对译文那栏恒回零,与「这个 def 没这条译文」同形。
        -- key_state 说的是另一件事:这个键在不在**槽位名册**上(injection_keys)。
        -- 三态见 Snapshot.InjectionKey.State。**它不记「键是哪种拼法」** ——
        -- 拼法读者看 key 那一格就是,而两件事挤在一列里正是 0.8.0 那版的错法。
        --
        -- **两列同时为 NULL = 这次导入没查名册**(导出器早于 0.9.0),
        -- 那种库里 path 仍是数据源原样。不许把 NULL 当成 resolved —— 那等于宣布查过了。
        --
        -- 上面四列都是后加的,**没有涨 schema_version**:旧库照旧能读,只是少几条判据
        -- (同 type_fields 的 path_id,靠列名认)。
        CREATE TABLE translations (
            def_id     INTEGER,
            def_type   TEXT,
            def_name   TEXT NOT NULL,
            path       TEXT NOT NULL,
            key        TEXT,
            key_state  TEXT,
            -- 游戏自己的判决:这条 defInjection 真注进去了吗。NULL = 没这一位
            -- (导出器早于 0.10.0,或这一行是从磁盘语言文件收割来的 —— 那些行
            -- 游戏根本没读过)。与 key_state 两条独立的路:这一列是实测,那一列是推算。
            applied    INTEGER,
            translated TEXT,
            original   TEXT,
            language   TEXT,
            source_mod TEXT,
            source_file TEXT,
            source_file_count INTEGER,
            origin     TEXT NOT NULL
        );

        -- 注入键层。一个「可以被注入译文」的槽位一行,与译文在不在无关。
        -- path 是下标式(stages.0.label),suggested_path 是把手式(stages.observed_corpse.label)。
        -- 语言文件里两种都合法、都真注入得上,所以译者写的那一串得先归一到一种,
        -- 否则 --path 就要求调用方先猜对是哪一种。
        --
        -- **这是槽位名册:一个可注入槽位一行,一个不漏**(导出器 0.9.0 起)。全在册才使得
        -- 「这个键在不在册」问得出口,而那一问是译文表 key_state 的唯一依据。0.8.0 那版
        -- 只发「带信息」的行(两键不同的、或不许译且有文本的),于是问不出口,判据退化成
        -- 拿字段表比对 —— 整表注入的键不带元素下标(rulePack.rulesStrings),字段表里那条
        -- 路径带(…rulesStrings[0]),逐字比一次不中。实测 1348 条判「配不上槽位」里 956 条
        -- 是这么冤枉的。所以 0.8.0 的库在能力位上算「没量过」,见 ExportMeta。
        --
        -- 空表与「这一档没导」在 SQL 上同形,不许当成「量过了、没有」;查询侧靠能力位
        -- (IndexesInjectionKeys)决定读不读。
        -- 四列字符串全进字典:49.7 万行里 def_type 只有 222 个不同值(1595 倍冗余)、
        -- def_name 1.4 万(31 倍)、两条路径各 4 万上下(各 4 倍)。表连索引 103.0M → 31.3M。
        --
        -- **def_type / def_name 没有删掉**,尽管七个库连旧代共 440 万行逐行与 defs 经
        -- def_id 接出来的一致、def_id 一个空都没有。因为 SnapshotImporter.Owner 判不出
        -- 归属时**有意写 null**(同名跨 def 类型是常态,它不肯挑一个),那时这两列是该行
        -- 仅剩的身份。那个 0 是这几份快照的性质,不是 schema 的性质。
        --
        -- 顺带修掉一处排序规则失配:读侧是 `def_name = @n COLLATE NOCASE`,而
        -- idx_ik_defname 是 BINARY 的,49.7 万行上一次都用不上(导入期那次自查是 BINARY,
        -- 索引一直在为它服务)。现在大小写不敏感那一问落在 1.4 万行的名字字典上,主表按整数号找。
        CREATE TABLE injection_key_types (
            id       INTEGER PRIMARY KEY,
            def_type TEXT NOT NULL
        );

        CREATE TABLE injection_key_names (
            id       INTEGER PRIMARY KEY,
            def_name TEXT NOT NULL
        );

        -- path 与 suggested_path 共用一份:两者是同一个槽位的两种拼法,取值域高度重叠
        -- (4.4 万 + 5.5 万条,并起来 5.6 万)。
        CREATE TABLE injection_key_paths (
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL
        );

        CREATE TABLE injection_keys (
            def_id            INTEGER,
            def_type_id       INTEGER,
            def_name_id       INTEGER NOT NULL,
            path_id           INTEGER NOT NULL,
            suggested_path_id INTEGER NOT NULL,
            is_collection  INTEGER NOT NULL DEFAULT 0,
            translation_allowed INTEGER NOT NULL DEFAULT 1,
            full_list_translation_allowed INTEGER NOT NULL DEFAULT 0
        );

        -- Keyed 译文 —— 界面文案。**这张表里一行都不属于任何 def**:key 是
        -- `"SomeKey".Translate()` 里那个 SomeKey,不带点、没有类型维、与 defName 无关。
        -- 所以它不能挤进 translations(那张表的主键形状是 def_name + path),也没有 def_id。
        --
        -- placeholder:语言包里有这个 key 但值是占位 —— 它实际显示的是英文,而在表里
        -- 与真译文同形。不带出来的话,「没译」就与「没有这个 key」分不开了。
        -- 覆盖冲突只存赢家(用户裁决):source_file/source_line 说清最终生效的那一句出自哪里。
        CREATE TABLE keyed (
            id          INTEGER PRIMARY KEY,
            key         TEXT NOT NULL,
            translated  TEXT,
            original    TEXT,
            language    TEXT,
            source_file TEXT,
            -- 同 translations 的那一列:一个 mod 常同时铺 1.4/ 1.5/ 1.6/ 三套 Languages,
            -- 逐列全同的几行折成一行,折掉的份数只剩这一列说得出。运行时那一层为 NULL。
            source_file_count INTEGER,
            source_line INTEGER NOT NULL DEFAULT 0,
            source_mod  TEXT,
            placeholder INTEGER NOT NULL DEFAULT 0,
            origin      TEXT NOT NULL
        );

        -- 继承层。**唯一一张不是「游戏内存里的对象」的表** —— 它是打补丁之前的 XML 原文,
        -- 因为「谁继承谁」在导出时点已经被 XmlInheritance.Clear() 抹掉了。
        -- patch_ops 让这份时间差逐条可见,而不是靠一句总的免责声明糊过去。
        CREATE TABLE xml_nodes (
            id          INTEGER PRIMARY KEY,
            def_type    TEXT NOT NULL,
            name        TEXT,
            parent_name TEXT,
            abstract    INTEGER NOT NULL DEFAULT 0,
            def_name    TEXT,
            label       TEXT,
            source_mod  TEXT,
            source_file TEXT,
            patch_ops   INTEGER NOT NULL DEFAULT 0,
            -- 0.5.0 起才有值。旧库没有这两列,查询侧靠能力位决定读不读,不许把缺列当 0。
            patch_ops_defname INTEGER NOT NULL DEFAULT 0,
            patch_ops_label   INTEGER NOT NULL DEFAULT 0
        );

        -- 每个 XML 节点(含不参与继承的普通 def)实际写出来的字段路径。
        -- patch 之前的原文,用来回答 Replace 还是 Add。
        -- inner_text:0.6.0 起才有值。叶子的行内文本;Class= 那一行是属性值。
        -- 旧库没有这一列,查询侧靠能力位决定读不读,不许把缺列当「没写过」。
        -- patched:0.7.0 起才有值。真 = 打完补丁的文档里有这一行、磁盘上的原文没有。
        -- 那一档路径只在 0.7.0 起才出现在这张表里 —— 老库里它们干脆不在,而「不在」
        -- 在 xml 列上印的是 no(该 Add),与这里的「补丁加的」(该 Replace)正相反。
        CREATE TABLE xml_written (
            def_type    TEXT NOT NULL,
            node_key    TEXT NOT NULL,
            key_is_name INTEGER NOT NULL DEFAULT 0,
            path        TEXT NOT NULL,
            inner_text  TEXT,
            patched     INTEGER
        );

        -- 一个 def 类型能有的字段路径全集,与值无关。用来把「全是 null」和「没有这个字段」分开。
        --
        -- 路径提进字典表:纯官方 1373 万行里只有 52 万个不同 path,平均 116 字符 ——
        -- 逐行存字符串是 26 倍冗余,实测占整库一半以上(表 1.9G / 库 4.4G)。
        -- 冗余这么高是因为所有 Def 子类共享基类那棵字段树,而深度 6 那一层占了 78%。
        --
        -- 不涨 schema_version:精确相等的检查会让磁盘上每一份旧库拒读,连同 --keep 留下的
        -- 那些旧代 —— 而它们唯一的用途正是拿来 diff。形状靠表/列在不在分辨,见下。
        CREATE TABLE type_field_paths (
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL
        );

        -- (类型, 路径) 那张展开表**已经拆掉了**。它是一次 JOIN 的结果,而两个因子小得多:
        --
        --   哪个类型带哪几棵子树   type_subtrees   4959 行    56K
        --   一棵子树摊开是哪些路径  subtree_paths   52.4 万行   6.2M
        --
        -- 纯官方 baseline 上,这两张表加起来是原表 1374 万行的 3.85%,连 type_names 一共
        -- 6.3M —— 旧形状是表 352.9M + 覆盖索引 359.4M = 712.3M,113 倍。整库 1144.9M → 438.8M。
        -- 冗余出在:所有 Def 子类共享基类那棵字段树,于是同一棵子树被 222 个类型各抄一遍。
        -- 「子树」按路径**首段**切(`comps[0].props.x` 归到 `comps`)—— 2884 棵子树对
        -- 2865 个不同首段,比值 1.007:一个字段名底下摊开成什么样几乎完全由字段名决定,
        -- 因为决定它的是那个字段的 C# 类型,与哪个 Def 子类持有它无关。
        --
        -- 这是**无损的集合去重**,不是内容取舍:切分是划分(每条路径恰属一个首段),
        -- 折叠是去重,复原是并集。改造前后的真库上比过两边的**全部** 1373.9 万对:
        -- 行数相等、集合哈希逐对相同、222 个类型一个不多不少。
        --
        -- 查询侧不因此变快也不变慢(实测三条 --path-contains,1.1~1.3s,差异在噪声里)——
        -- 那一层本来就不是热点。省的是磁盘,而磁盘上这一层曾占整库 62%。
        --
        -- 磁盘上于是有三种形状,一律靠表/列在不在分辨(SnapshotDb.TypeFieldsAreSubtrees):
        --   现行  type_subtrees + subtree_paths
        --   旧    type_fields(def_type, path_id)
        --   更旧  type_fields(def_type, path)
        CREATE TABLE type_names (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL
        );

        -- 两张都是 WITHOUT ROWID:表自己就是那条索引。带 rowid 的话还要再建一条覆盖索引
        -- 把两列连 rowid 抄一遍 —— 旧形状上那条索引(359.4M)比表本身(352.9M)还大。
        CREATE TABLE type_subtrees (
            type_id    INTEGER NOT NULL,
            subtree_id INTEGER NOT NULL,
            PRIMARY KEY (type_id, subtree_id)
        ) WITHOUT ROWID;

        CREATE TABLE subtree_paths (
            subtree_id INTEGER NOT NULL,
            path_id    INTEGER NOT NULL,
            PRIMARY KEY (subtree_id, path_id)
        ) WITHOUT ROWID;

        -- 一条「与新实例不同」的值,在同类型的 def 里有多普遍。
        --
        -- code_default 只证得了「与刚 new 出来的实例不同」,而 ResolveReferences 会给
        -- 同类型的每个 def 都塞上同一个值(如 ThingDef.soundImpactDefault)—— 那种行读起来
        -- 与「有人专门给这个 def 挑了这个值」一模一样。
        --
        -- 分不清「XML 写的」与「引擎事后填的」:那要在 ResolveReferences 前后各取一次值,
        -- 而导出跑在 StaticConstructorOnStartup、resolve 早已做完,插进去只能上 Harmony,
        -- 而 DataMod 刻意无依赖。所以不猜成因,只报可核对的事实:同类型里有多少个 def
        -- 也是这个值。
        --
        -- 只收「过半且不少于 8 个」的组 —— 类型只有三五个 def 时「大多数」不成话。
        CREATE TABLE shared_values (
            def_type TEXT NOT NULL,
            path     TEXT NOT NULL,
            value    TEXT,
            defs     INTEGER NOT NULL
        );

        -- 经济面。外延与 vanilla DebugOutputsEconomy.ItemAndBuildingAcquisition 的 where
        -- 子句逐字一致(有市场价的物品 + 玩家可建或可小型化的建筑),偏离它会让分布分位数
        -- 无法与游戏内那张表对照。
        --
        -- 不与 defs 建外键:这一层的消费方(分布统计、对照池切分)全程只用 def_name 与 mod,
        -- 而 translations 那套「按名字找回 def id」在这里是纯开销。
        --
        -- **列一律可空、不设 DEFAULT 0** —— NULL 与 0 在这张表里处处是两件事:
        -- 工时为 0 的利润率、没有成本链的链尾占比、vanilla 自己都算不出的推算价,
        -- 给一个 0 就等于给了一个会被统计进分布的数。
        --
        -- REAL 而不是 TEXT:定点两位是**中间格式**那一侧的规则(防科学计数法与 locale
        -- 小数点),进了库还存字符串只会让排序与分位数查询变成字符串比较。
        CREATE TABLE economy (
            id                      INTEGER PRIMARY KEY,
            def_name                TEXT NOT NULL,
            label                   TEXT,
            category                TEXT,
            mod                     TEXT,
            market_value            REAL,
            producible              INTEGER NOT NULL,
            made_from_stuff         INTEGER NOT NULL,
            is_weapon               INTEGER NOT NULL,
            is_apparel              INTEGER NOT NULL,
            market_value_defined    INTEGER NOT NULL,
            -- 四态:not_producible / used / recipe / ok。前两态的 calculated_market_value
            -- 为 NULL —— 两种「空」混成一个,消费侧就会把它们统计进分布、拉低整段分位。
            calc_state              TEXT NOT NULL,
            calculated_market_value REAL,
            cost_to_make            REAL,
            profit                  REAL,
            profit_rate             REAL,
            work_to_produce         REAL,
            cost_list               TEXT,
            -- 成本表有一支由难度开关决定的变体时,这里是那个开关名;NULL = 没有变体。
            -- 导出跑在没有 storyteller 的那一刻,而 CostListForDifficulty.Applies 在那时
            -- 无条件为假 —— 于是上面几列**永远是非变体那支**,而变体存不存在在数字上看不出来。
            -- inverted 为真时变体在开关关着时生效(vanilla 的 Turret_Mortar),那正是绝大多数
            -- 存档的状态,于是这一行印出来的数是玩家基本见不到的那一支。
            cost_difficulty_var     TEXT,
            cost_difficulty_inverted INTEGER NOT NULL DEFAULT 0,
            -- 1.0 = 造价全部来自链尾物**手填的**市场价,该行 profit 不反映真实生产消耗。
            chain_end_share         REAL,
            -- 自有指标,不是 vanilla 的量。命名与 cost_to_make 分开是硬要求。
            cost_deep               REAL,
            profit_deep             REAL
        );

        -- 成本链逐项。拆表而不是在 economy 上塞一列 JSON:「谁的成本链里有 Steel」是会被
        -- 问到的反查方向,而 JSON 列上问它只能全表扫 + 字符串匹配。
        CREATE TABLE economy_cost_chain (
            economy_id INTEGER NOT NULL,
            ordinal    INTEGER NOT NULL,
            thing_def  TEXT NOT NULL,
            count      INTEGER NOT NULL,
            unit_value REAL,
            -- 这一项自己没有 recipeMaker —— vanilla CostToMake 的递归在它身上停住。
            chain_end  INTEGER NOT NULL
        );

        -- 能产出同一个物的全部配方。行数 > 1 即表示该物的 calculated_market_value 有
        -- **加载顺序依赖**:CalculableRecipe 返回 DefDatabase 里第一个匹配。
        CREATE TABLE economy_recipes (
            economy_id       INTEGER NOT NULL,
            ordinal          INTEGER NOT NULL,
            def_name         TEXT NOT NULL,
            product_count    INTEGER,
            work_amount      REAL,
            self_referential INTEGER NOT NULL
        );

        CREATE TABLE mods (
            ordinal    INTEGER PRIMARY KEY,
            package_id TEXT NOT NULL,
            name       TEXT,
            version    TEXT
        );

        CREATE VIRTUAL TABLE defs_fts USING fts5(
            def_name, label, description, translated,
            content = '', prefix = '2 3', tokenize = 'unicode61'
        );

        -- keyed 自己的 FTS。**不能并进 defs_fts**:那张表的 rowid 是 def 的 id,
        -- 而 keyed 的行没有 def —— 借用别人的 rowid 空间会让两边的命中互相冒充。
        CREATE VIRTUAL TABLE keyed_fts USING fts5(
            key, translated, original,
            content = '', prefix = '2 3', tokenize = 'unicode61'
        );
        """;

    /// <summary>一处带 <c>COLLATE NOCASE</c> 的查找,以及它当前落在哪。</summary>
    /// <param name="NeedsIndex">true = schema 里必须有对应的 NOCASE 索引(闸会查)。
    /// false = 实测不需要,理由写在 <paramref name="Why"/> 里。</param>
    public sealed record NoCaseLookup(string Table, string Column, bool NeedsIndex, string Why);

    /// <summary>
    /// 查询侧带 <c>COLLATE NOCASE</c> 的每一列,以及它要不要一条同排序的索引。
    ///
    /// 规则本身早写在 <c>idx_fv_leaf_nc</c> 那一对的注释里 —— 但注释长在**已经修好的那一处**,
    /// 而下一个人是在**别处**建新表,不会先去读它。于是 <c>type_fields</c> 照旧建了 BINARY
    /// 索引,1373 万行上实测 12.2s 对 0.113s。这份清单把那条规则搬到一个查得到的地方,
    /// 并且逼着每一处「不加索引」写出自己的理由和实测数,而不是留成沉默。
    ///
    /// **限度**:闸只查得了这里登记的项与 schema 是否一致,查不出「新建的表忘了登记」。
    /// 建表时查询要 NOCASE,请自己往这里加一行 —— 这一条没有机器兜底。
    /// </summary>
    public static readonly IReadOnlyList<NoCaseLookup> NoCaseLookups =
    [
        // 值从 field_values 挪进了字典,于是这一问从 150 万行落到 6.5 万行,主表那一对
        // BINARY/NOCASE 索引并成一条整数索引。
        new("field_value_values", "value", true, "6.5 万行(races 11.1 万),按值反查的等值支。"),
        // leaf 从 field_values 挪到了 field_value_paths(它是 path 的纯函数),于是这一问
        // 从 150 万行落到 2.6 万行,两条索引一起没了。同 path 那条的理由与实测。
        new("field_value_paths", "leaf", false,
            "2.6 万行,与 path 同表。全扫,再按 path_id 顺 idx_fv_pathid 回表。"),
        // 这个查找此前落在 type_fields.def_type 上,1373 万行,失配时 SQLite 转去覆盖扫
        // path 索引:12.2s,NOCASE 后 0.113s —— 那条实测是这份清单存在的由来。子树分解之后
        // 同一个查找落在 type_names 的 222 行上,索引与否都量不出来,于是登记为不需要。
        new("type_names", "name", false,
            "222 行(纯官方的 def 类型数)。全扫,与 NOCASE 与否无关。"),

        // 这一列现在住在 field_value_paths 里(2.6 万行),NOCASE 与否都是全扫,
        // 而 2.6 万行的全扫是 8ms。此前登记在 field_values 上的那条判断(「换 197ms 不值」)
        // 只量了 --exact-path 的等值比,没量后缀 LIKE —— 后者才是主路径,实测 0.42s。
        new("field_value_paths", "path", false,
            "2.6 万行。谓词一律是 LIKE '%x',前缀不定,索引帮不上忙;全扫实测 8ms。"),
        new("defs", "def_type", false, "1.6 万行。BINARY 索引仍被当覆盖索引扫,实测 39ms。"),
        new("defs", "def_name", true,
            "1.6 万行,但 inherit 的后代集 CTE 里它挨的是**逐行**一次覆盖扫(一次相关子查询 "
            + "加一条 NOCASE 的 join)。此前登记为 false、理由写「covering scan 39ms」—— "
            + "那量的是单次;实测 inherit --path-contains 8437ms → 402ms。"),
        new("defs", "class",    false, "没有索引,1.6 万行全扫。"),
        new("xml_written", "def_type", false,
            "19 万行。实测优化器仍走 idx_xw_key 的双列等值查找(42ms)—— 失配没有兑现成代价。"),
        new("keyed", "key", false, "1.3 万行,覆盖索引扫。"),
        // 这一条**此前根本没登记**(清单自己的限度:查不出新表忘了登记)。当时它挨的是
        // injection_keys 的 49.7 万行,而 idx_ik_defname 是 BINARY 的 —— 又一次失配。
        // 字典化之后这一问落在 1.4 万行的名字字典上,主表按整数号找。
        new("injection_key_names", "def_name", false,
            "1.4 万行。全扫,再按整数号回主表 —— 主表那一侧是 idx_ik_defname。"),
        new("shared_values", "def_type", false, "314 行。"),
        new("economy", "category", false, "1038 行。"),
        new("economy", "calc_state", false, "同上。"),
    ];

    /// <summary>
    /// 名册的两条索引,**导入中途就得建**,不能等到最后跟别的一起来 —— 它是唯一一处
    /// 导入自己要回头查这张表的地方:每条运行时译文都拿键串反查槽位。没有索引时那是
    /// 每条一次 49.7 万行全扫,2.68 万条译文实测把导入撑到 1475 秒(占全程 96%);
    /// 建完索引再走同一段,代价回到建这两条索引本身。
    ///
    /// 「索引最后建」那条通则对别的表都成立,对这张不成立,分界是**导入期间读不读它**。
    /// </summary>
    /// <remarks>
    /// 字典化之后这两条都是整数索引。**字典表自己一条索引都不用**:导入期那次自查不再
    /// 拿字符串查库,它在内存里已经攒着同一份字典(发号就在那儿),直接绑号 —— 于是
    /// 「导入期间读不读它」那条分界对字典表是「不读」。
    /// </remarks>
    public const string InjectionKeyIndexes = """
        CREATE INDEX idx_ik_defname ON injection_keys(def_name_id);
        CREATE INDEX idx_ik_suggested ON injection_keys(def_type_id, def_name_id, suggested_path_id);
        """;

    public static void CreateInjectionKeyIndexes(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = InjectionKeyIndexes;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 索引在批量插入之后才建 —— 导入是一次性写,先建索引会显著变慢。
    /// **例外见 <see cref="InjectionKeyIndexes"/>**:导入期间自己要查的那张表不在此列。
    /// </summary>
    public const string Indexes = """
        CREATE INDEX idx_defs_name  ON defs(def_name);
        -- 同一条 collation 规则的**第三次**落点(前两次:field_values 的 leaf/value、
        -- type_fields 的 def_type)。`inherit --path-contains` 的后代集 CTE 里,
        -- `d.def_name = x.def_name COLLATE NOCASE` 与一个同样 NOCASE 的相关子查询,
        -- 配 BINARY 的 idx_defs_name 用不上索引:SQLite 转去**逐行**覆盖扫整条索引。
        -- baseline 上实测 `inherit BaseWeapon --path-contains statBases` 8437ms → 402ms。
        --
        -- 上面那条 BINARY 的留着:defs 只有 1.6 万行,而按 def_name 的等值查找(get)
        -- 是 BINARY 的,改掉就轮到它失去索引。
        CREATE INDEX idx_defs_name_nc ON defs(def_name COLLATE NOCASE);
        CREATE INDEX idx_defs_type  ON defs(def_type);
        CREATE INDEX idx_defs_mod   ON defs(source_mod);
        CREATE INDEX idx_fv_def     ON field_values(def_id);
        -- 字典化之后的回表方向:谓词在 field_value_paths 上跑完,拿一批 path_id 回来找行。
        -- 没有它的话优化器只能把 150 万行整个扫一遍去比 path_id,字典化就白做了。
        CREATE INDEX idx_fv_pathid  ON field_values(path_id);
        -- leaf 那两条索引没了 —— 那一列不在这张表上了(见 field_value_paths 的注释)。
        -- 回表方向,同 idx_fv_pathid:谓词在 6.5 万行的值字典上跑完,拿一批号回来找行。
        -- 整数列没有 BINARY/NOCASE 之分,所以这里只需要一条 —— 原来那一对共 49.4M。
        CREATE INDEX idx_fv_value   ON field_values(value_id);
        -- 大小写不敏感那一问现在落在字典上。等值比对得上索引;`LIKE '%x%'` 用不上,
        -- 但那是 6.5 万行的扫,不是 150 万行的扫。
        CREATE INDEX idx_fvv_value_nc ON field_value_values(value COLLATE NOCASE);
        CREATE INDEX idx_tr_defname ON translations(def_name);
        CREATE INDEX idx_keyed_key   ON keyed(key);
        CREATE INDEX idx_xn_name    ON xml_nodes(name);
        CREATE INDEX idx_xn_parent  ON xml_nodes(parent_name);
        CREATE INDEX idx_xn_defname ON xml_nodes(def_name);
        CREATE INDEX idx_xw_key     ON xml_written(def_type, node_key);
        CREATE INDEX idx_xw_path    ON xml_written(path);
        -- 声明层(type_names / type_subtrees / subtree_paths)一条索引都没有,三条理由各不同:
        --   type_names   222 行,全扫。此前那条 idx_tf_type_nc 是同一个查找的落点,
        --                当时挨的是 1373 万行,所以非 NOCASE 不可(12.2s 对 0.119s);
        --                拆表之后同一个查找落在 222 行上,索引与否都量不出来。
        --   两张 WITHOUT ROWID   表自己就是主键那条索引,查询正是顺着主键走的。
        --   反查方向(一条路径落在哪几棵子树里)   现在没有查询要它。真要加,先得有用它的查询。
        --
        -- type_field_paths.path 也没有索引,而 `--path-contains` 的谓词正落在它上面:
        -- 那是 `LIKE '%x%'`,前缀不定,索引帮不上忙,只会把优化器骗去扫自己。旧形状上
        -- 那条 path 索引就是这么单独占了 1.7G,还把 12.2s 那条计划钓了出来。
        CREATE INDEX idx_sv_type    ON shared_values(def_type);
        CREATE INDEX idx_econ_name  ON economy(def_name);
        CREATE INDEX idx_econ_mod   ON economy(mod);
        CREATE INDEX idx_econ_chain ON economy_cost_chain(economy_id);
        -- 反查方向:哪些物的成本链里有这个原料。
        CREATE INDEX idx_econ_chain_thing ON economy_cost_chain(thing_def);
        CREATE INDEX idx_econ_recipes ON economy_recipes(economy_id);
        """;

    public static void Create(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public static void CreateIndexes(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = Indexes;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>库不可读时抛这个 —— 消息面向调用方,指出下一步做什么。</summary>
public sealed class SnapshotFormatException(string message) : Exception(message);
