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
        // 2026-09-05 起不给 --limit 就是全量,基线不必装下两千行:显式压一页,对照意图不变。
        { "empty-arg-keyed",       ["keyed", ""] },
        { "empty-arg-keyed-bare",  ["keyed", "--limit", "25"] },
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
        // 这条与上一条只差一个 --limit,而**整套基线此前没有一份走到过这两条命令的截断态** ——
        // 于是 ece5f54 换截断文法时,这两处漏在旧文法上,字节闸一声不响(它覆盖的是命令形态,
        // 不是数据形态)。
        { "get-path-filter-truncated", ["get", "Apparel_ShieldBelt", "--path-contains", "comps", "--limit", "1"] },
        { "get-path-no-match",     ["get", "Apparel_ShieldBelt", "--path-contains", "zzzz"] },
        { "get-truncated-export",  ["get", "Bullet_Revolver"] },
        // xml 列:here / parent / no / under,加上值回连后的 here、两层标签的 here、
        // 「元素归位但这一格没写」的 no。主 fixture 是 0.2.0,那一列在它上面根本不出现。
        { "get-xml-written",       ["get", "ChildGun", "--defaults", Fixture.PresenceArg] },
        // 三档新判据收窄到路径上:回连 here、两层 here/确定 no、真 under。全表那份列宽
        // 跟着最长路径走,这份钉的是取值本身。
        { "get-xml-rejoined",      ["get", "ChildGun", "--defaults", "--path-contains", "costList",
                                    "--path-contains", "things", "--path-contains", "Hyperlinks",
                                    Fixture.PresenceArg] },
        // 0.6.0:记下文本之后,costList 的 count 是 here、quality 是 no。0.5.0 那两份
        // 基线一个字节都不许动 —— 它们走的是没文本的旧路。
        { "get-xml-text",          ["get", "ChildGun", "--defaults", Fixture.PresenceTextArg] },
        { "get-xml-text-zero",     ["get", "PatchedGun", "--defaults", "--path-contains", "costList",
                                    Fixture.PresenceTextArg] },
        { "get-xml-text-tie",      ["get", "TwinGun", "--defaults", "--path-contains", "costList",
                                    Fixture.PresenceTextArg] },
        { "get-xml-text-bare",     ["get", "BareGun", "--defaults", "--path-contains", "costList",
                                    Fixture.PresenceTextArg] },
        // 0.7.0:路径取自打完补丁的 XML,补丁加的行带 +patch。
        { "get-xml-patched",       ["get", "PatchGun", "--defaults", Fixture.PresencePatchArg] },
        { "get-xml-patched-tag",   ["get", "PatchListGun", "--defaults", Fixture.PresencePatchArg] },
        // 2026-09-01:上面每一条 presence 用例都带 --defaults,于是**不带它的那条路在新快照上
        // 一份基线都没有** —— 而 xml 列在那条路上就已经印着,只是解释它的那句「Not listed:」
        // 走的是另一支代码。改那句时字节闸一声不响。盲测里一个被试正是在这条路上把
        // xml=no 读成「所以是补丁加的」,而 0.7.0 的 no 意思正相反。
        // 两档各钉一份:限定语本身是分档的(读补丁前 / 读补丁后),一份基线钉不住两个取值。
        { "get-xml-notlisted-patch", ["get", "PatchGun", Fixture.PresencePatchArg] },
        { "get-xml-notlisted-prepatch", ["get", "ChildGun", Fixture.PresenceArg] },
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
        // 2026-09-07:一次取几个 def / 一整类。三个入口一套块渲染,单名那条路的基线(上面
        // 全部)一个字节不许动 —— 下游脚本与 skill 都照着它写。这一批钉的是多块那条路:
        // 块序 = 名字给的顺序;缺席的名字只留一句 note、其余照印、退出码 0;撞名与
        // 「不是这个类型」两句在多名下逐字不变;--type 不给名字 = 整类,按 def_name 排。
        { "get-multi",             ["get", "Apparel_ShieldBelt", "Bullet_Revolver"] },
        { "get-multi-json",        ["get", "Apparel_ShieldBelt", "Bullet_Revolver", "--json"] },
        { "get-multi-missing",     ["get", "Apparel_ShieldBelt", "NoSuchDef", "Bullet_Revolver"] },
        { "get-multi-missing-json", ["get", "Apparel_ShieldBelt", "NoSuchDef", "Bullet_Revolver", "--json"] },
        // 全部缺席才是 1。
        { "get-multi-all-missing", ["get", "NoSuchOne", "NoSuchOther"] },
        { "get-multi-collision",   ["get", "Firefoam", "Bullet_Revolver"] },
        { "get-multi-typed-miss",  ["get", "Bullet_Revolver", "Firefoam", "--type", "StatDef"] },
        // 同一个名字给两遍只印一遍:两块逐字相同,第二块会被读成另一个同名 def。
        { "get-multi-repeat",      ["get", "Bullet_Revolver", "bullet_revolver"] },
        { "get-type-all",          ["get", "--type", "TestBaseDef"] },
        { "get-type-all-json",     ["get", "--type", "StatDef", "--json"] },
        { "get-type-all-unknown",  ["get", "--type", "NoSuchDef"] },
        // 什么都不给:用法错,两条出路都点名。
        { "get-nothing",           ["get"] },
        // 类型打头 + 多名:改写仍发生,名字全部保留。
        { "deftype-lead-get-multi", ["get", "ThingDef", "Apparel_ShieldBelt", "Bullet_Revolver"] },
        { "where-hit",              ["where", "compClass", "RimWorld.CompShield"] },
        // 第三方的类坐在官方 def 上 —— mod 列答的不是「谁挂的」。两份摆一起:
        // 上面那条是游戏自己的类,一个字都不许多;这条才该出声。
        // 盲测里那六个人敲的是**不带 --exact** 的形态,基线跟着敲同一条 ——
        // 曾经按 --exact 门控过,而那道门恰好让这句话在唯一测到过的场合哑火。
        { "where-authorship",       ["where", "compClass", "TestMod.CompBoltedOn"] },
        // 出路那半句跟着「xml 列读的是哪份 XML」分档:补丁后读的快照上 no 能排除 patch,
        // 补丁前读的不能。两档各一份,否则改了其中一档另一档静默跟着错。
        { "where-authorship-prepatch", ["where", "compClass", "TestMod.CompBoltedOn", Fixture.PresenceArg] },
        { "where-authorship-patch",    ["where", "compClass", "TestMod.CompBoltedOn", Fixture.PresencePatchArg] },
        // 一行是一个(def, 路径)对,而同一个 def 可以在多条路径上命中 —— 于是 line 1 那个
        // 数不是 def 数。此前它印的是「N defs」,真快照上 `where capacity Consciousness`
        // 是 155 行 / 80 个 def(AlcoholHigh 一个占四行)。两份摆一起:两数不等时补一句
        // 说破,相等时(where-hit)一个字都不许多。
        { "where-rows-not-defs",    ["where", "stat"] },
        // 加载期由 C# 造出来的 def 混在结果里的两种看相。判定落在**行上**(declared_in),
        // 而句子数的是整个结果集 —— 两份摆一起:整页那份带着 code 与 xml 两种行,
        // 一行那份把唯一的 code 行挤了出去而句子照样在。位置也一起钉住:句子在表**上方**,
        // 它改的是每一行怎么读,沉到表下就是批 B 那个盲区。
        { "where-generated-mixed",  ["where", "soundDrop", "Standard_Drop"] },
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
        // 一行里两态并存:soundInteract 上三个 def 的值就是 Standard_Pickup,Meat_Muffalo 的
        // 是 Standard_PickupSlow。此前这两批合成一列 defs 印成 4,而「值就是它的有几个」
        // 与「只是含着的有几个」是两个问题 —— 真快照上 194 个真实查询值里 55 个是这个形态。
        // 这一份钉的是**数据形态**,不是命令形态:上面那几份的命令形状全覆盖了,但没有一份
        // 的数据让两列同时非零。
        { "where-value-both-kinds", ["where", "--value", "Standard_Pickup"] },
        // 同一条理由的**第三次**,而这一次差的不是 flag 是 def_type:上面那两份的别处形状
        // 与命中行同类型,于是「按命中行的 def_type 收窄」这个决定在字节层从没出过声 ——
        // 收窄掉与没收窄掉,在那两份基线里逐字相同。这一组的答案大头在**另一个类型**上。
        // 文本与 --json 各钉一份:--json 那条路上这句话是 notes[] 里的一项,措辞改动
        // 只动文本不动 notes 的话,两边会漂。
        { "where-value-cross-type",      ["where", "ingredients[0].filter.thingDefs[0]", "--value", "Bloomstone", "--exact"] },
        { "where-value-cross-type-json", ["where", "ingredients[0].filter.thingDefs[0]", "--value", "Bloomstone", "--exact", "--json"] },
        // 同一句话的**沉默**那一侧:值只坐在这一条路径上,别的类型也没有。沉默此时是
        // 「真的没有别处」,不是「没算」—— 这一份在,放开 def_type 之后那句话才不会变成
        // 每查必出的背景噪声(恒定出现的文字会被当噪声过滤掉,与它说不说得对无关)。
        { "where-value-cross-type-none", ["where", "workerClass", "--value", "Verse.TestWorker", "--exact"] },
        // 上面那句话印出来的形状,**原样粘回来**要能跑。它印的是折叠形状
        // (`costList[].thingDef`),而 `[]` 的下标通配此前只在 --exact-path 下生效 ——
        // 不加旗时 `[]` 走字面匹配,恒空。两份摆一起:折叠形状当后缀用(完整路径),
        // 以及只给尾巴一段(部分路径)—— 后者 --exact-path 也救不了,它匹配的是整条。
        { "where-folded-path",        ["where", "costList[].thingDef", "Bloomstone"] },
        { "where-folded-path-tail",   ["where", "thingDefs[]", "Bloomstone"] },
        // 折叠形状是这个工具**自己印出去**的写法(Shape():`statBases[7].stat` →
        // `statBases[].stat`),而收路径的入口不止 where/values 这两个位置参数。
        // 下面五格钉的是「同一串 `[]` 在每个入口上都得当下标通配」——
        // 前四格各是一个吃 --path-contains 的命令(get 的行、get 筛空时那个「同类型
        // 别的 def 有没有」探针、fields 的类型侧、inherit 的兄弟计数,四条独立的
        // SQL 拼装),第五格是判据侧:命中之后「整段一次都没命中」那句话不许因为
        // `[]` 比不上 `[0]` 而说假。少钉哪一格,那一格就会在别人改对时留在原地。
        { "get-folded-path",          ["get", "Apparel_ShieldBelt", "--path-contains", "comps[].props"] },
        { "get-folded-path-whole",    ["get", "Apparel_ShieldBelt", "--path-contains", "statBases[].stat"] },
        { "fields-folded-path",       ["fields", "ThingDef", "--path-contains", "comps[].props"] },
        // 路径轴这一轮(2026-09-09)要改的三处,先各钉住现状:
        //
        //   1. `--path` 这个词在 get / inherit 上是 --path-contains 的别名,在 fields 上
        //      什么都不是(实测 15 次被拒,15 条意图全是 --path-contains),而在 docs 上
        //      是 --out 的别名 —— 一个词三种命运。docs 那一头由参数参考的闸盯着。
        //   2. `--exact-path` 只有 where / values 认。而 get / fields 印的
        //      「inside a longer name: N」自己承认这一句给不出出路,因为出路那个词
        //      在这一族不存在。
        //   3. 多段路径的后缀是纯文本,不在段边界上对齐(第三格);单段那一支走 leaf
        //      列等值,本来就是对齐的(第四格) —— 同一个缺省下两套判据。
        { "usage-fields-path",        ["fields", "ThingDef", "--path", "comps"] },
        { "usage-get-exact-path",     ["get", "Apparel_ShieldBelt", "--exact-path", "statBases[].stat"] },
        { "usage-fields-exact-path",  ["fields", "ThingDef", "--exact-path", "graphicData.texPath"] },
        { "usage-inherit-exact-path", ["inherit", "Bullet_Revolver", "--exact-path", "projectile.damageAmountBase"] },
        { "get-path-none-whole",      ["get", "Firefoam", "--path-contains", "raphicData"] },
        { "usage-path-both-dials",    ["get", "Apparel_ShieldBelt", "--path-contains", "comps",
                                       "--exact-path", "comps[0].compClass"] },
        { "where-suffix-crosses-segment",  ["where", "graphicData.texPath"] },
        { "values-suffix-crosses-segment", ["values", "graphicData.texPath"] },
        { "where-suffix-single-segment",   ["where", "texPath"] },
        // 路径轴的第二个槽(2026-09-09,续)。where / values 的路径轴只有位置参数一个槽,
        // 语义写死成「后缀 / 整条」二选一,而实测 11 次伸手要的是两个**正交**的条件:
        // 叶子是什么、上面某处有什么。八个真实案例里六个的那一段祖先**不紧邻叶子**,
        // 且形状数不止一种(thingDefs ∧ filter 在 baseline 上是 13 种),多段后缀写不出来。
        // 第三格是那个边角:没有位置参数时 --exact-path 无物可钉,现在静默无效。
        { "usage-where-path-contains",   ["where", "thingDefs", "Bloomstone", "--path-contains", "filter"] },
        { "usage-values-path-contains",  ["values", "thingDefs", "--path-contains", "filter"] },
        { "usage-where-exact-path-alone", ["where", "--value", "Bloomstone", "--exact-path"] },
        // inherit 那格换库:默认那份里进了继承层的 def 一个带下标路径都没有,
        // 而 ChildGun(costList[0].*,parent 是 BaseGun)只在 presence 里。
        { "inherit-folded-path",      ["inherit", "ChildGun", "--path-contains", "costList[].count",
                                       Fixture.PresenceArg] },
        // 反向的洞:读者写**真下标**时,under 那句拿 Shape() 的结果(`statBases[]`)
        // 去比,而带下标的查询词按字面比 —— 于是 `statBases[0]` 永远判不出 under,
        // 印的是「none of those is under …」。三格摆一起,三种写法得给同一个答案。
        { "where-dotted-tail-is-value-indexed", ["where", "statBases[0].MarketValue"] },
        { "where-dotted-tail-is-value-folded",  ["where", "statBases[].MarketValue"] },
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
        // 点分路径落空的三档。三者此前印的是同一句话(只有那个名字不同),而它们要的
        // 下一步互不相同 —— 会话语料里 166 次「路径不存在」有 64 次是前两档。
        //   statBases.stat        路径对、只是少写了下标(索引里是 statBases[0].stat)
        //   statBases.MarketValue 末段不是字段而是取值,它坐在 statBases[].stat 上
        //   statBases.zzznope     两条都不成立 —— 这一档的措辞必须保持不动,
        //                         否则前两档的新话就没有可对照的「真没有」
        { "where-dotted-missing-index", ["where", "statBases.stat"] },
        { "where-dotted-tail-is-value", ["where", "statBases.MarketValue"] },
        { "where-dotted-really-absent", ["where", "statBases.zzznope"] },
        // 少写下标那一档带上值:救回来的是一张真表,不是一句提示 —— 表在场时那句改写
        // 必须照说(有结果那档最贵:零还会让人再看一眼,一张表不会)。
        { "where-dotted-missing-index-value", ["where", "statBases.stat", "MarketValue"] },
        // --exact-path 不进这条救援:调用方点名了「整条路径就长这样」,替他改写等于
        // 把一个明确的否定答案换成另一个问题的肯定答案。
        { "where-dotted-missing-index-exact", ["where", "statBases.stat", "--exact-path"] },
        // 同一个缺陷在 values 上逐字同形 —— 它也按后缀匹配、也拿点分路径当参数。
        // 一条承诺得对每条到达空的路径成立,只修 where 那条等于把另一半留在原地。
        { "values-dotted-missing-index", ["values", "statBases.stat"] },
        { "values-dotted-really-absent", ["values", "statBases.zzznope"] },
        // 翻过头那条路径 return 得早,而改写这件事对它一样成立 —— 一条承诺得对
        // 每条到达输出的路径成立,而这条正是最容易漏的那条(它连表都没有)。
        { "where-dotted-missing-index-past-end", ["where", "statBases.stat", "--offset", "9"] },
        { "values-dotted-missing-index-past-end", ["values", "statBases.stat", "--offset", "9"] },
        // 下标落在末尾的那一形(标量列表)。语料里最常失败的那条路径
        // stuffProps.categories 正是它 —— 中间那处放行救不了它。
        { "where-dotted-trailing-index", ["where", "stuffProps.categories"] },
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
        // 几个名字一次给。守的是尾部那两段按**名字**说话:「几个节点答应同一个名字」不是
        // 「这次印了几块」,而与 get 的计数差额也是一个名字一份。
        { "inherit-multi",         ["inherit", "BaseBullet", "Bullet_Revolver"] },
        { "inherit-multi-json",    ["inherit", "BaseBullet", "Bullet_Revolver", "--json"] },
        { "inherit-multi-missing", ["inherit", "BaseBullet", "NoSuchNode"] },
        { "inherit-multi-all-missing", ["inherit", "NoSuchNode", "NoSuchNodeEither"] },
        { "get-xml-node-only",     ["get", "BaseBullet"] },
        { "list-limited",          ["list", "ThingDef", "--limit", "2"] },
        // class 那一列同质时不印,而 JSON 里照样得有值 —— 文本面看不出这件事,
        // 上一行那份基线对它一个字都不响。两份:同质(列不印)与显式点了 class 的那次
        // (用户敲的就是它,回答里更不能是 null)。
        { "list-limited-json",     ["list", "ThingDef", "--limit", "2", "--json"] },
        { "list-classed-json",     ["list", "ThingDef", "--own-class", "Verse.ThingDef", "--limit", "2", "--json"] },
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
        // 几个 def 类型一次问。守的是:每个类型各出一句带类型名的计数、行并进同一张表且
        // def_type 在末列、class 那一列按并集出(一个类型异构就印,同质那几个照填真值)。
        { "list-multi",            ["list", "ThingDef", "AlloyPartDef"] },
        { "list-multi-json",       ["list", "ThingDef", "AlloyPartDef", "--json"] },
        { "list-multi-paged",      ["list", "ThingDef", "AlloyPartDef", "--limit", "2"] },
        // 一个类型不存在:其余照印、退出码 0。全都不存在才 1。
        { "list-multi-missing",    ["list", "ThingDef", "NoSuchTypeXYZ"] },
        { "list-multi-all-missing", ["list", "NoSuchTypeXYZ", "NoSuchTypeABC"] },
        { "fields-filtered",       ["fields", "ThingDef", "--path-contains", "comps"] },
        // 几个类型一次问。守三件事:每个类型各有一句带名字的计数(不带名字两句读起来
        // 像同一个类型说了两遍)、行并进同一张表且 def_type 在末列(单类型调用里它折进
        // 表头,多类型调用里它印出来)、`completeness` 只发一块(具名块发两次会撞键)。
        { "fields-multi",          ["fields", "ThingDef", "AlloyPartDef"] },
        { "fields-multi-json",     ["fields", "ThingDef", "AlloyPartDef", "--json"] },
        // --limit / --offset 按类型各算各的,不是并起来切一刀。
        { "fields-multi-paged",    ["fields", "ThingDef", "AlloyPartDef", "--limit", "2"] },
        // 一个类型不存在:其余照印、退出码 0。全都不存在才 1。
        { "fields-multi-missing",  ["fields", "ThingDef", "NoSuchTypeXYZ"] },
        { "fields-multi-all-missing", ["fields", "NoSuchTypeXYZ", "NoSuchTypeABC"] },
        { "values-coverage",       ["values", "compClass"] },
        // 几条路径一次问。守的是:每条各出一句带路径名的计数、`field` 那一格按路径各摆
        // 一份(单条路径也是数组)、行并进同一张表且 field_path 在末列、`completeness`
        // 只发一块。
        { "values-multi",          ["values", "compClass", "thingClass"] },
        { "values-multi-json",     ["values", "compClass", "thingClass", "--json"] },
        // --limit 按路径各算各的,不是并起来切一刀。
        { "values-multi-paged",    ["values", "compClass", "thingClass", "--limit", "1"] },
        // 一条路径查空:其余照印、退出码 0。全都查空才 1。
        { "values-multi-missing",  ["values", "compClass", "zzznotafield"] },
        { "values-multi-all-missing", ["values", "zzznotafield", "zzzalsonotafield"] },
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
        // 同一个词在别的命令上是选项、在这条上是位置参数,而「这里怎么写」是算得出来的
        // —— 连值一起填好。这一格钉的是那条**算法**,不是 --field 这个词的归属:它在
        // read 侧早已删掉(Docs/12 的跨命令碰撞),在 get / inherit 侧也随 2026-09-09
        // 那批零调用别名一起删了,而这句话照样成立。
        { "usage-field-is-positional", ["where", "--field", "compClass"] },
        // 值给了两遍且不一样。位置参数与 --value 说的是同一件事,挑一个跑下去的话
        // 另一个被丢了在输出里看不出来。
        { "usage-value-twice",     ["where", "compClass", "RimWorld.CompShield", "--value", "Other"] },
        // ── 选项面的一轮改动,先钉住改之前长什么样(2026-09-09) ────────────────
        // 依据是真实调用里逐字数出来的伸手写法,产地 tools/scan-flag-rewrite.py 与
        // tools/scan-alias-spelling.py。每一格都是**这一轮要翻的那一格**,不是回归护栏:
        // 改完之后它们都会变,读 diff 就是验收。
        //
        // 伸手写 --values 的有 15 次 / 13 会话,实参给的全是字段路径,而紧接着的重写
        // 十之八九是 --path-contains 同一个词。
        { "usage-get-values",      ["get", "Apparel_ShieldBelt", "--values", "statBases"] },
        // 反向:--field 是声明里挂着的别名,而 get / inherit / fields 三条命令上逐字
        // 零次。这一格钉的是「今天它还通」。
        { "usage-get-field-alias", ["get", "Apparel_ShieldBelt", "--field", "statBases"] },
        // read 上写行区间的第一直觉:64 + 64 次 / 49 份会话,--start 与 --end 完全成对。
        { "usage-read-start-end",  ["read", "vanilla/Verse/Outline.cs", "--start", "3", "--end", "6"] },
        // 与上一格同批:--lines 在场时再给 --start,两种说法指同一件事。
        { "usage-read-lines-and-start", ["read", "vanilla/Verse/Outline.cs", "--lines", "1-3", "--start", "5"] },
        // --start 今天是 --offset 的别名(search/where/list/values/fields 五条命令),
        // 而这个意思在全部真实调用里**一次都没被用过**。
        { "usage-values-start-as-offset", ["values", "statBases.stat", "--start", "1"] },
        // code-search 的位置参数就是正则,于是 29 次 / 26 会话伸手写 --regex 去断言它。
        { "usage-code-search-regex", ["code-search", "public", "--regex"] },
        // ── `all` 这个取值的三条路 ────────────────────────────────────────────
        // 此前一道闸都没有。`--limit all` 在 d155104(2026-09-05)已经是用法错误,
        // 而它旧代占过全部调用的 65.6%(16298/24862);`--lines all` 还通,与不给
        // `--lines`、与 `--lines 1` 三者输出逐字节相同。这三格钉的是各自今天的原话,
        // 改完读 diff 就是验收。产地 tools/scan-all-token.py 与 tools/scan-all-rewrite.py。
        { "usage-limit-all",       ["get", "Apparel_ShieldBelt", "--limit", "all"] },
        { "usage-lines-all",       ["read", "vanilla/Verse/Outline.cs", "--lines", "all"] },
        { "usage-max-per-file-all", ["code-search", "public", "--max-per-file", "all"] },
        // ── def 类型打头 ──────────────────────────────────────────────────────
        // `list <defType>` 与 `fields <defType>` 把类型放在位置上,而 get / values /
        // search / where 把类型放在 --type 上。消费侧会把前者外推到后者,写成
        // `<命令> <SomethingDef> <真正的参数>` —— 2026-08-31 在 9282 次真实调用里量到
        // get 122 / values 57 / where 82 次,占各自调用量的 2%–7.6%。
        //
        // 三条命令的落点不同,所以基线一档一份:
        { "deftype-lead-get",      ["get", "ThingDef", "Apparel_ShieldBelt"] },
        { "deftype-lead-values",   ["values", "ThingDef", "thingClass"] },
        { "deftype-lead-search",   ["search", "ThingDef", "shield"] },
        // 首位长得像类型、而快照里没有这个类型。静态判据(以 Def 结尾)认不出这件事,
        // 于是这一份钉的是「认错之后说的话仍然诚实」—— 真实语料里 Mincho_ThingDef
        // 这种以 Def 结尾的 def **名**是存在的。
        { "deftype-lead-get-unknown-type", ["get", "NoSuchThingDef", "Apparel_ShieldBelt"] },
        // where 的两档差别是这次改动的整个理由,必须摆在一起看。
        //
        // 超额那档撞墙,自证:
        { "deftype-lead-where-over", ["where", "ThingDef", "costList[0].thingDef", "Bloomstone"] },
        // 不超额那档**不撞墙**。where 只有两个位置参数,`where ThingDef Bloomstone` 正好填满,
        // 而路径按后缀匹配 —— `ThingDef` 命中了真实存在的 `costList[0].thingDef`。于是问
        // 「哪些 ThingDef 用了 Bloomstone」的人拿到一张语法正常、路径存在、看着像答案的表,
        // 答的却是「哪些 def 的某个 thingDef 字段等于 Bloomstone」。54/82 落在这一档。
        { "deftype-lead-where-silent", ["where", "ThingDef", "Bloomstone"] },
        // 同一档的空结果面:后缀匹配够不着时给的是一个**可信的零**,而零正是「没有」的判据。
        { "deftype-lead-where-silent-empty", ["where", "HediffDef", "Anesthetic"] },
        // 反向:小写同名的合法查询。`thingDef` 是真字段,路径匹配 NOCASE,两者从入参上
        // 区分不开 —— 所以 where 这一档只许出声,不许重解释。这份基线钉的是「没被抢走」。
        { "where-path-named-like-type", ["where", "thingDef", "Bloomstone"] },
        // inherit 走的是 XML 节点层,没有类型这个过滤面。只说破,不新增能力。
        { "deftype-lead-inherit",  ["inherit", "ThingDef", "Bullet_Revolver"] },
        // 显式给过 --type 时重解释让位 —— 否则「按位置写的那个」会悄悄盖掉「明写的那个」,
        // 而两者不一致正是最该出声的时候。
        { "deftype-lead-type-given", ["get", "ThingDef", "Apparel_ShieldBelt", "--type", "HediffDef"] },
        // list 的第一个位置参数本来就是类型。这里多出来的那个词不是「放错格的类型」,
        // 指路语因此不许发 —— 它会把人往一条不存在的写法上带。
        { "deftype-lead-list-extra", ["list", "ThingDef", "Bloomstone"] },
        // ── where --type ──────────────────────────────────────────────────────
        // 全套查询命令里只有 where 没有类型面,而它恰好是误形最贵的那一条。
        { "where-type",            ["where", "thingDef", "Bloomstone", "--type", "AlloyPartDef"] },
        // 被 --type 筛空。上一份不带 --type 时有结果,这一份只差一个类型 ——
        // 「过滤掉了」说成「没有」是这套代码最贵的那种错,--scope 那侧早有一条分流接着,
        // 类型这侧必须有对称的一条。
        { "where-type-filtered-empty", ["where", "thingDef", "Bloomstone", "--type", "MoltenRecipeDef"] },
        // 快照里根本没有这个类型 —— 与「有这个类型但没这个值」是两件事。
        { "where-type-unknown",    ["where", "thingDef", "Bloomstone", "--type", "NoSuchDef"] },
        // 不给路径那条分支出的是 paths 表,不是 matches。一个标着 Narrows 的选项
        // 在一半问法上不生效,比它不存在更贵。
        { "where-type-value-only", ["where", "--value", "Bloomstone", "--type", "AlloyPartDef"] },
        // 夹具恒追加 --db/--config,而总览那条分支要求 argv 恰好一个词。
        { "help-overview",         ["--help"] },
        // `--help <command>` 不接,但那个词不许被默默扔掉 —— 说清这一屏是什么,
        // 并把该打的那一条(`<command> --help`)原样给出来。
        { "help-with-command",     ["--help", "search"] },
        { "help-get",              ["get", "--help"] },
        // Remarks 里那段 patch 口径与 identity 块的 patch_ops 说的是同一件事,而 r14 抓到
        // 一个受测者读了输出的新句、再引这里的旧句把它降格成「通用免责措辞」驳回。
        { "help-inherit",          ["inherit", "--help"] },
        // read 的选项面这一轮要动(--start / --end),整页钉住。
        { "help-read",             ["read", "--help"] },
        // where 的 --limit / --offset 数的是**行**((def, 路径)对),而模板的 what 一度传的是
        // "defs" —— 这条命令是全套里唯一一行不等于一个 def 的,那个词在别处都是真话。
        { "help-where",            ["where", "--help"] },
        { "help-code-search",      ["code-search", "--help"] },
        { "help-sources-sync",     ["sources", "sync", "--help"] },
        // snapshot rename 的契约全在 Remarks 与这几条报错上:三处缺席要说「在哪找过」,
        // 撞名要说清是哪一处,旧名不存在要说「三处都没有」而不是只报库找不到。
        { "help-snapshot-rename",  ["snapshot", "rename", "--help"] },
        { "snapshot-rename-missing", ["snapshot", "rename", "nosuchname", "newname"] },
        { "snapshot-rename-collision-db", ["snapshot", "rename", "fixture", "other"] },
        { "snapshot-rename-collision-rml", ["snapshot", "rename", "fixture", "fixture-current"] },
        { "snapshot-rename-same",  ["snapshot", "rename", "fixture", "fixture"] },
        { "snapshot-rename-no-args", ["snapshot", "rename"] },
        { "snapshot-rename-one-arg", ["snapshot", "rename", "fixture"] },
        // 没配 decompiled_dir 时说的那句话。反编译树是**唯一**不在快照里的数据源,
        // 这条路必然被走到,输出必须说清该往哪补一行配置。
        // 这一条要的是**没有**配置,所以自带 --config 覆盖掉 Fixture.Run 默认追加的那份。
        { "sources-not-configured", ["sources", "list", "--config", "no-such-config.toml"] },
        // 以下每条盯 code-search 的一件事:
        { "code-search-hit",       ["code-search", ": ThingComp"] },
        // 上下文窗口重叠:-C 1 打在连着命中的五行上,窗口要合并。
        { "code-search-context",   ["code-search", "public", "--file-glob", "ThingComp.cs", "-C", "1"] },
        // 不对称窗口。纯 N 必须与上面那份逐字节相同,所以新形态自己立闸:0-2 是只往下,
        // 2+0 是只往上,两种分隔符都要钉住,且第一行自报实际窗口。
        { "code-search-context-after", ["code-search", "public", "--file-glob", "CompShield.cs", "--context", "0-2"] },
        { "code-search-context-before", ["code-search", "public", "--file-glob", "CompShield.cs", "--context", "2+0"] },
        // 写错时说清接受什么形式,不是笼统的 invalid argument。
        { "code-search-context-bad", ["code-search", "public", "--context", "nope"] },
        // 模式本身的两条报错路径。此前一道闸都没有 —— 2026-09-09 位置参数从 <pattern>
        // 改名 <regex> 时这三句(这两句加上超时那句)的措辞跟着改,而测试一格都没红。
        // 超时那句进不了基线:它要一个真会跑超时的模式。
        { "code-search-bad-regex", ["code-search", "foo("] },
        { "code-search-html-escaped", ["code-search", "&lt;defName&gt;"] },
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
        // 打空的名字在另一条命令上真有意义时,拒绝消息有两句话可说。先说的必须是
        // **这条命令**叫它什么 —— 只说「别处认它」的话,一次改名就把用得最多的那个词
        // 指向了最不相干的命令,而两种消息都以 exit 2 收场,同形。
        //
        // 这一格原本钉的是 `get --path`。那个名字后来收进了 --path-contains 的别名
        // (真实调用里打了 112 次全打空),于是改指到 --all;2026-09-09 撤掉 --all-fields
        // 与 `sources sync --all` 两个零用量别名之后,--all 两头的属性一起没了,再换到
        // --path-glob:它在 get 上的近似候选是 --path-contains、在 code-search 上是真别名。
        // **不是把这一格删了** —— 删了就只剩一句 "Did you mean" 的常见形状在钉,
        // 而两句话的排序纪律再没人管。
        //
        // 换车时才看清那两个别名不是纯死重:`--all` 一次都没被敲过(逐字 0),但它在
        // 声明里挂着,`get --all` 才拿得到 "Did you mean --defaults"。撤掉之后那句退成
        // 完整选项清单 —— 代价落在提示上,不落在任何被记录过的调用上。
        { "get-retired-path",      ["get", "Apparel_ShieldBelt", "--path-glob", "comps"] },
        // 0.8.0 那一档:译文的 path 归一到字段表文法,游戏自己认的那一串落在
        // key 列。三档各一行:把手式、下标式、把手已过期配不上任何槽位的。
        // 最后那一档必须自证 —— 游戏那边同样注入不上,而它在表上与一条正常译文同形。
        { "get-injkey-forms",      ["get", "ObservedLayingCorpse", Fixture.InjKeyArg] },
        // 归一过之后字段表的路径贴回来真能筛到译文 —— 这正是归一要买的东西。
        { "get-injkey-bracket",    ["get", "ObservedLayingCorpse", "--path", "stages[0].label",
                                    Fixture.InjKeyArg] },
        // 第三种写法:语言文件里的点下标式。两列都不存着这一串(path 是方括号、key 是把手),
        // 它只能靠过滤前那道归一活着 —— 没有这道闸,它筛空,而筛空与「没有译文」同形。
        { "get-injkey-dotindex",   ["get", "ObservedLayingCorpse", "--path", "stages.0.label",
                                    Fixture.InjKeyArg] },
        // 把手式:译文表中,字段表零 —— 口径承诺的正是这个不对称。
        { "get-injkey-handle",     ["get", "ObservedLayingCorpse", "--path",
                                    "stages.observed_corpse.label", Fixture.InjKeyArg] },
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
        // 可表达的形式。两份基线:整层第一页(2026-09-05 起要显式 --limit 才分页),以及这条意图本身。
        { "keyed-all",             ["keyed", "--limit", "25"] },
        { "keyed-all-placeholders", ["keyed", "--empty-translation"] },
        // 枚举走的是分页文法而不是精确 key 那一路,所以翻过头这条分支也得有。
        { "keyed-all-past-end",    ["keyed", "--empty-translation", "--offset", "9"] },
        // --empty-translation 是收窄参数,计数要念回它划的那道线 —— 不念的话「1 key.」会被
        // 读成「filler 一共命中一条」,而真值是 2100 条里有一条占位。
        { "keyed-text-placeholders", ["keyed", "filler", "--empty-translation"] },
        // 零结果的两种成因:代码里有这个字面量而语言文件里没有(死 key),
        // 以及问的其实是个 def 名 —— 后者该被指回 get/search,而不是报「没有」。
        // 几条查询一次问。守的是:每条各出一句带查询词的计数(名词还可能一句一个 ——
        // 精确命中数的是「几条来源」,按文案搜数的是「几个 key」),行并进同一张表且
        // query 在末列,表下方那两句只说一遍。
        { "keyed-multi",           ["keyed", "CannotUseNoPower", "没有电力"] },
        { "keyed-multi-json",      ["keyed", "CannotUseNoPower", "没有电力", "--json"] },
        // 一条查空:其余照印、退出码 0。全都查空才 1。
        { "keyed-multi-missing",   ["keyed", "CannotUseNoPower", "NoSuchUiKey"] },
        { "keyed-multi-all-missing", ["keyed", "NoSuchUiKey", "NoSuchUiKeyEither"] },
        { "keyed-miss",            ["keyed", "NoSuchUiKey"] },
        { "keyed-miss-def",        ["keyed", "Apparel_ShieldBelt"] },
        // 经济面。这一层的每一种「空」都有自己的成因,而它们印出来同形 ——
        // 一份基线守一种成因,合并任何两份都会让区别在字节上消失。
        { "economy-all",           ["economy"] },
        // JSON 面两条路各一份。这一层此前**九份基线全是文本面**,于是整层少七个键这件事
        // 在字节闸上一声不响 —— 文本面本来就只印十五列,少的正好是没印的那些。
        // 两份摆一起,守的是「键集不随命中方式变」;文本列可以不同,键不许。
        { "economy-all-json",      ["economy", "--json"] },
        { "economy-one-json",      ["economy", "TestModGun", "--json"] },
        // 齐全的一行 + 两张子表。两个配方能产它,于是推算价有加载顺序依赖;
        // 第二个还是自引用的 —— 两句说破都在这一份里。
        { "economy-one",           ["economy", "TestModGun"] },
        // chainEndShare 到顶:profit 只是「市场价减去你自己填的另外几个数」。
        // 引用的那个数必须与表里印出来的逐字一致(是 1 不是 1.00)。
        { "economy-chain-end",     ["economy", "Apparel_ShieldBelt"] },
        // calcState=ok 而推算价是 0 —— **算不出**,不是「值零」。
        { "economy-zero-calc",     ["economy", "Meat_Muffalo"] },
        // 收窄参数在场时计数要念回它划的那道线。
        { "economy-scoped",        ["economy", "--scope", "test.mod"] },
        { "economy-sort-profit",   ["economy", "--sort", "profit-rate"] },
        // 零结果的两种成因:筛干净了(整层非空),与问的是个不在这一层里的 def。
        { "economy-filtered-empty", ["economy", "--calc-state", "not_producible", "--category", "Building"] },
        { "economy-miss-def",      ["economy", "Bullet_Revolver"] },
        { "economy-miss",          ["economy", "NoSuchThingAtAll"] },
        // 几个名字。三张表并起来而不是各出一块 —— 守的是 costChain / recipes 的 product 列
        // 在场且逐行对得上,以及配方那两句说破按**每个物**判(三个物各一条配方加起来是 3,
        // 而那不是加载顺序依赖)。
        { "economy-multi",         ["economy", "TestModGun", "Apparel_ShieldBelt", "Meat_Muffalo"] },
        { "economy-multi-json",    ["economy", "TestModGun", "Apparel_ShieldBelt", "--json"] },
        // 一部分落空:其余照印、退出码 0,落空的只留一句名单 —— 单名那两段详细说破
        // 在这里会按名字重复好几遍,而读的人要的是「哪几个没有」。
        { "economy-multi-missing", ["economy", "TestModGun", "NoSuchThingAtAll"] },
        // 全部落空才 1。这一份与 economy-miss 摆一起:单名那条路的两段说破一字未改。
        { "economy-multi-all-missing", ["economy", "NoSuchThingAtAll", "NoSuchThingEither"] },
        // 同一个名字给两遍只查一遍 —— 两份行逐字相同,第二份会被读成另一个同名的物。
        { "economy-multi-repeat",  ["economy", "TestModGun", "testmodgun"] },
        // 四态里另外三种(没量过 / 跳过了 / 签名对不上)不在这里:它们要各自一份库,
        // 而库路径是绝对的、含机器名,进不了回显行。守它们的是 GrammarTests 里
        // 「经济面四态各说各的」那条。
        // read 的两处错法:定位到哪个文件、以及配平括号找到的是不是那一段。
        // 轮廓:注释/字符串/字符字面量里的括号不许算数,方法体里的 if 不许变成成员,
        // 带初值的字段不许被初值里的括号认成方法。
        { "read-outline",          ["read", "Outline.cs", "--source", "vanilla", "--outline"] },
        { "read-outline-truncated", ["read", "Outline.cs", "--source", "vanilla", "--outline", "--limit", "2"] },
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
        // 命名空间限定名是 get/where 自己印出的形态,按路径解必然落空。末段走裸名回退,
        // 必须自报;撞车仍只列不选;真没有时仍指向 code-search,不许说成别的意思。
        { "read-typename",         ["read", "RimWorld.CompShield"] },
        { "read-typename-ambiguous", ["read", "RimWorld.Outline"] },
        { "read-typename-missing", ["read", "RimWorld.NoSuchType"] },
        // 两种读法同时传:不排优先级,当场说破这是两件事。
        { "read-two-modes",        ["read", "Outline.cs", "--lines", "1-3", "--member", "Shared"] },
        // 几个文件一次读。三种读法各一份 —— 裸行那份守的是「第二个文件起有一条线与标题行」
        // (行号从 1 重开,不划线两段正文在文本面粘成一片),--member 那份守的是同一个名字
        // 在几个文件里各自命中,--outline 那份守的是 file 一列在多文件下印出来、单文件下折叠。
        { "read-multi-lines",      ["read", "vanilla/Verse/Outline.cs", "vanilla/Verse/Widgets.cs", "--lines", "1-3"] },
        { "read-multi-member",     ["read", "vanilla/Verse/Outline.cs", "vanilla/Verse/Tuples.cs", "--member", "Shared"] },
        { "read-multi-outline",    ["read", "vanilla/Verse/Outline.cs", "vanilla/Verse/Pair.cs", "--outline"] },
        { "read-multi-json",       ["read", "vanilla/Verse/Outline.cs", "vanilla/Verse/Widgets.cs", "--lines", "1-3", "--json"] },
        // 一个文件解析不到:其余照印、退出码 0。全都解析不到才 1。
        { "read-multi-missing",    ["read", "vanilla/Verse/Outline.cs", "NoSuchFile.cs", "--lines", "1-3"] },
        { "read-multi-all-missing", ["read", "NoSuchFile.cs", "NoSuchFileEither.cs"] },
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
    /// 插值漏了 $ 时 C# 把 {plan.To} 当普通字符原样印出来,编译不报错;而断言常常只钉住
    /// 句子的前半截,漏的又总在后半截,于是闸绿着把花括号印给了用户。基线是逐字节的,
    /// 残留一定落在里面 —— 但只覆盖有基线的那些路径,进不了基线的输出这条闸看不见。
    /// </summary>
    [Fact]
    public void 基线里没有没插上值的占位符()
    {
        if (!Directory.Exists(SnapshotDir)) return;
        var files = Directory.GetFiles(SnapshotDir, "*.txt");
        Assert.True(files.Length > 0, $"一个基线都没读到,这条闸等于没跑:{SnapshotDir}");

        // C# 插值表达式的形状:{标识符.成员}。JSON 的 {} 与 {"key" 不在此列。
        var leak = new System.Text.RegularExpressions.Regex(
            @"\{[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\}");
        var offenders = new List<string>();
        foreach (var f in files)
            foreach (System.Text.RegularExpressions.Match m in leak.Matches(File.ReadAllText(f)))
                offenders.Add($"{Path.GetFileName(f)} → {m.Value}");

        Assert.True(offenders.Count == 0,
            "基线里有没插上值的占位符,说明那句话漏了 $:\n  " + string.Join("\n  ", offenders));
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
        // Pooling=False:成因见 SnapshotDb.Open。这里原本靠 ClearAllPools() 把文件放开,
        // 而那是进程级的 —— 并行跑的别的用例正在用的连接会被它一起处置掉。
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // keyed_fts 是 contentless 的,DELETE 不认 —— 清空要走 fts5 自己那条命令。
            cmd.CommandText = "INSERT INTO keyed_fts(keyed_fts) VALUES('delete-all'); DELETE FROM keyed;";
            cmd.ExecuteNonQuery();
        }

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
        // Pooling=False:同上一条,成因见 SnapshotDb.Open。
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE keyed SET placeholder = 0;";
            cmd.ExecuteNonQuery();
        }

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
