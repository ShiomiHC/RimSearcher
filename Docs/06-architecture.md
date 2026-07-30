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
3. **C# 侧**:符号级阅读外包 DecompilerServer MCP(00 已裁);跨文件正则**收进 CLI**
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
| 工程 | [机器事实] | net472 + Krafs.Rimworld.Ref;编译产物不进库(02-8),csproj 输出到 `ModStaging/`(About/ + Assemblies/ 各就各位),由 `export` 期间的目录联接接进 `<game>/Mods/RimSearcher` —— 见下「导出器按需接挂」,该节 2026-07-30 裁定推翻了「常驻 Mods 目录」;About.xml 留发布缝 |

### 自动化编排 [全新](第二轮裁决 6)

`rimsearcher export --modlist <name>` 全流程,真实 ModsConfig.xml **永不触碰**:

1. 解析 `.rml` 取有序 packageId(见下节);
2. **启动前验证**:逐个对照本机三处 mod 目录,缺失即失败并报候选,不烧游戏启动;
3. 制备隔离 savedatafolder:真实 Config/ **整体复制**(`Mod_*.xml` 设置影响 patch 结果,
   03 甲的 ConditionalSettings/EasyMode),就地改写 ModsConfig.xml 的 activeMods 节点;
4. `RimWorldWin64.exe -savedatafolder=<隔离> -rimsearcher-export=<出口>`
   (`-savedatafolder` 重定向整个 SaveData,`GenFilePaths` 实证);
5. 等进程退出+出口文件出现,超时可配(大 modlist 载入分钟级);
6. 自动 `snapshot import` + **指纹自校**:**发射用的** ids 序列 == 产出 meta 的 ids 序列。
   期望环境由 CLI 主动制造并验证,自动检测只剩「手动导出归属谁」一个用途。

   两处按实现校正(2026-07-31):基准是**发射列表**而不是请求列表 —— 发射列表 = 请求 +
   `ResolveDependencies` 补的硬前置 + 导出器自己,拿请求列表去比,每一次自动补全都会
   报一条假的「加载的和要求的不一样」。而不等时**不报错**:发一条 `Staleness` 声明,
   退出码仍是 0。理由是这时快照已经建好且是有效的 —— 它如实描述了游戏**实际**加载了什么
   (最常见的成因是某个 mod 自己的加载检查没过),把它判成失败会让一份可用的快照被丢掉;
   该说的是「你要的和你得到的不是一回事」,而那正是一条声明的活。

备份还原方案否决记录:「换入后还原前」崩溃窗口 + 游戏退出可能回写 ModsConfig 的竞争。

### 导出器按需接挂 [全新](2026-07-30 裁定)

导出器是**工具而不是内容**,平常玩游戏时它出现在 mod 列表里是纯粹的副作用。
所以它不再常驻 Mods 目录,改成:构建拼出 `ModStaging/`(About/ + Assemblies/),
config 的 `datamod_dir` 指着它,`export` 开跑前在 `<game>/Mods/RimSearcher` 接一个
**目录联接**过去,跑完断开。`datamod status / attach / detach` 是手工面(设置页那个
导出入口没有 CLI 能替它接挂)。

- **为什么是联接不是「拷进去、拷完删掉」**:后者与上面被否决的备份还原是同一个形状 ——
  中途一崩就留下半个 mod(About.xml 在而 Assemblies 空了)。联接只有一个目录项,
  建与断都是原子的,不存在「断了一半」。
- **只删 reparse point,绝不删真目录**:接挂点上若是使用者手工装的那一份,一律不碰。
  删除走 `recursive: false` —— 对联接它删链接本身,对非空真目录它抛。
  「误删使用者的东西」在实现层就不可能发生,而不是靠调用处记得判断。
- **断开是后置条件**:`using` 是唯一的断开产地,每条 throw / return 都经过它;
  唯一漏得掉的是进程被强杀,由**下一次** Attach 接管清理(并把「它本来就在」说出去)。
- **接挂只让游戏看得见,不让它启用**:启用仍然是隔离 savedata 那份 ModsConfig 的事。
  于是哪怕残骸留下,使用者的游戏里它也是未启用状态。
- **顺带收进来的教训**(2026-07-30 实测):Mods 目录里**一条悬空链接就能让整个 mod
  系统死掉** —— `ModLister.RebuildModList()` 在 `ModMetaData.Init()` 里 `GetFiles`
  一个不存在的目标,异常从**静态构造函数**抛出,`ModsConfig` 连带初始化失败,一个 mod
  都不加载,游戏空转在主菜单反复抛 NRE。从编排侧看就是 `stage 'none'` 挂到超时。
  真凶是一个与本工具无关的旧链接(`Kiiro PsyArt`),但结论对导出器自己的联接同样成立:
  **坏联接必须认得出来并清掉**,而「认」的判据是 reparse point 属性本身,不是
  「能不能解析到目标」—— 恰恰是解析不到的那种最需要被清理。闸在
  `DataModLinkTests.坏联接也认得出来并被清掉`。

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

> **产地提示(2026-07-31 校)**:本节写的是设计时的分层意图。**表与列的产地唯一在
> `Sources/RimSearcher.Core/Storage/SnapshotSchema.cs`**,每张表的成因写在它自己的建表注释里。
> 下面这份清单跟到 schema **Version 6**;再有变动改那边,这里只留意图与择优来源。

- 基础表 **[上游沿语义,列名自立]**:`defs` / `field_values` / `defs_fts`。
  设计时写的是「列名沿用上游」,**实际一列都没沿用**,而三张表照上游列名去查是一列都查不到:
  `defs` 用单列 `source_mod`(上游是 `mod_name` + `package_id` 两列)、没有 `full_data`,
  另有 `generated` / `class` / `fields_truncated`;`field_values` 是 `def_id` / `path` /
  `leaf` / `value` / `is_default`;`defs_fts` 第四列是 `translated`。
- `field_values.is_default` **[全新]**:这一行与「这个类型刚 new 出来时」的关系,**三态**
  (0 一定被改过 / 1 与代码默认值无从区分 / 2 没法比)。存原样而不是压成 bool ——
  「没法比」并进任何一边都会让呈现侧说出一句它证不了的话。出口是 `get` 的 `code_default` 列。
- `shared_values` 表 **[全新]**(schema 6,第八轮 ep34):一条「与新实例不同」的值在同类型里
  有多普遍。非有它不可 —— `code_default` 那一列能证的只有「与刚 new 出来的实例不同」,
  而读的人一律读成「有人给这个 def 挑了这个值」。分辨「XML 写的」与「引擎事后填的」在这份
  快照里**没有产地**(要在 ResolveReferences 前后各取一次,而导出跑在
  StaticConstructorOnStartup;插进去只能上 Harmony,而 DataMod 刻意无依赖),
  所以不猜成因,只报可核对的事实:3538 分之 2658,读的人自己判。import 时一次 GROUP BY 扫出,
  只收「过半且不少于 8 个」的组。
- `keyed` + `keyed_fts` 表 **[全新]**(schema 5):界面文案那一层。**这张表里一行都不属于
  任何 def** —— key 是 `"SomeKey".Translate()` 里那个 SomeKey,没有类型维、与 defName 无关,
  所以挤不进 `translations`(那张表的主键形状是 def_name + path)。`placeholder` 列区分
  「有 key 没译文」与「有译文」,不带它出来「没译」就与「没有这个 key」分不开。
  FTS 也必须自立:`defs_fts` 的 rowid 是 def 的 id,借用它的 rowid 空间会让两边的命中互相冒充。
- `xml_nodes` 表 **[全新]**(schema 3):继承层,详见下文「继承层」一节。
  **唯一一张不是「游戏内存里的对象」的表** —— 它是打补丁之前的 XML 原文。
- FTS 构建 **[上游逻辑,移址 CLI]**:unicode61 + CJK bigram 展开(02-8:别丢
  `ExpandCjkBigrams`);Microsoft.Data.Sqlite 自带 FTS5,无 Interop 问题。
- 噪声过滤 **[混合]**:清单内容沿上游、末段匹配语义,**单一产地在 import 侧**(02-2);
  `generated` 不在清单里(03 甲)。
- `meta` 表 **[全新]**:**键值表**(`key` / `value`),不是单行 —— import 写六行:
  `schema_version` / `export_meta_json`(中间格式首行 meta 原样)/ `fingerprint` /
  `imported_at_utc` / `def_count` / `source_file`(键名常量在 SnapshotSchema.cs:20-25)。
  **指纹事实的唯一产地**,config.toml 只存别名指针。
- 截断自证列 **[全新]**:`defs.fields_truncated`(该 def 被上限截掉的条数,0 = 完整)。
  「字段被截」与「没有该字段」必须可区分(02-3;离群 mod 687 个 def 是实证样本,03 乙)。
- `schema_version` **[全新]**:自立计数,**不兼容上游 db** —— 无 meta 或版本不符拒读并
  指导重导(错误消息不含本机路径,发布缝)。历次:3 加 `xml_nodes`、4 加
  `field_values.is_default`、5 加 `keyed`、6 加 `shared_values`(**当前 6**)。
  这份台账的产地是 SnapshotSchema.cs 的 `Version` 注释,这里只做索引。
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
| `read` | [全新] | 落盘反编译树的**逐字阅读**:`--member` / `--type` / `--outline` / `--lines`。三轮 R5 实测:CLI 没有这条能力时调用方不会转投 MCP,只会拿 `code-search` 拼正则 —— 那条链拼了七轮,最后交出的是一段拼接起来的伪代码。收进来之后与 MCP 的分工是**元数据级归 MCP、落盘文本归 CLI**(见旁路一节) |
| `keyed` | [全新] | 界面文案那一层(schema 5)。玩家看见的字里超过一半不来自任何 def,而这条路不通的样子与「查不到」逐字同形 |
| `snapshot truncated` | [全新] | 导出时被砍过字段的 def 名单;截断自证列的出口 |
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
- **上下文预算 [硬约束]**(第二轮裁决 9;口径在第二轮盲测后修订一次,见下「实现阶段
  敲定的口径」):声明区**正常态零边界字节,但计数恒在**——三态文法的本质是省字节,
  省掉的是「怎么才能看到更多」那半句,不是数字本身;截断/过期/环境外命中等异常才发声,每类一行;
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
- **别名与取值 [实证,07-②]**:参数名发明是常态(fileFilter 一个意图 9 种拼法),
  高频拼写变体(下划线/驼峰/同义词)有意接受、别名收一个产地;未知 flag 报错必须
  带近似候选;`--limit all` 为正式取值。pattern 含 `&lt;`/`&gt;` 时提示转义误用(07-⑥)。
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
`datamod_dir`(导出器构建产物,按需接挂的目标)、scope 组。

**modlist 不在这里**(2026-07-31 校:原写「modlist 别名」,而 config 从来没有这个键,
写了会被静默忽略)。别名就是文件名(去掉 `.rml`),查找目录固定两处、均不可配:
游戏 SaveData 的 `ModLists/` 与 config.toml 同级的 `modlists/`(产地 `ModListIo.Directories`)。
**不放**:指纹事实(产地在 db meta)、任何声明文本。

### 测试(01 可移植清单的落点)

- 文法/措辞系统(GrammarRules / CountedNoun / OutputText)整体移植,写法教训照旧
  (判产地槽,不判渲染字;1338603 那条)。
- 字节级基线 + **基线枚举接文法检查**(SnapshotGrammarGateTests 连带搬,01 那条缝)。
- 闸的事实侧 = 真跑 CLI 进程读 stdout(比 MCP 形态链路更短)。
- 「先立字节级闸再改代码」适用条件:**复用上游实际输出的环节**先固化现状;
  全新写的输出直接按文法闸 + 快照建绿基线,没有「固化旧缺陷」一步(本轮已澄清)。

## 层 4 · skill

- `SKILL.md` **手写**:命令决策树、pipeline、何时转 DecompilerServer、恢复策略。
  决策树须显式承接旧习惯迁移(07-④):三成正则流量曾搜 Defs XML,新形态下该意图
  归 db 查询(名字前缀 / `find` / `values`),不引导则调用方会拿 code-search
  搜已不存在的 XML。
  **不复述参数**(指路 cli-reference),**不教绕路**(04 验收条款;上游
  「Always prefix-search」是反例,修的是 CLI)。上游「Never fall back to shell tools」
  一条不继承 —— 本形态下 CLI 自身就是 shell 工具,该 guardrail 改写为
  「文本检索用 code-search,不用裸 grep」。上游「写任何 Harmony patch 前必须
  `get_il`」同步收窄为「**transpiler 前必须**」——IL 的不可替代场景仅此一个,
  Prefix/Postfix 读反编译 C# 即可(05-3)。
- `references/cli-reference.md`:**生成产物**(见声明层),手改无效、闸会红。
- `references/decompiler-mcp.md` **[上游+实测扩写]**:上游那份只教了 44 个工具中的 13 个
  (05-9)。要补:继承关系全套、`search_*` 的 regex 与过滤、多 context 管理、
  Harmony 辅助(`suggest_transpiler_targets` 等)、`batch_get_decompiled_source`/
  `plan_chunking`;并提醒 `set_decompile_settings` 未知键静默忽略,设完读返回值核对。
- skill 本身进被测物(04 盲测一节):盲测发现 skill 在教绕路 → 修 CLI。
- skill 文件住本仓 `skills/`(沿上游布局);会话发现路径到实际项目使用时再裁
  (用户裁决,不阻塞)。

## 旁路 · C# 阅读能力

- **元数据级**(callers/callees、派生/覆写、IL、版本 diff、加载中的 assembly 里的单成员):
  DecompilerServer MCP,00 已裁。能力洞与固有缺陷底账在 05,skill 的 decompiler-mcp.md 承接。
- **落盘树的逐字阅读:`read`(2026-07-30 收进 CLI,推翻本篇原写的「符号级阅读外包 MCP,
  不动」)**。分界不在「符号级 / 非符号级」而在**问的是元数据还是文本** ——
  `read <file> --member <name>` 要的是那几行字,MCP 不在时它是唯一的路。
  三轮 R5 实证:CLI 没有这条能力时调用方不会转投 MCP,只会拿 `code-search` 拼正则,
  实测拼了七轮,最后交出的是一段拼接起来的伪代码 —— 而那份伪代码与真源码同形。
  产地 `Commands/ReadCommand.cs`,类注释里记着这笔账。
- 跨文件正则:`code-search`(本篇层 3),对象是落盘目录。
- **类型定位 [本地体验,零索引]**:落盘树本身即类型级符号索引(WholeProjectDecompiler
  一类型一文件、命名空间分目录、按源分根)——`code-search` 加类型/文件名模式,
  FuzzyMatcher 复用(与 def_name 同一实现两个数据集),scope = 选根目录。
  符号级工作流两段式:树上跨源模糊定位类型 → DecompilerServer 对该类型精查
  (`list_members`/单成员/调用图)。master `locate→inspect→read_code` 链路的新对应物。
  **实证权重(07-①)**:read_code 占真实消费流量 52%,此两段式是主干道非旁支,
  skill 教学与盲测覆盖列最高优先级;类型定位模式纳入 kind 前缀语法(07-⑤)。
  **范围按 05-9 收窄**:DecompilerServer 的 `search_types`/`search_members` 自带 regex
  与丰富过滤,单 context 内的符号定位已够用;落盘树类型定位的独立价值收缩为
  **一次查询跨全部源**(外包侧要遍历 context)。据此其优先级下调至与 code-search 同批,
  不作为第一批必做项。
  残余损失仅两样且可近似:跨源成员级模糊搜索(正则近似)、跨源成员大纲一次视图
  (两段式多一跳,非丢能力);「类型↔def」方向反而升级为精确反查(05-8)。
- ~~**继承图洞**~~ **[已实测消除,05-9]**:DecompilerServer 具备
  `find_derived_types(transitive)` / `find_base_types` / `get_overrides` /
  `get_implementations` 全套且走元数据(实测 ThingComp → 378 个派生类型)。
  InheritorsMap 不需自建,05-5 的 callvirt 补法在外包侧直接可做。
- **确认存在的洞 [05-9]**:①~~**版本 diff 的字节基线**~~ **[已由 `sources sync` + git 补上,
  见下]**;②**单次查询不跨 context**——多 assembly 可并存但要遍历 alias,属迭代成本;
  ③**任意正则匹配方法体文本**——`search_string_literals` 只覆盖 IL 字面量,形状搜索仍归
  `code-search`。
- 落盘再生成 **[已落地 2026-07-30,`sources sync` / `sources list`]**:后备记账兑现,
  DecompileService 砍成命令带走(它只依赖 `ICSharpCode.Decompiler` 这个 NuGet 包,
  与 MCP 的任何管线无关)。旧世系 `SourceSyncService` 那九百行**不搬**,理由见下一节。
  注:落盘树仅服务 `code-search`(方法体形状搜索)与跨全源类型定位;DecompilerServer
  自身**不需要**它(load_assembly 实测 0.4 秒自带 warm,无预处理,05-9)。

### 版本间差异交给 git,不自己实现 [2026-07-30 裁定]

旧世系 `sync_sources` 的 `check → sync → diff` 三段里,只有 `sync` 值得搬。`diff` 那一段
(含 `SourceHistoryStore`、成员级折叠、分页)整体废弃,判据是**能力对比**而非工作量:

| | 自制 diff | git |
|---|---|---|
| 上次 ↔ 现在 | 有 | 有 |
| 重命名检测 | 无 | 有 |
| 跨多个游戏版本回溯 | 无 | 有 |
| 单文件演化史 | 无 | `git log -p` |

零代码,严格强于原有能力。落地形态:`decompiled_dir` 自己是一个 git 仓
(`S:\works\CS\RimSearcher\Decompiled`,首次入库 14502 文件 / 145 万行,`.git` 13 MB)。

这条路成立的前提是**产出逐次可复现**,否则 diff 里全是噪音。实测踩到一处:反编译器给
每个 `.csproj` 生成随机 `ProjectGuid`,一次「什么都没变」的重跑里 16094 个 `.cs` 逐字节
相同、29 个 `.csproj` 全红。改成由项目名算出的固定值后,`--force` 全量重跑的 git 工作树
**零改动**。语言档位锁 C#9 同属这一条(档位一变整棵树重排)。

防外传换了实现:旧世系往每棵树里写一个内容为 `*` 的 `.gitignore`,防的是「使用者的
mod 工程仓库顺手把产物一起提交」。顾虑对,但这棵树现在自己就是 git 仓 —— 嵌套的 `.git`
让外层仓库根本看不进来,比 ignore 规则更硬(外层改不掉它)。留着那个文件反而屏蔽掉
唯一的 diff 能力。现由 `pre-push` 钩子 + 「没有 remote」承担,钩子已实测拦得住。

### 该反编译谁:复刻游戏的算法,启用集合取自快照

「一个 mod 里哪些 dll 在跑」不是「找出所有 dll」。实测 HAR 装了六年,20 个 dll 里在用的
只有 2 个;RatkinGene 的 `1.6` 与 `1.6_unofficial` 是靠 `IfModActive` 开关的互斥分支,
两套只该进一套。反编译进错的那一套,后果是 `code-search` 拿出根本没在跑的代码 ——
而它长得跟真答案一模一样。

所以 `ModFolders` **复刻游戏自己的两个方法**,不另立启发式:

- `LoadFolders` ← `ModContentPack.InitLoadFolders()`:loadFolders.xml 的版本键两级回退
  (精确 → 小于等于当前的最高一个 → `default`),声明过就**只**用它(根目录/Common/版本
  目录一概不再自动补),条目倒序即优先级,`IfModActive`/`IfModActiveAll`/`IfModNotActive`
  按启用集合裁,比对忽略 `_steam` 后缀。
- `Assemblies` ← `GetAllFilesForModPreserveOrder(mod, "Assemblies/")`:按 `Assemblies/`
  起算的**相对路径**去重,高优先级目录赢(HAR 的根 `Assemblies/AlienRace.dll` 正是这样被
  `1.6/Assemblies/` 顶掉的)。

启用集合从**快照**来,不是 config 里手写一条 `active_mods`(旧世系的做法)。判据:快照的
mod 列表是游戏亲自答的,手写清单会漂 —— 这个错刚在 export 上付过代价(手写 races 列表
漏了一个前置,换来一次挂死的无头导出)。附带收益是**树名统一**:树名就是 packageId,
也就是 `rimsearcher mods` 第二列、`--scope` 认的那个,整个工具里 mod 只有一套名字。
旧世系那套短名(`HAR`/`miho`/`Ratkin`)是第二个产地,已改名。

搜索目录也顺带修对了:`ReferencePaths` **无条件**带上游戏的 Managed 目录,旧世系只按
AssemblyRef 名字命中的 dll 所在目录来加。实测收益可量化 —— 重建旧世系建过的 6 棵 mod 树,
54 个 `.cs` 有实质改动,而那**不是 mod 更新**(Wolfein 的 dll 时间戳比旧树的反编译还早):
139 处凭空的强制转换消失(`((CompApparelVerbOwner)(object)((Thing)x).TryGetComp<T>()`
→ `x.TryGetComp<T>()`)、具名实参恢复、`base.Generate` 取代 `((GenStep_Signal)this).Generate`。
解析不到 `Def`/`Thing`,反编译器只能插一堆转换把类型糊过去,而读代码的人看不出那是噪音。

**残余盲区(逐条申报,不用一句免责糊)**:`.rimsearcher-source.json` 记的是来源 dll 的
哈希,所以「树过期了」判得准;但 loadFolders.xml 里若出现游戏支持而这里未复刻的写法,
落点是多反编译或少反编译一个目录。已复刻的是上面列出的全部条件形式;`LoadFolder` 类
在 1.6 只有这三种条件属性,故当前无已知缺口。

## 与 04 顺序的衔接

04 建议顺序继续有效,本篇只加两条修订:

1. 步骤 1(上游输出建基线)适用面经再评估**实际趋零,裁定跳过**:输出契约全面更新
   (散文声明区+结构主体+收口纪律),「沿用上游实际输出」的环节不存在;且跑上游 CLI
   本身需要一份 db(又依赖进游戏导出),为一批注定作废的基线烧一轮游戏启动不值。
   全部输出直接按新契约「文法闸+快照建绿」施工。
2. 新增环节的插入位置:声明层 + docs 渲染器宜早(步骤 2 输出改造时一并立),
   `code-search` 与 `snapshot` 族独立于 1-8,随时可插;skill 生成闸随 docs 渲染器落地。

## 开放点(动手时裁,不阻塞开工)

- ~~ConsoleAppFramework 是否保留~~ **已关闭(2026-07-29 实测)**:三条判据里不合两条,
  换手写解析。真正的收获不是「够不够用」,而是手写解析的**声明模型**才使得
  「声明区产地唯一」成为可能 —— `CommandSpec`/`OptionSpec` 是唯一产地,
  `--help` 与 markdown 参考页是它的两个渲染器,入库那份有字节闸盯着。
- ~~散文区在 stdout 顶部还是 stderr~~ **已关闭**:结果与散文一并走 stdout,报错走 stderr。
  更要紧的是默认格式:**紧凑文本**,`--json` 显式开启。理由在 `TextRenderer` 的
  文档注释里 —— 消费方是读 stdout 的 LLM,这一条直接服务上下文预算。
- mod 设置是否进快照指纹:设置变化会改 patch 结果(03 甲),严格说影响数据身份;
  第一批只在 meta 存设置文件哈希留缝,不参与寻址比对。
- ~~继承图是否自建~~ **已关闭**:05-9 实测外包侧全套具备,不自建。

## 实现阶段敲定的口径(2026-07-29/30)

这几条都不是设计时想到的,是跑起来之后被现实改的。

**导出器的反射绑定照抄游戏自己的。** 原先只绑 `BindingFlags.Public`,
而 `DirectXmlSaver` / `DefInjectionUtility` 绑的是 `Instance | Public | NonPublic`。
1.6 的 `ThingDef.verbs` 与 `ProjectileProperties.damageAmountBase` 都是私有字段 ——
漏掉它们意味着「这把枪打什么弹、这颗弹多少伤害」在快照里根本不存在,
而输出侧**无从区分「没这个字段」和「没看见这个字段」**,缺席会被读成事实。
过滤口径同样照抄:跳过编译器后备字段与游戏亲自标了 `[Unsaved]` 的运行期字段,
不另立一套判据。

**过期声明的判据是「这次调用说没说过」,不是「以前选没选过」。**
带了 `--snapshot`/`--db` 就是当场声明了环境,静默;`snapshot use` 固定的**要发声** ——
那是过去某一刻的选择,而 mod 列表会变,不一致恰恰说明它过时了。
代价是实测出来的:Core-only 快照 + 22 个已启用 mod,`find` 返回一条裸计数,
按三态文法读就是「全世界只有这一个」。

**导出器不算内容。** 它自动补进发射列表(`modlist save` 从游戏捕获的列表天然没有它),
并且在指纹自校与环境比对的**两侧**都被摘掉 —— 不摘的话,导出完成的下一秒就会给自己
造出「有 1 个 mod 不再启用」的假过期,正常态的零声明字节当场失守。

**「有 label」压在「名字前缀」之上。** 反过来排的那版让没有 label 的 `Shield_Break`
排在 `Apparel_ShieldBelt` 前面,只因名字以 shield 开头。前缀命中说明「名字长得像」,
有没有 label 说明「这东西给人看」,后者才是提问的人真正在筛的。

**名词登记处的闸挪进测试。** 「没登记就抛」方向对但落点错:那一抛发生在用户面前,
表现为一条裸栈追踪。扫源码的两个调用入口(`Render("x")` 与 `TruncationNotice(..., "x", ...)`),
漏登记在提交前就是红的;顺带扫出五个从没用过的登记名词。

**上下文预算改口径:「正常态零声明字节」→「零边界字节,计数恒在」。**
(第二轮盲测,04)原口径把裸计数这一态也一并省掉了 —— 于是三态文法的「裸 N」
**从来没渲染出来过**,`TruncationNotice` 在未截断时直接 return。
靠沉默传达「这就是全部」是那一轮最贵的一条错误来源:四个 agent 独立踩,
其中一个据此二次确认了错答案(`search VoidNode` 跑两遍逐字相同、都没有计数句,
于是断定 22 条即全集,真值 23)。

判据是这样的:**沉默只有在「只有一种机制能让输出变短」时才无歧义**。而这里有两种 ——
行数上限,以及匹配级别的提前停止(`search` 命中第一级就不再往下找)。
两种机制同时在场,「没说被截」就不再等价于「没被截」。所以省的必须是那半句
「怎么才能看到更多」,而不是数字本身:数字是结论,那半句才是修辞。

落点是 `Report.CountNotice`(无条件打 `Tally.Render`)与 `TruncationNotice`
(只在截断时追加 how-to-see-more),`NoticeKind` 同时分出 `Count` 与 `Filter` ——
调用方自己要求的过滤不是截断,混用会被机器侧读成结果不完整。
配套的闸有两道,缺一道就走形:一道判「完整态只准有计数那一句,不准有边界/建议类散文」,
另一道判「没有边界可申报时完整结果集只有计数」—— 少了后一道,那条 boundary 尾注
就可能悄悄变成常驻声明,而那正是 00 论据 3 淘汰掉的东西。

**又开了一态:`get` 的共享值 advisory 是恒发的**(第八轮 ep34,2026-07-31 补记)。
它两个分支都说 —— 有共享值时点名哪几行、一条都没有时也明说一句。照上面两道闸的字面
它像是「常驻声明」,而判据得再往下一层:**这一句随调用变化**(它报的是这次印出来的那些行
里哪几行、分母是这个类型有多少个 def),所以它是自证不是免责声明,与 00 论据 3 淘汰掉的
「每次返回挂同一句话」不是一回事。靠沉默承载「这些值都是这个 def 自己的」正是这套输出
一直在清的东西 —— 与「靠沉默传达完整」同一个病。

那两道闸跑在 `find` 上,够不着 `get`;这一态目前没有独立的闸,只被 `get` 的字节基线钉着。

**继承层:先判成架构边界,后来补成了一层。**(第二轮盲测 F11 → 本轮落地)

前半段的事实没有变:`XmlInheritance.Clear()` 在 `LoadedModManager.LoadAllActiveMods` 结尾
被调用,早于 `StaticConstructorOnStartup` —— 导出时点上 ParentName 与抽象父节点**已经不在
内存里了**,抽象节点更是从头到尾没有 Def 实例。所以 identity 里那个恒为 null 的 `parent` 键
该删(它在替不存在的数据作伪证),这一条依然成立。

**改掉的是「所以答不了」这个推论。**当时的处置是在 `get` 查无此 def 时**无条件**声明边界,
理由写的是「快照里没有任何痕迹可供判断这个名字是不是抽象父节点」。这句话把「当前快照里没有」
说成了「拿不到」。拿得到:`DirectXmlLoader.XmlAssetsInModFolder(mod, "Defs/")` 在任何时点
都能调,走的是 `mod.foldersToLoadDescendingOrder`,也就是游戏自己解析完的 loadFolders.xml、
版本目录、同名文件优先级去重。挂 Harmony 从来不是唯一出路,而**自己写一个 XML 读取器**才是
真正该拒绝的那条 —— 它必然在上面那三件事上跟游戏分家,读到游戏根本没加载的文件比读不到更坏。

于是加了 `kind=xmlnode` 一层与 `xml_nodes` 表,出口是 `inherit` 命令。代价说清楚:
**这是快照里唯一一层不是「游戏内存里的对象」的数据,它是打补丁之前的 XML。**
这份时间差不用一句常驻免责声明糊过去 —— 每个具名节点随行带出 `patch_ops`
(有多少条 PatchOperation 的 xpath 点了它的名),0 就一个字都不说,非 0 就报出数字。

量过两次,而两次差了一个数量级,**这正是逐条记账而不是写一句比例的理由**:
- **全部已装 mod**(含未启用的创意工坊件):1781 个具名节点里 82 个被点名,4.60%;
  5835 条 xpath 里 423 条(7.25%)按 `@Name=` 寻址,集中在 BaseStoryteller /
  WaterDeepBase / MapCommonBase / LTS_DoorBase / BaseMakeableGrenade / RatkinFactionBase
  这类高流量基节点上。
- **当前 `modded` 快照**(24 个启用项,Vethara 那套):883 个具名节点里只有 2 个被点名。

比例随启用了哪些 mod 整体漂移,所以任何一个写死的百分比都会在别人的环境里说谎。
原先那句「抽象父节点定义了什么偶尔会偏」在第一组数据下是**不合格**的 —— 它偏在最常被问的
那几个节点上;而在第二组数据下它又高估了。逐条报数是唯一两边都不撒谎的写法。

层的规模(modded,24 mod):5213 个节点,其中 883 个具名、616 个抽象、4861 个带 ParentName。
Core 那 2011 条与磁盘上 `Data/Core/Defs` 的逐条统计对得上。

抽象节点没有自己的字段表,而这不是缺口:它写的每一条都已经合并进每个子节点,**并且那一份是
patch 之后的**。所以指路到具体子节点比在这一层复制一份 patch 前的原文强 —— 后者是同一份数据的
劣质副本,而劣质副本会被当成权威。

**导出跑无头。**(本轮实测)导出在 `StaticConstructorOnStartup` 里做完就 `Root.Shutdown()`,
整条路径一帧都不渲染,图形设备纯属开销。默认 `-batchmode -nographics`;逃生口是
`--show-window`,给加载期真要图形设备的 mod 用。实测 23 mod + 导出器:无头 26 秒、
带窗口 27 秒,产出的 defs / field_values / translations 逐项相同。
**不走「开个 640×480 小窗」那条**,虽然它也跑得通:`-screen-width`/`-screen-height` 会写进
`HKCU\…\Screenmanager*`,而那是 `-savedatafolder` 隔离不到的地方(实测确实改了一个键)。
「真实配置永不触碰」这条约定里,注册表也算真实配置。

**无头导出的隐形挂起,与三层防线。**(本轮实测)手写的 `races` 列表漏了
`Ancot.AncotLibrary`,游戏在**读定义之前**弹了一个「缺前置」对话框 —— 无头下它既看不见也
点不掉,进程活着不动,一直挂到人工中止。从编排侧看,这与「正在慢慢加载」长得一模一样。
这是**缺席被读成事实**那一类的又一次:没有信号被当成了没有问题。三层收:

1. **发射前查依赖。** 读 About.xml 的 `modDependencies` / `modDependenciesByVersion`,
   把没在列表里的硬前置**插到第一个需要它的 mod 之前**(补在后面等于没补),反复扫到不动为止;
   声明了却没装的,与「列表里的 mod 没装」合成**一条**错误消息一起报 —— 分两条报,第二条
   要等第一条改完再跑一遍才出得来,那等于让人白跑一次几十秒的加载。
   **只取这两节**:`loadAfter` / `loadBefore` 是排序提示,`incompatibleWith` 是反向关系,
   四者元素形状一模一样,粗暴全收会自动补进一个游戏明确说了不能同开的 mod
   (排查时我自己就踩了这一脚:`brrainz.harmony → cn.morereasonablemortars` 是个假阳性)。
2. **游戏侧自报阶段。** DataMod 在两个分界点写 `<out>.progress`:`Mod` 子类构造完
   (程序集加载完、**定义还没开始读**)记 `mod-classes`,`StaticConstructorOnStartup` 里
   导出开始记 `exporting`。**曾经想用「加载完了 CPU 却贴近零」认卡死 —— 那是代理指标,
   而代理会撒谎**:一段真慢的 I/O 会被判成卡死,一个空转的 mod 又会把真卡死盖过去。
   自报之后,「停在哪一步」是事实,只有「这一步为什么久」才是推测。
3. **`-logfile` 落到临时目录。** Unity 默认那份 `Player.log` 这次跑**可能一个字都不写**
   (实测:那次挂死的导出,它的时间戳停在半小时前)。失败时临时目录不清理 ——
   清理证物等于让下一次调查从零开始。

**阶段停顿是软限制,硬停只有 `--timeout`。**(用户裁决)阈值注定选不准:`mod-classes →
exporting` 那一段随 mod 数量放大,20 mod 实测 35 秒,上百 mod 的整备列表要几分钟是正常的。
拿一个猜出来的数去杀进程,就是毁掉一次已经付过的加载 —— 而**误杀不可逆,误报只花一行字**,
所以选可逆的那一侧。到 120 秒只往 stderr 写一句(停在哪一步、这一步意味着什么、
要看对话框走 `--show-window`、以及**明说还在等**),同一阶段只说一遍,`exporting` 不说
(那一段本来就长,没有下一步可讲,一句没有下文的提醒只是噪音)。这句话必须**当场刷出去**:
攒进 `Report` 等命令结束才渲染,跟没说一样。
判据本身抽成纯函数 `ExportCommand.Decide`,因为那是本命令唯一会主动杀进程的地方,
而起一次游戏要几十秒,靠实测覆盖不到「什么时候不该杀」。

实测阶段时刻(20 mod 的 `races`):3 秒 → `mod-classes`,38 秒 → `exporting`,53 秒完;
`modded`(24 mod)全程 30 秒;vanilla 8 秒。

**`def_type` 是分桶键,不是运行时类。** `GenDefDatabase.AllDefTypesWithDatabases()`
只产出「没有非抽象 Def 祖先」的类型,子类共用基类的 DefDatabase。于是
`CreepJoinerAggressiveDef` 的 3 个实例躺在 `CreepJoinerBaseDef` 桶里,
而 `list CreepJoinerAggressiveDef` 曾经报「不存在」。这与第一轮 `BindingFlags.Public`
那条同构:**快照丢掉了游戏有的一个区分,而缺席被读成事实**。
收法是让桶与类两个维度都可查(`--class` 过滤、异构桶补 class 列、
未命中时反查 class 并给出真正能用的命令),不是改分桶。
