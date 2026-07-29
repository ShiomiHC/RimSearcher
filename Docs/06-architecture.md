# 06 · 架构设计(2026-07-29)

定位:00 拍板骨架(DataMod 导出 → SQLite → CLI → skill)之后的分层设计产地。
每个设计点标注择优来源:**[上游]** / **[本地]**(master 资产,01 有账)/ **[全新]** /
**[混合]**。缺陷修复项引 02 编号,调查事实引 03/05,不复述内容。

## 需求口径(本轮 grill 敲定,用户裁决)

1. **受众**:本机自用为主,留发布缝 —— 目录结构与 skill 不写死本机路径;不做 update
   命令 / 安装管线 / GUIDED_SETUP(02-8 的 UpdateChecker 两条教训转为发布缝备忘,启用时再兑现)。
2. **输出形态**:结构化主体 + 散文声明区。随调用变化的自证(截断/过期/未知 flag)必须在
   stdout 该次输出里;恒定教学入 SKILL.md;参数声明产地唯一在代码(判据:R51 —— 声明要
   写进它作用的那个块)。
3. **C# 侧**:符号级阅读外包 DecompilerServer MCP(00 已裁,不动);跨文件正则**收进 CLI**
   (`code-search`,修正 01 对 SourceIndexer「整个扔掉」的口径:正则扫描段带走);
   反编译落盘再生成短期由 master 的 sync_sources 代管(master 转维护但活着)。
4. **快照**:多快照,身份 = db meta 里的 modlist 指纹;选择显式优先(`--snapshot` /
   `snapshot use`),`ModsConfig.xml` 自动检测只作兜底与自证(用户补充裁决:当前游戏
   启用的不一定是期望查询的环境);config.toml 只放路径与别名,不复制指纹。

## 总览

```
游戏内 DataMod ──导出──▶ SQLite 快照(可多份,meta 自带身份)
                              │
                 rimsearcher CLI(快照寻址 + 查询 + code-search)
                              │ stdout(结构主体 + 散文声明区)
                 LLM 调用方 ◀─┘   ▲
                    │             │ 教学/指路
                    └── SKILL.md + references/(cli-reference.md 为生成产物)

旁路:反编译落盘目录(master 代管再生成)◀── code-search 扫描
      DecompilerServer MCP ◀── 符号级 C# 阅读(skill 指路)
```

## 层 1 · 导出器(DataMod)

| 设计点 | 择优 | 说明 |
|---|---|---|
| 取数入口 | [上游] | `GenDefDatabase.AllDefTypesWithDatabases()` 逐类型枚举(03 甲) |
| 噪声清单 | [混合] | 清单内容沿上游,**产地收成一份**、末段匹配语义(02-2);`generated` 从清单剔除保留入库(03 甲:区分 XML 定义与代码生成,对查询方有信息量) |
| 深度/体量上限 | [上游+声明] | MaxFieldDepth 语义沿上游(叶子不占深度,覆盖比旧世系深 2~3 层,03 乙换算);数值可调,**截断必须落库自证**:defs 行加截断标志列(见层 2),CLI 呈现时声明。02-3 |
| 原子写入 | [全新] | 临时文件写完 rename 替换(02-6);journal_mode=OFF 可保留,裸奔的是先删后写不是 pragma |
| meta 表写入 | [全新] | 见层 2;指纹在导出时点采集(启用 packageId **有序**列表 + 各 mod 版本 + 游戏 build + 语言 + 导出时间 + schema_version + 各上限参数值)。**顺序入指纹**:激活顺序 = patch 应用顺序(03 甲),换序就是另一份数据 |
| patch 溯源拦截 | [可选后备] | ApplyPatches Harmony dump + UnifiedDiffFormatter(03 甲拦截点);不在第一批,04 步骤 9 |
| FTS 构建 | [上游] | unicode61 + CJK bigram 展开(02-8:改 FTS 结构别丢 `ExpandCjkBigrams`) |
| SQLite.Interop 预载 | [上游] | 仅 DataMod 侧需要(net472);脆但可接受,02-8 |

## 层 2 · SQLite schema

- 基础表 **[上游]**:`defs` / `field_values` / `defs_fts`(形状见上游 cli-reference,列名沿用)。
- `meta` 表 **[全新]**:单行,内容见层 1「meta 表写入」。**指纹事实的唯一产地在这里**,
  config.toml 只存别名指针。
- 截断自证列 **[全新]**:`defs.fields_truncated`(该 def 被 MaxFieldValuesPerDef /
  MaxFieldDepth 截掉的条数,0 = 完整)。「字段被截」与「没有该字段」必须可区分
  (02-3;离群 mod 那 687 个 def 是现成实证样本,03 乙)。
- `schema_version` **[全新]**:自立计数,**不兼容上游 db** —— CLI 读到无 meta 或版本不符
  的库,拒读并指导重导(留发布缝:错误消息不含本机路径)。
- 模糊体验 **[本地]**:FuzzyMatcher(01)移植到 def_name 层,或最低限 FTS 查询自动补
  前缀 `*`(02-7)。落点在 CLI 查询侧,不改 FTS 结构。

## 层 3 · CLI

### 命令面(命名沿上游短词惯例,体验逐点择优)

| 命令 | 择优 | 说明 |
|---|---|---|
| `search` | [混合] | 上游 FTS 底子 + 本地模糊体验(02-7):调用方不该需要知道 `*` 才搜得到复合名 |
| `get` / `find` / `list` / `fields` / `values` / `types` / `mods` | [上游] | 语义沿上游;输出契约按下节改造 |
| `code-search` | [本地] | SearchRegexAsync 扫描段移植(chunk 扫描 + Regex 超时 + 诊断回传),对象是反编译落盘目录;**三刀自证契约整体带走**(每文件预览上限 / 文件数上限 / 未扫全 → `at least N`,三刀分开声明) |
| `snapshot`(子族:`list` / `import` / `use` / `status`) | [全新] | 快照登记、指纹比对、自动检测报告 |
| `docs` | [全新] | 维护用:把声明层渲染成 markdown 参数表(见「声明层」) |
| ~~`update`~~ | — | 不做(自用);发布缝备忘:02-8 两条教训 |

### 输出契约(01 呈现层资产的落点)

- **结构主体**:JSON(snake_case 沿上游),保管道可组合性。
- **散文声明区**:头部/尾注,承载随调用变化的自证 —— 三态截断文法(裸 N / `N of M` /
  `at least M`,01)、快照过期警告(02-4)、暗截断声明(02-3)、未知 flag 提示。
  文法闸只管散文格子,结构主体由 schema 闸管。
- **收口纪律 [本地]**:行尾一律 LF、TrimEnd 尾空行(01 ToolResult 条目,成因照旧)。
- **未知 flag [全新,同构 ExtraAcceptedKeys]**:解析层严格模式,未知 flag 必须报错并
  给近似候选;静默吞掉是 CLI 形态第一新雷区(04 盲测第一轮就要覆盖)。
- **错误消息是一等公民**:CLI 无 schema 校验兜底,拼错的命令行只能靠运行时消息救;
  缺参/错参消息进文法闸,地位同 master 的缺参消息(01「闸的事实侧」:真跑进程读 stdout)。

### 声明层(产地唯一的 CLI 形态实现)

- 每条命令/参数的声明文本 = 代码里的常量;散文中的数字一律常量插值
  (master `SearchRegexTool.Description` 范式:改上限,散文自动跟)。
- 同一份声明,两个渲染器:`--help` 与 `docs`(markdown)。
- `skills/rimsearcher/references/cli-reference.md` 是 **生成产物 + 字节级闸**:
  测试跑 docs 渲染器与 committed 文件逐字节比对,漂移即红(master 七份 tools/list
  基线的同一纪律,判据零新写)。
- 声明什么,沿 ToolArgs.cs 政策原文(01):拒绝的必须声明、接受的不许禁、
  夹紧的不声明成硬约束(数写进散文)。
- 上游反面对照:三份声明零同步(贫瘠 `--help` + SKILL.md 速查表 + 手写 cli-reference)。

### scope 与快照(两层,语法分家)

- **快照选择** = 用哪次导出,三层优先级,显式恒胜自动:
  1. 本次调用显式指定:`--snapshot <别名>` 或 `--db <path>`;
  2. `snapshot use <别名>` 固定的活动快照(持久在 config.toml/state);
  3. 都没有才走自动检测(读 `ModsConfig.xml` ↔ 各快照 meta 指纹比对,命中即选)。
  **自动检测不覆盖显式选择**(用户裁决:当前游戏启用的不一定是正在查询的目标环境)。
  但无论选择来自哪层,**每次输出的声明区都报告「所用快照 ↔ 当前 ModsConfig」的比对
  结果**:一致 / 版本漂移(= 02-4 过期警告)/ modlist 不同(提示但不改选择)/
  无匹配快照(明说请进游戏导出)。寻址与过期自证是同一次比对的两个产出;
  不一致只声明,不静默切换。
- **scope 过滤** = 快照内按 mod 维度筛结果 **[本地]**:组 / 别名 / `all,-vanilla`
  排除语法(01;上游只有 `--mod` 单值)。组定义在 config.toml。
- 同一语法符号不背两种语义:`--scope` 只管过滤,不选快照。

### config.toml

机器事实与偏好:游戏路径(`ModsConfig.xml`、DataMod 导出目录)、快照库目录、
modlist 别名、scope 组。**不放**:指纹事实(产地在 db meta)、任何声明文本。

### 测试(01 可移植清单的落点)

- 文法/措辞系统(GrammarRules / CountedNoun / OutputText)整体移植,写法教训照旧
  (判产地槽,不判渲染字;1338603 那条)。
- 字节级基线 + **基线枚举接文法检查**(SnapshotGrammarGateTests 连带搬,01 那条缝)。
- 闸的事实侧 = 真跑 CLI 进程读 stdout(比 MCP 形态链路更短)。
- 「先立字节级闸再改代码」适用条件:**复用上游实际输出的环节**先固化现状;
  全新写的输出直接按文法闸 + 快照建绿基线,没有「固化旧缺陷」一步(本轮已澄清)。

## 层 4 · skill

- `SKILL.md` **手写**:命令决策树、pipeline、何时转 DecompilerServer、恢复策略。
  **不复述参数**(指路 cli-reference),**不教绕路**(04 验收条款;上游
  「Always prefix-search」是反例,修的是 CLI)。上游「Never fall back to shell tools」
  一条不继承 —— 本形态下 CLI 自身就是 shell 工具,该 guardrail 改写为
  「文本检索用 code-search,不用裸 grep」。
- `references/cli-reference.md`:**生成产物**(见声明层),手改无效、闸会红。
- `references/decompiler-mcp.md` **[上游]**:现成一份,随裁随改。
- skill 本身进被测物(04 盲测一节):盲测发现 skill 在教绕路 → 修 CLI。

## 旁路 · C# 阅读能力

- 符号级(反编译单成员、callers/callees、IL、版本 diff):DecompilerServer MCP,00 已裁。
  能力洞与固有缺陷底账在 05,skill 的 decompiler-mcp.md 承接。
- 跨文件正则:`code-search`(本篇层 3),对象是落盘目录。
- 落盘再生成:短期 master `sync_sources` 代管;**后备记账**:若 master 退役,把
  DecompileService 砍成独立命令带走(自包含百行级,锁 C#9 保 diff 基线,05-2)——
  用现成 ilspycmd 会丢该基线,不取。现在不动手。

## 与 04 顺序的衔接

04 建议顺序继续有效,本篇只加两条修订:

1. 步骤 1(上游输出建基线)按「测试」一节的适用条件收窄:沿用上游输出的命令才固化现状。
2. 新增环节的插入位置:声明层 + docs 渲染器宜早(步骤 2 输出改造时一并立),
   `code-search` 与 `snapshot` 族独立于 1-8,随时可插;skill 生成闸随 docs 渲染器落地。

## 开放点(动手时裁,不阻塞开工)

- ConsoleAppFramework 是否保留:声明层重做后,其自带 help 生成若不够渲染需求就换手写
  解析(顺带解决未知 flag 严格模式);动手第一步验证。
- 三态文法与 JSON 主体的具体缝合格式(散文区在 stdout 顶部还是 stderr):盲测第一轮
  前定稿即可,倾向 stdout 顶部(stderr 在管道场景会被 LLM 调用方漏读)。
