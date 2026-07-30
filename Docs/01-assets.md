# 01 · 择优清单:从 master 带走什么

原则一句话:**扔掉的是「怎么拿到数据」,带走的是「怎么把数据说清楚」**。
master 130 个提交的热区全在呈现层(LocateTool 36 次改动、InspectTool 33、ReadCodeTool 28、
TraceTool 27),这些资产与存储层无关,不论各点择优选了谁的做法都原样成立。
定位(00):逐点择优的本地侧证据库 —— 每条资产即本地 2.x 在该点胜出的记录。

## 带走(设计资产,每条附产地)

| 资产 | 产地(`git show master:…`) | 说明 |
|---|---|---|
| 三态截断文法 | `Sources/RimSearcher.Server/Tools/InspectTool.cs`(AppendResolvedXml 一带注释) | 裸 N = 完整集;`N of M` = 被截、M 为总数;`at least M` = 下界。教训:表头无行数时「裸=完整」曾被第十三轮盲测方归纳成假规则并交付给用户 |
| 截断自证 | 各工具 header 逻辑 | 任何有上限的列表必须让调用方能区分「就这么多」与「被截了」 |
| 能力边界诚实声明 | InspectTool 的 PatchNote(R62)、`Tools/Output/ConditionalReport.cs` | 「说清自己没做什么」要写进它作用的那个块,不只写进 tools/list 描述(R51 教训:两次盲测靠通读 schema 才救回来) |
| 产地唯一 | 提交 `8ca8ed6`(参数别名收一个产地)、`76a6b22`(成员粒度判据) | 一份判据一个产地,两侧立闸防漂 |
| 声明政策 | `Sources/RimSearcher.Server/Tools/ToolArgs.cs` 顶部注释(提交 `c01be24`) | 「声明什么」的唯一判据:严格照声明生成请求的调用方会不会吃亏。服务端**拒绝**的必须声明(否则白跑一轮)、**接受**的不许禁(否则请求发不出去)、**夹紧**的不声明成硬约束(把数写进 description)。CLI 形态同构:声明侧 = `--help` + SKILL.md,「校验型客户端」= 照文档拼命令行的 LLM |
| 闸的事实侧取行为 | `ParamRulesTests`(提交 `bf1af7e` / `a76a254`) | 「两侧立闸」的另一侧不许是另一份声明(schema 验 schema 两边同时错照绿):名单侧可反射声明,事实侧**真跑一次**——造刚好越界的语料看实际返回几条、喂空参接住缺参消息。CLI 上更直接:跑一次进程读 stdout |
| ToolResult 输出收口 | `Sources/RimSearcher.Server/Tools/ToolResult.cs` | 行尾一律 LF(AppendLine 在 Windows 出 CRLF 会与写死的 `\n` 混形)、TrimEnd 尾空行(空行被 LLM 读成「后面被截断了」引出多余重查) |
| 未知参数提示 + ExtraAcceptedKeys | `Sources/RimSearcher.Server/Tools/ITool.cs` | 接口宽容 → 调用方类推 → 静默丢弃的键必须提示,否则调用方以为过滤生效了。CLI 形态同理(未知 flag) |
| scope 语法设计 | `Sources/RimSearcher.Server/Tools/ScopeAndLimitArgs.cs`、`Core/ScopeCatalog.cs` | 组 / 别名 / `all,-vanilla` 排除语法;实现可弃,语法带走(上游只有 `--mod` 单值) |
| 文法/措辞系统 | `Sources/RimSearcher.Tests/GrammarRules.cs`、`Core/CountedNoun.cs`、`Core/OutputText.cs` | 与数据源完全无关,整体移植。写法教训(提交 `1338603`):规则判「说没说」,不许用 Contains 短子串重新声明「该怎么说」——`beCAUSE ` 能替 `use ` 蒙混,同一句话红不红取决于成因措辞;判产地渲染的槽空不空,不判渲染完的字 |
| 字节级基线方法 | `Sources/RimSearcher.Tests/OutputSnapshotTests.cs` + `Snapshots/` | 「先立字节级闸再改代码」的载体;基线份数基准见 M1 落账(61→73)。**连带 `SnapshotGrammarGateTests`(提交 `454402e`)一起搬**:基线只判「与上次一样」,一份落地时就带违规的基线永远绿;文法闸只吃被喂到的格子——两层各自全绿、中间漏一整块,曾有四份带违规的基线同时躺在仓里。收法是把基线枚举接上文法检查,判据零新写 |
| 盲测方法论 | master worktree 本地 docs(未推送,见 CLAUDE.local.md 指路) | 观察 LLM 调用方误用 → 修输出;CLI 形态需重搭观察点,见 04 |
| 可移植测试类 | OutputSnapshotTests / OutputGrammarGateTests / OutputReadabilityTests / OutputVolumeCapTests / GrammarRulesTests / CountedNounRegistryTests 等 | 787 用例(68 文件)中与取数无关的部分 |
| staleness 机制设计 | `Sources/RimSearcher.Server/SourceChangeProbe.cs`、`SessionUpdateNoticeTests.cs` | 换数据源后以「导出快照过期」形态重生(02-4) |
| UnifiedDiffFormatter | `Sources/RimSearcher.Core/Core/UnifiedDiffFormatter.cs` | 若做 patch 前后 diff(03 拦截点)直接复用 |
| 模糊匹配实现 | `Sources/RimSearcher.Core/Core/FuzzyMatcher.cs`(148 行) | 特例:取数层里唯一值得考虑带走的实现——上游 FTS5 搜索要用户手加 `*` 前缀(02-7),locate 的模糊体验优于它;可移植到 def_name 列表上 |

## 落账(2026-07-30,04 验收要求「无第三态」)

上表逐条的去向。「已移植」指落地并有闸盯着,不是「写了个像的」。

| 资产 | 去向 |
|---|---|
| 三态截断文法 | `Output/CountedNoun.cs` 的 `Tally`;闸在 `GrammarTests` + `GateTests.基线里没有伪截断的计数`。**第二轮盲测后修订**:「裸 N」这一态原先从未渲染出来过(`TruncationNotice` 未截断时直接 return),靠沉默传达「这就是全部」被四个 agent 独立读错。现在裸计数无条件打出,省的是「怎么看到更多」那半句,成因与判据记在 06 |
| 截断自证 | `Report.CountNotice` / `TruncationNotice`;**完整集合零边界字节但计数恒在**,被截必发声。两道闸:完整态只准有计数一句、且没有边界可申报时一个字的散文都没有(缺后者,边界尾注会退化成 00 论据 3 淘汰掉的常驻免责声明) |
| 能力边界诚实声明 | `NoticeKind.Boundary`。R51 那条「写进它作用的那个块」落在 `get` 的导出侧截断标记上 |
| 产地唯一 | `CommandSpec`/`OptionSpec` 是唯一产地,`--help` 与 markdown 参考页是两个渲染器;闸是 `GateTests.入库的参数参考与声明渲染逐字节一致` |
| 声明政策 | `ArgParser` 严格模式 + 有意接受的拼写变体(07-② 的 9 种写法);数字从 `Limits` 插值进散文,`DeclarationTests` 盯着 |
| 闸的事实侧取行为 | `ProcessTests` —— 真起进程读 stdout。**不做「找不到就跳过」**:xunit 2.x 没有真跳过,拿 `Assert.True` 冒充会把「没跑」记成「跑过且通过」 |
| ToolResult 输出收口 | `Output/Report.cs` 里的 `OutputText`(`Finish` / `Newline`;没有单独的 OutputText.cs,2026-07-31 校);LF、TrimEnd、单个结尾换行,进程侧也验 |
| 未知参数提示 | `ArgParser` 未知 flag 报错带近似候选;无候选时直接列出接受的参数,免得再跑一轮 help |
| scope 语法设计 | `Snapshot/ScopeFilter.cs`,`all,-vanilla` 排除语法与配置组都在 |
| 文法/措辞系统 | `CountedNoun` / `OutputText` / `GrammarTests`。1338603 的写法教训贯彻到底:判产地渲染的槽空不空 |
| 字节级基线方法 | `OutputSnapshotTests` + `Snapshots/`(首版 36 份,2026-07-31 实测 99 份)。`SnapshotGrammarGateTests` 那道缝合进 `GateTests`——基线逐行喂回文法检查,已验证故意写坏会红。**另补 SKILL.md 两道闸**:它是手写的、又按 04 的口径「本身进入被测物」,原先反而是唯一没人守的产物 —— 现在文中每条 `rimsearcher …` 命令行与收窄开关表都对着注册表验,故意写错开关会红 |
| 盲测方法论 | workflow 盲测**八轮**(首版写「两轮」;第四轮起跑成回归实测,第六~八轮的种子、结论与方法论在 04a)。结果与教训在 04 与 04a。第二轮的场景种子改从 Vethara 会话 transcript 逐条抽真实 episode(不再按 07 的意图分布编),同时兼任改版后的回归轮 |
| staleness 机制设计 | `CommandBase.AnnounceSnapshot`;判据在实现阶段改过一次,记在 06 |
| 模糊匹配实现 | `Search/FuzzyMatcher.cs`,Ordinal-vs-CurrentCulture 那条教训原样带注释搬来;另加 `StripKindPrefix` 应对 07-⑤ 的 `method:` 前缀 |
| 可移植测试类 | **部分移植**。OutputSnapshotTests / OutputGrammarGateTests(并入 GateTests)/ OutputVolumeCapTests(并入 GrammarTests 的声明区行数上限)/ GrammarRulesTests / CountedNounRegistryTests 都在。**OutputReadabilityTests 未移植** —— 它判的是表格可读性(列宽、对齐),而这里的表格渲染已被字节基线整体钉死(凡带列对齐的都算,不写死份数),再立一层同源的判据是 schema 验 schema |
| UnifiedDiffFormatter | **明确弃置(暂)**。它的用武之地是 patch 前后 diff,而本轮**没有做** patch 拦截点 —— 运行时导出拿到的就是合并后的结果,「前」那一半根本不在场。真要做 03 的拦截点时再从 master 取,产地已记在上表 |

## 扔掉(取数实现,被「游戏运行时已加载数据库」整体替代)

`SourceIndexer` / `RoslynHelper` / `DecompileService` / `XmlInheritanceHelper` /
`IndexCacheService`(JSON 快照)/ `DefIndexer` / `AssemblyScanner` / `ScopeCatalog` 实现 /
`IndexGate` / `IndexHost` / Startup 一族。

勿因惋惜捡回:它们回答的问题(「XML 合并后长什么样」)在运行时导出方案下由导出时点的
运行时数据直接回答。

**两个例外**(下面两段),两个都是「这句话原先写错了」而不是后来改的主意。

**「谁继承谁」那一半是例外,这句话原先写错了。**运行时数据答不了它 —— `XmlInheritance.Clear()`
在导出时点之前就跑过了。补法不是捡回 `XmlInheritanceHelper`(自己写 XML 读取器,必然在
loadFolders/版本目录/优先级去重上跟游戏分家),而是在 DataMod 里调游戏自己的
`DirectXmlLoader.XmlAssetsInModFolder`,单独收一层 `kind=xmlnode`。成因与代价在 06。

**`SourceIndexer` 的正则扫描段是第二个例外,这一条也写错了。**它回答的不是「XML 合并后
长什么样」,而是「方法体文本里有没有这个形状」—— 运行时数据答不了。那一段已带走,落成
`code-search`,产地 `Sources/RimSearcher.Core/Commands/CodeSearchCommand.cs`
(三刀自证契约整体带走:每文件预览上限 / 文件数上限 / 未扫全 → `at least N`,三刀分开声明)。
同一处修订记在 06 需求口径 3 与层 3 命令面表;CodeSearchCommand 的类注释里点着本节的名。

**C# 源码阅读能力的分工也不是「整半边外包」**(2026-07-31 校)。元数据级
(callers/callees、派生/覆写、IL、版本 diff)归 DecompilerServer MCP,skill 里引导
(上游 `skills/rimsearcher/references/decompiler-mcp.md` 已有现成一份);而**落盘反编译树的
逐字阅读收进了 CLI** —— `read` 命令,产地 `Commands/ReadCommand.cs`。成因是三轮 R5:
CLI 没有这条能力时调用方不会转投 MCP,只会拿 `code-search` 拼正则,实测拼了七轮,
最后交出的是一段与真源码同形的伪代码。
