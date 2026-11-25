using Microsoft.Data.Sqlite;

namespace RimSearcher.Storage;

public static class SnapshotSchema
{
    public const string MetaKeyRaw = "export_meta_json";
    public const string MetaKeyFingerprint = "fingerprint";
    public const string MetaKeyImportedAtUtc = "imported_at_utc";
    public const string MetaKeyDefCount = "def_count";
    public const string MetaKeySourcePath = "source_file";

    public const string MetaKeyHarvestedRoots = "harvested_roots";

    public const string MetaKeyTranslationsHarvest = "translations_harvest";
    public const string TranslationsHarvestOn = "on";
    public const string TranslationsHarvestOff = "off";
    public const string TranslationsHarvestNoRoots = "no-roots";

    public const string MetaKeyContent = "content_fingerprint";

    public const string MetaKeyEconomyState = "economy_state";

    public const string MetaKeyExportTimings = "export_timings_ms";

    public const string MetaKeyImportTimings = "import_timings_ms";

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
            -- 值长度是展示取舍、集合是宽度问题。
            truncated_by_cap    INTEGER NOT NULL DEFAULT 0,
            truncated_by_length INTEGER NOT NULL DEFAULT 0,
            truncated_by_depth  INTEGER NOT NULL DEFAULT 0,
            truncated_by_items  INTEGER NOT NULL DEFAULT 0,
            -- 读值时抛了异常、没进字段表的格子(导出器 0.25.0 起;更早的遇到这种值整次导出失败)。
            truncated_by_error  INTEGER NOT NULL DEFAULT 0
        );

        -- is_default:这一行与「这个类型刚 new 出来时」的关系,取值见 IntermediateFormat.DefaultState
        -- (0 一定被改过 / 1 与代码默认值无从区分 / 2 没法比)。存原样而不是存 bool ——
        -- 「没法比」并进任何一边都会让呈现侧说出一句它证不了的话。
        -- 路径提进字典表,同 type_field_paths 那一手:行数远多于不同 path。省下的空间是次要的,
        -- 主要收益在**谓词跑在哪张表上**:后缀匹配写成 `path LIKE '%x'`,前导通配让任何
        -- B-tree 都用不上,在主表上是一次全表扫;跑在字典上再按 path_id 回表,快得多。
        -- leaf 住在这儿而不是 field_values 上:它是 NoiseFilter.Leaf(path) 的返回值,
        -- **path 的纯函数**(单一算处,就在导入那一行),于是每条 path 底下它是常量。
        -- 逐行存等于把少数几个答案抄很多遍。
        --
        -- 收益的大头是**两条索引整个消失**:谓词一律是
        -- `leaf = @x COLLATE NOCASE`,原来要在主表上配一条 NOCASE 索引,
        -- 而 BINARY 那条(DISTINCT/GROUP BY 用)得一并留着。
        -- 现在这一问落在字典上,回表顺已有的 idx_fv_pathid 走。
        CREATE TABLE field_value_paths (
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            leaf TEXT NOT NULL
        );

        -- 值也进字典:冗余很高。同 leaf,大头在索引:按值反查一律带 COLLATE NOCASE,
        -- 而 DISTINCT/GROUP BY 是 BINARY,于是值留在主表上就得配一对索引。
        -- 换成号之后主表只剩一条整数索引,大小写那一问落在字典上。
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
        -- 拼法读者看 key 那一格就是。
        CREATE TABLE translations (
            def_id     INTEGER,
            def_type   TEXT,
            def_name   TEXT NOT NULL,
            path       TEXT NOT NULL,
            key        TEXT,
            key_state  TEXT,
            -- 游戏自己的判决:这条 defInjection 真注进去了吗。NULL = 没这一位
            -- (这一行是从磁盘语言文件收割来的 —— 游戏根本没读过)。
            -- 与 key_state 两条独立的路:这一列是游戏的判决,那一列是推算。
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
        -- **这是槽位名册:一个可注入槽位一行,一个不漏**。全在册才使得「这个键在不在册」
        -- 问得出口,而那一问是译文表 key_state 的唯一依据。只发「带信息」的行(两键不同的、
        -- 或不许译且有文本的)的话问不出口,判据退化成拿字段表比对 —— 整表注入的键不带
        -- 元素下标(rulePack.rulesStrings),字段表里那条路径带(…rulesStrings[0]),逐字比
        -- 一次不中。
        -- 四列字符串全进字典:def_type 的不同值极少,冗余很高。
        --
        -- **def_type / def_name 没有删掉**,尽管现有数据里它们逐行与 defs 经
        -- def_id 接出来的一致、def_id 也没有空值。因为 SnapshotImporter.Owner 判不出
        -- 归属时**有意写 null**(同名跨 def 类型是常态,它不肯挑一个),那时这两列是该行
        -- 仅剩的身份。那是现有数据的性质,不是 schema 的性质。
        --
        -- 顺带修掉一处排序规则失配:读侧是 `def_name = @n COLLATE NOCASE`,而
        -- idx_ik_defname 是 BINARY 的,在主表上一次都用不上(导入期那次自查是 BINARY,
        -- 索引一直在为它服务)。现在大小写不敏感那一问落在名字字典上,主表按整数号找。
        CREATE TABLE injection_key_types (
            id       INTEGER PRIMARY KEY,
            def_type TEXT NOT NULL
        );

        CREATE TABLE injection_key_names (
            id       INTEGER PRIMARY KEY,
            def_name TEXT NOT NULL
        );

        -- path 与 suggested_path 共用一份:两者是同一个槽位的两种拼法,取值域高度重叠。
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

        -- 继承层。**唯一一张不是「游戏内存里的对象」的表** —— 它是 XML,
        -- 因为「谁继承谁」在导出时点已经被 XmlInheritance.Clear() 抹掉了。
        -- 0.16.0 起取自打完补丁的文档;更老的导出是磁盘原文(DataLayers.PostPatchInheritanceRow)。
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
            patch_ops_defname INTEGER NOT NULL DEFAULT 0,
            patch_ops_label   INTEGER NOT NULL DEFAULT 0
        );

        -- 每个 XML 节点(含不参与继承的普通 def)实际写出来的字段路径,用来回答 Replace 还是 Add。
        -- inner_text:叶子的行内文本;Class= 那一行是属性值。
        -- patched:真 = 打完补丁的文档里有这一行、磁盘上的原文没有。导出时拿不到那份文档
        -- (PatchRoute=none)的库里它们干脆不在,而「不在」在 xml 列上印的是 not-written(该 Add),
        -- 与「补丁加的」(该 Replace)正相反。
        CREATE TABLE xml_written (
            def_type    TEXT NOT NULL,
            node_key    TEXT NOT NULL,
            key_is_name INTEGER NOT NULL DEFAULT 0,
            path        TEXT NOT NULL,
            inner_text  TEXT,
            patched     INTEGER
        );

        -- 节点上写了 Inherit="False" 的元素路径,def 元素自己是空串。继承合并到那里时祖先在它
        -- 底下写的子元素全部丢掉(属性留着),于是那些行既不是 parent,也不占列表下标。
        CREATE TABLE xml_no_inherit (
            def_type    TEXT NOT NULL,
            node_key    TEXT NOT NULL,
            key_is_name INTEGER NOT NULL DEFAULT 0,
            path        TEXT NOT NULL
        );

        -- 一个 def 类型能有的字段路径全集,与值无关。用来把「全是 null」和「没有这个字段」分开。
        --
        -- 路径提进字典表:不同 path 远少于行数,逐行存字符串冗余极高,占整库一半以上。
        -- 冗余这么高是因为所有 Def 子类共享基类那棵字段树。
        --
        -- leaf:path 的末段(NoiseFilter.Leaf,下标不算),给落空路径按名字问声明集用。
        -- 没有它就是对全部路径的 `LIKE '%.x'` 全扫。
        CREATE TABLE type_field_paths (
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            leaf TEXT NOT NULL
        );

        -- (类型, 路径) 那张展开表**已经拆掉了**。它是一次 JOIN 的结果,而两个因子小得多:
        --
        --   哪个类型带哪几棵子树   type_subtrees
        --   一棵子树摊开是哪些路径  subtree_paths
        --
        -- 这两张表加起来只有原表的百分之几。
        -- 冗余出在:所有 Def 子类共享基类那棵字段树,于是同一棵子树被每个类型各抄一遍。
        -- 「子树」按路径**首段**切(`comps[0].props.x` 归到 `comps`)—— 子树数与不同首段数
        -- 几乎相等:一个字段名底下摊开成什么样几乎完全由字段名决定,
        -- 因为决定它的是那个字段的 C# 类型,与哪个 Def 子类持有它无关。
        --
        -- 这是**无损的集合去重**,不是内容取舍:切分是划分(每条路径恰属一个首段),
        -- 折叠是去重,复原是并集。
        --
        -- 查询侧不因此变快也不变慢 —— 那一层本来就不是热点。省的是磁盘。
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
        -- 把两列连 rowid 抄一遍 —— 旧形状上那条索引比表本身还大。
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

        -- 每个 ShaderDef 的顶点 / 片元阶段一行。resolved_path 与 unresolved 恰有一个非空:
        -- 引擎找不到文件,与导出器在这一版游戏上调不到解析器,是两件事。
        CREATE TABLE shader_sources (
            def_name      TEXT NOT NULL,
            stage         TEXT NOT NULL,
            file          TEXT NOT NULL,
            resolved_path TEXT,
            unresolved    TEXT
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

    public sealed record NoCaseLookup(string Table, string Column, bool NeedsIndex, string Why);

    public static readonly IReadOnlyList<NoCaseLookup> NoCaseLookups =
    [
        new("field_value_values", "value", true, "字典表,按值反查的等值支。"),
        new("field_value_paths", "leaf", false,
            "字典小,与 path 同表:全扫,再按 path_id 顺 idx_fv_pathid 回表。"),
        new("type_names", "name", false,
            "def 类型名,行数极少:全扫,与 NOCASE 与否无关。"),

        new("field_value_paths", "path", false,
            "谓词一律是 LIKE '%x',前缀不定,索引帮不上忙;字典小,全扫便宜。"),
        new("defs", "def_type", false, "表不大,BINARY 索引仍被当覆盖索引扫。"),
        new("defs", "def_name", true,
            "inherit 的后代集 CTE 里它挨的是**逐行**一次覆盖扫(一次相关子查询加一条 NOCASE 的 "
            + "join):单次扫便宜,逐行就不便宜。"),
        new("defs", "class",    false, "没有索引,表不大,全扫。"),
        new("xml_nodes", "name", true, "每个 def 往上走继承链,一层一次。"),
        new("xml_nodes", "def_name", true, "每个 def 找自己的节点一次。"),
        new("xml_nodes", "parent_name", true, "子节点与 inherit 的后代集 CTE 逐层查。"),
        new("xml_written", "def_type", false,
            "优化器仍走 idx_xw_key 的双列等值查找 —— 失配没有兑现成代价。"),
        new("xml_no_inherit", "def_type", false, "同 xml_written,走 idx_xni_key 的双列等值查找。"),
        new("keyed", "key", false, "表不大,覆盖索引扫。"),
        new("injection_key_names", "def_name", false,
            "名字字典:全扫,再按整数号回主表 —— 主表那一侧是 idx_ik_defname。"),
        new("type_field_paths", "leaf", true,
            "大表,落空路径按末段问声明集;没有索引就是全扫。"),
        new("shared_values", "def_type", false, "表极小。"),
        new("translations", "def_name", true, "get 每个 def 查一次自己的译文;表有几万行。"),
        new("economy", "def_name", true, "get 每个 def 查一次;表不大,但逐 def 查就不便宜。"),
        new("economy", "category", false, "表小。"),
        new("economy", "calc_state", false, "同上。"),
    ];

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

    public const string Indexes = """
        CREATE INDEX idx_defs_name  ON defs(def_name);
        -- 两条都要:按名字取 def 是 `def_name = @n COLLATE NOCASE`(先取再挑逐字对上的),
        -- 配 BINARY 的索引用不上;两侧都是库里的名字的连接(inherit 的后代集)是 BINARY 的。
        CREATE INDEX idx_defs_name_nc ON defs(def_name COLLATE NOCASE);
        CREATE INDEX idx_defs_type  ON defs(def_type);
        CREATE INDEX idx_defs_mod   ON defs(source_mod);
        CREATE INDEX idx_fv_def     ON field_values(def_id);
        -- 字典化之后的回表方向:谓词在 field_value_paths 上跑完,拿一批 path_id 回来找行。
        -- 没有它的话优化器只能把整张表扫一遍去比 path_id,字典化就白做了。
        CREATE INDEX idx_fv_pathid  ON field_values(path_id);
        -- leaf 那两条索引没了 —— 那一列不在这张表上了(见 field_value_paths 的注释)。
        -- 回表方向,同 idx_fv_pathid:谓词在值字典上跑完,拿一批号回来找行。
        -- 整数列没有 BINARY/NOCASE 之分,所以这里只需要一条。
        CREATE INDEX idx_fv_value   ON field_values(value_id);
        -- 大小写不敏感那一问现在落在字典上。等值比对得上索引;`LIKE '%x%'` 用不上,
        -- 但那是字典上的扫,不是主表上的扫。
        CREATE INDEX idx_fvv_value_nc ON field_value_values(value COLLATE NOCASE);
        CREATE INDEX idx_tr_defname ON translations(def_name);
        CREATE INDEX idx_tr_defname_nc ON translations(def_name COLLATE NOCASE);
        CREATE INDEX idx_keyed_key   ON keyed(key);
        CREATE INDEX idx_xn_name    ON xml_nodes(name);
        CREATE INDEX idx_xn_parent  ON xml_nodes(parent_name);
        CREATE INDEX idx_xn_defname ON xml_nodes(def_name);
        -- 按名字找节点、找子节点都是 NOCASE 的;BINARY 那三条留给逐字比较(y.name = x.parent_name、join defs)。
        CREATE INDEX idx_xn_name_nc    ON xml_nodes(name COLLATE NOCASE);
        CREATE INDEX idx_xn_defname_nc ON xml_nodes(def_name COLLATE NOCASE);
        CREATE INDEX idx_xn_parent_nc  ON xml_nodes(parent_name COLLATE NOCASE);
        CREATE INDEX idx_xw_key     ON xml_written(def_type, node_key);
        CREATE INDEX idx_xw_path    ON xml_written(path);
        CREATE INDEX idx_xni_key    ON xml_no_inherit(def_type, node_key);
        -- 声明层(type_names / type_subtrees / subtree_paths)一条索引都没有,三条理由各不同:
        --   type_names   行数极少,全扫,索引与否都量不出来(同一个查找落在大表上时
        --                非 NOCASE 不可)。
        --   两张 WITHOUT ROWID   表自己就是主键那条索引,查询正是顺着主键走的。
        --   反查方向(一条路径落在哪几棵子树里)   只有落空路径按末段问声明集时要,
        --                索引在下面 idx_sp_path。
        --
        -- type_field_paths.path 也没有索引,而 `--path-contains` 的谓词正落在它上面:
        -- 那是 `LIKE '%x%'`,前缀不定,索引帮不上忙,只会把优化器骗去扫自己。旧形状上
        -- 那条 path 索引就是这么白占了空间,还把查询计划钓偏了。
        -- leaf 是等值问,NOCASE 跟 where 的路径匹配走。
        CREATE INDEX idx_tfp_leaf_nc ON type_field_paths(leaf COLLATE NOCASE);
        CREATE INDEX idx_sp_path    ON subtree_paths(path_id);
        CREATE INDEX idx_sv_type    ON shared_values(def_type);
        CREATE INDEX idx_econ_name  ON economy(def_name);
        CREATE INDEX idx_econ_name_nc ON economy(def_name COLLATE NOCASE);
        CREATE INDEX idx_econ_mod   ON economy(mod);
        CREATE INDEX idx_econ_chain ON economy_cost_chain(economy_id);
        CREATE INDEX idx_shader_def ON shader_sources(def_name);
        -- 反查方向:哪些物的成本链里有这个原料。
        CREATE INDEX idx_econ_chain_thing ON economy_cost_chain(thing_def);
        CREATE INDEX idx_econ_recipes ON economy_recipes(economy_id);
        """;

    private static IReadOnlyList<string>? _expected;

    public static IReadOnlyList<string> ExpectedColumns()
    {
        if (_expected is not null) return _expected;
        using var mem = new SqliteConnection("Data Source=:memory:;Pooling=False");
        mem.Open();
        Create(mem);
        _expected = Columns(mem);
        return _expected;
    }

    public static string? FirstMissing(SqliteConnection db)
    {
        var have = new HashSet<string>(Columns(db), StringComparer.Ordinal);
        var tables = new HashSet<string>(have.Select(c => c[..c.IndexOf('.')]), StringComparer.Ordinal);
        foreach (var c in ExpectedColumns())
        {
            if (have.Contains(c)) continue;
            var table = c[..c.IndexOf('.')];
            return tables.Contains(table) ? $"列 '{c}'" : $"表 '{table}'";
        }
        return null;
    }

    private static List<string> Columns(SqliteConnection db)
    {
        var cols = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT m.name, p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
                          "WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) cols.Add(rd.GetString(0) + "." + rd.GetString(1));
        return cols;
    }

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

public sealed class SnapshotFormatException(string message) : Exception(message);
