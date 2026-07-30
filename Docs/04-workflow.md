# 04 · 工作方式与迁移路径

## 约定(沿 master 惯例)

- **先立字节级闸再改代码**:动任何输出之前,先给该输出建快照基线,改动才有对照物。
  **适用条件按 06 收窄(2026-07-30)**:只有**复用上游实际输出**的环节才固化「带着缺陷的
  现状」;全新写的输出直接按文法闸建**绿**基线,没有「先固化旧缺陷」这一步。
  实测适用面趋零 —— 99 份基线一份不例外,全是本仓 CLI 自己的输出。
- **产地唯一**:一份判据一个产地。文档层同理 —— 本 Docs 是决定与调查的产地,
  CLAUDE.local.md 只放指针与机器事实,不复制内容。
- **单线就在本分支直接提交**,不为一条线专门开 feature 分支(那只多一次合并)。这是默认口径
  不是禁令 —— 存在并行工作时用 worktree 隔离,worktree 里带自己的分支是正常做法。要避免的
  只有一件事:在用户的主工作目录里切分支。(master 的既有约定,同口径)

## 建议顺序(每步可独立停,序号 ≠ 强依赖)

前提说明:此顺序以上游可跑的管线为起步脚手架,但这只是施工顺序,不是站队 ——
每步落地时该点用谁的做法(上游代码 / 本地设计 / 全新写)仍按 00 的逐点择优裁,
择优判成全新写时整块替掉上游实现也在此顺序之内。

1. ~~上游 CLI 现有输出建字节级基线(固化现状,含缺陷)~~ —— **已裁定跳过**,适用面经再评估实际趋零,成因见 06「与 04 顺序的衔接」第 1 条(体例同下面划掉的 `update`)
2. 截断自证 + 三态文法进 CLI(修 02-1)
3. 噪声清单收产地(修 02-2;导出侧修对,清单共享)
4. db 加 meta 表 + CLI 过期自证(修 02-4;导出/查询两侧同步)
5. 导出原子化(修 02-6)
6. 移植文法/措辞测试(01 清单里的测试类,配合 2 的输出改造落地)。**基线枚举同步接上
   文法检查**(01「字节级基线方法」那条的缝):这道缝与步骤 1 跳不跳无关 —— 任何一份
   只被逐字节比对、没被文法检查扫过的基线都是永远绿的死角,而「基线是绿的」恰恰会被
   读成「这条输出合格」。落点是 `GateTests` 里把基线逐行喂回文法检查那一段
7. scope 设计移植(`--mod` 单值 → 组 / 排除语法;数据侧对应 mod 维度)
8. 模糊查找体验(修 02-7,FuzzyMatcher 或 FTS 自动前缀)
9. (可选)ApplyPatches 拦截 dump + patch 溯源(03 拦截点;独立于 1-8)

## 盲测重搭

master 方法论:观察 LLM 调用方对输出的误用 → 修措辞/文法,而非修文档。MCP 形态下
误用直接发生在工具调用里,可观察;CLI 形态链路多一跳:模型读 skill → 拼命令行 → 读 stdout。

要点:
- **skill 文档本身进入被测物**。上游 SKILL.md 教「Always prefix-search `shield*`」是
  把缺陷外包给用户的反例 —— 盲测里若发现 skill 在教绕路,修的是 CLI,不是 skill。
- 观察点设在「模型拿到任务 → 最终引用了什么答案」全链,记录格式沿 master 本地 docs
  的盲测记录惯例(逐轮编号、R 编号规则落账)。
- 未知 flag 的静默吞掉是 CLI 形态的新雷区(对应 master 的 ExtraAcceptedKeys 教训),
  第一轮盲测就该覆盖。
- **冷启动用 subagent workflow 模拟盲测**(用户提议,2026-07-29):subagent 天然真盲
  (不知设计内情);场景种子取自 07 的真实意图分布(含误用样本:转义 pattern、typo、
  XML 搜索旧习惯);每个场景 agent 只给任务+SKILL.md+CLI,收集全链轨迹归纳误用。
  定位 = 冷启动替代与输出改动后的回归工具;模拟方与设计者同源、措辞多样性覆盖不全,
  真实消费方会话仍是终审。
- **归纳阶段固定两个 agent,第二个专门逆着自评复核归因**(三轮起,双向:既查该退回工具的
  `my_own_mistake`,也查把自己疏忽记到工具头上的虚报,并单独扫 `useful: false` 里
  连摩擦都没记的沉默缺陷)。这一条不是可选项 —— 三轮它一个人赚回 4 条被自责藏起来的
  缺陷、2 条虚报、6 条沉默缺陷,外加「cost 被自责压低」这个二阶发现。
- **种子改由不知情代理从 episode 池里挖**(用户提议,2026-07-30):照靶子挑出来的种子
  形状全是「冲着某个命令表面去」,与不知情代理挑的**一条都不重合**。判据只给「难在信息
  形状上」「藏陷阱的优先」,不告诉它们靶子是什么。可执行的种子、开场词、schema 与前置闸
  全在 `04a-blindtest-seeds-r4.md`;episode 池是 `tools/blindtest-episodes.json`(252 条,`/tools/` 在 .gitignore 里,不进库但长期在磁盘上)。

### 第一轮结果(2026-07-29,10 场景 / 9 答出 / 1 死路)

方法有效,而且**贵的信号都不是「查不到」,是「查到了却读出错误结论」**。
十场里有五处被 agent 自己标成 `nearly_wrong_answer` —— 差一步就把错的答案交出去。
按归因分,`cli_output` 远多于 `cli_error_message` 与 `skill_doc`:输出的**形状**
比措辞更容易骗人。

落到 CLI 的九处(全部已修,见 fe46ee6):

1. **唯一的死路是数据侧**:导出器只绑 public 字段,私有的 `verbs` /
   `damageAmountBase` 整个不存在。口径见 06「实现阶段敲定的口径」。
2. `get` 没有字段投影 —— 295 个字段里找 statBases 只能 `--limit all` 再 grep 输出,
   而**管道会连同截断声明一起滤掉**,「被截了」于是变成「没有」。加 `--path`。
3. `values` 只给值不给产地。后缀匹配把语义不同的路径并成一张表,
   `values damageAmountBase` 报出「-1 / 37 defs」险些被当成「到处都是 -1」;
   覆盖率的分母原本要靠人手工加 110 行才敢下结论。补 matched_paths / def_types /
   defs_with_field 三行。
4. `find` 空结果指了条走不通的路。**skill 自己的示范例子**
   (`find compClass CompProperties_AmbientSound`)就是零结果 —— XML 写
   `CompProperties_X`,字段存的是解析后的 `CompX`。现在直接端出真实值域里的近似项
   并说破这条规律。
5. `search` 的值域不含 C# 类名,而错误消息把人指向 `code-search` —— 那条路找得到类,
   永远找不到用它的 def。
6. `code-search` 的补救建议不管用:`--source vanilla` 换不掉文件数上限(vanilla 自己
   就超)。加 `--max-files`;按确定顺序逐棵树扫并点名未读到的树(原本首屏全是 mod 代码,
   没有任何迹象表明 vanilla 还在后面);命中数与文件数分开报。
7. `--limit` 管不住译文表,`get X --limit 5` 吐八十行。
8. 过期声明的判据(见 06)。
9. 搜索排序把基础设施顶到玩家可见内容前面(见 06)。

一条方法论上的教训:**「路径用反斜杠」这类摩擦被十个 agent 里的七个报成
`my_own_mistake`,不该照单全收**。同一个坑七个人踩,那就不是七次个人失误,
是场景交待方式的缺陷 —— 下一轮种子里路径要给成可直接粘贴的形态。

### 第二轮结果(2026-07-30,11 场景 / 6 答出 / 2 死路 / 5 险些答错)

**种子换了产地**:第一轮的场景是照 07 的意图**分布**编的,这一轮是从 Vethara 会话
transcript 里**逐条抽出来的真实 episode**(252 个 episode 里按调用数排序取头部,连
用户原话与当时的调用序列一起带上),编号即 `epNN`。抽取脚本是一次性的,留在 scratchpad
未入库;每条种子的产地记在下表的「原型」列。这轮同时兼任**改版后的回归轮** ——
第一轮的九条修复逐条回测。

| 场景 | 原型(Vethara 真实调用) | 结局 |
|---|---|---|
| S1 世界地图 expanding 贴图枚举 | ep70 `search_regex "expandingIconTexture>World/…"` | 险些答错 |
| S2 CompProperties_CascadeOnDestroyed 挂在哪 | ep66 同名 XML grep | 答出 |
| S3 抽象父节点 BuildingNaturalBase | ep66 `inspect def:BuildingNaturalBase` | 死路 |
| S4 Milira 的 beamMoteDef | ep26 `search_regex beamMoteDef` | 死路 |
| S5 VoidNode 全量 + MonolithGleamingVoidNode | ep3 `inspect def:VoidNode` | 险些答错 |
| S6 「心灵迟钝」是不是一档 trait | ep156 用户原话 + `inspect PsychicSensitivity` | 答出 |
| S7 CreepJoinerAggressiveDef 全量 | ep215 `trace CreepJoinerAggressiveDef` | 险些答错 |
| S8 HealthCardUtility 伤病排序 | ep93 `read_code DrawHediffRow` | 答出 |
| S9 MapPortal 三问 | ep143/ep18 | 险些答错 |
| S10 带 HTML 转义的旧检索式 | 真实误用样本,必然零命中 | 险些答错 |
| S11 CarryDownedPawnToPortal(vanilla 里根本没有) | ep193,历史上是 Vethara 自建的 | 答出 |

两条死路当时都判成了**能力边界**而非缺陷:S3 撞上继承层整块不在快照里(见下 F11),
S4 撞上 Milira 不在本机快照覆盖里。**S3 那一条后来被推翻** —— 继承层已补上,
`inherit BuildingNaturalBase` 现在答得出。判成边界的东西要再验一次「是原理上的还是当下实现的」,
这一条就是拿盲测种子当反例的实证。

结论一句话:**第一轮最贵的信号是「输出让人读出错结论」,这一轮变成了
「输出让人读不出边界」** —— 工具在假装自己已经把话说全了。两条跨 agent 的头号问题
都是这个形状:完整集合一个字不打计数(4 个 agent 独立踩,S5 靠它二次确认了一个错答案),
以及 `--json` 同名 def 键碰撞静默丢数据(S7/S10 各差一步交出错答案,而输出自己
一边说「匹配到 1 个字段」一边给空数组)。

#### 回归:第一轮九条的现状

6 条 held / 1 条 partial / 2 条本轮未触发。

- **held**:私有字段绑定(S4 拿到了 `verbs[0].beamMoteDef`,1.6 里 `verbs` 是私有的)、
  `--path` 投影、`values` 的产地块(S3 的决定性一次:`find parent` 的「out of 49 values」
  险些被读成「父字段存在」,是 matched_paths 指出这 49 个来自 `ThingCategoryDef.parent`
  才拧回来)、`find` 空结果说破 `CompProperties_X → CompX`、`code-search` 的
  `--max-files`/点名未扫树、搜索排序。
- **partial —— `search` 空结果**:不再指向 `code-search` 了,但两条分支各自误诊。
  S3 的 `BuildingNaturalBase` 被判成「That looks like a class」推向三条必然空手的路
  (它其实是抽象 def 的 `Name`);S4 的 `Milira` 走非类名分支被指向 `types`,而真因是
  这个 mod 不在快照覆盖里,该指的是 `mods` / `snapshot status`。
- **not_exercised**:`--limit` 管住译文表(11 条轨迹没有一条用小 limit 压译文,
  抱怨的噪声都发生在默认 60 字段与 `--path` 路径上 —— 于是暴露的是另一件事,见 F6);
  过期声明(`snapshot status` 一律一致态,Staleness 分支从未触发 —— 暴露的是相邻的
  F8:一致态下也不说这次用的是哪个快照)。

#### 十八处发现(全部已修,除注明外)

跨 agent 的八条,按代价排序:

1. **F1 `--json` 撞名丢数据**。`def:` 的键三段(含 def_type),`fields:`/`translations:`
   只两段,同名跨 def_type 时后写的静默覆盖。修法不是把键补成三段(「键里拼名字」
   本来就没法安全解析),是把顶层改成恒定形状 `defs: [{def, fields, translations}, …]`,
   单 def 也是长度 1 的数组;`JsonRenderer` 同时改成键碰撞直接抛,不再无条件覆盖。
2. **F2 完整集合不打计数**。三态文法的「裸 N」这一态**从来没渲染出来过** ——
   `TruncationNotice` 在未截断时直接 return。而 SKILL.md 教的是「裸计数=完整集」,
   文档承诺的信号在实现里不存在。口径修订见 06。
3. **F3 `find` 零结果一句话覆盖三种互斥成因**,且带 value 的分支根本不查字段是否存在。
   S9 的 `find thingClass MapPortal` 报「out of 207 values」,真因是 MapPortal 是
   抽象基类、6 个 def 用的是它的 5 个子类。现在三种成因分流,并把 `values` 的产地块
   搬到零结果路径上。
4. **F4 `--type` 只挂在 `search` 上,被拒时建议 `--limit`**。四个 agent 独立敲了同一条
   并吃同一句报错。`--limit` 之所以被推荐,是因为它有别名 `top`,编辑距离恰好压线 ——
   别名现在不参与打分。另加「别的命令有、这条没有」的专用文案。
5. **F5 def 自身的 class 只在 `get` 的 identity 区露面**。`list CreepJoinerAggressiveDef`
   报「不存在」,而它真实存在、只是被并进 `CreepJoinerBaseDef` 桶
   (`AllDefTypesWithDatabases` 只产出没有非抽象 Def 祖先的类型)。加 `--class` 过滤、
   异构桶补 class 列、未命中时反查 class 并给出可用命令。
6. **F6 `--path` 只作用于字段表**,description 与整张译文表照打 —— 「精确地问」
   反而单位信息量最低。
7. **F7 两套「完整」互相打架**。导出截断只在 `get` 单 def 上发声,而 find/values/fields
   的计数同样以「已索引路径」为界。加 `snapshot truncated` 列出那批 def 以供交叉验证,
   受影响的命令带一条**限定到相关 def 类型**的尾注(不是常驻免责声明 —— 00 论据 3)。
8. **F18 `search` 结果表没有「命中位置」列**,label 空的行看起来像没查到东西。
   加 `matched_on`。S6 的 `search 心灵迟钝` 现在直接显示
   `degreeDatas.psychically_dull.label`,而这正是那题错答案的反证。

单 agent 的十条:F8 多快照时不说用的哪个 / F9 三级匹配「命中第一级就停」不可见
(`search VoidNode` 因此漏掉 `MonolithGleamingVoidNode`,22→23)/ F10 没有按值反查字段的
入口(猜错字段名会拿到一个看着很完整的假答案,加 `find --value`)/ **F11 继承层整块缺席**
/ F12 没有命令能回答「mod X 装了没」/ **F13 DecompilerServer 四条限制(不在本仓,记入 05)**
/ **F14 ToolSearch 载不回 decompiler 入口工具(修在参考页,给可粘贴的 select 行)**/
F15 `snapshot list` 把 `(active)` 塞进 name 单元格 / F16 `--path` 的过滤命中数被标成
`truncation` / F17 全局选项放命令前被当成命令拒绝。

**F11 当轮结成的是边界声明,不是数据修复**:反编译核实
`LoadedModManager.LoadAllActiveMods` 结尾调 `XmlInheritance.Clear()`,在
`StaticConstructorOnStartup` 之前 —— 导出时点上 ParentName 与抽象父节点**已经不存在了**。
于是删掉 identity 里那个恒为 null 的 `parent` 键(它在替不存在的数据作伪证),
改在 `get` 查无此 def 时无条件说明这条边界。

**后续(本轮)推翻了这条处置的后半段。**「运行时拿不到」是对的,「所以答不了」是错的:
`DirectXmlLoader.XmlAssetsInModFolder` 任何时点都能调,继承层已补成 `kind=xmlnode` 一层,
出口是 `inherit` 命令,那句无条件边界随之删除(改为命中具名节点时才点名说)。
这条错误的形状值得记:**把「当前实现没有」说成了「原理上没有」,而后者是更强的结论**——
与本轮反复在查的那类错误(缺席被读成事实)同构,只不过这次犯在文档与设计裁决上。

#### 方法论

- **种子取自真实 transcript 比按分布编更狠**:S3/S9/S10 三条最贵的发现都来自
  「用户当年真这么问过」的形状,编不出来。
- 归因仍以 `cli_output` 为主(11 条),`cli_error_message` 4 条,`skill_doc` 仅 1 条 ——
  与第一轮同向:**输出的形状比措辞更容易骗人**。
- 一处闸的自噬:`GateTests.基线里的复数都是登记过的形态` 把「2 field paths」里的
  `field` 截出来跟单词名词 `field` 对,把一句完全正确的话判红 —— **1338603 那条教训
  在闸自己身上复发**。改成按最长登记名词匹配。
- **修 F9 的补丁自己犯了 F2 的错,两次。** 给 `search` 加子串补扫时,第一版按**已显示的行**
  去重(`--limit 3` 把没显示出来的 FTS 命中当成新增,报「3 of 41」),改完又**先截断
  再累加**(M 跟着 limit 缩,报「3 of 22」而真值 23)。两种写法都让人按小 limit 试一次
  就拿到一个错的总数 —— 而三态文法的全部价值就在那个 M 上。收法:去重在 SQL 侧对整个
  FTS 命中集做,全量算进总数、只取得下的进表;并立一条「M 不随 `--limit` 变」的断言
  加两份字节基线。**教训是盲测发现的缺陷类型会在修它的补丁里复发**,所以每条修复都要
  问一遍「这一条自己犯了它要修的那个错吗」。
- **SKILL.md 原先是唯一没有闸的产物**。参考页有逐字节闸(生成产物),基线有 25 份,
  而 04 明写「skill 文档本身进入被测物」的那一份是手写的、没人守 —— F4 的前身正是
  它里面一句「Every command that can produce a long list takes --path, --type, --scope
  or --files」,而 `--type` 当时只挂在一条命令上。现在文中每条 `rimsearcher …` 命令行
  与收窄开关表都对着 `CommandRegistry` 验。

### 第三轮结果(2026-07-30,10 场景 / 2 答出 / 0 死路 / 8 险些答错)

**定位**:第二轮回修之后新增的查询侧表面首测(`inherit` / `list --scope` 空结果措辞),
外加第二轮明确留下的三条尾巴(`search` 空结果 partial、`--limit` 压译文表与过期声明两条
not_exercised)。种子仍取自 Vethara transcript,但换成上两轮**没用过的尾部 episode**。

**死路清零**——前两轮那种「工具压根答不了」基本消失。但归因分布变了向:
`cli_output` 56 / **`skill_doc` 17** / `cli_error_message` 10 / `data_side` 7 /
`my_own_mistake` 5。前两轮 `skill_doc` 都只有 1 条,这一轮跳到 17,而这是本轮
最重要的一条产出,见下方方法论第一条。

#### 六个靶子的判定

| 靶 | 判定 | 依据 |
|---|---|---|
| `inherit` 命令 | defective | 命令本身立住(S1 用 6 次、S2 用 10 次都拿到干净决断),但没有 `--type`(`inherit Basic` 静默挑一个,而 `get` 对同名字说「4 defs share the name」)、信息性回答退出码 1、抽象节点自己声明的字段值不呈现 |
| 空结果成因分流 | defective | 两种误诊都还在且逐条复现,详见 R8 |
| mod 不在快照的指路 | defective | 完全没修,详见 R10 |
| scope 组筛空措辞 | **clean** | `64e82e8` 修住了:分子分母都给、没把过滤器筛空说成「快照里没有这个 def type」、指路指向 mod 装载侧 |
| `--limit`/`--path` 压译文表 | **clean** | 机械层面立住,源码侧一致(先按 paths 过滤再 Take)。真正的问题在 `--type`,归 R2 |
| 多快照说清用的是哪个 | defective | 名字说了(每条命令首行都打),但「这个选择会怎样把答案变形」没说,详见 R10 |

#### 四条 fatal

- **R1 `get` 把 C# 声明默认值、`ResolveReferences` 兜底值和作者手写的 XML 值印成
  一模一样的行**(5 场景)。四个场景的错结论全部由这张表直接生成,而且**每次错的
  那一行都恰好是「字段名和用户提问一字不差」的那一行**——S1 差一句话就交出
  「只出现一次定义在 `children[0].burstCount = 1`」,而那个 1 属于另一个 child
  (用户问的触手 burstCount 是 40),且 `burstCount` 压根不是重复限制器
  (`SubEffecter_Sprayer.MakeMote` 读的是**每次 trigger 的 fleck 数**)。
  修向:导出侧记下「这一行是否等于该字段的 C# 声明默认值」,`get` 把 authored / default
  分开;做不到就退一步,凡覆盖数等于该 def type 全量的字段贴一句「值可能来自代码兜底」。
- **R2 `inherits_from` 与译文表只按 defName 关联,`--type` 只收窄字段表**(2 场景)——
  于是在 `def_type MentalStateDef` 标题下凭空印出 `inherits_from PsycastBase`。
  **最恶劣的是按 SKILL 教的加了 `--type` 之后,「N defs share the name」那句提示
  反而消失,错行留下,对冲归零。** S2 的证词要记:这次同名的那个恰好叫
  `RitualGlowAbstract`,名字离谱到引起怀疑;若叫 `BaseKnowledge` 之类中性名,
  错答案百分之百交出去。
- **R3 `code-search` 被 `--max-files` 截断后照样打裸的「No line matched」+ 指路去
  `search`/`find`**(6 场景),而默认 4000 低于单棵 vanilla 树(10222 文件)。
  多树扫描时措辞只列「从未触及的树」,把「被触及但只读了一部分的那棵」伪装成读完了。
- **R10 落空时从不把「可能是因为这个快照选择」说出口**(3 场景)。S1 给了最强的证据:
  工具从别的快照知道该 def 属于 anomaly、也知道本快照的 mod 列表,「它的 mod 不在
  这个快照里」这句话是**可算出来的**,它没说。另有一词两义:快照名 `vanilla` 不含
  任何 DLC,而 `--scope vanilla|core|official|base` 反过来 = Core + 全部 DLC,
  两份文档都没写。

#### 其余十一条

R4 `code-search` 第三道闸(每文件 20 命中,`--limit all` 抬不动、无开关、两份文档都没写) /
R5 CLI 侧没有读文件/行区间/方法体的能力而 SKILL 把这类问题整条路由给 decompiler MCP,
CLI-only 时读代码退化成编造正则 / R6 `inherit` 承诺的 patch 计数在干净节点不打印 /
R7 计数的名词与实际所数的东西不一致(「N defs」数的是路径命中行) / R8 零结果四种误诊 /
R9 多态子对象的类名不进快照,而按类反查时给的是「是真没有不是拼错」的确定性否定 /
R11 未知**选项名**被漂亮拒绝,但选项**取值**和已接受却无效的 flag 不受保护 /
R12 信息性回答与零命中都退出码 1 / R13 `-C` 不合并重叠窗口 /
R14 `--json` 数据数组键名无文档,取错键得到安静的空数组 /
R15 `code-search` 的树枚举与 `sources list` 对不上(把 `.git` 当成一棵源码树)。

#### 方法论

- **闸的形状不对,不是没有闸。** 第二轮给 SKILL.md 立的闸只验**命令行存在性**
  (每条 `rimsearcher …` 对着 `CommandRegistry` 验),不验**承诺的语义**。这一轮四条
  `skill_doc` 全是「文档承诺了、实现没做到」:R4 把闸门数成两道(封闭列举语气,实际
  有第三道)、R6「zero means what you see is what the game read」(干净节点根本不打印
  那个零)、R11「Unknown options are rejected rather than ignored」(只覆盖选项**名**)、
  R14「nothing is lost」(`--json` 键名无文档,猜错静默返回空数组)。**每一条承诺句都得有
  一份对着实现验的断言**,否则文档就是下一轮 `skill_doc` 归因的产地。
- **归因复核要做成常设工序,而且要双向。** 上一轮的教训(七个 agent 把同一个坑报成
  `my_own_mistake`)这一轮做成了独立的复核 agent,产出超过预期:5 条 `my_own_mistake`
  里 **4 条应退回工具**,全是同一个动作(接管道),根因是三条参数面缺口——所有表命令
  **没有 `--offset`/`--skip`**(S1 要 92 行里的第 61–92 行,`Select-Object -Skip 60`
  不是图快是唯一办法)、`snapshot truncated` 没 `--type`、没有 read-file/read-member。
  禁令句「Do not pipe the output through grep」立了规矩却没给出口——**规矩越正当,
  违规者越倾向把成本记在自己头上**;决定性证据是唯一没踩坑的 S2 把同一件事明确记成
  文档缺陷。反向也查出 **2 条虚报**:S4 把 `find --value '/Things/Mote'` 的失败记成
  `cli_output`/high,真因是它自己在 Git Bash 里发的命令、MSYS 把前导斜杠改写成了
  `C:/Program Files/Git/Things/Mote`——它自己换 PowerShell 复现证明过这一点却没更新
  结论,**照这条去修会在一个不存在的 bug 上动刀**。
- **`useful: false` 里藏着连摩擦都没被记下来的缺陷。** 挖出 6 条,最危险的是
  `code-search --limit N 会静默提前终止扫描`——10 份轨迹零记录,只以一条注记存在,
  而 SKILL.md 明写「`--limit` 只管行,扫描面是 `--max-files`」,口径直接冲突。
- **自责不只藏缺陷,还把 cost 从 high 压成 low。** 5 条 `my_own_mistake` 的 cost
  全部是 low,而同一个底层缺陷归给工具时一律 high。S9 的 grep 恰好吞掉了「1 file had
  more than 20 matches」那句,空结果读起来像「文件里没有声明」——同一道闸它自己在
  friction 里评 high,经由管道造成实际误判的那次评了 low。
- **「把当前实现没有说成原理上没有」第三轮复发。** R1 的**症状**被四份轨迹一致评为
  fatal,而让它无法自救的那个**能力缺口**(「给我看作者写的那层」)在多数轨迹里被归到
  `data_side` / 原理限制。S10 甚至在同一份轨迹里自相矛盾——boundary_claims 写着
  「这是当下没做到,`inherit` 已经证明工具有读 mod XML 的通道」,friction 里归了
  `data_side`。这个形状已经连续三轮出现(二轮 F11 犯在设计裁决上,这轮犯在归因上)。
- **种子改由不知情代理挖,能绕开设计者偏差。** 本轮 10 条种子是照靶子挑的,
  形状全是「冲着某个命令表面去」;另开一组 8 个 low-effort 代理各拿 32 条 episode
  独立粗筛(判据只给「难在信息形状上」「藏陷阱的优先」,不告诉它们靶子是什么),
  挑出来的形状**一条都没重合**——「查到了却不生效」「伪 defName 假命中」「混编造名字
  的存在性判定」这三类,照靶子挑挑不出来。产物见 `04a-blindtest-seeds-r4.md`,
  留作 R1/R2/R3/R10 修完之后的回归轮种子:那时它才是真正无设计者偏差的一轮。

### 第四轮(2026-07-30):跑成了回归实测,不是盲测

R1–R15 全部修完之后,按 `04a` 的 13 条种子对着三份真快照与本机 33 棵源码树逐条打命令。
**逐条判定与四处新缺陷的全文在 `04a-blindtest-seeds-r4.md` 第六节**,此处只留三件
影响后续做法的事:

- **它不是盲测,种子也没被消耗。** 盲测要求跑的人不知情,而这一轮是知道全部修法的人
  自己跑 —— 知道靶子在哪就不会踩进去,`nearly_wrong_answer` 结构上产不出来。13 条种子
  **仍是第五轮的盲测种子**,`04a` 第五节的「已用编号」没有追加。真盲测这一轮**没跑,
  仍然欠着**,要跑就得由不知情代理跑。
- **十五条靶子全部 held,而真数据另抓到四条新缺陷**(F0–F3,均已修完立闸:
  `8fb3fd1` / `156f625` / `abfaafc` / `1a530f4`)。其中两条是**修复自己带出来的
  同形物** —— F0 出自 R15、F1 出自 R11。照缺陷造的语料照不出修复的影子,
  这类只有真数据跑得出来。
- **「错的输出与对的输出同形」是一个可枚举的检查项。** 四条新缺陷各占一个新位置:
  成因之间、参数名与值文法之间、参数的两种解释之间、断言作用域与字面读法之间。
  下一轮带着这四类去看,而不是只看输出文本。

### 第五轮结果(2026-07-30,13 场景 / 13 答出 / 0 死路 / 7 险些答错)

**欠了两轮的真盲测,这一轮补上了**:13 条种子由不知情代理跑,再加两段归纳(跨场景 +
逆着自评的归因复核),之后把候选修复清单发回原 13 份轨迹做反事实自评并取共识。
**全文在 `04a` 第七节**,此处只留影响后续做法的四件事:

- **失败面整体迁移了。** 零死路、零放弃、13/13 交付完整答案,而 7 份是
  `nearly_wrong_answer`、13/13 自评 `confidence: high` —— 置信度与正确性完全脱钩。
  报错句「撞墙不给出路」零出现(`cli_error_message` 从 10 掉到 2),参数静默忽略、
  JSON/文本冲突、性能抱怨、分页语义不清全部零出现。**入口和文档的毛病清完了,
  剩下的全在输出的语义精度上。**
- **`skill_doc` 的性质从「缺」变成「超发」。** 14 条里至少 6 条是文档白纸黑字承诺、
  实现不兑现(scope 展开播报、零结果自报成因、`--defaults lists everything`、
  截断注脚可 cross-check)。这类不能靠改文档收场 —— 承诺方向是对的,该修实现。
- **自责会把 cost 从「后果成本」偷换成「恢复成本」。** 9 条 `my_own_mistake` 里
  6 条应改归工具:归工具时算的是「这条缺陷把答案带偏多远」,归自己时只算
  「我爬回来花了几条命令」。全卷复现率最高的缺陷类(收窄开关缺失 + 文档禁管道,
  10 份轨迹)恰好因此拿到最低定价。**逆着自评复核这一步不能省。**
- **把候选修复清单发回轨迹做反事实自评,推翻了清单里的三处落点。** 三处的真相都比
  清单写的更窄更便宜(见 `04a` 第七节)。教训直白:**动刀前先读产地** ——
  三处全是没读源码就写的。同一步还挣出两条定价规则:更便宜的选项通常是 Pareto 优
  (七处同形,上下文预算是 LLM 调用方的第一约束),以及 schema 级改动一票也不该给
  (拿一份轨迹的摩擦买一次全量重导,是把别人的账单记在自己头上)。

### 第六~八轮(2026-07-30/31):全文在 04a

本篇停在第五轮,而后面三轮的种子、逐轮结论与方法论**全在 `04a-blindtest-seeds-r4.md`**
(§八 / §九 / §十)—— 04a 开篇那句「方法论与逐轮结论在 04,不在这里复制」对这三轮不成立,
以本节为准。摘要:

- 判据从「按靶子清单往下做」换成**「拿真实工作撞到什么修什么」**:八条靶子全清之后复跑,
  第八轮只做了 bug 与纠错两条,拓展两条**明确不做**。
- 落地的代码在 git 里各有一条:第六轮九条(`3f77e59`)、第七轮(`3a1bcce` / `303bb98` /
  `3af47cf`)、第八轮(`252d262` / `370c19c`,落账 `af6fb96`)。
- 第八轮之后另做过一次**文档-代码-skill 同步审计**(2026-07-31),四条代码 bug 与二十条
  文档腐坏,提交信息里各自记着账。

## 验收(迁移完成的定义)

- 01 表中每项资产:已移植 / 明确弃置并记录原因 —— 无第三态(「大概搬了」不算)
- 02 的 1-6 修完;7-9 可延后但要有去向记录
- `dotnet test` 全绿,基线快照是闸
- skill 文档不含任何「绕自家缺陷」的教学
