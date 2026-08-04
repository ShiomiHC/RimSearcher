using System.Text;

namespace RimSearcher.Tests;

/// <summary>
/// 字节级基线。跑一批固定调用,把 stdout 逐字节钉进 <c>Snapshots/</c>。
///
/// 这道闸看的是**输出契约**:措辞、列宽、声明区位置、空行、行尾 —— 凡是调用方读得到的
/// 东西,改动都会在这里变红。
///
/// 基线不对时用 <c>RIMSEARCHER_UPDATE_SNAPSHOTS=1 dotnet test</c> 重写,然后**读 diff**。
/// </summary>
[Collection(Collection)]
public class OutputSnapshotTests
{
    /// <summary>
    /// 每条基线都是一次真实调用。名字既是文件名也是这条用例在说什么。
    /// 覆盖面:查名字、看细节、反查、值域、代码搜索、报错路径。
    /// </summary>
    public static TheoryData<string, string[]> Cases => new()
    {
        { "search-hit",            ["search", "shield"] },
        { "search-miss",           ["search", "zzzznothing"] },
        { "search-miss-classlike", ["search", "CompShield"] },
        // 像类名、而且**哪儿都算不出落点**的那一档 —— 唯一走到「按形状猜」的分支,
        // 也是第九轮盲测里 CLI 唯一一处确定假话的原产地。
        { "search-miss-classlike-nowhere", ["search", "CompProperties_NoSuchThing"] },
        // 落空的四种成因,一种一份基线 —— 各自要的下一步不同,而其中三种的答案就在同一个库里。
        { "search-miss-xmlnode",   ["search", "BaseBullet"] },
        { "search-miss-deftype",   ["search", "ThingDef"] },
        { "search-miss-class",     ["search", "TestVariantDef"] },
        { "search-miss-mod",       ["search", "ludeon.rimworld"] },
        // 被自己的 --scope 挡住 —— 「过滤掉了」被说成「没有」是最贵的那种。
        { "search-miss-scoped",    ["search", "TestModGun", "--scope", "ludeon.rimworld"] },
        // 第五种落点:打进来的是屏幕上的一句界面文案 —— 问的是「这句**话**是什么」,
        // 而 search 的索引里没有它。
        { "search-miss-keyed",     ["search", "没有电力"] },
        // 同一句话由几个 key 各自承载时不许挑一个说成「就是这个」—— 真数据里
        // 「转至事件发生地点」同时是 JumpToLocation 与 ClickToJumpToProblem。
        { "search-miss-keyed-multi", ["search", "转至此处"] },
        // 落空的成因里,自己施加的过滤排在猜测之前 —— 两份摆一起:被 scope 滤空的那次
        // 不许再猜抽象基类,真零那次两种成因并列。
        { "where-value-scoped-empty", ["where", "thingClass", "RimWorld.Bullet", "--scope", "test.mod"] },
        { "where-value-class-miss",   ["where", "compClass", "RimWorld.CompNoSuchThing"] },
        // scope 展开在**有结果时**也要说:组名那份必须带展开句,写死 packageId 那份不多说一个字。
        { "where-scope-group",      ["where", "thingClass", "RimWorld.Bullet", "--scope", "vanilla"] },
        { "where-scope-literal",    ["where", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld"] },
        // 位置参数只含标点。两段式子命令匹配把 `<命令> <词>` 归一化后与命令名比,而
        // 归一化只留字母数字 —— 于是这个词整个消失,argv 短了一截而没有一个字说过。
        // 位置参数可选的命令(list / keyed)落点最贵:输出与**真·无参调用**逐字同形。
        { "punct-only-arg-list",   ["list", "%"] },
        { "punct-only-arg-keyed",  ["keyed", "*"] },
        // 空串是同一族里最贵的一档,而它一直在闸外:上面那两份靠「归一化后与原串不等」
        // 触发说破,空串两边相等,于是唯一那句解释正好在最该说的输入上不发。
        // keyed 的裸调用列整层,`keyed ""` 却报零 —— 两者必须摆在一起看。
        { "empty-arg-keyed",       ["keyed", ""] },
        { "empty-arg-keyed-bare",  ["keyed"] },
        // search 一侧更贵:FTS 无词返回零 → 触发译文原文兜底 → 兜底拿原串跑 LIKE '%%'
        // 匹配全体,而兜底那句 Boundary 的措辞假定了「主搜真的没命中」。
        { "empty-arg-search",      ["search", ""] },
        { "punct-only-arg-search", ["search", "."] },
        // 快照标签。别的用例一律显式传 --db(= 调用方自己选的,不报),于是这条输出
        // 在闸上一个字都不响了很久 —— 落点只有「没人指定库 + 目录里不止一份 + 配置钉了一份」
        // 这一个组合。两份摆一起:有声明行时标签贴在第一条上,没有时它自己成行。
        { "snapshot-tag",          ["search", "shield", Fixture.Pinned] },
        { "snapshot-tag-json",     ["search", "shield", "--json", Fixture.Pinned] },
        // 换一份已注册的快照就拿得到 —— 这句话是算得出来的,不该报成「没有」。
        { "get-other-snapshot",    ["get", "OnlyInOtherSnapshot"] },
        { "inherit-other-snapshot", ["inherit", "OnlyInOtherSnapshot"] },
        { "search-typo",           ["search", "Aparel_ShieldBelt"] },
        // 混合命中:两条 FTS + 一条只有子串扫描找得到,再加一个小 limit ——
        // 钉住「N of M 的 M 不随 limit 变」。
        { "search-substring",      ["search", "VoidNode"] },
        { "search-substring-cap",  ["search", "VoidNode", "--limit", "2"] },
        { "get-full",              ["get", "Apparel_ShieldBelt"] },
        { "get-path-filter",       ["get", "Apparel_ShieldBelt", "--path-contains", "comps"] },
        { "get-path-no-match",     ["get", "Apparel_ShieldBelt", "--path-contains", "zzzz"] },
        { "get-truncated-export",  ["get", "Bullet_Revolver"] },
        // 代码默认值的三个落点:字段名与提问一字不差、值却是声明默认值 ——
        // 点了名就必须印出来,并且当场说清它是哪一种。
        { "get-code-default-path", ["get", "Bullet_Revolver", "--path-contains", "burstCount"] },
        { "get-code-default-all",  ["get", "Bullet_Revolver", "--defaults"] },
        { "get-code-default-json", ["get", "Bullet_Revolver", "--defaults", "--json"] },
        { "get-generated",         ["get", "Meat_Muffalo"] },
        { "get-missing",           ["get", "NoSuchDef"] },
        // 同名跨 def_type。两份的分工:不带 --type 时提示在场;带 --type 时提示不许消失、
        // 且父节点/译文不许串味。
        { "get-name-collision",    ["get", "Firefoam"] },
        { "get-name-collision-typed", ["get", "Firefoam", "--type", "StatDef"] },
        // 桶名不一致(XML 根元素 TestVariantDef,def 落在 TestBaseDef 桶)时 inherits_from 仍要在场。
        { "get-bucket-mismatch",   ["get", "VariantOne"] },
        { "where-hit",              ["where", "compClass", "RimWorld.CompShield"] },
        // 一行是一个(def, 路径)对,而同一个 def 可以在多条路径上命中 —— 于是 line 1 那个
        // 数不是 def 数。此前它印的是「N defs」,真快照上 `where capacity Consciousness`
        // 是 155 行 / 80 个 def(AlcoholHigh 一个占四行)。两份摆一起:两数不等时补一句
        // 说破,相等时(where-hit)一个字都不许多。
        { "where-rows-not-defs",    ["where", "stat"] },
        // 加载期由 C# 造出来的 def 混在结果里的两种看相。判定落在**行上**(declared_in),
        // 而句子数的是整个结果集 —— 两份摆一起:整页那份带着 code 与 xml 两种行,
        // 一行那份把唯一的 code 行挤了出去而句子照样在。位置也一起钉住:句子在表**上方**,
        // 它改的是每一行怎么读,沉到表下就是批 B 那个盲区。
        { "where-generated-mixed",  ["where", "soundDrop", "Standard_Drop", "--limit", "all"] },
        { "where-generated-offpage", ["where", "soundDrop", "Standard_Drop", "--limit", "1"] },
        // 点名字段时同一个值还坐在别的路径形状上(Standard_Pickup 同时在 soundPickup 与
        // soundInteract 上)。补这一份的**理由本身值得记**:`where <字段> --value` 这个
        // 命令形态早就有基线(上面两份就是),但没有一份的**数据**满足触发条件,于是这条
        // 分支在字节层从没出过声。字节闸覆盖的是命令形态,不是数据形态 —— 一条分支可以
        // 在命令面上全覆盖、而永远不触发。
        { "where-value-elsewhere",  ["where", "soundPickup", "--value", "Standard_Pickup", "--exact"] },
        // 同一条理由的第二次:不带 --exact 时值是子串匹配,而点名字段这条路此前一个字
        // 都不说(不点名字段那条路一直说着 —— 跨产地口径不一致)。上面那份带着 --exact,
        // 于是整套基线里**没有一份走过缺省态**,而缺省态才是多数人走的路。
        { "where-value-substring",  ["where", "texPath", "--value", "Things/Building"] },
        // 打进 fields 的名字不是 def 类型,而反编译树里有同名类型 —— 那儿才答得出这个问题。
        // 三档摆一起:唯一一棵树命中、跨树同名(不许把一个挑选说成一个事实)、哪儿都没有
        // (那时一个字都不许多说,否则它就成了免责声明)。
        { "fields-miss-in-source",  ["fields", "ThingComp"] },
        { "fields-miss-two-trees",  ["fields", "Outline"] },
        { "fields-miss-nowhere",    ["fields", "NoSuchTypeXYZ"] },
        { "where-miss-compprops",   ["where", "compClass", "CompProperties_Shield"] },
        { "where-miss-field",       ["where", "noSuchField", "x"] },
        // 单位置参数落空的三档。敲一个词进来的人多半给的是**值**而不是字段路径
        // (这条命令的正脸就是「从一个类名或一个值反查 def」),所以名字的落点要当场算:
        //   CompShield      它是某些 def 的字段取值 —— 指得动填好参数的 find
        //   Bullet_Revolver 它是 def 名 —— NameLookup 那句「is not a def name」在这里是假话
        //   noSuchField     哪儿都不是 —— 只剩那句带 <text> 占位的通用指路
        { "where-miss-name-is-value", ["where", "CompShield"] },
        { "where-miss-name-is-def", ["where", "Bullet_Revolver"] },
        // def 名那一档的另一半:没有任何字段指向它 —— 那时不许指向一条空手而归的 --value,
        // 「没有谁按名字引用它」本身就是答案。顺带钉住同名跨类型不让这句话变形。
        { "where-miss-name-unreferenced", ["where", "Firefoam"] },
        { "where-miss-bare",        ["where", "noSuchField"] },
        // 另一半问法。行的形状不同,--json 的顶层键也就不同(matches / paths)。
        { "where-by-value",         ["where", "--value", "CompShield"] },
        // 继承层的四条路各钉一份:抽象节点(有子、被 patch 点名)、具体 def(往上走)、
        // 断链(父不在快照里)、名字不在这一层 —— 四条的措辞各说一件不同的事。
        { "inherit-abstract",      ["inherit", "BaseBullet"] },
        // 第三条分支:声明了 Name= 而没有 xpath 点它。此前这一支一个字不说,于是那个 0
        // 沉默地断言「游戏读到的就是这份原样」—— 而按 defName 定位的补丁不进这个计数。
        { "inherit-named-unpatched", ["inherit", "BaseProjectile"] },
        // 抽象节点侧的 same_value:参照值从子树众数来,而那一列在场与否是这条命令
        // 唯一分得开「这层声明了它」与「后代各写各的」的地方。
        { "inherit-abstract-path", ["inherit", "BaseProjectile", "--path-contains", "soundDrop"] },
        // 祖先侧的 patch_ops 列有无各钉一份。Bullet_Revolver 自己 0、父 BaseBullet 是 2
        // (与真快照里 BaseMechanoid → BasePawn 同构);Firefoam 的整条链全 0,那一列不许出现 ——
        // 全零时它每行同值,是纯噪声。
        { "inherit-def",           ["inherit", "Bullet_Revolver"] },
        { "inherit-ancestors-clean", ["inherit", "Firefoam"] },
        { "inherit-broken-chain",  ["inherit", "TestModGun"] },
        { "inherit-not-in-layer",  ["inherit", "Apparel_ShieldBelt"] },
        { "inherit-missing",       ["inherit", "NoSuchNode"] },
        { "get-xml-node-only",     ["get", "BaseBullet"] },
        { "list-limited",          ["list", "ThingDef", "--limit", "2"] },
        { "list-scope-empty",      ["list", "HediffDef", "--scope", "test.mod"] },
        // 排除式 scope 的静默错表:被排除掉的那部分照样有命中,而留下的结果表干净、完整、
        // 看不出任何问题。上面那几条 scope 闸全是白名单形式,照不出这个形态。
        // 三份摆一起:被排除部分有命中(该说)、被排除部分为空(不许说)、白名单式(不许说)。
        { "list-scope-excluding",  ["list", "ThingDef", "--scope", "all,-test.mod"] },
        { "list-scope-excluding-empty", ["list", "HediffDef", "--scope", "all,-test.mod"] },
        // 吃 scope 的每条命令各钉一份 —— 这句话的产地是 CommandContext.AnnounceExcluded 一处,
        // 但**每条命令各自决定数什么**(def / def type / path / value),数错口径的话
        // 「被排除的那半边有多少」与表上那个数不可比。search 那条还兼测模糊回退:
        // vanilla 侧一个 Void 都没有,表里印的是拼写最接近的,而 test.mod 侧有三个真命中。
        { "search-scope-excluding", ["search", "Void", "--scope", "all,-test.mod"] },
        { "where-scope-excluding",  ["where", "soundDrop", "--scope", "all,-test.mod"] },
        { "where-value-scope-excluding", ["where", "--value", "Standard_Drop", "--scope", "all,-test.mod"] },
        { "values-scope-excluding", ["values", "soundDrop", "--scope", "all,-test.mod"] },
        { "truncated-scope-excluding", ["snapshot", "truncated", "--scope", "all,-test.mod"] },
        // 打错类型名再带 --own-class:此前这一支手抄了 DefTypeMiss.Say,抄的是产地后来长出
        // 近似候选之前的那一版,于是拼错 + --own-class 是唯一拿不到拼写建议的路。两支同一个问题。
        { "list-typo-classed",     ["list", "ThingDf", "--own-class", "TestVariantDef"] },
        { "fields-filtered",       ["fields", "ThingDef", "--path-contains", "comps"] },
        { "values-coverage",       ["values", "compClass"] },
        { "values-miss",           ["values", "noSuchField"] },
        // 零结果的第四种成因:敲的名字是**上一层**。索引只存叶子,`comps` 自己不落行,
        // 值在 `comps[0].compClass` 上 —— 而 C# 字段名就长这样,是最容易敲的那个词。
        // 与 values-miss / where-miss-field 是配对的:那两条是真不存在,这两条是存在但更深,
        // 输出必须**不同形**。此前两者逐字一样,把 statBases(2394 个 def)报成了「没有」。
        { "values-miss-deeper",    ["values", "comps"] },
        { "where-miss-deeper",     ["where", "comps", "x"] },
        // list 的另一半:不给 def 类型时列类型总表。
        { "list-types",            ["list"] },
        { "mods",                  ["mods"] },
        // modlist 此前一份基线都没有,而这条命令答的正是「搜遍了几份」——
        // 那个分母本来挂在 CountNotice 的截断参数上,而这个 Tally 恒完整,于是一次都没印出来过。
        { "modlist-search",        ["modlist", "show", "--find", "test.notinsnapshot"] },
        // limit 取 2 而不是 3:默认值行不进表,ShieldBelt 只剩 3 条可列,--limit 3 截不到东西,
        // 而这份基线要的正是「JSON 里的截断声明」。
        { "json-mode",             ["get", "Apparel_ShieldBelt", "--limit", "2", "--json"] },
        // 代码块在 --json 里得是行,不是一串 "path:line:text" 字符串 ——
        // 路径里本来就可能有冒号,拼起来解析不回去。
        { "json-code-search",      ["code-search", "public", "--file-glob", "ThingComp.cs", "-C", "1", "--json"] },
        { "json-read-member",      ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--json"] },
        { "usage-unknown-flag",    ["search", "shield", "--lmit", "5"] },
        { "usage-unknown-command", ["serach", "shield"] },
        // 退役的旧命令名。近似候选救不了它(find 与 where 一个字母都不像),
        // 不专门接住的话,印出来的与「这个词从来就不是一条命令」逐字同形。
        { "usage-retired-command", ["find", "compClass", "RimWorld.CompShield"] },
        // 同一个词在别的命令上是选项、在这条上是位置参数。--field 是 get / inherit / read
        // 认的写法,搬到 find 上就落空,而「这里怎么写」是算得出来的 —— 连值一起填好。
        { "usage-field-is-positional", ["where", "--field", "compClass"] },
        // 值给了两遍且不一样。位置参数与 --value 说的是同一件事,挑一个跑下去的话
        // 另一个被丢了在输出里看不出来。
        { "usage-value-twice",     ["where", "compClass", "RimWorld.CompShield", "--value", "Other"] },
        // 夹具恒追加 --db/--config,而总览那条分支要求 argv 恰好一个词。
        { "help-overview",         ["--help"] },
        // `--help <command>` 不接,但那个词不许被默默扔掉 —— 说清这一屏是什么,
        // 并把该打的那一条(`<command> --help`)原样给出来。
        { "help-with-command",     ["--help", "search"] },
        { "help-get",              ["get", "--help"] },
        // Remarks 里那段 patch 口径与 identity 块的 patch_ops 说的是同一件事,而 r14 抓到
        // 一个受测者读了输出的新句、再引这里的旧句把它降格成「通用免责措辞」驳回。
        { "help-inherit",          ["inherit", "--help"] },
        // where 的 --limit / --offset 数的是**行**((def, 路径)对),而模板的 what 一度传的是
        // "defs" —— 这条命令是全套里唯一一行不等于一个 def 的,那个词在别处都是真话。
        { "help-where",            ["where", "--help"] },
        { "help-code-search",      ["code-search", "--help"] },
        { "help-sources-sync",     ["sources", "sync", "--help"] },
        // 没配 decompiled_dir 时说的那句话。反编译树是**唯一**不在快照里的数据源,
        // 这条路必然被走到,输出必须说清该往哪补一行配置。
        // 这一条要的是**没有**配置,所以自带 --config 覆盖掉 Fixture.Run 默认追加的那份。
        { "sources-not-configured", ["sources", "list", "--config", "no-such-config.toml"] },
        // 以下每条盯 code-search 的一件事:
        { "code-search-hit",       ["code-search", ": ThingComp"] },
        // 上下文窗口重叠:-C 1 打在连着命中的五行上,窗口要合并。
        { "code-search-context",   ["code-search", "public", "--file-glob", "ThingComp.cs", "-C", "1"] },
        // --limit 只管印几行,不许缩短扫描:总数必须仍是准数(「N of M」而非「at least N」)。
        { "code-search-limit",     ["code-search", "public", "--limit", "2"] },
        // 单文件上限:同上,过了上限的命中仍要进总数。
        { "code-search-per-file",  ["code-search", "public", "--max-per-file", "1"] },
        // 文件数上限咬下去:某棵树只读了一部分要说破、没读到的树要点名、
        // .git 与空树不许出现在名单里。
        { "code-search-max-files", ["code-search", ": ThingComp", "--max-files", "2"] },
        // 同一道闸 + 零命中:「没匹配到」与「没读完」必须分得开。
        { "code-search-capped-miss", ["code-search", "zzzznothing", "--max-files", "2"] },
        // 真零结果:扫完了确实没有。这一条才该指路去 search / find。
        { "code-search-miss",      ["code-search", "zzzznothing"] },
        // 第三种零结果:glob 一个文件都没打中 —— 带 '/' 的 glob 匹配的是相对**根目录**
        // 的整条路径,少写树名就全空。
        { "code-search-glob-empty", ["code-search", "public", "--file-glob", "Verse/ThingComp.cs"] },
        // 第四种零结果:树在名单里、目录也在,里面一个文件都没有 —— 真因是这棵树该 sync 一遍,
        // 不许与上一条同形(否则答案会变成「改 glob」)。
        { "code-search-empty-tree", ["code-search", "public", "--source", "zz.emptytree"] },
        // 别名 --file-extension 收下 'cs',值却按 glob 解 —— 两种文法的零结果要分得开。
        { "code-search-bare-ext",  ["code-search", "public", "--file-extension", "cs"] },
        // 不带 '/' 也不带 '.' 的 glob:调用方想取的是目录/命名空间,挑中的却是文件名。
        // 这一支有命中,于是没有任何落空消息会响 —— 一份完整的答案答的是另一个问题。
        { "code-search-nameonly-glob", ["code-search", "public", "--file-glob", "*Thing*"] },
        // --path-contains 筛空的两种成因:真没有这条路径 vs 给进来的文本其实是个**值**
        // (stat 名装在 statBases[N].stat 里)。
        { "get-path-is-value",     ["get", "Apparel_ShieldBelt", "--path-contains", "MarketValue"] },
        // 第三种:字段在同类型别的 def 上有(Meat_Muffalo 的 ingestible.*),这个 def 上是 null。
        { "get-path-on-kin",       ["get", "Apparel_ShieldBelt", "--path-contains", "ingestible"] },
        // 退役的旧名 --path:它在 docs 上仍是 --out 的别名,于是拒绝消息有两句话可说。
        // 先说的必须是**这条命令**叫它什么 —— 只说「docs 认它」的话,一次改名就把
        // 用得最多的那个词指向了最不相干的命令,而两种消息都以 exit 2 收场,同形。
        { "get-retired-path",      ["get", "Apparel_ShieldBelt", "--path", "comps"] },
        // --source 已经给出时,补救措施里不许再列 --source。
        { "code-search-source-cap", ["code-search", "public", "--source", "vanilla", "--max-files", "1"] },
        { "code-search-no-tree",   ["code-search", "public", "--source", "HAR"] },
        // --snapshot 在这条命令上一寸范围都不收。两份钉的是**位置**:那句话紧跟计数句,
        // 落在取景区而不是末尾脚注区 —— 会写 `--snapshot vanilla` 的人正是把它当成范围
        // 过滤器的人,而计数句尾巴上「across N source trees」不会纠正他。
        { "code-search-snapshot-unused", ["code-search", ": ThingComp", "--snapshot", "core", Fixture.Pinned] },
        // 查无此名的那一档在别的命令上一直是硬错,这条命令此前静默放行(懒寻址顺带把
        // 名字校验也变懒了)。名字取 'vanilla' —— 实证里出问题的就是它。
        { "code-search-snapshot-no-such", ["code-search", ": ThingComp", "--snapshot", "vanilla", Fixture.Pinned] },
        // 界面文案接上代码行。语料那三行各是一种形态,这一份同时钉住三件事:
        // 查得到的 key 进表、查不到的字面量点名、运行时拼出来的 key 单独说。
        { "code-search-ui-text",   ["code-search", "Translate"] },
        // 同一次调用关掉它:那三条声明必须一起消失,不许留一句孤零零的边界话。
        { "code-search-no-resolve-keys", ["code-search", "Translate", "--no-resolve-keys"] },
        // keyed 的两个方向。key → 显示什么;文案 → 是哪个 key(带上「拿它去搜代码」那一步)。
        { "keyed-hit",             ["keyed", "CannotUseNoPower"] },
        // 查询词恰好是一个真 key,而同前缀还有别的 —— 精确命中把前缀匹配关掉的那一刻。
        // 两份基线摆在一起:收窄了的那次要说破,前缀那次照旧两行都在。
        { "keyed-exact-collapses", ["keyed", "CommandSettle"] },
        { "keyed-prefix-both",     ["keyed", "CommandSettl"] },
        { "keyed-text",            ["keyed", "没有电力"] },
        // keyed 自己那条下一步提示:一个 key 时命令填好,几个 key 时说破要按行挑 ——
        // 填第一个等于替读的人挑了一个。
        { "keyed-text-multi",      ["keyed", "转至此处"] },
        // 占位:表里它与真译文同形,而游戏显示的是英文。这一份守的是那句说破在场。
        { "keyed-placeholder",     ["keyed", "TodoKey"] },
        // 过滤器筛空 ≠ 没有这个 key。
        { "keyed-placeholder-none", ["keyed", "CannotUseNoPower", "--empty-translation"] },
        // 第三条路:不给查询词的整层枚举 —— 「把还没译的全列出来」这条意图要有一种
        // 可表达的形式。两份基线:整层第一页,以及这条意图本身。
        { "keyed-all",             ["keyed"] },
        { "keyed-all-placeholders", ["keyed", "--empty-translation", "--limit", "all"] },
        // 枚举走的是分页文法而不是精确 key 那一路,所以翻过头这条分支也得有。
        { "keyed-all-past-end",    ["keyed", "--empty-translation", "--offset", "9"] },
        // --empty-translation 是收窄参数,计数要念回它划的那道线 —— 不念的话「1 key.」会被
        // 读成「filler 一共命中一条」,而真值是 2100 条里有一条占位。
        { "keyed-text-placeholders", ["keyed", "filler", "--empty-translation"] },
        // 零结果的两种成因:代码里有这个字面量而语言文件里没有(死 key),
        // 以及问的其实是个 def 名 —— 后者该被指回 get/search,而不是报「没有」。
        { "keyed-miss",            ["keyed", "NoSuchUiKey"] },
        { "keyed-miss-def",        ["keyed", "Apparel_ShieldBelt"] },
        // read 的两处错法:定位到哪个文件、以及配平括号找到的是不是那一段。
        // 轮廓:注释/字符串/字符字面量里的括号不许算数,方法体里的 if 不许变成成员,
        // 带初值的字段不许被初值里的括号认成方法。
        { "read-outline",          ["read", "Outline.cs", "--source", "vanilla", "--outline"] },
        // 同名成员分属两个类型:不带 --type 全给并说破归属,带 --type 只给一份。
        { "read-member",           ["read", "vanilla/Verse/Outline.cs", "--member", "Shared"] },
        { "read-member-typed",     ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--type", "Inner"] },
        // 「有这个成员但不在那个类型里」与「整个文件都没有」是两句不同的话。
        { "read-member-wrong-type", ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--type", "Nope"] },
        { "read-member-missing",   ["read", "vanilla/Verse/Outline.cs", "--member", "Shard"] },
        { "read-type",             ["read", "vanilla/Verse/Outline.cs", "--type", "Inner"] },
        // 裸行三态:一段、整份、越过末尾。翻页参数与总行数恒在,这条命令的分页就靠它。
        { "read-lines",            ["read", "vanilla/Verse/Outline.cs", "--lines", "7-12"] },
        { "read-whole-file",       ["read", "vanilla/Verse/Widgets.cs"] },
        // 截断行的两种处境逐字同形:还剩一页,与还剩几十页。前者翻一下就完了,后者盲翻是
        // 荒谬路径,而这条行给的唯一出路一直是 --lines。页数摆出来才分得开。
        { "read-many-pages",       ["read", "vanilla/Verse/Outline.cs", "--limit", "4"] },
        { "read-past-end",         ["read", "vanilla/Verse/Outline.cs", "--lines", "900"] },
        { "read-line-cap",         ["read", "vanilla/Verse/Outline.cs", "--type", "Outer", "--limit", "4"] },
        // 基名撞车时不选,只列 —— 选错的输出与选对的逐字同形。
        { "read-ambiguous",        ["read", "Outline.cs"] },
        { "read-no-file",          ["read", "NoSuchFile.cs"] },
        // 路径的中间段写错、文件名对。名字唯一时读下去 —— 但**必须说破**:后面每一句
        // 印的都是解析出来的那条路径,不说的话这次输出与「路径本来就写对了」逐字同形,
        // 而调用方会把那条错路径记下来接着用。名字仍撞车时照旧不选。
        { "read-wrong-dir",        ["read", "vanilla/RimWorld/Widgets.cs"] },
        { "read-wrong-dir-ambiguous", ["read", "vanilla/RimWorld/Outline.cs"] },
        // 两种读法同时传:不排优先级,当场说破这是两件事。
        { "read-two-modes",        ["read", "Outline.cs", "--lines", "1-3", "--member", "Shared"] },
        // 括号配平法认错声明的三种形态(语料见 Fixture.WriteSourceTree)。
        //
        // 元组类型:`internal (int left, int right) Split(int at)` 的第一个顶层 '(' 是类型。
        // 取它左边的标识符 = 取到修饰符,于是 Split 与 bounds 双双消失,列里剩两个
        // 叫 internal / private 的「方法」,而**行号是对的**。
        { "read-outline-tuple",    ["read", "vanilla/Verse/Tuples.cs", "--outline"] },
        // 同一件事在 --member 上的样子:名字白纸黑字在文件里,命令说没有,
        // 而它给的理由(「配平括号不是解析」)会把人引去改拼写。
        { "read-member-tuple",     ["read", "vanilla/Verse/Tuples.cs", "--member", "Split"] },
        // 约束连写:`where T : class where U : struct` 里的 `class where` 被认成类型声明,
        // 压栈之后 Declarable 放行,方法体里的 if 跟着变成成员 —— 一处误判毁一整块。
        { "read-outline-constrained", ["read", "vanilla/Verse/Constrained.cs", "--outline"] },
        // 泛型元数不同的同名类型。这一条不是错,是歧义:两行轮廓逐字相同,
        // --type 会把两段都给出来而消歧提示发不出(它只在 --type 缺席时说话)。
        { "read-outline-arity",    ["read", "vanilla/Verse/Pair.cs", "--outline"] },
        { "read-type-arity",       ["read", "vanilla/Verse/Pair.cs", "--type", "Pair"] },
        // 分页的三个位置:中间页要说自己从第几条起、末页不许再给下一页的参数、
        // 翻过头不是「没有这个东西」。
        { "page-middle",           ["list", "ThingDef", "--limit", "2", "--offset", "2"] },
        { "page-last",             ["list", "ThingDef", "--limit", "4", "--offset", "5"] },
        { "page-past-end",         ["list", "ThingDef", "--offset", "900"] },
        // 同一套文法长在另外三条命令上。search 的结果集是「FTS 命中」接着「子串补扫」两段拼的,
        // 翻页要在拼好的那条序列上走 —— 两段各自跳一次 offset 会让第二页重印第一页的补扫结果。
        { "page-search",           ["search", "VoidNode", "--limit", "1", "--offset", "1"] },
        { "page-fields",           ["fields", "ThingDef", "--limit", "3", "--offset", "3"] },
        { "page-values",           ["values", "thingClass", "--limit", "1", "--offset", "1"] },
        // 负偏移在 SQLite 里等同于 0 —— 不拦下来,「少给了一个负号」与「这就是第一页」同形。
        { "page-negative",         ["list", "ThingDef", "--offset", "-2"] },
        // 参数被夹紧就要当场说破。
        { "limit-clamped",         ["list", "ThingDef", "--limit", "5000"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 输出与基线逐字节一致(string name, string[] argv)
    {
        var (stdout, stderr, code) = Fixture.Run(argv);

        // stdout / stderr / 退出码是同一个契约的三面,三者一起进基线。
        var actual = new StringBuilder()
            // 空参数照直拼进去是隐形的:`keyed ""` 与真·无参调用的回显行只差一个尾空格,
            // 而尾空格闸自己不许留 —— 于是两条不同的调用在基线里长成一样。加引号。
            .Append("$ rimsearcher ").Append(string.Join(' ', argv.Select(a => a.Length == 0 ? "''" : a)))
            .Append('\n')
            .Append("exit ").Append(code).Append('\n')
            .Append("--- stdout ---\n").Append(stdout)
            .Append("--- stderr ---\n").Append(stderr)
            .ToString()
            .Replace("\r\n", "\n");

        var path = Path.Combine(SnapshotDir, name + ".txt");

        if (Environment.GetEnvironmentVariable("RIMSEARCHER_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(SnapshotDir);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path),
            $"No baseline for '{name}'. Run with RIMSEARCHER_UPDATE_SNAPSHOTS=1 to create it, then read the diff.");

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 基线目录里不许有没人认领的文件 —— 删了用例却留下基线,那份文件看着还在闸内、
    /// 其实早没人跑。
    /// </summary>
    [Fact]
    public void 基线目录里没有孤儿文件()
    {
        if (!Directory.Exists(SnapshotDir)) return;
        var claimed = Cases.Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.EnumerateFiles(SnapshotDir, "*.txt")
                               .Select(Path.GetFileNameWithoutExtension)
                               .Where(n => n is not null && !claimed.Contains(n))
                               .ToList();
        Assert.True(orphans.Count == 0, $"Baselines with no case: {string.Join(", ", orphans)}.");
    }

    /// <summary>
    /// 「keyed 这一层整个是空的」与「这个 key 不在里面」是两件事。空层只可能来自一份缺了
    /// 这一节的快照,所以这句话必须说**快照**,不许说 key。
    ///
    /// 这条走不了字节级基线:它要一份自己动过手的库,而 <c>--db</c> 是绝对路径,
    /// 印进基线就把本机 TEMP 路径绑死了。
    /// </summary>
    [Fact]
    public void keyed层为空时说破是快照的缘故而不是查不到()
    {
        var db = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "keyed-empty.db");
        if (File.Exists(db)) File.Delete(db);
        File.Copy(Fixture.Db, db);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // keyed_fts 是 contentless 的,DELETE 不认 —— 清空要走 fts5 自己那条命令。
            cmd.CommandText = "INSERT INTO keyed_fts(keyed_fts) VALUES('delete-all'); DELETE FROM keyed;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (empty, _, code) = Fixture.Run("keyed", "CannotUseNoPower", "--db", db);
        Assert.Equal(1, code);
        Assert.Contains("no keyed translations at all", empty);
        // 成因归到快照身上,而不是归到问的那个 key 身上。
        Assert.Contains("property of the snapshot", empty);
        Assert.DoesNotContain("No keyed translation matches", empty);

        // 反面:库里有这一层、只是没这个 key 时,上面那句话一个字都不许出现。
        var (missing, _, _) = Fixture.Run("keyed", "NoSuchUiKey");
        Assert.DoesNotContain("no keyed translations at all", missing);
        Assert.Contains("No keyed translation matches", missing);

        // 不给查询词那一路也要说快照,不能拿一个不存在的 query 拼进句子。
        var (bare, _, bareCode) = Fixture.Run("keyed", "--db", db);
        Assert.Equal(1, bareCode);
        Assert.Contains("property of the snapshot", bare);
        Assert.Contains("what this layer holds", bare);
    }

    /// <summary>
    /// 「一条占位都没有」是一个**完整的肯定回答**(这份快照译全了),而按行数它走的是
    /// exit 1。那句话必须把两件事都说出来:覆盖率是满的,以及退出码非零只是因为一行都没印。
    ///
    /// 与上面那条同理走不了字节级基线:它要一份自己动过手的库。
    /// </summary>
    [Fact]
    public void 整层没有占位时说的是覆盖率满而不是查不到()
    {
        var db = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "keyed-no-placeholders.db");
        if (File.Exists(db)) File.Delete(db);
        File.Copy(Fixture.Db, db);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE keyed SET placeholder = 0;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (text, _, code) = Fixture.Run("keyed", "--empty-translation", "--db", db);
        Assert.Equal(1, code);
        Assert.Contains("carry a real translation", text);
        // 分母是整层的行数,不是「筛剩下的零」。
        Assert.Contains("2107 keyed translations", text);
        Assert.Contains("the exit code is still non-zero", text);
        // 「没找到」的措辞一个字都不许出现:那会把「译全了」说成「查不到」。
        Assert.DoesNotContain("No keyed translation matches", text);
    }

    internal static string SnapshotDir => Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Tests", "Snapshots");

    /// <summary>
    /// 读写基线的测试类共用的 collection 名。xUnit 默认一个测试类一个 collection、
    /// collection 之间并行,而基线目录被一个类写、另一个类读 —— 同名进一个 collection
    /// 才能让它们串行。闸在 <c>GateTests.读写基线的测试类同属一个collection</c>。
    /// </summary>
    internal const string Collection = "baseline-files";
}
