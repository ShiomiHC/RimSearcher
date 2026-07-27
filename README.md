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

### 面向查询性能优化
- 预建索引 + N-gram 候选筛选
- 启动后冻结索引（`FrozenDictionary`）优化只读查询吞吐
- 搜索结果带上限控制，避免超长输出拖慢上下文

### 低 Token 消耗（LLM 友好）
- 采用先定位再深入的查询链路（`locate` → `inspect`/`trace` → `read_code`），避免一次返回大段无关文本
- `locate` / `trace` / `search_regex` 工具采用结果上限与预览截断，控制上下文体积并保持关键信息密度
- `read_code` 支持按 `methodName`/`extractClass` 精确读取代码，未指定成员时再按小范围行号读取，避免一次返回整个文件

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
全局模糊定位入口。

**支持内容**
- C# 类型、成员（方法/属性/字段）、XML Def、文件名
- 过滤语法：`type:` `method:` `field:` `def:` `scope:`
- CamelCase 缩写与拼写容错（如 `JDW`）
- `scope` / `limit` 参数（见「配置」一节）

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
深度分析单个 Def 或 C# 类型。

**Def 模式**
- 展示 Def 类型、来源文件
- 返回继承合并后的 XML
- 提取关联 C# 类型并尝试映射到索引文件

**C# 模式**
- 返回继承关系图
- 返回类成员大纲（字段/属性/方法签名）

**示例**
```text
Apparel_ShieldBelt
RimWorld.CompShield
```

---

###  `rimworld-searcher__trace`
交叉引用追踪工具。

**模式**
- `inheritors`：列出某基类/接口的子类
- `usages`：查找符号文本引用（C# + XML），带行号预览

**示例**
```text
symbol: ThingComp, mode: inheritors
symbol: CompShield, mode: usages
```

---

###  `rimworld-searcher__read_code`
精确读取 C# 代码片段。

**支持读取方式**
- 指定成员：`methodName`（支持方法/属性/构造器/索引器/运算符）
- 指定类型：`extractClass`
- 指定行区间：`startLine` + `lineCount`

**路径支持**
- 绝对路径
- 已索引文件名（如 `CompShield.cs`）
- 文件基名（如 `CompShield`）

**示例**
```text
path: CompShield.cs, methodName: CompTick
```

---

###  `rimworld-searcher__search_regex`
全局正则检索（C# + XML）。

**特性**
- 可选 `fileFilter`（如 `.cs` / `.xml`）
- 结果按文件分组，显示行号预览
- 内置输出截断提示，避免超大响应

**示例**
```text
pattern: class.*:.*ThingComp
fileFilter: .cs
```

---

###  `rimworld-searcher__list_directory`
目录浏览工具。

**特性**
- 列出目录下文件与子目录（子目录以 `/` 结尾）
- 支持 `limit` 分页提示
- 受 `PathSecurity` 白名单约束（除非显式关闭）

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
- `version`：`diff` 专用，指定对比哪一代归档（默认最近一代）
- `limit`：`diff` 专用，文件列表条数上限，或给了 `file` 时的 diff 行数上限

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
5. `sync_sources(action="diff", file="RimWorld/CompShield.cs")`：看该文件的行级改动
6. 此后再查询时，若你先前问过的类型确实在这次同步中变了，返回里会点名提示

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

字段说明（key 的大小写与 `_` / `-` 分隔不敏感，`source_history_depth`、`source_history_depth`、`source_history_depth` 等价）：
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
- `check_source_updates`: 是否在启动时后台探测程序集与 XML 变更。只检测不反编译；发现变更且与当前会话查过的内容相关时，会在工具返回末尾附一条提示。默认 `true`
- `source_history_depth`: 保留几代反编译历史供 `diff` 使用，`0` 为不保留（默认）。每代只存本次被覆盖的旧文件（反向增量），一次游戏更新通常只动少量文件，占用远小于同等份数的完整副本
- `game_version`: mod 多版本目录的匹配键（如 `"1.6"`）。留空则从 `assemblies` / `mod` 路径上溯查找 `Version.txt` 自动判定
- `decompile_output_root`: 省略 `csharp` 时，默认输出目录的根。留空即 `<exe目录>/Decompiled`（与 `.cache/index` 同处一地）；写相对路径按 exe 目录解析。装在 `C:\Program Files` 之类不可写的位置时，改配一个可写目录

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

选中多个源时，结果每行尾部标注来源（如 `[vanilla]`、`[Milira]`）。落在作用域**之外**的命中会在结果末尾汇总计数（`Outside scope 'base': Ratkin 8, Milira 1`），避免把「当前作用域搜不到」误读成「不存在」；`trace usages` 与 `search_regex` 因为要真读文件，不做这项统计，作用域对它们是硬过滤。

`limit` 参数控制每段结果条数（默认 10），传 `"all"` 展开全部。低相关度结果会在出现明显分数断层时折叠，折叠行注明 `lower relevance`。

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
- 客户端工具列表中能看到 `rimworld-searcher__locate`、`rimworld-searcher__inspect` 等 6 个工具。
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
