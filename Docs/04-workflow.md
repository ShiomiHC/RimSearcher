# 04 · 工作方式与迁移路径

## 约定(沿 master 惯例)

- **先立字节级闸再改代码**:动上游任何输出之前,先给该输出建快照基线 —— 包括固化它
  现有的缺陷形态,改动才有对照物。
- **产地唯一**:一份判据一个产地。文档层同理 —— 本 Docs 是决定与调查的产地,
  CLAUDE.local.md 只放指针与机器事实,不复制内容。
- 本分支直接提交,不开 feature 分支(worktree 即隔离;master 的既有约定)。

## 建议顺序(每步可独立停,序号 ≠ 强依赖)

前提说明:此顺序以上游可跑的管线为起步脚手架,但这只是施工顺序,不是站队 ——
每步落地时该点用谁的做法(上游代码 / 本地设计 / 全新写)仍按 00 的逐点择优裁,
择优判成全新写时整块替掉上游实现也在此顺序之内。

1. 上游 CLI 现有输出建字节级基线(固化现状,含缺陷)
2. 截断自证 + 三态文法进 CLI(修 02-1)
3. 噪声清单收产地(修 02-2;导出侧修对,清单共享)
4. db 加 meta 表 + CLI 过期自证(修 02-4;导出/查询两侧同步)
5. 导出原子化(修 02-6)
6. 移植文法/措辞测试(01 清单里的测试类,配合 2 的输出改造落地)。**基线枚举同步接上
   文法检查**(01「字节级基线方法」那条的缝):步骤 1 固化的正是「带着缺陷的基线」,
   文法闸落地后不接上,那批基线就成了永远绿的死角
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

两条死路都是**能力边界**而非缺陷:S3 撞上继承层整块不在快照里(见下 F11),
S4 撞上 Milira 不在本机快照覆盖里。

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

**F11 结成的是边界声明,不是数据修复**:反编译核实
`LoadedModManager.LoadAllActiveMods` 结尾调 `XmlInheritance.Clear()`,在
`StaticConstructorOnStartup` 之前 —— 导出时点上 ParentName 与抽象父节点**已经不存在了**。
这是架构边界不是缺陷。于是删掉 identity 里那个恒为 null 的 `parent` 键(它在替不存在的
数据作伪证),改在 `get` 查无此 def 时无条件说明这条边界。

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

## 验收(迁移完成的定义)

- 01 表中每项资产:已移植 / 明确弃置并记录原因 —— 无第三态(「大概搬了」不算)
- 02 的 1-6 修完;7-9 可延后但要有去向记录
- `dotnet test` 全绿,基线快照是闸
- skill 文档不含任何「绕自家缺陷」的教学
