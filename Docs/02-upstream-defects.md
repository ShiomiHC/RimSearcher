# 02 · 上游缺陷清单(择优证据库·上游侧)

上游 `8a0a4f7`。按危害排序。行号以该提交为准。每条的用法:逐点择优(00)时上游做法
在该点的失分项;若某点仍选上游做法,则该条转为接手要修的。

## 1. 截断不自证(全 CLI)

`search` / `list` / `find` / `fields` / `values` 全部返回裸 JSON 数组,`LIMIT` 命中与否
不可区分;`list` 有 `--offset` 分页却拿不到总数,不知道翻到哪算到头。
→ 用三态文法改造:结果携带 total(多一次 `COUNT(*)`)。这是 master 上被盲测反复
验证过的第一优先级问题。

## 2. 噪声清单两份手写,语义已漂(实证,不是推测)

- 导出侧 `Sources/RimSearcher.DataMod/DefExporter.cs`:`TryInsertFieldValue` 用
  `SkipFieldNames.Contains(fieldPath)` —— fieldPath 是**全路径**(如 `comps[0].index`),
  拿裸字段名清单匹配不上 → 嵌套噪声全部入库,只有顶层被拦。
- 查询侧 `Sources/RimSearcher.Cli/Program.cs` 的 `IsNoiseField`:取路径末段再匹配 —— 语义正确。

两份清单内容今天恰好相同(`debugRandomId` / `defNameHash` / `generated` / `index` /
`shortHash` / `ignoreConfigErrors` / `ignoreIllegalLabelCharacterConfigError` +
前缀 `modContentPack.`),判据已经不同。这正是 master 用「产地唯一」治过的病。
→ 导出侧修成末段匹配(数据库瘦身),清单收成一个产地;查询侧兜底可留但共享清单。

## 3. 暗截断无声明

`MaxFieldDepth=3`(field_values 不含深层字段)、`MaxFieldValuesPerDef=5000`、
`MaxJsonDepth=10`(JSON 超深写 `"..."`)。ThingDef 的 comps 套 props,3 层不够用。
输出无任何提示,`fields`/`find` 查不到深层字段时调用方会得出「没有这个字段」的错误结论。
→ 上限本身可调,声明必须有(能力边界诚实声明,01 资产)。声明成什么按 01 的声明政策判:
这三个都是**静默截断**(不是拒绝),故不声明成硬约束,写进输出与 help 的散文并带上数。

## 4. 快照过期静默

db 无导出元数据。游戏或 mod 更新后查询照常返回旧数据,无任何征兆 —— 比 master 的
staleness 机制(SourceChangeProbe + 会话内过期提示)倒退,且更隐蔽。
→ db 加 meta 表:导出时间、游戏 build、启用 mod 清单+版本;CLI 每次输出时与当前
环境比对并声明。导出侧与查询侧同步改(见 04 顺序)。

## 5. 无测试

上游仓库零测试项目。按 04 的顺序,基线先行再动代码。

## 6. 导出非原子

`Export` 先 `File.Delete` 旧库再从头写,中途崩溃(或游戏被杀)= 一个库都没有。
`PRAGMA journal_mode=OFF; synchronous=OFF` 加剧此事(导出场景本身合理,但配上
先删后写就是裸奔)。→ 写临时文件,完成后 rename 替换。

## 7. search 模糊体验弱

FTS5 `unicode61` 分词,复合名(`Apparel_ShieldBelt`)搜 `shield` 不中,必须 `shield*`
前缀 hack —— 上游 SKILL.md 专门教用户绕(「Always prefix-search」)。skill 文档教用户
绕自家缺陷是反模式(04 盲测一节);按 01 的声明政策这是可判定的,不只是观感:吃亏的
恰恰是照文档走的那批调用方,故修的只能是 CLI。master 的 locate 是真模糊匹配。
→ 移植 FuzzyMatcher 到 def_name 层,或最低限度 FTS 查询前自动补 `*`。

## 8. 杂项

- FTS5 靠 `LoadLibrary` P/Invoke 预载 `SQLite.Interop.dll`,脆 —— 但仅 DataMod 侧
  (net472 + System.Data.SQLite)需要;CLI 侧用的 Microsoft.Data.Sqlite 自带 FTS5,无此问题。
- 编译产物(dll/pdb/SQLite.Interop)直接进库(`RimSearcher_DataMod/Assemblies/`),
  发布方式要重新考虑。
- `update` 命令的版本位数比对上游已在 `1fe397e` 修过(硬截三段);fork 后 `Repo` 常量
  必须改 —— master 的 `UpdateChecker.cs` 注释里有「版本一落后就把 fork 用户导流到上游
  releases」的教训,和 24h 缓存文件要带仓库名防串的坑,两条都适用。
- CJK bigram 展开(`ExpandCjkBigrams`)与去噪清单是 `d81e667` 一并进的,中文检索
  依赖它,改 FTS 结构时别顺手丢了。

## 落账(2026-07-30,04 验收要求「1-6 修完,7-9 有去向」)

| # | 状态 | 落点 |
|---|---|---|
| 1 截断不自证 | 已修 | `Tally` 三态 + `Report.TruncationNotice`;完整集合零字节,被截必发声。`code-search` 有**四刀**分开的截断声明(行数 / 单文件 / 文件数 / 正则超时),原因不同旋钮也不同 |
| 2 噪声清单两份手写 | 结构性消解 | B 案把建库搬到 CLI 侧,噪声只剩一个产地 `Storage/NoiseFilter.cs`,游戏侧不再有第二份。改口径的代价从「重开一局游戏」降到「重导一次」。`ImportTests.噪声字段不进库` 盯着 |
| 3 暗截断无声明 | 已修 | 导出侧截断随 def 入库(`fields_truncated`),`get` 在该 def 的块里说破:「路径不在下面**不等于** def 没有它」。这条区分上游整个略过 |
| 4 快照过期静默 | 已修 | `AnnounceSnapshot`。判据在实现阶段改过一次(见 06),实测代价记在 04 |
| 5 无测试 | 已修 | 122 项。名单侧 / 事实侧 / 字节基线三层,基线已验证故意写坏会红 |
| 6 导出非原子 | 已修 | 中间格式带结束标记与条数,对不上一律拒收;temp→rename;失败不留半成品(`ImportTests` 三条) |
| 7 search 模糊体验弱 | 已修 | `BuildMatchQuery` 自动补 `*`,调用方永远不需要知道它;FTS 落空再退模糊匹配。**外加实现阶段发现的一条**:值域不含 C# 类名要说清,否则零结果会被读成「模糊匹配坏了」 |
| 8-① FTS5 P/Invoke 脆 | 不适用 | 游戏侧不再建库,`System.Data.SQLite` 整个不需要。DataMod 是纯托管、零原生依赖 |
| 8-② 编译产物进库 | 已修 | `.gitignore` 挡掉 `bin/`、`obj/`、`.build/`;DataMod 输出直接落本机 Mods 目录 |
| 8-③ update 命令 | **明确弃置** | 本轮不做自更新。那条教训(fork 用户被导流到上游 releases、缓存文件要带仓库名)只在有 update 命令时才成立;真要做时产地在 master 的 `UpdateChecker.cs` |
| 8-④ CJK bigram 别丢 | 已守住 | `Storage/FtsText.ExpandCjkBigrams`;`ImportTests.中文标签搜得到` 是闸,真实 mod 语料上另验过四个词均命中 |
