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

## 扔掉(取数实现,被「游戏运行时已加载数据库」整体替代)

`SourceIndexer` / `RoslynHelper` / `DecompileService` / `XmlInheritanceHelper` /
`IndexCacheService`(JSON 快照)/ `DefIndexer` / `AssemblyScanner` / `ScopeCatalog` 实现 /
`IndexGate` / `IndexHost` / Startup 一族。

勿因惋惜捡回:它们回答的问题(「XML 合并后长什么样」「谁继承谁」)在运行时导出方案下由导出时点的
运行时数据直接回答。C# 源码阅读能力(read_code / trace 的那半边)按决定外包给
DecompilerServer MCP,skill 里引导(上游 `skills/rimsearcher/references/decompiler-mcp.md`
已有现成一份)。
