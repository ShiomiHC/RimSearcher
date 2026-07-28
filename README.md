# RimSearcher
[![Latest Release](https://img.shields.io/github/v/release/ShiomiHC/RimSearcher?style=flat-square&color=333&logo=github)](https://github.com/ShiomiHC/RimSearcher/releases/latest)

一个基于 MCP 的 RimWorld 源码检索与分析服务。它把本地 RimWorld C# / XML 数据建立为可查询索引，让 AI 助手能在真实源码上定位、追踪、阅读和解释逻辑，减少“幻觉式回答”。

采用 Roslyn + XML 继承解析，支持高并发只读查询。
> MCP 协议版本: `2025-11-25`

---

## 1. 核心特性

### 精准 C# 解析（Roslyn）
- 单次解析提取类型继承和成员索引（方法/属性/字段/事件）
- 支持类大纲、成员体提取、继承链追踪
- 支持方法、属性、构造器、索引器、运算符级别读取

### XML Def 继承合并
- 递归解析 `ParentName` 链路
- 合并父子节点并处理列表容器/覆盖逻辑
- 输出可直接阅读的“最终 Def 结果”

### C# 与 XML 语义桥接
- 从 Def 自动提取关联 C# 类型（如 thingClass / compClass / workerClass）
- 在 `inspect` 中同时展示 Def 信息与关联代码路径

### 本地化译名
- `locate` / `inspect` 命中 def 时附带当前语言的译名，不必再靠英文 label 反查中文名
- 语言默认跟随游戏设置（读 `Prefs.xml` 的 `langFolderName`），也可显式指定或关闭
- 三类来源都收：本体与 DLC 的官方语言包（`.tar`）、mod 自带的 `Languages`、只有 `Languages` 的独立汉化包
- 按 `<DefType>/<defName>` 精确匹配。跨类型重名的 defName 不会串——`Animals` 在 `SkillDef` 下是「驯兽」，在 `MainButtonDef` 下是「动物」
- 多个源译同一个 def 时，`[[sources]]` 里靠后的那个赢（近似 RimWorld 的「后加载覆盖先加载」）；同一源内按 mod 的目录优先级
- 译名只作为附注，`inspect` 的 Resolved XML 保持原样

### 面向查询性能优化
- 预建索引 + N-gram 候选筛选
- 启动后冻结索引（`FrozenDictionary`）优化只读查询吞吐
- 搜索结果带上限控制，避免超长输出拖慢上下文

### 低 Token 消耗（LLM 友好）
- 采用先定位再深入的查询链路（`locate` → `inspect`/`trace` → `read_code`），避免一次返回大段无关文本
- `locate` / `trace` / `search_regex` 工具采用结果上限与预览截断，控制上下文体积并保持关键信息密度
- `read_code` 支持按 `methodName`/`extractClass` 精确读取代码，未指定成员时再按小范围行号读取，避免一次返回整个文件
- 结果行不重复印能从符号名逐字推出来的文件名（`locate` / `trace` / `inspect` 共用同一条判据，见 `locate` 一节）——留下的每一个文件名都是「它不在同名文件里」的信号
- **同源标签只印一次**：一段结果全部来自同一个源时，`[vanilla]` 这类来源标记提到表头（`**C# Types** [vanilla]:`），逐行不再重复；真的混源时才逐行印。于是**行末出现来源标记，本身就是「这段结果跨了多个源」的信号**。`scope` 已经把源钉死时一个标记都不印。**段头的方括号描述的是这一段的总数，不是它底下列出来的那几行**——这两者在被截断时不是一回事：`10 of 36 members` 的 36 条里可能有 2 条来自 mod，而印出来的 10 行恰好全是 vanilla（同分按源优先级排序，截断留下的前缀系统性地偏向 vanilla，这是结构性偏置而非巧合）。故被截断的一段若总数跨了多个源，段头改印构成：`**Members** [vanilla 34, Cinders 2]`、`Subclasses of 'ThingComp' (511 …) … [vanilla 378, Milira 41, RatkinAnomaly 21, …]`。**各源之和恒等于表头那个总数**，这是它自证的全部本事。总数本来就单源时照旧只印一个源名，一个字不多；没被截断时也不印构成——那时行本身就是构成。**`[source]` 里装的是配置里的源名，不是命名空间**：本机上大多数源两者恰好同形（`Milira` 的类型都在 `Milira.` 下），于是它会被当成命名空间拿去拼全限定名——而 `vanilla` 的类型分散在 `Verse` / `RimWorld` 等好几个命名空间下，`Cinders` 的类型全在 `Embergarden.` 下，拼出来的名字一个都不存在。这句写在 `scope` 参数的常驻契约里
- **条件加载目录逐条打标**：`loadFolders.xml` 里带 `IfModActive` 之类条件的目录，索引侧一律收下而不判条件（见「mod 展开规则」），故命中落在里头时行末挂一个键 `[conditional: 1.6/CE]`，整份返回末尾一条脚注把键兑换成成因（`` `1.6/CE` [Cinders] needs CETeam.CombatExtended active ``）。行内只放键、成因整份说一次，与来源标记同一套判据；`loadFolders.xml` 的一条 `li` 展开成 `Defs` / `Patches` / `Assemblies` 好几个目录时，脚注仍只算一条——那几个目录说给调用方听的是同一句话。**没打标就是不在这类目录里**，这句反面读法写在脚注最后一句里（`Rows without that tag were checked and are not inside such a folder`——「checked」不可省，它堵的是把「没标记」读成「没查过」）：不说的话这个记号只能单向使用（看见了才有意义），而调用方要判的恰恰是「我手上这条到底受不受影响」。脚注同时说清另外两件推不出来的事：**条件不成立时那个目录整个不加载、里头的内容在游戏里不存在**（只说「命中不等于生效」的话，「这条到底在不在我游戏里」这个正面问题一句话都答不了，调用方只能把它记成悬案）；以及**这个记号只管目录这一层**，目录里的补丁或 def 可能另带自己的门（`PatchOperationFindMod`、`MayRequire`），那些不在这里报——记号在场时最容易被读成「门就这一道」。整份返回只落在一个目标上时（`read_code` 一个文件、`inspect` 一条 def）键与成因合成一句、不再另起脚注，上面三句边界在那一形里同样都有。反面那句还落在 `tools/list` 的常驻契约里，故只调 `inspect`、返回里一个条件记号都没出现时，「没标记」照样是可用的结论。配了 `active_mods` 的源一个字都不打：那里的条件已经判过了，再打标是把有答案的问题重新说成悬案。条件里的 packageId 保留 `loadFolders.xml` 的原样拼写（`CETeam.CombatExtended`），拿它能直接去 mod 列表里对号
- **截断提示全服一套文法**：`... +N more <什么> (<怎么拿到>)`。看到 `... +` 开头的行就是被截断了，`<什么>` 恒有名词（它数的到底是哪一类），括号里那句就是下一步该怎么传参，各工具不必分别认。例：
  `... +71 more methods (pass limit:'all' for the whole list, or read one with read_code methodName)` ·
  `... +367 more of 387 lines (pass startLine=20)` · `... +43 more entries (pass offset=7 for the next page, or a larger limit)`
  - 名词与 N **单复数一致**（`... +1 more C# type` / `... +18 more C# types`），故这个槽可以直接当句子读。表头、尾注、启动提示里的计数同此判据，全服不写 `N thing(s)`
  - **总数已知时写成 `... +N more of M <什么>`**（`... +19 more of 22 matching lines in this file` · `... +82 more of 132 matching files (50 listed; …)`）。**文件那一形还要印出「这次列了几个」**——同一个工具的 scan-stopped 那一形明写 `only the first 50 files are listed`，两形不对齐时读者只能做 132−82 的减法，而那 50 是个从不出现在返回里的常数。只给增量的话，读者要拿它和印出来的条数相加才得到总数，而「上面印了几条」并不总是常数——扫描停在预览配额上时最后一个文件可能只印了 1–2 行也带折叠，那条被诱导出来的心算就会算错。`of` 的读法与表头一致：**看到 `of` 就是没给全**。总数推不出来时（扫描停了、后面的文件没打开过）不写 `of`
  - 唯一不带括号的一形是 `trace usages` / `search_regex` 的**每文件**折叠行 `... +N more of M matching lines in this file`：它的下一步整份返回里只说一次（见下条），不逐文件重复
  - 括号里那句**会说清 `limit:'all'` 够不够**：藏起来的比服务端上限（200）还多时写的是 `pass limit:'all' for the first 200; the rest needs a narrower query`，只有真能一次拿全时才写 `pass limit:'all' to expand`。`'all'` 从来不是「无限」，它只把上限抬到 200
- 扫描类工具（`trace usages` / `search_regex`）停在预览上限时另有一句共用的 `... more matches exist (…)`——与 `... +N more` 的区别是**它数不出还剩多少**（后面的文件根本没打开过），故不给数字。括号里同样给下一步；`limit` 已经是 `'all'` 时不会再劝你提 `limit`
- 这两个工具还共用一句 `... some files were not scanned in full (…)`：有文件读不开、或大到只扫了前 20000 行时才出现，**并点名是哪几个文件**（列前三个，其余记数）。不点名时调用方无从判断那个文件与本次查询有没有关系，只能把整份结果一律当成下界。**它一出现，表头的命中数就改口成 `at least N matching lines`，且表头就地带一句指向它的引用**（`; 'at least' comes from the trailing 'not scanned in full' note, not from limit`）——成因同现还不够：盲测里三条互不相干的任务链在成因确实在场的返回上各自把那个下界归给了 `limit` 的默认值（`at least 105` 与 default 100 只差 5，算术上太顺），而真成因隔在整份结果之后。**下界记号必须携带可指认的成因引用**。改口本身的判据不变：那时那个数只是下界，即便被截断的那个文件在已扫部分零命中也一样——行闸之后有没有命中谁也不知道。反过来说，表头直接写 `N matching lines` 就是确定值
- **两个扫描类工具报的数都是「命中行」，不是「命中次数」**：判据是逐行 `IsMatch`，同一行被 pattern 命中两次仍只算一行。表头与每文件折叠行用的是同一个量纲
- **表头回显生效的大小写档位**。`search_regex` 的 `ignoreCase` 默认 true 而只写在参数表里，于是同一个 pattern 的命中数会因为一个没人传过的开关而浮动，返回里却没有任何字段能事后判断跑的是哪一档——拿它去「交叉验证」`trace usages` 的数时，两边其实跑的是同一个默认开关。`trace usages` 是固定的不分大小写全词匹配，故未截断时它还多给一个数：`Text matches for 'CompRefuelable' (108 matching lines in scope 'base', whole word and case-insensitive — 82 of them match the query's own casing)`。C# 的命名习惯保证「类型 `CompRefuelable` → 局部变量 `compRefuelable`」，那 26 行的差额正是纯变量行；不给这个数，108 会被当成「这个类被引用了 108 处」写进结论
- 但**表头的数在截断与未截断两种情形下量纲不同**，措辞会说清是哪一个：截断时写 `first N preview lines`（数的是**印出来的**行，每文件封顶 3 条），未截断时写 `N matching lines`（数的是命中行，含没印出来的）。两个数不可横向比较——`first 100 preview lines` 背后的命中量可以远大于另一次查询的 `49 matching lines`
- `locate` / `trace inheritors` 的逐源越界脚注在**跨多个源**时先给合计（`Outside scope 'base': 47 matches — Milira 15, RatkinAnomaly 10, …`）——同一份返回里 scope 内的量在表头已经加总好，这一行句式并列却要读者临时心算，是整份输出唯一要做算术的地方。只有一个源落在外面时不加，那时合计逐字等于那一个数。**合计的名词跟着构成走**：只有一个段参与时用那个段自己的名词（`Outside scope 'base': 3 files — Wolfein 2, Cinders 1`），泛称 `matches` 只留给真的跨段累加——正文写着 `4 files`、脚注紧跟着写 `3 matches`，同一屏两个计数词指的是同一类东西，读者得先确认它们不是两个量。**合计跨了多个段时还要点名构成**（`Outside scope 'base': 2122 matches (1607 members + 491 C# types + 24 XML defs) — miho 1151, Milira 286, …`）：参与相加的段随命中形态变，同一条查询换个 scope 就可能多出一段，于是同一个源的计数在两次调用里对不上，而调用方无从判断哪一次算漏了、哪一次算多了。

- 这两个工具的 scope 是**硬过滤**：落选的文件根本没被打开，故它们给不出 `locate` / `trace inheritors` 那条逐源的 `Outside scope 'X': …` 计数。缺席会被读成「scope 外没有」，所以它们改为明说一句 `Files outside scope 'X' were never opened, … this tool never prints such a line`
- 同一份返回里出现**重名文件**时，文件名补上刚好能把它们分开的那几级目录（`Core/Defs/…/RangedIndustrial.xml` 与 `Biotech/Defs/…/RangedIndustrial.xml`）——判据与结果行文件名同源：唯一就只印基名，重名才补。两个工具都叫调用方 `use read_code on a file`，而 `read_code` 收基名，不消歧那句下一步就是错的
- 有文件的预览被折叠时，末尾补一句 `... previews are capped at 3 lines per file and no parameter widens that; use read_code on a file to see the rest`。**它整份返回只出现一次，且只在真有文件撞上那个上限时出现**——没有这句，就是没有任何文件因为「每文件 3 行」被折叠。这条单列的原因是其余折叠行都以 `(pass …)` 收尾，留空会被读成「这里漏印了参数名」，而这个上限确实没有参数放得宽
  - 注意扫描停在预览配额上时，**最后一个文件**的折叠行成因是配额耗尽而非每文件上限（它可能只印了 1–2 行就折叠）。那种折叠不触发这句脚注，它的成因由 `... more matches exist (scan stopped at the N-preview cap…)` 解释
- 返回不以空行结尾——结尾空行会被读成「后面还有、被截断了」

### 跟随游戏与 mod 更新
- 按 sha256 检测已配置源的程序集变化，可一键重新反编译（进程内完成，不依赖外部工具）
- 保留数代源码历史，支持文件级与行级 diff，游戏更新后能直接看出改了什么
- 重新同步后索引就地重建，不需要重启服务

### 运行模型与边界
- 本地运行，核心检索不依赖网络
- 网络请求仅用于版本更新提示（可关闭）
- 反编译仅针对使用者自己配置的本地程序集，且由 `sync_sources` 显式触发，不会自动进行

---

## 2. 七大工具

以下为实际注册的 MCP 工具名与能力说明。

###  `rimworld-searcher__locate`
全局模糊定位入口，也是**唯一接受近似输入**的工具——其余工具都要求准确名字，先用它把残缺或拼错的名字换成准确名。

**支持内容**
- C# 类型、成员（方法/属性/字段）、XML Def、Def 字段内容、文件名
- CamelCase 缩写与拼写容错（如 `JDW`）。**成员段与其余三段同等**：`method:CompTickRar` 会给出 `CompTickRare`。成员模糊匹配曾先把候选截成一个固定大小的池子再看谁够分（先按索引枚举序硬截 200 条，后改为按 2-gram 重合度取前 500）——两版都是「先截断、再判断」，于是池外有没有够分的键谁也说不出来。现已整个撤掉：60 分线可以逐条翻译成前缀 / 词边界 / camel 首字母 / 编辑距离 ≤ 3 四种结构条件，每一种都是一次区间查询，故**够分的键是精确枚举出来的**，与候选池大小、索引枚举序都无关。剩下的唯一一道限额是「一次最多展开 12000 个名字键」，撞上它时表头改口 `at least` 并附成因脚注（见「结果分段」）
- `scope` / `limit` 参数（见「配置」一节）
- def 结果附带译名（`` `Beer` (100%) - ThingDef "beer" / 啤酒 ``）。查询本身仍按英文/defName 匹配

**过滤前缀**（写在查询串里，不是独立参数）

| 前缀 | 别名 | 作用 |
| --- | --- | --- |
| `type:` | `t:` `class:` `c:` | 只搜 C# 类型 |
| `method:` | `m:` | 只搜方法 |
| `field:` | `f:` `property:` `p:` | 只搜字段/属性 |
| `def:` | `d:` | 只搜 XML Def |
| `scope:` | `s:` `source:` `in:` | 等同于 `scope` 参数 |

- 冒号后**带不带空格都行**（`type:CompShield` 与 `type: CompShield` 等价）。光杆前缀（`type:`）视同没写，返回会说明它被忽略了
- **不在上表里的前缀不是过滤器**，整个 token 会当成普通搜索词去匹配（于是 `member:CompTick` 零命中，而 `method:CompTick` 有两百条）。这种情况返回会明确点出来，不再让调用方把「前缀写错了」读成「这个符号不存在」

**结果分段**：`C# Types` / `Members` / `XML Defs` / `Content Matches`（按 Def 的字段值命中，而非按名），每段各自受 `limit` 约束并独立折叠。表头一行给出**各段列出了几条、这个 scope 里一共有几条**（`## 'Pawn' — 1 of 768 C# types, 3 of 1931 members`），不必自己数行、也不必拿折叠行去做加法就能判断要不要调 `limit`。没被截断的段不写 `of N`——**看到 `of` 就是被截了**，与折叠行是同一条读法。`Members` 段的总数还有一种形态 `N of at least M`：一条关键词能匹配的**成员名键**超过服务端一次展开的上限（12000）时，M 只是地板而不是总数，此时末尾另有一句脚注把这个上限说出来。与扫描类工具的 `at least N matching lines` 是同一条读法：**出现 `at least` 就说明这个数只是下界**，且成因一定写在返回里。反过来说，没有那句脚注、表头直接写 `N members`，这个 M 就是该 scope 下的确定总数——包括 `method:Notify_` 这类共同前缀成百上千的查询。这一条以前不成立：成员搜索当年是先把候选截成一个 500 键的池子、再看谁够分，于是「池外还有没有够分的」只能靠启发式猜；现在是先把够分的键精确算出来（60 分线可以逐条翻译成前缀 / 词边界 / camel 首字母 / 编辑距离 ≤ 3 四种结构条件，每一种都是一次区间查询）、再看装不装得下，故「是不是下界」有确切依据。（这里与 `trace inheritors` 的 `(381 …) Listed below: 200` 是同一个口径，只是排版不同：两个工具的表头都同时给总数与显示数。）另有 `Files` 段（已索引的文件路径）：四段全部零命中时它是兜底，按名模糊列出若干条；四段有命中时它只补上**基名与查询词逐字相同**的那一份，且不重复已经出现在 `C# Types` 里的同名项——文件名是一等查询目标，不该因为顺带蹭到一条低分 def 就整段消失。**查询显式带了扩展名、而索引里没有同名文件时，返回末尾另有一句 `No indexed file is named 'X' in scope '…'; anything listed above matched the query as a name, not as that file.`**——那时 `Files` 段整个不出现，版面上只剩别的段落对同一个查询串做的模糊命中（查 `LoadFolders.xml` 回的是「1 C# type: `LoadFolder` (37%)」），而近名文件与逐字命中在路径上往往只差一个词尾（`RangedIndustrialGrenades.xml` 之于 `RangedIndustrial.xml`），缺席因此读不出来。零命中时不挂这句——那时表头的 `No results for 'X'` 已经说完了。

**`C# Types` / `Members` / `XML Defs` / `Files` 四段每行尾部的 `(N%)` 是匹配分，`100%` 以下一律不是所查的那个名字**，故任何一段的总数都含近名命中。**`Content Matches` 段整段不带 `(N%)`，表头也永远没有 `(K at 100%)`**——那一段的排序键是「命中了几个关键词」而不是 0~100 的相似度，两个量纲不通。它不需要判别器：内容索引按整词建键（下划线不是分词符，故 `Apparel_ShieldBelt` 保持为一个 token），查询走全等查表，**每一条内容命中按构造就是整词、不区分大小写的逐字命中**。这一条恒真，故只写在 `tools/list` 里、不印进返回。另外这一段的行首**不是**命中项而是宿主：写成 `PawnKindDef.apparelRequired.li in \`Mercenary_Slasher\``，即「`Mercenary_Slasher` 这个 def 的这个字段里写着你查的那个词」——其余四段的行首才是被查中的东西本身。反过来不成立：成员分是 `baseScore + keywordBonus` 封顶 100，**两个以上关键词**时一个 90 分的前缀命中也能被推到 100，故成员段的 `100%` 是「本次的最高分」而不是「拼写逐字相同」的保证（单关键词查询如 `method:CompTick` 没有这个口子）。这一条对 `method:` / `field:` 同样成立：它们把搜索限定在成员上，**不**把名字匹配变成精确匹配（`method:CompTickRar` 的表头是 `3 of 31 members`，而这 31 条里一条 `100%` 都没有——列出来的全是 `CompTickRare` 这类 90 分前缀命中，索引里根本没有叫 `CompTickRar` 的方法。反过来，`method:CompTick` 那 200 条里也混着 `CompTickRare` / `CompTickInterval`，而默认 `limit:10` 印出来的头几行恰好都是 `100%`）。

**总数里混着近名时，表头就地说清其中几条是逐字同名的**：`10 of 1591 members (35 at 100%)`。`N of M` / bare `N` 说的是**完整性**（这一段有没有被截断），而「有几个方法叫 `Draw`」问的是**精确性**——两件事此前由同一个数承载，而版面上只印得出前者：`method:Draw` 印出来的 10 行全是 `100%`，`method:PostSpawnSetup` 的 `10 of 104` 印出来的 10 行也全是 `100%`，可后者的 104 条**全部**逐字同名、前者的 1591 里只有 35 条是。两种情形在默认视图里曾逐字同形。判据是「全集里满分的有几条」，与总数相等时一个字都不印（`104 members` 保持原样），故它出现本身就是「这个总数不是你要的那个数」的信号。`(K at 100%)` 里的 `100%` 与行内的 `(N%)` 是同一个记号，可就地兑换。`Members` 段只在**单关键词**查询上给这个数——多关键词时 `100%` 只是最高分（见上一段），那时它会是个假数，故宁可不印。表头改口成 `at least` 时同样不印：总数自己都是下界，再挂一个精确数会被读成两处独立的不确定性。

**字段值索引只收长度 ≥ 3 的 token，查询里的短词会被整段跳过**。查 `'shield 20'` 时 `20` 一个 def 字段都没被搜过，而 `Content Matches` 段的缺席与「这个值不在任何 def 里」逐字同形。故短词一出现就在末尾挂一句脚注点名说它没被搜、并指向 `search_regex`（那条路径匹配短字面量）。三个字符正好在界内，不印——这句只在真跳过了东西时出现。

**成员行按「宿主类型 + 成员名 + 种类 + 文件」去重，同名重载折成一行**。于是成员段的行数与总数数的是**成员名**的个数，不是方法声明的个数——`Verse.FleckStatic.Draw` 一行背后可能是两个重载。要展开某一行有几个重载，`inspect` 那个类型。

**同分并列的结果次序是定的**。分数与名字长度都并列时，末级按符号全名（再按文件路径）排序，故同一条查询换个进程、换个索引重建轮次都给同一批结果——`method:CompTick` 这类几百条同分同长的查询尤其依赖这一条。与 `search_regex` / `trace usages` 是同一条可复现保证。

这条保证**不只管打分路径**。索引的六份倒排表把值装在 `ConcurrentBag` 里，而 bag 的枚举序由并发写入顺序决定——同一份语料换个进程重扫就可能给出不同的次序。它们在冻结时一律定序（按 `Ordinal` 全序，冻结前后同一套），因为有三处拿这个次序当结论：一个类型摊在多个文件里时**它属于哪个源**取的是第一份文件的路径（继承树与类型搜索的 scope 归属都走它）；`read_code` 拿基名解析时读的是候选表第一条，而「排在前面的那个源」这条规则在**同一个源里有多份同名文件**时并列，兜底完全落在这个次序上；`GetPathsByType` 一类候选的分数恒为 100，同源之间同样只剩它。不定序的话，这三处会随索引重建静默翻面——包括 `[source]` 标签和越界计数。

**结果行末尾的文件名只在推不出来时才印**。`- \`CompShield\` (100%)` 后面没有文件名，意思就是它在 `CompShield.cs` 里（成员行同理，按宿主类型名推）。**印出来的每一个都是意外**——`- \`PawnFilter\` (100%) - Dialog_BeginRitual.cs` 表示这个符号不在同名文件里，值得看一眼。同一类型分散在多个文件时也会印，并带 `+N more files`。

**示例查询**
```text
def:Apparel_ShieldBelt
type:CompShield
method:CompTick
field:energy
scope:mods pawn
```

---

###  `rimworld-searcher__inspect`
深度分析单个 Def 或 C# 类型。**不做模糊匹配**：名字必须完整，大小写可以不管（返回会回显索引里的准确拼写），拼错与残缺都解析不出来——先用 `locate` 拿准确名。`def:` / `type:` 前缀会被自动剥掉。

**Def 模式**
- 展示 Def 类型、来源文件、译名（`localization_description` 开启时连译文描述一起）。`Type:` 行同时回答「这个 DefType 的 C# 类在不在索引里」：光名字 = 在，且在同名文件里；带文件注 = 在别的文件里或有多份；`(C# class not indexed)` = 不在索引里
- 返回沿整条 `ParentName` 链合并后的 XML——任何单个 XML 文件都不含这份内容。但**「合并」只指继承**：服务端不解析 mod 的 `PatchOperation`，也不越过当前 `scope`，所以这不是运行中的游戏看到的那份定义。被 patch 改过、或被 scope 外的 mod 覆盖过的 def，这里给出的数字看着权威却是过期的。**这句边界就写在 `**Resolved XML**` 那行标题里**（`(mod PatchOperations are not applied, so a mod patch against this def is not reflected below)`），不是只写在正文前的散段里：调用方是照着 XML 正文抄数值的，而正文可以长到几百行、`xmlStartLine` 续读时更是连开头都看不见
- **字段不标来源**。要区分「这个字段是它自己写的」还是「继承来的」，去 `read_code` 读 `File:` 那个路径——那份文件里只有它自己未合并的几行，继承来的字段恰恰不在其中。此前这件事只写在 `xmlStartLine` 这个分页参数的说明里，而那个参数只在合并 XML 过长时才需要读
- 头部固定给一行**父链状态**：合并成功印 `Inheritance chain: A <- B`，没有父则明说，某一环查不到则给**警告**并指出「下面这份不是完整生效定义、继承来的字段全缺」。三种情形此前渲染得逐字同形，调用方无从分辨自己拿到的是不是半成品。链真的多于一环时，这一行还就地说清下面那份 XML 是谁：`— the XML below is these merged together, so it is not the content of the 'File:' path above`。「字段不标来源」那条（下一条）说的是同一件事，但它写在别处，而 `File:` 行就在这一行的正上方——两行相邻却互不指认时，读者会把 `File:` 当成这份 XML 的出处
- 合并 XML 过长会被截断（首屏给头 200 行 + 尾 50 行）。**续读用 `xmlStartLine`，不要去 `read_code` 读 `File:` 那个路径**：那份文件里只有该 def 自己未合并的几行，继承来的字段恰恰不在其中。截断提示会直接给出下一次该填的 `xmlStartLine`
- 提取关联 C# 类型（`thingClass` / `compClass` / `workerClass` 等）并尝试映射到索引文件，列在 `Linked C# Types` 段；文件名同样只在推不出来时才印（与 `locate` / `trace` 同一条判据）
- `defType` 参数用于同名 def 撞车时指定看哪一个（`Human` 同时是 ThingDef、BodyDef 和 HediffGiverSetDef）。不传时返回会列出所有同名类型，据此再传一次即可。它是 def 类型，不用于收窄 C# 模式
- **def 无条件压过同名 C# 类型**，而类型索引在这一支里从来没被查过。整份返回里唯一的同名披露只枚举 def，于是那份沉默会被读成「不存在同名 C# 类型」的独立证据——`Fire` 就是现成反例（`ThingDef Fire` 与 `Verse.Fire` 同时存在，两次调用都只回 def，连歧义提示都不出现，因为 def 侧本来就不歧义）。同名类型**确实存在**时补一句注文，并把出路连参数值一起给出：`Reach the type with read_code path:'Fire.cs' extractClass:'Fire'`。查不到就一个字不印——那时的沉默才真正代表「没有」
- **`limit` 在 def 模式从不被读**（它只作用于 C# 大纲），而调用方传它时指望的恰恰是「别截断」，且 def 模式确实会截断、只是换了个参数。真传了才补一句 `'limit' applies to the C# type outline only and was ignored here; the merged XML below is paged with 'xmlStartLine'`

**C# 模式**
- 返回**基类链**，与 def 模式同一种写法的一行式：`Inheritance chain: Pawn <- ThingWithComps <- Thing <- Entity`（链上用短名，全限定名在大纲的 `Class:` 行）。接口不在这条链上，要看实现关系用 `trace mode:"inheritors"`
- 这一行同时钉住**下面那份大纲的辖域**：`— inherited members are not in the outline below at any limit`。大纲只列本类型自己声明的成员，`Pawn` 上找不到 `Map` 是因为它声明在 `Verse.Thing` 上，`limit:'all'` 展开全部 118 条属性也不会出现——辖域不说清的话，折叠行读起来就像「展开就是全部成员」
- 返回类成员大纲：**按种类分块**（`Properties:` / `Fields:` / `Methods:` 各一行表头 + 缩进的签名行），某类没有成员就整块不出现。构造器、索引器、运算符不进大纲，但 `read_code` 仍能按名读到它们
- 枚举列出其值（含显式赋的数值，如 `Resetting = 7`；表头 `Enum:` 已经说明下面每行是什么，取值行不再逐行挂种类前缀），委托列出其签名（含类型参数表与约束）
- 大纲每类成员默认最多列 40 条（三类各自独立计数），超出的在原处标明还剩多少。**取回被折叠的成员只有一条路：`limit:'all'`**（`limit` 也收具体数字）。`locate` 要先知道成员名字、`read_code extractClass` 到 2000 行就二次截断，对触发折叠的大类型这两条都走不通
- 这里的 `'all'` 是**真无限**，不受其他工具那个 200 条服务端上限的夹持——单个类型成员数过 200 是常态（`Pawn` 有 326 个）。全量大纲仍比读一遍类体便宜得多
- 同名类型分散在多个源时，只渲染作用域里优先级最高的那一份大纲，其余只报路径并给出两条真能走通的出路（把 `scope` 收到那个源，或把那个路径交给 `read_code extractClass`）——几份大纲通常高度重合，而体积按文件数翻倍；只说「outline omitted」等于给一条没有下文的死路
- 方法体不在这里，归 `read_code`

**示例**
```text
Apparel_ShieldBelt
RimWorld.CompShield
```

---

###  `rimworld-searcher__trace`
交叉引用追踪工具。

**模式**
- `inheritors`：列出某基类/接口的**传递闭包**子类与实现类树——间接后代（子类的子类）同样列出。默认展开到服务端硬上限 200，**树比 200 大时照样是截断的**，`limit` 抬不动这个上限。截断时保留的是浅层：调用方先要的是「谁直接继承了它」。撞上这个上限时折叠行给的下一步有两条：`re-trace a listed type as its own root (depths then restart from it)`，或 `narrow scope to one source — a per-source subtree is listed in full whenever it fits under the cap`。通用的「narrow the query」在这里根本不成立（`trace` 的查询就是一个类型名，没有可收窄的余地），而只给「换根重跑」那一条时语气太足：盲测里调用方据此断定「要拼出全树得对 306 个直接子类逐个盲试」，转而去跑正则补料，多花四次调用、产出一份自己都标注为「非完整」的名单——而 `scope:'Milira'` 一次就回了 41 条、含全部 depth 2/3/4、无折叠
  - **列表按深度排序，来源优先级不参与**。继承关系是精确的，每个候选的分数恒为 100，于是通用的「同分按来源 Rank 排」会把 Rank 顶成首要键——跨源查询里 `vanilla` 的 depth 4 因此排在 `Milira` 的 depth 1 前面，表头那句 `shallowest first` 成了假陈述，而「截断留下的恒是最浅的那一批」这条保证也被当场推翻（200 的配额先被 vanilla 吃光，被砍掉的恰恰是别的源的**直接**子类）。现在候选按深度升序原样保留，来源只用来判归属
  - 表头括号里的每个数都描述 **scope 内的整棵树**：`(381 in scope 'base', transitive — indirect descendants included; 221 direct, deepest 4 levels down)`。只有后面单列的 `Listed below: 200, shallowest first — nothing deeper than depth 1 is listed` 描述这次列出来的那一段，**且没被截断时整格不印**（同「看到 `of` 就是被截了」那条读法）。此前 `direct` 与 `deepest` 数的是截断后的切片，而它俩紧跟在描述全树的总数后面、句法完全对称——`ThingComp` 因此写出「381 … 200 direct, deepest 1 level down」，读者据此断定这棵树只有一层，实际有四层
  - **`Listed below` 那一格还要说清缺的是哪几层**：截断保留浅层，`ThingComp` 那 200 条**全部停在第 1 层**，于是整份列表一个 `[depth N]` 标记都没有，而表头写着 `deepest 4 levels down`。版面上这两处分列两地、都不说彼此的关系——盲测里的读法是「第 2–4 层要么不存在、要么零零星星」，实际那三层被整层砍掉了。深度确实列全了（没截断、或截断恰好停在最深一层）时写的是 `, shallowest first`，不提缺层。措辞是 `nothing **deeper than** depth 1 is listed` 而不是 `nothing below depth 1`：在一棵树上 below 既可读成「比 1 更深」也可读成「排在这一行下方」，而后一种读法把整句变成「1 层以下什么都没列」——恰好与真值相反
  - **只有间接后代带标记**（`[depth 2]` 起）；直接子类不标，表头会在**这次真印了标记**时补一句 `untagged = direct`。一个标记都没印时不讲解这套记法——讲了反而会让读者去找它。补的那句是 `untagged = direct (depth 1)`：不写出那个 `1`，读者只知道无标记行「是直接子类」，却没有依据把它接到 `[depth 2]` 这套编号上去，也就无从判断「无标记」在深度轴上占的是哪一格
  - 零结果分两种，措辞不同：**索引里没有这个类型名**（拼写待核，去 `locate`）与**类型在索引里、只是没人继承它**（这已经是答案）
    - 后者还再分两种：scope 外也确实没有时才写 `(this is an answer, not a lookup failure)`；scope 外有派生类时改写成 `…but it does have subclasses outside that scope, so this is not the whole answer`。「这是答案」这句背书只在真的是完整答案时给——否则它会盖过下面那行小字的越界计数，整份返回被读成「没有子类」
    - 零结果**不再劝 `scope:'all'` 重试**。继承闭包是全域算出来再按 scope 过滤的，「索引里有没有这个类型名」也与 scope 无关，故换个 scope 返回逐字相同：那句劝退在「这是答案」后面语气正相反，在「拼写待核」后面则保证白跑一轮。真有越界派生类时，逐源计数那行仍在
  - 越界的那行还会补一句**把两边合起来看整棵树是什么形状**（`; including them the tree is 306 direct, deepest 4 levels down`）：表头的 `221 direct, deepest 4 levels down` 只算 scope 内，而闭包本身是全域算的，两个数并列时读者会把 scope 内的形状当成这个类型的固有形状
- `usages`：符号的**逐行文本匹配**（不分大小写的全词匹配），C# 与 XML 都扫，带行号预览。默认 50 条，每个文件最多 3 行预览，其余记为 `+N more of M matching lines in this file`
  - 表头动词是 `Text matches for`，不是 `References to`。后者配上「文件 + 行号 + 代码」的正文排版读起来就是一份引用清单，于是那个数被当成「这个符号被引用了多少处」——而它含大小写不同的同名标识符、含注释掉的行、含无关类型上的同名成员
  - 与 `search_regex` 同款保证：**同一条查询恒给同一份答案**，截断时拿到的是文件表的**前缀**（按文件名排序，扫描与展示同一个顺序），把 `limit` 调大只会往后接上更多文件，不会把先前给过的换掉
  - 同款的还有完整性契约：读不开的文件与只扫到 20000 行的大文件会在末尾计数上报，同时表头改口 `at least N matching lines` 并就地带上指向那句尾注的引用（见 `search_regex` 一节）。没有那句尾注、表头直接写 `N matching lines`，这 N 就是该 scope 下的确定总数

> `usages` 不是调用图：同名成员挂在无关类型上也会混进同一份列表，而经由继承发生的调用则会漏掉。

**示例**
```text
symbol: ThingComp, mode: inheritors
symbol: CompShield, mode: usages
```

---

###  `rimworld-searcher__read_code`
从**某一个指定文件**里精确读取源码。`path` 收的是文件（已索引的文件名或绝对路径），不是搜索词——手上只有搜索词时先走 `locate`。

**三种互斥模式**（同时传多个时，`extractClass` > `methodName` > 行区间）。真的传多了时，返回**第一行就说清是谁赢了、谁被丢掉**：`// note: 'extractClass' takes precedence — methodName:'CompTick' and startLine/lineCount were not applied`。择一规则此前只写在 schema 里，返回里零字，于是一份「只按 `extractClass` 出的整类」会被当成「按我给的三个条件共同筛出来的」。只在真的多传了才印

| 模式 | 参数 | 说明 |
| --- | --- | --- |
| 整个类型 | `extractClass` | 类/结构/接口/记录的完整实现体，枚举与委托声明同样可取。上限 2000 行（与行区间模式同一个上限），超出会截断并报出**这个类自己有多少行、以及它所在文件有多少行**（`'X' is 3200 lines of a 5100-line file and the cap is 2000`）——只报前一个数时读者无从判断这个类是不是整份文件。下一步的建议按本次实际传了什么给：传了 `methodName` 就劝 `drop extractClass to get just 'CompTick'`（那个参数本来就在手上，只是被择一规则丢掉了），没传才劝 `pass methodName for one member` |
| 单个成员 | `methodName`（+ 可选 `className`） | 方法、属性、字段、事件、构造器（类名或 `.ctor`）、索引器（`this`）、运算符（`+`）、枚举值——凡 `locate` 列得出的成员都行。文件里同名成员会**全部**返回，传 `className` 才只取一个 |
| 裸行区间 | `startLine` + `lineCount` | `startLine` 为 0 基；未指定成员时走这条 |

前两种要解析 C#，**XML 文件只有行区间模式可用**（读 Defs 原文就走这条）。

**路径支持**
- 绝对路径
- 已索引文件名（如 `CompShield.cs`）
- 文件基名（如 `CompShield`）

**返回里必须读到的四件事**
- 三种模式的头部都印**解析后的绝对路径**，成员与整类模式统一为一行 `// <种类> <名字>[ in <所属类型>] — <路径>:<行号>`（如 `// Method CompTick in RimWorld.CompShield — …/CompShield.cs:118-137`）。`in <所属类型>` 只在它与成员名不同名时出现。行号给的是**整段的范围**而不只是起点：只印起点时，读者拿它当 `startLine` 续读会从成员的第一行重新开始；成员本身只占一行时不印范围（那时 `118-118` 只是噪音）。文件里有多个同名成员时，每条正文之前各一行并带 `[i/N]` 编号——**看到 `[3/3]` 就是拿全了**，不必猜后面还有没有
- 传基名时，`scope` 决定哪个源胜出；作用域内有多份同名文件时会追加一行 `note: N files share this name in scope …` 并列出其余候选——不看这行就可能把某个 mod 的覆盖版当成 vanilla 原版。读的是哪一份也说清：**`scope` 表达式里排在前面的那个源**，判据就在同一句话里的那个表达式上
- `className` 只是**过滤器**。过滤后没有候选时，返回会说清「这个成员确实在这个文件里，只是不在你点的那个类里」并列出它实际声明在哪几个类型、第几行；只有整个文件里都没有才报 not found
- 传了目录会明说「这是目录，不是文件」并指向 `list_directory`；文件找不到时**回显你给的整条路径**，并区分「这条路径在磁盘上不存在」与「没有同名文件进过索引」
- **错误返回与成功返回带同样的文件身份**：not found / 文件过大 / 行号越界都报解析后的绝对路径（不是基名），上面那几条 `note:` 也照带。基名撞名时，「没找到」说的是哪一份不必再猜

**示例**
```text
path: CompShield.cs, methodName: CompTick
```

---

###  `rimworld-searcher__search_regex`
在已索引的 C# 与 XML 上跑 .NET 正则。

**特性**
- 可选 `fileFilter`（如 `.cs` / `.xml`）与 `scope`，两者都在扫描前下推生效，不是拿到结果再筛。**有命中时表头也回显这个过滤器**（`(1 matching line in scope 'base', case-insensitive, files filtered to '.xml')`）：`scope` 与 `ignoreCase` 本来就一直回显，唯独 `fileFilter` 只在零命中那一支才提——于是「这个数是全语料的还是只算了 `.xml` 的」在有命中时反而看不出来，而这正是把命中数当结论用时最要紧的一格
- 结果按文件分组，每个文件最多 3 行预览（其余记为 `+N more matching lines in this file`），最多列 50 个文件
- `limit` 默认 100 条命中。**小到咬人的 `limit` 不是「少列几行」，它把总数整个换掉**：表头退化成 `first N preview lines`、扫描当场停在那里，`N matching lines` 那个总数不再出现。想要总数就传 `limit:'all'`；够不到上限时 `limit` 才只影响列出的条数、不影响报出来的总数
- 零命中且传了 `fileFilter` 时，消息会回显该过滤器与它留下的候选文件数——`.txt` 这类把候选集筛成 0 的过滤，不该被说成「scope 里没有这个模式」
- 命中是**原始文本**：注释掉的代码、停用的 XML、注释里的散文一律计入。所以「命中 22 行」不等于「存在 22 个东西」——问「这个文件里定义了几个 def」时，要拿 `locate` / `inspect` 复核，别直接用这个数
- **同一条查询恒给同一份答案**。扫描按候选表顺序分块推进、命中按 `(文件序号, 行号)` 排序后再截，所以截断时拿到的是候选表的**前缀**：复查一遍是同一批文件，把 `limit` 调大只会往后接上更多，不会把先前给过的换掉。**候选表顺序就是印出来的顺序**（按文件名，同名再按完整路径），故「这个文件没出现在结果里」可以按字母序直接判断是真没有还是被截了
- 会让命中集不完整的路径**全部**在末尾明说，**因此没有那些提示的输出就是完整命中集**：
  - 扫描停在命中上限（此时同时提示 `limit:'all'` 可把上限抬到 200）
  - 文件数超 50（未截断时折叠行同时给出总数与已列出数 `... +82 more of 132 matching files (50 listed; narrow the pattern or the scope)`；截断状态下那个文件数只是「已扫预览里的去重文件数」、不是命中文件总数，输出会点明并因此不给 `of`）
  - 正则在某文件上超时（灾难性回溯）被中途弃扫、文件读不开被跳过、单文件只扫到 20000 行——三者都计数上报

**示例**
```text
pattern: class.*:.*ThingComp
fileFilter: .cs
```

---

###  `rimworld-searcher__list_directory`
列出某个**绝对路径**目录下的文件与子目录（子目录名以 `/` 结尾）。

**特性**
- 路径必须是服务端的已索引源根（`config.toml` 各源解析出的 `csharp` / `xml` 路径，含省略 `csharp` 时拿到的反编译输出目录）或其下级目录。白名单之外一律拒绝，**源根的父目录也在拒绝之列**。拒绝消息与工具描述都会**列出本机上真实可用的根路径**，不必先撞一次再猜。举例是**按「盘符 + 第一级目录」分族轮流取**的，不是按配置序取头几条：反编译产物在配置里排在前面，按序取只会取到 `Decompiled\*` 那一族，装着 XML 的游戏与创意工坊目录一条都露不出来，读的人会以为它们不在白名单里。那句话同时说清这个数是什么：`These 87 roots are the indexed folders of the 11 configured sources listed under 'scope' — one source usually spans several roots, so this count is not a source count.`——一个源通常摊成 `csharp` / `xml` 两条以上的根，`87` 与 `scope` 那 11 个源名不是一个量纲，并排出现时会被当成同一件事的两种说法
- 条目**先排序再截断**：子目录在前、文件在后，各自按名序。所以截断后拿到的是「按名序的前 N 个」，缺席是可推理的
- 列的是**目录在磁盘上的实际内容**，不按「索引收没收」过滤：索引从没收进来的文件、以及贴图音效这类非源码资产，一样会出现在这里。工具描述里明说了这一条——白名单是按索引源根定的，很容易被读成「列出来的都是索引里的」，据此把某个文件的存在当成它已被索引的证据
- 输出头部固定给**总条目数**。`limit` 默认 100，服务端上限 1000；传 `0` 或负数表示用满上限
- `offset` 翻页。目录条目数超过 1000 时，这是唯一能把它枚举完的途径——脚注会直接算出下一页该填的值。`search_regex` 顶不上这个用：它匹配的是**文件正文行**不是文件名，`fileFilter` 也只是路径后缀，写不出「限定在这个目录下」
- `skip_path_security = true` 时上述白名单检查整体关闭

---

###  `rimworld-searcher__sync_sources`
程序集跟随与源码同步。仅对在配置里声明了 `assemblies` 的源生效，未声明的源视为手工副本，同步流程会跳过。

**三种动作**

| `action` | 行为 |
| --- | --- |
| `check`（默认） | 只比对程序集 sha256，报告哪些源变了。只读，通常几十到几百毫秒 |
| `sync` | 对变更的源重新反编译，并就地重建索引（**不需要重启**） |
| `diff` | 列出上次同步的源码增删改；带 `file` 参数则返回该文件的行级 diff |

**参数**
- `sources`：逗号分隔的源名，限定操作范围；省略即覆盖全部可跟随源
- `file`：`diff` 专用，取自 diff 列表的相对路径，给出后返回行级 unified diff
- `method`（+ 可选 `className`）：`diff` 专用，与 `file` 同用，只 diff 该成员而不是整个文件
- `granularity`：`diff` 专用，`files`（默认）只列变更文件路径，`members` 额外解析每个**列出的**文件，报出其中哪些方法/属性/字段变了
- `version`：`diff` 专用，指定对比哪一代归档（默认最近一代）。数字与 `'v0002'` 这类显式 id 都收
- `sources` 里的名字**分两种失败**：源在 `config.toml` 里但没配 `assemblies`（如 `vanilla`）会明说「它是已配置的源、只是不可跟随，去补 `assemblies` 路径」；名字压根不存在才报「没有这个源」并列出全部已配置源。两者的修复动作不同，输出不会把它们说成同一句话
- `source_history_depth = 0` 时不留归档，`sync` 的回执**不会**再指向 `action='diff'`（那条调用必然报错），而是提示先把该项设成 ≥1
- `limit`：`diff` 专用，文件列表条数上限，或给了 `file` 时的 diff 行数上限；`granularity="members"` 下它同时是解析预算
- `offset`：`diff` 专用（不带 `file` 时），跳过前若干个变更文件，用于翻页；列表末尾会印出下一页该填的 `offset`

**行为要点**
- 反编译走进程内的 `ICSharpCode.Decompiler`，语言档位锁定 C# 9（Unity 2022.3 的实际水平），不依赖外部 `ilspycmd`
- 引用集从程序集元数据的 `AssemblyRef` 推导，mod 引用 `Assembly-CSharp`、Harmony 或其它前置 mod 都能自动解析
- mod 的多版本目录（`1.4/`、`1.5/`、`1.6/`…）只取当前游戏版本那一份，其余是历史死代码
- 内容相同的程序集按 sha256 去重，只反编译一次
- 先写暂存目录、成功后才替换，中途失败不会留下半份源码
- 输出目录若非空且缺少 `.rimsearcher-decompiled` 标记，会拒绝写入，避免配置笔误抹掉手工源码副本
- `check` 的逐行报的是**程序集自身的差异**，不是「本工具做了什么」：`6 unrecorded, 0 changed, 0 gone (of 6 assemblies)` 的判据只有一条——这些路径在上次 sync 的记录里没有哈希。表头把这件事说全（`the counts below are pending work — this call decompiled nothing`），免得待办清单被读成战果
- **「记录丢了」与「从来没反编译过」是两种相反的处境**，而它们的计数逐字同形（都是 `N unrecorded (of N)`）。判别器是输出目录里的 `.rimsearcher-decompiled` 标记：产物还在就明说「索引可以就这么用，跑 sync 只是重建记录、换不来任何查询结果的变化」；产物不在才提示该跑 sync。整批清一色时这半句归结论行说一次，混杂时才逐行印
- 同一条判据也管着挂在**每一次查询返回**末尾的那条过期提示：一处内容差异都没观察到时它说的是「本会话确认不了索引是不是最新的」，而不是「源变了、结果可能过时、去跑 sync」——后者三句都是假的，而它给出的补救是分钟级的全量重反编译

---

## 2.5 系统架构

```text
RimSearcher Architecture (Narrow)

MCP Client 
  |
  | JSON-RPC (MCP)
  v
RimSearcher.cs (runtime)
  |- request routing / concurrency / cancel / progress / logging bridge
  v
Program.cs (bootstrap)
  |- load config + PathSecurity
  |- try cache -> fallback full scan -> save cache
  |- start MCP server
  |
  +-- IndexCacheService
  |     |- .cache/index/manifest.json
  |     `- .cache/index/index.bin (compressed snapshot)
  |
  `-- UpdateChecker
        `- .cache/.update-cache (latest version + check time)

Tool Layer
  |- locate | inspect | trace | read_code | search_regex | list_directory | sync_sources
  |
  +-- SourceIndexer
  |     |- RoslynHelper / FuzzyMatcher / QueryParser
  |     `- Local C# source (decompiled or hand-copied)
  |
  +-- DefIndexer
  |     |- XmlInheritanceHelper / FuzzyMatcher / QueryParser
  |     `- Local RimWorld XML (Data/Defs...)
  |
  `-- SourceSyncService (assembly following)
        |- AssemblyScanner       sha256 + AssemblyRef -> reference set
        |- DecompileService      ICSharpCode.Decompiler, C# 9
        |- SourceHistoryStore    reverse-delta history -> .cache/index/history
        `- IndexGate / IndexRebuilder
              `- suspend queries, clear + rescan in place (no hot swap)
```

**启动流程**
1. 读取配置（优先 `RIMSEARCHER_CONFIG`，未设置时回退到同目录 `config.toml`）
2. 把 `[[sources]]` 摊平为索引侧的统一视图（同名的块归为同一个源）
3. 初始化路径安全策略
4. 自动准备缓存目录（`<exe目录>/.cache/index`）
5. 尝试加载索引缓存（`manifest.json` + `index.bin`）
6. 缓存未命中时扫描 C# / XML 并建索引，然后回写缓存
7. 冻结索引（读优化）
8. 若启用 `check_source_updates`，后台并行探测程序集与 XML 变更（只记录，不反编译）
9. 注册工具并启动 MCP 服务

**索引重建**：`sync` 之后索引会就地清空重扫，而非新建一份再切换——热替换会让新旧两份索引同时驻留、内存翻倍。代价是重建期间（vanilla 单源实测约 3 秒）到达的查询会挂起等待，完成后统一放行，因此不会读到半成品索引。

---

## 3. 典型工作流

### 场景：分析护盾腰带如何生效
1. `locate(def:Apparel_ShieldBelt)`：定位 Def
2. `inspect(Apparel_ShieldBelt)`：看合并后 XML 与关联 C# 类型
3. `inspect(RimWorld.CompShield)`：看继承链和类大纲
4. `read_code(path=CompShield.cs, methodName=CompTick)`：读取核心逻辑
5. `trace(symbol=CompShield, mode=usages)`：追踪相关引用

### 场景：游戏或 mod 更新后跟进变更
前提：相关源在配置里声明了 `assemblies`，且 `source_history_depth >= 1`。

1. 启动时后台已自动探测过。若变更涉及你正在查的内容，工具返回末尾会出现提示
2. `sync_sources(action="check")`：确认哪些源的程序集变了
3. `sync_sources(action="sync")`：重新反编译并就地重建索引，不需要重启
4. `sync_sources(action="diff")`：看这次同步改了哪些源码文件
5. `sync_sources(action="diff", sources="某Mod名", granularity="members")`：把范围收到某个 mod，并列出每个文件里具体是哪些方法/属性/字段变了
6. `sync_sources(action="diff", file="RimWorld/CompShield.cs")`：看该文件的行级改动
7. `sync_sources(action="diff", file="RimWorld/CompShield.cs", method="CompTick")`：只看某一个成员的行级改动，不必在整文件 diff 里翻找
8. 此后再查询时，若你先前问过的类型确实在这次同步中变了，返回里会点名提示

`version` 用数字往前数：`1`（或 `-1`）是最近一代归档，`2` 是再往前一代；超出保留代数会夹到最老的一代并在开头说明。写成 `v0002` 这样的字面量 id 同样有效。

`granularity="members"` 要为每个**列出的**文件建两棵语法树，解析量因此始终等于输出量，用 `limit` 一个旋钮就能控住。

**想看全量**：`limit` 单页上限 2000，超出的部分用 `offset` 翻页——列表末尾会直接印出下一页的 `offset` 该填多少。概览里单个文件最多列 20 条成员变化（防止一个被大改的文件淹没整份列表），把 `file` 收窄到该文件再加 `granularity="members"` 就会列出它全部的变动成员，不截断。

**关于提示的克制**：一条提示只在「这个会话确实问过该内容」且「它确实受影响」时才发出。同步前只判得到源级（哪个源变了），同步后有了文件级 diff 才能精确到具体类型；问过的东西一个都没变时不会打扰你。同一批变更在一个会话内也只提示一次。

---

## 4. 性能与安全

| 维度 | 当前实现 |
|------|----------|
| 索引策略 | 启动优先加载本地缓存，未命中时扫描并冻结索引（`FrozenDictionary`） |
| 索引缓存 | `manifest.json + index.bin`，默认目录 `<exe目录>/.cache/index` |
| 模糊匹配 | N-gram 候选过滤 + 评分排序 |
| 并发控制 | MCP 请求并发上限 10 |
| 正则搜索保护 | 全局/单文件命中上限 + 行数上限 + regex 超时 |
| 路径安全 | 白名单根目录校验（`skip_path_security = false` 时生效） |
| 反编译隔离 | 先写暂存目录、成功后才替换；输出目录缺 `.rimsearcher-decompiled` 标记且非空时拒绝写入 |
| 产物不外流 | 输出目录内自动写入 `.gitignore`（内容 `*`），避免反编译产物被误提交进版本库 |
| 索引重建 | 就地清空重扫而非热替换（避免内存翻倍），重建期间查询挂起等待 |
| 源码历史 | 反向增量，仅存被覆盖的旧文件，按 `source_history_depth` 轮转 |

### 索引缓存说明

- 缓存目录：`RimSearcher.Server.exe` 同目录下 `/.cache/index`
- 缓存文件：`manifest.json`（元数据）+ `index.bin`（压缩索引快照）
- 首次启动通常会全量建索引并写缓存；二次启动通常会直接命中缓存，这能显著提升二次启动速度
- 若需要强制重建，删除 `/.cache/index` 后重启该程序即可
- 当前策略下，配置路径变化或缓存结构版本变化会触发自动重建
- `verify_source_freshness`（默认开启）会把各源目录下 `.cs`/`.xml` 的**大小与修改时间**摘要一并纳入指纹，于是 Steam 更新过的 mod 也会自动触发重建。只 stat 不读文件内容，成本约百毫秒级；源全是不会变动的手工副本时可以关掉
- 语言包（选中语言的目录或 `.tar`）同样进指纹：汉化包更新既不改路径集合也不动任何 Def，不纳入的话那份带旧译名的缓存会一直命中


---

## 5. 快速开始

### 前置要求
> 运行 Release 版 `RimSearcher.Server.exe` 需要 [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；
> 
> 若需本地编译源码，则需要安装 .NET 10 SDK。

### 安装步骤
1. 从 [Releases](https://github.com/ShiomiHC/RimSearcher/releases) 下载 `RimSearcher.Server.exe`。
2. 创建 `config.toml`

配置示例：
```toml
default_scope = "base"

verify_source_freshness = true
skip_path_security      = false
check_updates           = true
check_source_updates    = true
source_history_depth    = 2

# 一个 [[sources]] 块 = 一个逻辑源的全部路径
[[sources]]
name       = "vanilla"
csharp     = 'C:\RimWorldSource\1.6\Core'
xml        = [
  'C:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs',
  'C:\SteamLibrary\steamapps\common\RimWorld\Data\Royalty\Defs',
  'C:\SteamLibrary\steamapps\common\RimWorld\Data\Biotech\Defs',
]
assemblies = 'C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'

# mod 只需指根目录，版本目录由 loadFolders.xml + game_version 自动展开
[[sources]]
name = "HAR"
mod  = 'C:\SteamLibrary\steamapps\workshop\content\294100\839005762'

# 组名 → 源名列表
[scope_groups]
base = [ "vanilla", "HAR" ]
```

> 路径用**单引号**（TOML 字面量字符串）包起来，Windows 路径可以从资源管理器整条粘进去，反斜杠不用转义。
> 双引号字符串里 `\` 是转义符，那种写法得写成 `C:\\...` 或 `C:/...`。

**最简写法**：省略 `csharp`，只指程序集目录，产物落到 `<exe目录>/Decompiled/<源名>`，无需自己规划源码目录：

```toml
[[sources]]
name       = "vanilla"
assemblies = 'C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'
```

配好后跑一次 `sync_sources(action="sync")` 即可。目录在首次启动时就会建出来（空目录不影响索引缓存），产物写入后目录内会自动带上 `.gitignore` 和 `.rimsearcher-decompiled` 标记。

字段说明（key 的大小写与 `_` / `-` 分隔不敏感，`source_history_depth`、`sourceHistoryDepth`、`source-history-depth`、`SourceHistoryDepth` 等价）：
- `[[sources]]`: 一个块声明一个逻辑源的全部路径。`csharp` / `xml` / `assemblies` 三者都可以写单个字符串或字符串数组
  - `csharp`: 源码目录。**配了 `assemblies` 时，第一个 `csharp` 路径就是反编译输出目标**，其余视为附加的只读源码目录。整个 `csharp` 省略不写时，输出目标默认为 `<exe目录>/Decompiled/<源名>`
  - `xml`: 该源的 Def 目录，可多个（各 DLC 的 `Defs`、mod 的 `Defs` + `1.6/Defs`）
  - `assemblies`: 该源的程序集目录。**配了才能被 `sync_sources` 跟随**；留空即视为手工维护的源码副本，同步流程跳过
  - `mod`: mod 根目录（可多个）。写了它就不必再手写 `xml` / `assemblies`——见下方「mod 根自动展开」。与手写的 `xml` / `assemblies` 可以并存，展开结果追加在手写项之后
  - `active_mods`: 判定 `loadFolders.xml` 条件目录用的 packageId 白名单。留空即条件目录全收；配了就是「只有这些前置算启用」，其余条件目录一概不收。只在启动日志报出互斥分支时才需要配
- `[scope_groups]`: 作用域组，组名 → 源名列表；一个源可同属多组，组内顺序即同分时的排序优先级。组名会连同它的成员一起写进每个工具的 `scope` 参数说明（`groups: base (vanilla + HAR), …`）——否则返回里的 `scope: base` 与结果行的 `[vanilla]` 标签并排出现，而两者并不等价，调用方只能把它们当同义词
- `default_scope`: 未显式传 `scope` 参数时使用的作用域表达式；留空即全域。它是每个工具 `scope` 参数说明的**首句**，且就地展开成源名与后果（`default: 'base' = vanilla + HAR only, not everything installed — pass 'all' for that`）——排在段末时几乎必然被读成「默认应该是全部」，而 `base` 这个词本身也自带「基准全集」的暗示：问「有没有 mod 继承了 X」时，默认作用域恰好保证查不出来
- `verify_source_freshness`: 把源文件的大小/修改时间摘要纳入缓存指纹，让 Steam 更新过的 mod 自动触发索引重建（代价是启动时多几百毫秒的元数据枚举）
- `skip_path_security`: `true` 时关闭路径白名单检查（仅建议本地可信环境）
- `check_updates`: 是否启用版本更新提示（指 RimSearcher 自身的版本，与源跟随无关）
- `localization`: 查 def 时附带哪种语言的译名，默认 `"auto"`——读游戏 `Prefs.xml` 里的 `langFolderName`，读不到就不做本地化。也可以直接写语言名（`"ChineseSimplified"`，带不带 `(简体中文)` 后缀都认），或写 `"off"` 关掉。别名 `language` / `lang`
- `localization_description`: 是否连译文描述一起显示，默认 `false`。开启后只在 `inspect` 里出现（截断到 300 字），`locate` 永远只给译名
- `check_source_updates`: 是否在启动时后台探测程序集与 XML 变更。只检测不反编译；发现变更且与当前会话查过的内容相关时，会在工具返回末尾附一条提示。默认 `true`
- `source_history_depth`: 保留几代反编译历史供 `diff` 使用，`0` 为不保留（默认）。每代只存本次被覆盖的旧文件（反向增量），一次游戏更新通常只动少量文件，占用远小于同等份数的完整副本
- `game_version`: mod 多版本目录的匹配键（如 `"1.6"`）。留空则从 `assemblies` / `mod` 路径上溯查找 `Version.txt` 自动判定
- `decompile_output_root`: 省略 `csharp` 时，默认输出目录的根。留空即 `<exe目录>/Decompiled`（与 `.cache/index` 同处一地）；写相对路径按 exe 目录解析。装在 `C:\Program Files` 之类不可写的位置时，改配一个可写目录
- `share_index_host`: 多个 MCP client 各起一个进程时，是否让首个实例成为索引宿主、后续实例只做 stdio↔管道转发。默认 `true`；关掉的话每个进程各建一份索引（每份约 1 GB）。会合点按配置指纹划分，`skip_path_security` / `default_scope` / `scope_groups` 等会改变回答的项不同就不会共享同一个宿主
- `idle_timeout_minutes`: 空闲这么久后自动退出，`0`（默认）为不启用。父进程守护恒开，故这只是额外的兜底闸

**写错了会告诉你在第几行**：配置解析失败时，启动日志带的是 TOML 解析器的诊断，形如

```text
[ERROR] Program: Failed to load configuration | path=D:\...\config.toml,
        reason=(3,10) : error : Unexpected token found `␤` (token: `newline`) while expecting `]]` (token: `closebracketdouble`) | (4,1) : error : ... (+1 more)
```

而不是笼统的一句「解析失败」。同一类笔误重复多处时最多列前三条。「文件还没建」与「文件写错了」在日志里是两条不同的原因。

**mod 根自动展开**：`mod` 指向 mod 根目录后，工具按 RimWorld 自己的加载规则算出「这个游戏版本下真正生效的目录」，旧版本的 XML 和 dll 一律不进索引。

规则逐条核对过 1.6 的 `ModContentPack.InitLoadFolders` / `ModLoadFolders` / `LoadFolder.ShouldLoad` / `ModLister.AnyModActiveNoSuffix`：

- 有 `loadFolders.xml` 就以它为准。节点选择顺序是 `<v1.6>` → `≤1.6` 的最高版本节点 → `<default>`；节点内的列表**越靠后优先级越高**
- 没有 `loadFolders.xml` 则用默认布局：`1.6/` → `Common/` → 根目录，优先级依次递减
- 覆盖是**文件级**的，按相对于 mod 文件夹根的路径比对，不是 def 级合并——`Defs/Traits.xml` 只要在 `1.6/Defs/Traits.xml` 有同名文件，根目录那份整个不解析。同名 dll 同理
- 只收 `Defs`、`Patches`、`Assemblies`，`Languages` / `Textures` / `Sounds` 不进索引
- 源名取 `About.xml` 里的 `<name>`（workshop 目录名是纯数字 ID）；显式写了 `name` 则以显式的为准

**条件目录**（`IfModActive` = 任一启用、`IfModActiveAll` = 全部启用、`IfModNotActive` = 任一启用即排除，三者可并存取合取；packageId 比对不分大小写且忽略 `_steam` 后缀，回显则保留 `loadFolders.xml` 里的原样拼写）默认**全部收下**——手动指 mod 根时无从判断哪些 mod 处于启用状态，索引宽一点无害。**收了哪几个会在启动提示里点名**（``14 conditional folders in loadFolders.xml included unconditionally (1.6/Royalty, 1.6/Biotech, 1.6/CE/PLA and 11 more) — results from inside one come back tagged `[conditional: <folder>]` ``）：只说「收了 N 个」的话，调用方拿到一条 `1.6/CE/Defs/…` 下的命中时无从判断它是不是那 N 个之一，而这类目录的内容在没装对应前置的实机上根本不加载。

这份名单是**整份索引**的口径，回答不了「我手上这一条呢」——它对每一次调用都成立，因而对具体某一条什么也没说。故落在这些目录里的结果**逐条打标** `[conditional: 1.6/CE]`，成因由整份返回末尾的脚注给出（见「低 Token 消耗」一节）。程序集也算：`1.6/CE/Assemblies/EmbergardenCE.dll` 反编译出来的那棵源码树在 `Decompiled/Cinders/EmbergardenCE/`，与条件目录字面上毫无关系，靠 dll 基名映射回去后同样打标——否则脚注最后那句「没打标就不在条件目录里」对 C# 那一侧就是假的。

但有一种情形宽不得：**一个 mod 用两组互斥条件挂了两套内容**（前置 A 装了用这套、装了 B 用那套）。此时两套的文件同名，谁遮蔽谁由 `loadFolders.xml` 的书写顺序决定，而不是由哪个前置真的启用着决定——搜到的可能恰好是运行时不生效的那套。这种情形会在启动日志里报出来：

```text
[WARN] Mod layout note | detail=RatkinGene: mutually exclusive conditional folders, both included:
       Common [Solaris.RatkinRaceMod] vs Common [fxz.Solaris.RatkinRaceMod.odyssey] — set active_mods to pick one
```

照提示给那个源加 `active_mods` 即可选定一支：

```toml
[[sources]]
name        = "RatkinGene"
mod         = 'C:\SteamLibrary\steamapps\workshop\content\294100\3043354134'
active_mods = [ "Solaris.RatkinRaceMod" ]
```

`active_mods` 是白名单语义：配了之后该源的条件目录只认列出的这些前置，没列的一概不收（DLC 补丁目录也一样，需要的话把 `Ludeon.RimWorld.Ideology` 这类一并列上）。若白名单把内容全筛没了，会回退到全收并记日志——不让一个明确配了的 mod 变成空的。

实测 257 个已订阅 mod 里只有 4 个存在互斥分支，其余不必管这个字段。

以 HAR（`839005762`）为例，`game_version = "1.6"` 下展开的结果是：

```text
生效目录（优先级从高到低）
  1.6\Mods\Odyssey\Patches
  1.6\Mods\Ideology\Defs
  1.6\Defs
  1.6\Patches
  Defs                          ← 仍在列，它可能有独有文件
生效程序集
  1.6\Assemblies
  Assemblies
被顶掉、不进索引的文件
  Defs\ThingCategories.xml      ← 被 1.6\Defs\ 同名文件覆盖
  Defs\Thoughts.xml
  Defs\Traits.xml
  Assemblies\0Harmony.dll       ← 被 1.6\Assemblies\ 同名 dll 覆盖
  Assemblies\AlienRace.dll
```

`1.0`–`1.5` 六个版本目录一份都不进。该 mod 的根 `Defs` 三个文件恰好全被顶掉，正是那种「搜到的是 1.0 时代老定义」的陷阱。

mod 没适配当前版本（只有 `1.4/` 目录）时会回退到能用的最高版本并在日志里说明——按游戏语义它本该什么都不加载，但既然是手动指的，多半就是想搜它。展开结果与所有降级说明都记在启动日志的 `Mod folders resolved` / `Mod layout note` 两行里。

**源命名与作用域**：`name` 相同的多个条目归为**同一个源**，因此一个逻辑源可以跨多个根目录（如 HAR 的 C# 目录 + 两个 Defs 目录）。省略 `name` 时按路径末段推断（会跳过 `Defs`、`1.6` 这类无信息量的段）。

所有查询工具都接受 `scope` 参数：

| 写法 | 含义 |
| --- | --- |
| `scope: "vanilla"` | 单个源 |
| `scope: "base"` | 一个作用域组 |
| `scope: "vanilla,Milira"` | 并选多个（书写顺序 = 同分时的优先级） |
| `scope: "all"` | 全部源 |
| `scope: "all,-vanilla"` | 排除（`-` 或 `!` 前缀） |
| 不传 | 落到 `default_scope`（**不是全域**；本机默认是 `base` = vanilla + HAR） |

`locate` 还接受写在查询串里的 `scope:` 前缀（如 `"scope:mods pawn"`），与 `type:` / `def:` 等前缀同一套写法。

**参数名认不出时会说一声。** 各工具的主参数名互不相同（`locate=query` / `inspect=name` / `read_code=path` / `trace=symbol` / `search_regex=pattern`），服务端统一吸收别名与大小写/下划线差异。但把某个工具**独有**的参数类推到另一个工具上（`locate` 传 `defType`、`trace` 传 `fileFilter`）不会生效——这类键会被丢弃，返回末尾追加一行 `_Ignored unknown parameter(s): …_` 并列出本工具真正接受的参数。**没有这行就是全部参数都生效了**；反过来，看到它就说明你以为的那道过滤根本没发生，手上这份是未过滤的前 N 条。

选中多个源时，结果每行尾部标注来源（如 `[vanilla]`、`[Milira]`）。落在作用域**之外**的命中会在结果末尾汇总计数（`Outside scope 'base': Ratkin 8, Milira 1`），避免把「当前作用域搜不到」误读成「不存在」；`trace usages` 与 `search_regex` 因为要真读文件，不做这项统计，作用域对它们是硬过滤。

`limit` 参数控制每段结果条数，传 `"all"`（`0` 与负数同义）展开到服务端硬上限 200。默认值按工具而异：`locate` 是 10，`trace usages` 是 50，`search_regex` 是 100，`trace inheritors` 直接就是硬上限 200（子类树默认一次给全）。`list_directory` 的 `limit` 不走这套，见上文该工具一节；`inspect` 的 `limit`（大纲每类成员数）也不受 200 上限夹持，`'all'` 在那里是真无限。**解释不了的值（`"many"`、`true`、对象）一律报错，不会被静默换成默认值**——静默退回默认给出的是子集，调用方会把「少给的那部分」读成「一共就这么多」。低相关度结果会在出现明显分数断层时另行折叠，折叠行注明 `lower relevance`——**那一部分与 `limit` 无关，调多大都拿不回来**。折叠行会分别说清是哪一种，并且**把方向写进句子里**：`use a shorter, less specific query; folding is relative to the top score, so narrowing never brings these back and limit does not expand them`。断层是相对**本次最高分**算的，故收窄查询词只会把最高分推得更高、折掉更多——而「拿不回来」这半句单独出现时，默认读法恰好是往收窄的方向再试一次。

3. 在 MCP 客户端中把 `RimSearcher.Server.exe` 注册为 **stdio MCP Server**，并设置环境变量 `RIMSEARCHER_CONFIG` 指向上一步的 `config.toml`。

> 兼容模式说明：
> - 若设置了 `RIMSEARCHER_CONFIG`，优先读取该路径。
> - 若未设置，则回退到 `RimSearcher.Server.exe` 同目录下的 `config.toml`。

### 安装到 AI 助手（不同客户端配置差异）

#### 通用 MCP 客户端（Claude Desktop / Gemini CLI / Cursor 等）
```json
{
  "mcpServers": {
    "RimSearcher": {
      "command": "D:/Tools/RimSearcher/RimSearcher.Server.exe",
      "args": [],
      "env": {
        "RIMSEARCHER_CONFIG": "D:/your/custom/path/config.toml"
      }
    }
  }
}
```

#### GitHub Copilot（`servers` 结构）
```json
{
  "servers": {
    "RimSearcher": {
      "command": "D:/Tools/RimSearcher/RimSearcher.Server.exe",
      "args": [],
      "env": {
        "RIMSEARCHER_CONFIG": "D:/your/custom/path/config.toml"
      }
    }
  }
}
```

#### OpenCode（`mcp` 结构）
```json
{
  "mcp": {
    "RimSearcher": {
      "type": "local",
      "command": ["D:/Tools/RimSearcher/RimSearcher.Server.exe"],
      "enabled": true,
      "environment": {
        "RIMSEARCHER_CONFIG": "D:/your/custom/path/config.toml"
      }
    }
  }
}
```

常见注意事项：
- `command` 使用 `RimSearcher.Server.exe` 的绝对路径。
- 推荐始终配置 `RIMSEARCHER_CONFIG` 指向明确路径，避免多环境切换时误读配置。
- 若不设置 `RIMSEARCHER_CONFIG`，才要求 `config.toml` 与 exe 在同一目录。
- 修改客户端 MCP 配置后，重启客户端或重载 MCP 服务。
- 若客户端有工具白名单/权限开关，确保已允许 `RimSearcher`。

### 本地验证
手动验证时：
- 方式 A：设置环境变量 `RIMSEARCHER_CONFIG` 指向目标 `config.toml`。
- 方式 B：不设置环境变量，把 `config.toml` 放到 `RimSearcher.Server.exe` 同目录。

![配置示例](Image/Snipaste_2026-02-07_23-20-57.png)

然后运行 `RimSearcher.Server.exe`，若最后一条看到类似的JSON-RPC2.0日志即表示启动成功（不同版本可能看到的日志不同，但只要看到`RimSearcher MCP server started`都可视为成功启动）：
- 首次构建：`Program: Cache unavailable, rebuilding index` -> `Program: Index build completed ...` -> `Program: Index cache saved`
- 缓存命中：`Program: Index loaded from cache`
- 服务就绪：`RimSearcher MCP server started`

![启动成功示例](Image/Snipaste_2026-02-27_16-12-43.png)

快速检查是否接入成功：
- 客户端工具列表中能看到 7 个工具。支持 MCP `title` 字段的客户端显示的是短名（`locate`、`inspect`……），不支持的则显示完整名（`rimworld-searcher__locate` 这种形式）——两者是同一批工具，调用时用的始终是完整名。
- 执行一次 `locate`（例如 `def:Apparel_ShieldBelt`）能返回结果。

---

## 6. 更新提示说明

- 更新检查为非阻塞后台任务，不影响核心检索服务。
- 仅在 `check_updates = true` 时启用。
- 若遇到 GitHub 匿名限流，更新检查会静默失败，不影响工具功能。
- 更新信息默认通过日志通道输出；若 MCP 客户端不展示日志，则可能看不到该提示。
- 更新检查缓存文件路径：`<exe目录>/.cache/.update-cache`（与 `index` 文件夹同级）。

---

## 免责声明

- 本项目为第三方开源工具，与 Ludeon Studios 及 RimWorld 官方无隶属、赞助或背书关系。
- 本工具仅对用户本地提供的源码/XML进行索引与检索，不内置或分发任何游戏原始资源。
- 本工具可对使用者**自行配置的本地程序集**执行反编译（`sync_sources`，需显式触发），反编译产物仅写入本地目录，不上传、不分发。
- **反编译产物请勿提交至版本库或再分发**。它是游戏/Mod 代码的衍生物：留在本地供自己调试、查接口、做兼容属于常规的 modding 用途，公开分发则另当别论。工具已在输出目录内写入 `.gitignore` 以降低误提交风险，但这只是机制兜底，不替代使用者的判断。
- 第三方 Mod 的程序集各有其许可条款（不少为保留所有权利）。参考其实现以实现兼容是一回事，把反编译得到的代码搬进自己的项目是另一回事。
- 检索与分析结果仅供学习、调试与研究参考。
- 使用者应自行确保其数据来源、反编译行为与使用方式符合当地法律法规、RimWorld 相关协议及各 Mod 许可证要求。
- 因使用本工具造成的任何直接或间接损失，项目作者与贡献者不承担责任。

---

## License
MIT

> 如果这个项目对你有帮助，欢迎点个 Star⭐。
