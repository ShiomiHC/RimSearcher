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
- **同源标签只印一次**：一段结果全部来自同一个源时，`[vanilla]` 这类来源标记提到表头（`**C# Types** [vanilla]:`），逐行不再重复；真的混源时才逐行印。于是**行末出现来源标记，本身就是「这段结果跨了多个源」的信号**。`scope` 已经把源钉死时一个标记都不印
- **截断提示全服一套文法**：`... +N more <什么> (<怎么拿到>)`。看到 `... +` 开头的行就是被截断了，括号里那句就是下一步该怎么传参，各工具不必分别认。例：
  `... +71 more methods (pass limit:'all' for the whole list, or read one with read_code methodName)` ·
  `... +367 more lines (pass startLine=20)` · `... +43 more entries (pass offset=7 for the next page, or a larger limit)`
- 扫描类工具（`trace usages` / `search_regex`）停在预览上限时另有一句共用的 `... more matches exist (…)`——与 `... +N more` 的区别是**它数不出还剩多少**（后面的文件根本没打开过），故不给数字。括号里同样给下一步；`limit` 已经是 `'all'` 时不会再劝你提 `limit`
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
- CamelCase 缩写与拼写容错（如 `JDW`）
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
- **不在上表里的前缀不是过滤器**，整个 token 会当成普通搜索词去匹配（于是 `member:CompTick` 零命中，而 `method:CompTick` 有一百多条）。这种情况返回会明确点出来，不再让调用方把「前缀写错了」读成「这个符号不存在」

**结果分段**：`C# Types` / `Members` / `XML Defs` / `Content Matches`（按 Def 的字段值命中，而非按名），每段各自受 `limit` 约束并独立折叠。表头一行给出**各段各几条**（`## 'Pawn' — 5 C# types, 5 members`），不必自己数行就能判断要不要调 `limit`。另有 `Files` 段（已索引的文件路径）：四段全部零命中时它是兜底，按名模糊列出若干条；四段有命中时它只补上**基名与查询词逐字相同**的那一份，且不重复已经出现在 `C# Types` 里的同名项——文件名是一等查询目标，不该因为顺带蹭到一条低分 def 就整段消失。

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
- 展示 Def 类型、来源文件、译名（`localization_description` 开启时连译文描述一起）
- 返回沿整条 `ParentName` 链合并后的 XML，即完整生效定义——任何单个 XML 文件都不含这份内容
- 头部固定给一行**父链状态**：合并成功印 `Inheritance chain: A <- B`，没有父则明说，某一环查不到则给**警告**并指出「下面这份不是完整生效定义、继承来的字段全缺」。三种情形此前渲染得逐字同形，调用方无从分辨自己拿到的是不是半成品
- 合并 XML 过长会被截断（首屏给头 200 行 + 尾 50 行）。**续读用 `xmlStartLine`，不要去 `read_code` 读 `File:` 那个路径**：那份文件里只有该 def 自己未合并的几行，继承来的字段恰恰不在其中。截断提示会直接给出下一次该填的 `xmlStartLine`
- 提取关联 C# 类型并尝试映射到索引文件。文件名同样只在推不出来时才印（与 `locate` / `trace` 同一条判据）；`C# Class:` 那一行**存在与否**本身就是「这个 DefType 的 C# 类在不在当前作用域的索引里」
- `defType` 参数用于同名 def 撞车时指定看哪一个（`Human` 同时是 ThingDef、BodyDef 和 HediffGiverSetDef）。不传时返回会列出所有同名类型，据此再传一次即可。它是 def 类型，不用于收窄 C# 模式

**C# 模式**
- 返回**基类链**，与 def 模式同一种写法的一行式：`Inheritance chain: Pawn <- ThingWithComps <- Thing <- Entity`（链上用短名，全限定名在大纲的 `Class:` 行）。接口不在这条链上，要看实现关系用 `trace mode:"inheritors"`
- 返回类成员大纲：字段、属性、方法签名。构造器、索引器、运算符不进大纲，但 `read_code` 仍能按名读到它们
- 枚举列出其值（含显式赋的数值，如 `Resetting = 7`；表头 `Enum:` 已经说明下面每行是什么，取值行不再逐行挂种类前缀），委托列出其签名（含类型参数表与约束）
- 大纲每类成员默认最多列 40 条（三类各自独立计数），超出的在原处标明还剩多少。**取回被折叠的成员只有一条路：`limit:'all'`**（`limit` 也收具体数字）。`locate` 要先知道成员名字、`read_code extractClass` 到 2000 行就二次截断，对触发折叠的大类型这两条都走不通
- 这里的 `'all'` 是**真无限**，不受其他工具那个 200 条服务端上限的夹持——单个类型成员数过 200 是常态（`Pawn` 有 326 个）。全量大纲仍比读一遍类体便宜得多
- 同名类型分散在多个源时，只渲染作用域里优先级最高的那一份大纲，其余只报路径——几份大纲通常高度重合，而体积按文件数翻倍
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
- `inheritors`：列出某基类/接口的**传递闭包**子类与实现类树——间接后代（子类的子类）同样列出。默认直接展开到服务端硬上限 200，一次拿全整棵树。截断时保留的是浅层：调用方先要的是「谁直接继承了它」
  - **只有间接后代带标记**（`[depth 2]` 起）；直接子类不标，表头会在真有深层项时补一句 `untagged = direct`。整棵树全是直接子类时一个标记都不印，那本身就是答案
  - 零结果分两种，措辞不同：**索引里没有这个类型名**（拼写待核，去 `locate`）与**类型在索引里、只是没人继承它**（这已经是答案）
- `usages`：符号的**逐行文本匹配**（不分大小写的全词匹配），C# 与 XML 都扫，带行号预览。默认 50 条，每个文件最多 3 行预览，其余记为 `+N more in this file`
  - 与 `search_regex` 同款保证：**同一条查询恒给同一份答案**，截断时拿到的是文件表的**前缀**（按文件名排序，扫描与展示同一个顺序），把 `limit` 调大只会往后接上更多文件，不会把先前给过的换掉

> `usages` 不是调用图：同名成员挂在无关类型上也会混进同一份列表，而经由继承发生的调用则会漏掉。

**示例**
```text
symbol: ThingComp, mode: inheritors
symbol: CompShield, mode: usages
```

---

###  `rimworld-searcher__read_code`
从**某一个指定文件**里精确读取源码。`path` 收的是文件（已索引的文件名或绝对路径），不是搜索词——手上只有搜索词时先走 `locate`。

**三种互斥模式**（同时传多个时，`extractClass` > `methodName` > 行区间）

| 模式 | 参数 | 说明 |
| --- | --- | --- |
| 整个类型 | `extractClass` | 类/结构/接口/记录的完整实现体，枚举与委托声明同样可取。上限 2000 行（与行区间模式同一个上限），超出会截断并报出**这个类自己有多少行**，据此选下一步：`methodName` 单取一个成员，或 `startLine` 接着往下读 |
| 单个成员 | `methodName`（+ 可选 `className`） | 方法、属性、字段、事件、构造器（类名或 `.ctor`）、索引器（`this`）、运算符（`+`）、枚举值——凡 `locate` 列得出的成员都行。文件里同名成员会**全部**返回，传 `className` 才只取一个 |
| 裸行区间 | `startLine` + `lineCount` | `startLine` 为 0 基；未指定成员时走这条 |

前两种要解析 C#，**XML 文件只有行区间模式可用**（读 Defs 原文就走这条）。

**路径支持**
- 绝对路径
- 已索引文件名（如 `CompShield.cs`）
- 文件基名（如 `CompShield`）

**返回里必须读到的四件事**
- 三种模式的头部都印**解析后的绝对路径**，成员与整类模式统一为一行 `// <种类> <名字>[ in <所属类型>] — <路径>:<行号>`（如 `// Method CompTick in RimWorld.CompShield — …/CompShield.cs:118`）。`in <所属类型>` 只在它与成员名不同名时出现。文件里有多个同名成员时，每条正文之前各一行并带 `[i/N]` 编号——**看到 `[3/3]` 就是拿全了**，不必猜后面还有没有
- 传基名时，`scope` 决定哪个源胜出；作用域内有多份同名文件时会追加一行 `note: N files share this name in scope …` 并列出其余候选——不看这行就可能把某个 mod 的覆盖版当成 vanilla 原版
- `className` 只是**过滤器**。过滤后没有候选时，返回会说清「这个成员确实在这个文件里，只是不在你点的那个类里」并列出它实际声明在哪几个类型、第几行；只有整个文件里都没有才报 not found
- 传了目录会明说「这是目录，不是文件」并指向 `list_directory`；文件找不到时**回显你给的整条路径**，并区分「这条路径在磁盘上不存在」与「没有同名文件进过索引」

**示例**
```text
path: CompShield.cs, methodName: CompTick
```

---

###  `rimworld-searcher__search_regex`
在已索引的 C# 与 XML 上跑 .NET 正则。

**特性**
- 可选 `fileFilter`（如 `.cs` / `.xml`）与 `scope`，两者都在扫描前下推生效，不是拿到结果再筛
- 结果按文件分组，每个文件最多 3 行预览（其余记为 `+N more in this file`），最多列 50 个文件
- `limit` 默认 100 条命中
- 零命中且传了 `fileFilter` 时，消息会回显该过滤器与它留下的候选文件数——`.txt` 这类把候选集筛成 0 的过滤，不该被说成「scope 里没有这个模式」
- **同一条查询恒给同一份答案**。扫描按候选表顺序分块推进、命中按 `(文件序号, 行号)` 排序后再截，所以截断时拿到的是候选表的**前缀**：复查一遍是同一批文件，把 `limit` 调大只会往后接上更多，不会把先前给过的换掉。**候选表顺序就是印出来的顺序**（按文件名，同名再按完整路径），故「这个文件没出现在结果里」可以按字母序直接判断是真没有还是被截了
- 会让命中集不完整的路径**全部**在末尾明说，**因此没有那些提示的输出就是完整命中集**：
  - 扫描停在命中上限（此时同时提示 `limit:'all'` 可把上限抬到 200）
  - 文件数超 50（截断状态下那个文件数只是「已扫预览里的去重文件数」，不是命中文件总数，输出会点明）
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
- 路径必须是服务端的已索引源根（`config.toml` 各源解析出的 `csharp` / `xml` 路径，含省略 `csharp` 时拿到的反编译输出目录）或其下级目录。白名单之外一律拒绝，**源根的父目录也在拒绝之列**。拒绝消息与工具描述都会**列出本机上真实可用的根路径**，不必先撞一次再猜
- 条目**先排序再截断**：子目录在前、文件在后，各自按名序。所以截断后拿到的是「按名序的前 N 个」，缺席是可推理的
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
- `[scope_groups]`: 作用域组，组名 → 源名列表；一个源可同属多组，组内顺序即同分时的排序优先级
- `default_scope`: 未显式传 `scope` 参数时使用的作用域表达式；留空即全域
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

**条件目录**（`IfModActive` = 任一启用、`IfModActiveAll` = 全部启用、`IfModNotActive` = 任一启用即排除，三者可并存取合取；packageId 比对不分大小写且忽略 `_steam` 后缀）默认**全部收下**——手动指 mod 根时无从判断哪些 mod 处于启用状态，索引宽一点无害。

但有一种情形宽不得：**一个 mod 用两组互斥条件挂了两套内容**（前置 A 装了用这套、装了 B 用那套）。此时两套的文件同名，谁遮蔽谁由 `loadFolders.xml` 的书写顺序决定，而不是由哪个前置真的启用着决定——搜到的可能恰好是运行时不生效的那套。这种情形会在启动日志里报出来：

```text
[WARN] Mod layout note | detail=RatkinGene: mutually exclusive conditional folders, both included:
       Common [solaris.ratkinracemod] vs Common [fxz.solaris.ratkinracemod.odyssey] — set active_mods to pick one
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
| 不传 | 落到 `default_scope` |

`locate` 还接受写在查询串里的 `scope:` 前缀（如 `"scope:mods pawn"`），与 `type:` / `def:` 等前缀同一套写法。

**参数名认不出时会说一声。** 各工具的主参数名互不相同（`locate=query` / `inspect=name` / `read_code=path` / `trace=symbol` / `search_regex=pattern`），服务端统一吸收别名与大小写/下划线差异。但把某个工具**独有**的参数类推到另一个工具上（`locate` 传 `defType`、`trace` 传 `fileFilter`）不会生效——这类键会被丢弃，返回末尾追加一行 `_Ignored unknown parameter(s): …_` 并列出本工具真正接受的参数。**没有这行就是全部参数都生效了**；反过来，看到它就说明你以为的那道过滤根本没发生，手上这份是未过滤的前 N 条。

选中多个源时，结果每行尾部标注来源（如 `[vanilla]`、`[Milira]`）。落在作用域**之外**的命中会在结果末尾汇总计数（`Outside scope 'base': Ratkin 8, Milira 1`），避免把「当前作用域搜不到」误读成「不存在」；`trace usages` 与 `search_regex` 因为要真读文件，不做这项统计，作用域对它们是硬过滤。

`limit` 参数控制每段结果条数，传 `"all"`（`0` 与负数同义）展开到服务端硬上限 200。默认值按工具而异：`locate` 是 10，`trace usages` 是 50，`search_regex` 是 100，`trace inheritors` 直接就是硬上限 200（子类树默认一次给全）。`list_directory` 的 `limit` 不走这套，见上文该工具一节；`inspect` 的 `limit`（大纲每类成员数）也不受 200 上限夹持，`'all'` 在那里是真无限。**解释不了的值（`"many"`、`true`、对象）一律报错，不会被静默换成默认值**——静默退回默认给出的是子集，调用方会把「少给的那部分」读成「一共就这么多」。低相关度结果会在出现明显分数断层时另行折叠，折叠行注明 `lower relevance`——**那一部分与 `limit` 无关，调多大都拿不回来**，只能靠更精确的查询词或换个过滤前缀。折叠行会分别说清是哪一种。

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
