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

第二轮(DataMod 细化)追加裁决:

5. **B 案分工**:游戏侧只写中间格式(JSONL+gzip 流,纯托管),SQLite+FTS 由 CLI
   `snapshot import` 构建。磁盘健康顾虑已量化打消:落盘为压缩流(几十~几百 MB/次,
   量级估算),B 相对 A 的额外写入仅此一份,TBW 换算每年 <0.01%。
6. **导出自动化**:CLI 编排「隔离 savedatafolder + 指定 modlist + 无人值守导出 +
   自动 import + 指纹自校」,真实 ModsConfig.xml 永不触碰(备份还原方案否决:
   崩溃窗口 + 游戏退出回写竞争)。
7. **modlist 格式**:采用游戏原生 `.rml`,不发明新格式;合法生产者三个
   (游戏界面 / CLI / 手写含 LLM),CLI 宽读严写(用户裁决:不得强制依赖游戏内操作)。
8. **翻译**:label 列永远保真运行时值;translations 表两来源层——运行时 defInjections
   (环境内权威)+ import 静态收割(环境外 advisory,应对「使用者不一定主动加入
   本地化 mod」);FTS 双语索引。
9. **声明的上下文预算**:正常态零声明字节,异常才发声且从简,详情分流专用命令
   (00 论据 3 淘汰的「每次返回挂免责声明」不得重生)。

## 总览

```
游戏内 DataMod ──导出──▶ 中间格式(JSONL+gzip,含 meta 头+尾标记)
  ▲ 两入口:设置页按钮 /            │
    命令行无人值守(CLI 编排)        │ snapshot import(建库+登记+指纹自校)
                                   ▼
                        SQLite 快照(可多份,meta 自带身份)
                              │
                 rimsearcher CLI(快照寻址 + 查询 + code-search)
                              │ stdout(结构主体 + 散文声明区)
                 LLM 调用方 ◀─┘   ▲
                    │             │ 教学/指路
                    └── SKILL.md + references/(cli-reference.md 为生成产物)

旁路:反编译落盘目录(master 代管再生成)◀── code-search 扫描
      DecompilerServer MCP ◀── 符号级 C# 阅读(skill 指路)
```

## 层 1 · 导出侧(DataMod + CLI 编排)

### 分工(B 案,第二轮裁决 5)

游戏侧**只做反射遍历 + 写中间格式**(JSONL+gzip 流,纯托管零原生依赖);
SQLite 建库、FTS、噪声过滤全部在 CLI `snapshot import` 侧。论据:

- 建库与查询同居一个程序集 → 产地唯一由进程边界保证,02-2 病根结构性消解;
- 策略变化(噪声判据/分词/schema)只需重跑 import(秒级),不进游戏重导(分钟级);
  重建初期 schema 多轮变动,此条施工期价值最大;
- 建库逻辑进测试闸:固定一份中间文件作语料,import→建库→查询全链在 `dotnet test`
  里真跑(A 案下建库代码在游戏进程里,闸够不着);
- DataMod 缩到纯托管几十 KB,无 SQLite.Interop / LoadLibrary hack(02-8 整条消失);
- 风险对称性:A 脆在运行期(每次导出过原生加载),B 脆在设计期(中间格式契约,一次付清)。

### 中间格式 [全新]

- JSONL+gzip 单文件:首行 meta(见下)、每 def 一行、**尾行记录数标记**(完整性自证,
  游戏中途崩 = 尾标记缺失,import 拒收)。自带格式版本号。
- 契约面性质:反射遍历的原样倾倒,稳定极少变(与 A 案要跨进程同步的多变查询 schema
  相反,这是 B 的收益之一)。
- 游戏侧**不过滤噪声**:原样导出,过滤策略归 import 侧单一产地(02-2),
  策略变化免重导。
- **defInjections 倾倒**:def 之后追加枚举 `LanguageDatabase.activeLanguage.defInjections`
  ——每条注入自带路径、译文与 `replacedString`/`replacedList` **原文**(反编译实证:
  导出时刻译文在 def 对象上、被替换的原文在注入记录里,两者同时在场)。
  游戏语言为英文时该节自然为空,无需分支。

### 设计点

| 设计点 | 择优 | 说明 |
|---|---|---|
| 取数入口 | [上游] | `GenDefDatabase.AllDefTypesWithDatabases()` 逐类型枚举(03 甲) |
| 反射遍历 | [上游] | `ExtractFieldValuesRecursive` 语义(叶子不占深度,覆盖比旧世系深 2~3 层,03 乙换算);上限数值可调,**每 def 截断计数随行带出**(02-3 自证源头) |
| ImpliedDefs | [上游机制+呈现] | 运行时枚举自然捕获(00 论据 1);`generated` 保留入库(03 甲);`source_file` 对该批存 `"ImpliedDefs"` 事实,呈现侧按 R51 在作用块明示「代码生成,无 XML 源文件」;「从谁生成」反查走 `find` 通用路径(`race.corpseDef` / `entityDefToBuild` 等正向字段),不单独建模 |
| 触发入口 | [混合] | 设置页按钮(上游)+ 命令行无人值守分支(`-rimsearcher-export=<path>`,`GenCommandLine.TryGetCommandLineArg` 实证);两入口共用导出核心;时机 `StaticConstructorOnStartup`(ImpliedDefs 两批已生成),无人值守分支完成后 `Root.Shutdown()` |
| meta 采集 | [全新] | 写进中间格式首行:游戏 build、当前语言(label 是该语言产物)、**有序** packageId 列表+各 mod 版本、导出时间、各上限参数值、导出器版本、mod 设置文件哈希(留缝,见开放点)。**顺序入指纹**:激活顺序 = patch 应用顺序(03 甲) |
| 原子性 | [全新] | 游戏侧 temp+rename+尾标记(02-6);import 侧 temp db 建完 rename+登记 |
| patch 溯源拦截 | [可选后备] | ApplyPatches Harmony dump,走同一导出通道,CLI 侧 UnifiedDiffFormatter diff(03 甲);不在第一批(04 步骤 9);mod 工程给它独立源文件位,Harmony 依赖不搅进主导出路径 |
| 工程 | [机器事实] | net472 + Krafs.Rimworld.Ref;编译产物不进库(02-8),csproj 输出到本地 Mods 目录;About.xml 留发布缝 |

### 自动化编排 [全新](第二轮裁决 6)

`rimsearcher export --modlist <name>` 全流程,真实 ModsConfig.xml **永不触碰**:

1. 解析 `.rml` 取有序 packageId(见下节);
2. **启动前验证**:逐个对照本机三处 mod 目录,缺失即失败并报候选,不烧游戏启动;
3. 制备隔离 savedatafolder:真实 Config/ **整体复制**(`Mod_*.xml` 设置影响 patch 结果,
   03 甲的 ConditionalSettings/EasyMode),就地改写 ModsConfig.xml 的 activeMods 节点;
4. `RimWorldWin64.exe -savedatafolder=<隔离> -rimsearcher-export=<出口>`
   (`-savedatafolder` 重定向整个 SaveData,`GenFilePaths` 实证);
5. 等进程退出+出口文件出现,超时可配(大 modlist 载入分钟级);
6. 自动 `snapshot import` + **指纹自校**:请求的 ids 序列 == 产出 meta 的 ids 序列,
   不等即报错。期望环境由 CLI 主动制造并验证,自动检测只剩「手动导出归属谁」一个用途。

备份还原方案否决记录:「换入后还原前」崩溃窗口 + 游戏退出可能回写 ModsConfig 的竞争。

### modlist(`.rml`)[混合:游戏原生格式](第二轮裁决 7)

- 格式唯一 `.rml`(`SaveData/ModLists/`,游戏 mod 界面「保存模组列表」的产物);
  结构三块:meta 存档头(仅告警用)/ **有序 `ids`(唯一效力载体)** / `names`(展示糖)。
- 合法生产者三个:游戏界面、CLI `modlist save <name>`(抓当前 ModsConfig 落盘)、
  手写(含 LLM —— skill reference 写明结构,编 modlist 本身可被自动化)。
- **宽读严写**:CLI 读只要求 `ids`(meta/names 可缺,手写门槛 = 一列 packageId);
  写补全 names(查已装 mod About.xml)与 meta 头(gameVersion 从游戏目录读),
  保证游戏载入对话框兼容;`modlist save` 兼作手写文件的规范化升格通道。
- 手写笔误由编排第 2 步的启动前验证兜底。

## 层 2 · SQLite schema(import 侧构建)

- 基础表 **[上游]**:`defs` / `field_values` / `defs_fts`(形状见上游 cli-reference,列名沿用)。
- FTS 构建 **[上游逻辑,移址 CLI]**:unicode61 + CJK bigram 展开(02-8:别丢
  `ExpandCjkBigrams`);Microsoft.Data.Sqlite 自带 FTS5,无 Interop 问题。
- 噪声过滤 **[混合]**:清单内容沿上游、末段匹配语义,**单一产地在 import 侧**(02-2);
  `generated` 不在清单里(03 甲)。
- `meta` 表 **[全新]**:单行,内容 = 中间格式首行 meta 原样落库。**指纹事实的唯一产地**,
  config.toml 只存别名指针。
- 截断自证列 **[全新]**:`defs.fields_truncated`(该 def 被上限截掉的条数,0 = 完整)。
  「字段被截」与「没有该字段」必须可区分(02-3;离群 mod 687 个 def 是实证样本,03 乙)。
- `schema_version` **[全新]**:自立计数,**不兼容上游 db** —— 无 meta 或版本不符拒读并
  指导重导(错误消息不含本机路径,发布缝)。
- `translations` 表 **[全新]**(第二轮裁决 8):两来源层——**运行时注入**(环境内权威,
  来自中间格式的 defInjections 节,译文+原文)与**静态收割**(import 时扫描**所有已装
  mod** 的 `Languages/<快照语言>/DefInjected/`,只保留 defName 命中快照内 def 的条目,
  标来源 mod 与环境外标志)。要点:不判「翻译 mod」类型(无判据,也不需要——目标 mod
  自带翻译与第三方汉化包一视同仁,垃圾条目被 defName 过滤);**不替换任何字段值**,
  纯检索召回索引,故无注入 merge 语义问题,同路径多译文并存皆召回;带 language 列
  (跨语言收割官方 `Data/*/Languages` tar 留缝,第一批不做)。FTS 两层都进;
  命中环境外翻译时按 R51 聚合声明来源(见输出契约的上下文预算)。
- 模糊体验 **[本地]**:FuzzyMatcher(01)移植到 def_name 层,或最低限 FTS 查询自动补
  前缀 `*`(02-7)。落点在查询侧,不改 FTS 结构。

## 层 3 · CLI

### 命令面(命名沿上游短词惯例,体验逐点择优)

| 命令 | 择优 | 说明 |
|---|---|---|
| `search` | [混合] | 上游 FTS 底子 + 本地模糊体验(02-7):调用方不该需要知道 `*` 才搜得到复合名 |
| `get` / `find` / `list` / `fields` / `values` / `types` / `mods` | [上游] | 语义沿上游;输出契约按下节改造 |
| `code-search` | [本地] | SearchRegexAsync 扫描段移植(chunk 扫描 + Regex 超时 + 诊断回传),对象是反编译落盘目录;**三刀自证契约整体带走**(每文件预览上限 / 文件数上限 / 未扫全 → `at least N`,三刀分开声明) |
| `snapshot`(子族:`list` / `import` / `use` / `status`) | [全新] | 快照登记、指纹比对、自动检测报告;`import` 兼任建库(层 2),无参时扫描 config.toml 导出目录 |
| `export` | [全新] | 自动化编排全流程(层 1「自动化编排」) |
| `modlist`(子族:`list` / `save` / `show`) | [全新] | `.rml` 枚举/抓取/查看;宽读严写(层 1「modlist」) |
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
- **上下文预算 [硬约束]**(第二轮裁决 9):声明区**正常态零字节**——三态文法的本质
  是省字节(裸 N = 完整集,无一字额外);截断/过期/环境外命中等异常才发声,每类一行;
  逐条标注聚合成尾注(环境外翻译:行内 `*` + 尾注一行计数,CountedNoun 落点)。
  全量状态详情分流到 `snapshot status` 等专用命令,查询输出只发信号,skill 教分流。
  声明区行数上限入闸(OutputVolumeCapTests 移植落点)。形态差异记录:CLI 无会话态,
  master 的「本会话已提示过」(SessionUpdateNotice)不可复刻,控制手段即上述两条。
  00 论据 3 淘汰的整段免责声明不得重生。

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
  「文本检索用 code-search,不用裸 grep」。上游「写任何 Harmony patch 前必须
  `get_il`」同步收窄为「**transpiler 前必须**」——IL 的不可替代场景仅此一个,
  Prefix/Postfix 读反编译 C# 即可(05-3)。
- `references/cli-reference.md`:**生成产物**(见声明层),手改无效、闸会红。
- `references/decompiler-mcp.md` **[上游]**:现成一份,随裁随改。
- skill 本身进被测物(04 盲测一节):盲测发现 skill 在教绕路 → 修 CLI。

## 旁路 · C# 阅读能力

- 符号级(反编译单成员、callers/callees、IL、版本 diff):DecompilerServer MCP,00 已裁。
  能力洞与固有缺陷底账在 05,skill 的 decompiler-mcp.md 承接。
- 跨文件正则:`code-search`(本篇层 3),对象是落盘目录。
- **类型定位 [本地体验,零索引]**:落盘树本身即类型级符号索引(WholeProjectDecompiler
  一类型一文件、命名空间分目录、按源分根)——`code-search` 加类型/文件名模式,
  FuzzyMatcher 复用(与 def_name 同一实现两个数据集),scope = 选根目录。
  符号级工作流两段式:树上跨源模糊定位类型 → DecompilerServer 对该类型精查
  (`list_members`/单成员/调用图)。master `locate→inspect→read_code` 链路的新对应物。
  残余损失仅两样且可近似:跨源成员级模糊搜索(正则近似)、跨源成员大纲一次视图
  (两段式多一跳,非丢能力);「类型↔def」方向反而升级为精确反查(05-8)。
- **继承图洞 [已知洞]**:master `trace inheritors` 的 InheritorsMap 无 DecompilerServer
  对应物,且 05-5 的 callvirt 缺陷补法依赖它。过渡:`code-search` 正则 `:\s*Base\b`
  文本近似;若痛感明显,InheritorsMap 实现现成可搬(输入即落盘目录),见开放点。
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
- mod 设置是否进快照指纹:设置变化会改 patch 结果(03 甲),严格说影响数据身份;
  第一批只在 meta 存设置文件哈希留缝,不参与寻址比对。
- 继承图是否自建:过渡用 code-search 文本近似(旁路一节);盲测若显示痛感,
  搬 master InheritorsMap(输入=落盘目录,实现现成),作为 `code-search` 或独立
  命令的 mode 落地。
