# 03 · Def 数据侧调查(2026-07-29)

**边界**:Def 数据**怎么产生的**、**实际长什么样**。
C# 源码阅读能力(DecompilerServer 外包、IL、调用图、自建成本)不在本篇,产地是 05;
选型倾向不在本篇,产地是 00。

已付费:下列结论的调查成本已经支出,动手到对应环节先查本篇,勿重跑。
两篇来源不同,各自的复核途径写在篇首。

---

# 甲 · 数据产生机制(rimsearcher-dev MCP 实查)

以下均出自本地反编译索引(RimWorld 1.6)。复核途径:rimsearcher-dev MCP,或直接 grep
反编译目录(位置见 CLAUDE.local.md)。行号是反编译产物的,游戏更新后会漂,类名/方法名为准。

## 拦截点:「打完补丁的 XML」存在且 Harmony 可达

`Verse.LoadedModManager.LoadAllActiveMods` 内部顺序:

- L116 `ApplyPatches(xmlDocument, assetlookup)` —— 此后 `xmlDocument` = 全 mod 合并、
  全 patch 已应用、**尚未反序列化**的完整 XML
- L125 `ParseAndProcessXML(...)` —— 这一步才变成 Def 对象

`ApplyPatches` 是普通 public static 方法,签名
`(XmlDocument, Dictionary<XmlNode, LoadableXmlAsset>)`;assetlookup 把每个节点映射回
源文件。Harmony Postfix 挂上即得,几十行的量级。

用途(可选项,不在第一批):
- Prefix + Postfix 各 dump 一份 → `UnifiedDiffFormatter` diff = 「哪些 def 被 mod 改了什么」,
  补对象导出丢失的溯源(00 已知代价第三条)。
- 另挂 `PatchOperation.Apply`(基类有 `public string sourceFile` 字段)可记每条 patch 的
  来源文件与成败 —— 「这条 patch 因为 EasyMode=false 没生效」这种答案只有这条路能给,
  DataMod 的最终结果给不出。

## ImpliedDefs(00 决定性论据的证据)

`RimWorld.DefGenerator`:
- `GenerateImpliedDefs_PreResolve`:TerrainDefGenerator_Carpet / _Stone、
  ThingDefGenerator_Buildings(Blueprint+Frame)、_Meat、_Techprints、_Corpses、
  _Neurotrainer、RecipeDefGenerator(Make_X)、PawnColumnDefGenerator、
  GeneDefGenerator(Gene+Thought)、AnimationDefGenerator_Flying
- `GenerateImpliedDefs_PostResolve`:KeyBinding 一族
- `AddImpliedDef`:设 `generated=true`、`ResolveDefNameHash()`、
  `modContentPack?.AddDef(def, "ImpliedDefs")` —— 来源名是字符串,不是文件

即 Corpse_Muffalo / Make_ComponentIndustrial / Blueprint_Wall 这类 mod 作者常查的 def
只存在于对象层。注意上游导出的 `SkipFieldNames` 里有 `generated` —— 重建时考虑反而
**要保留**这个标记(它区分「XML 里找得到」和「代码生成」,对查询方有信息量)。

## PatchOperation 体系

- vanilla 15 子类:3 直接(`FindMod` / `Pathed` / `Sequence`);`Pathed` 下 `Add` /
  `AddModExtension` / `Attribute`(再派生 `AttributeAdd`/`AttributeRemove`/`AttributeSet`)/
  `Conditional` / `Insert` / `Remove` / `Replace` / `SetName` / `Test`
- XPath = BCL `XmlDocument.SelectNodes`(XPath 1.0),**无私有方言** —— 这是当初判定
  自建可行的关键,也适用于任何要理解 patch 的场景
- 应用顺序 = mod 激活顺序:`runningMods.SelectMany(rm => rm.Patches)`
- 基类 `success` 字段:Normal / Invert / Always / Never(控制流类依赖它)
- 当前已装 mod 的自定义子类共 3 个,行为**静态可判定,不需要执行**:
  - `Embergarden.PatchOperationAddSafe`:纯 XML 操作(xpath 命中则向 testName 子节点追加,缺则建)
  - `Embergarden.PatchOperationConditionalSettings`:读 mod 设置 bool,分支到内嵌的
    match/nomatch(都是标准 operation)。其 `GetSetting` 的 null 检查写反了
    (`if (field != null) Log.Warning("not present")` —— 找到了才告警),mod 作者的 bug,
    副作用是这些 key 必然存在
  - `RatkinAnomaly.PatchOperationEasyMode`:`RASettings.EasyMode` 为真才应用内嵌 operation
- mod 设置存在磁盘上可静态读取;文件名规则的产地:
  `LoadedModManager.GetSettingsFilename(modIdentifier, modHandleName)`(读它的实现,别猜)

## 上游 DataMod 取数入口

`GenDefDatabase.AllDefTypesWithDatabases()` → 逐类型
`GenDefDatabase.GetAllDefsInDatabaseForDef(type)`。
运行时 Def 对象上的 `modContentPack` / `fileName` 是**初始定义处**,不含「被哪些 patch
改过」—— 溯源要靠上面的拦截点,二者是互补关系不是二选一(同一次游戏启动先后都经过)。

---

# 乙 · 字段深度分布(磁盘 XML 全量扫描)

来源与甲篇不同:**不是** MCP 实查,是对磁盘 XML 的全量遍历统计。
起因是旧世系 `DefIndexer.IndexElementRecursive` 的 `depth >= 3` 截断,落点是 02-3
的「上限本身可调」——先把「调了会付多少代价」量出来。

复核:扫描脚本四份(depth-stats / probe-paths / mod-depth / outlier),口径见下,重跑即可;
脚本不进库,位置见 CLAUDE.local.md。本节只列数字与事实,不含选型结论。

## 口径

复现 `IndexElementRecursive` 的语义:入口 `(defElement, "", 0)`,即 **Def 元素本身为
depth 0**;`depth >= 3` 直接 return(该元素的名、值、子树全部不进索引);元素名与切词
均取长度 ≥3(`MinContentTokenLength`),切词用 `\W+`。
「可索引词」= (词, 元素) 对的计数,不是最终索引 key 数(全局同词会合并)——作为相对
比例有效,不能当索引体积读。

## 官方 Defs(6 个 DLC,1558 文件,13,809 def)

| 口径 | 总数 | depth 0–2 | depth ≥3 | 丢弃占比 |
|---|---:|---:|---:|---:|
| 元素 | 253,545 | 155,004 | 98,541 | 38.9% |
| 有值叶子 | 183,096 | 103,159 | 79,937 | 43.7% |
| 可索引词 | 320,763 | 186,029 | 134,734 | 42.0% |

depth 3 的词数(97,078)高于 depth 2(29,678)三倍以上,来源是文本生成语法模板:
`RulePackDef.rulePack.rulesStrings.li` 6893、`TaleDef` 1767、`InteractionDef` 1009、
`ResearchProjectDef` 972、`QuestScriptDef` 824+561、`MemeDef` 499+435,合计约 13,000 条整句。

按字段性质分,丢弃部分中:

- **`*Class` 类名字段**:全库 6,262,丢 1,598(25.5%)。
  `EffecterDef.children.li.subEffecterClass` 687、`ThingDef.comps.li.compClass` 284、
  `FleckDef.randomGraphics.li.graphicClass` 126、`ThingDef.verbs.li.verbClass` 80;
  另有 depth 5 的 `PawnRenderTreeDef.root.children.li.children.li.workerClass`。
- **玩家可见文本**(label/description 等):全库 15,083,丢 3,754(24.9%)。
  `ThoughtDef.stages.li.label` 1279 + `.description` 1081 占其中 63%;
  `ThingDef.tools.li.label` 431、`HediffDef.stages.li.label` 214。
- **数值**:`ThingDef.tools.li.power` / `cooldownTime` 各 498、
  `BodyDef.corePart.parts.li.coverage` 478。

## Mod(249 创意工坊 + 8 本地,7554 文件,34,985 def)

### 离群样本

单个 mod `Ancient urban ruins`(XMB.AncientUrbanrUins.MO,687 个 def)贡献
**1,380,152 条 depth≥3 有值叶子、约 404,000 个可索引词**,占全部 mod depth≥3 叶子的 80%:

```
AncientMarket_Libraray.CustomMapDataDef.terrains.li.value.li       549,820
AncientMarket_Libraray.CustomMapDataDef.roofs.li.value.li          433,720
AncientMarket_Libraray.CustomMapDataDef.thingDatas.li.allPositions.li  345,808
```

自定义 Def 类型 + 自定义字段名,内容是序列化的地图格子坐标与枚举。
下表已剔除该 mod;现状(depth 0–2)mod 侧索引量 518,260 词。

### 两种放宽判据的量化对照

- **名字白名单**:叶子名匹配 `*Class$` 或 `{label, description, labelShort, jobString,
  reportString, gerund, verb, defName, labelPlural, descriptionShort}` 时无视深度索引。
- **路径黑名单**:除噪声路径段(`rulesStrings` / `rulePack` / `generalRules` /
  `questNameRules` / `descriptionMaker` / `symbolPack` / `rules` 等)与上游
  `SkipFieldNames` 之外,depth≥3 全部索引。

| 方案 | 新增词量 | 相对现状 |
|---|---:|---:|
| 白名单 | +26,250 | +5.1% |
| 黑名单 | +339,709 | +65.5% |
| 黑名单(含离群 mod) | +744,211 | +143% |

两种判据的覆盖差(depth≥3 有值叶子分类,剔除离群 mod 后):

| 类别 | 叶子数 | 词数 |
|---|---:|---:|
| 命中白名单 | 13,533 | 26,250 |
| 属噪声路径 | 7,276 | 35,617 |
| 两者皆非 | 337,819 | 313,459 |

「两者皆非」= 白名单漏掉、黑名单会捞进来的部分。其字段名分布:`li` 146,648(占 43%),
其后为 `MainHand` 18,798 / `SecHand` 11,460(单个武器 mod 的配置表)、`def` 4,657、
`thingDef` 3,309、`count` 3,306、`countRange` 2,893、`duration` 2,161、`key` 2,042、
`offset` 2,035、`weight` 1,973、`coverage` 1,749、`cooldownTime` 1,636、`power` 1,630、
`minSeverity` 1,610、`browShapeDef` 1,380、`customLabel` 1,294、`mouthShapeDef` 1,220、
`texPath` 1,220、`damageDef` 917、`hediff` 879。

两条相关事实:

- `li` 只在**自身是叶子**时属噪声,作为中间层它是 `ThingDef.tools.li.label` 等有效
  路径的必经节点 —— 按名字无法区分这两种角色。
- 官方 Def 里不存在 `browShapeDef` / `mouthShapeDef` / `customLabel` / `damageDef` 一类
  字段名,它们是 mod 自造,任何据官方数据拟定的名字白名单都不含它们。

## 与上游 `MaxFieldDepth=3` 的换算(读码推断,未实测)

**两个 3 语义不同,数据不可直接套用。** 上游
`DefExporter.ExtractFieldValuesRecursive`(基座 `8a0a4f7`):

- 入口 `(def, "", 0)`,判据是 `depth > MaxFieldDepth`(`>` 非 `>=`)→ depth 0–3 均处理
- **叶子不占深度**:string / Type / ValueType / Def / Enum 字段在父对象的循环里直接
  `TryInsertFieldValue`,不递增 depth;只有复合对象与 List/Dictionary 才 `depth + 1`
- XML 侧相反:叶子本身也是 XElement,要走一次递归,占一层

对同一条数据:

| 路径 | XML 侧 | 对象侧 |
|---|---|---|
| `ThingDef.comps.li.compClass` | depth 3 → 被 `>=3` 丢弃 | `comps[0].compClass`,depth 2 叶子 → 入库 |

粗略换算:**XML 叶子 depth N ≈ 对象叶子 depth N−1**,叠加 `>` 与 `>=` 的一档差,
上游实际覆盖比旧世系深 2~3 层。即本节统计中「丢失」的 depth 3–4 主体,在对象导出下
多数已被覆盖;02-3 所记「comps 套 props,3 层不够用」的具体断点建议按此换算复核。

## 本数据在新架构下的适用边界

- **不适用**:上述丢弃量不能当作对象导出的丢弃量,原因见上一节换算。
- **仍适用**:分布形状(噪声集中在哪几类路径、长尾多长)、以及噪声治理判据的
  相对代价 —— 对象侧字段只会更多,因为 XML 未写出的默认值在对象上一律存在。
- **新增观察点**:离群 mod 那 687 个 def 在对象侧会大量触发
  `MaxFieldValuesPerDef=5000` 的 per-def 上限,是 02-3「暗截断无声明」的现成实证样本
  ——「这个 def 的字段被截了」与「这个 def 没有该字段」在当前输出里不可区分。

## 限制

- 官方数据是 RimWorld 1.6 六个 DLC 全量;mod 数据是**本机这套 249 个 mod 的样本,
  不是分布** —— 换一套 mod 列表,上表 mod 侧数字会变。
- 对象侧的一切结论出自读码,**未跑过导出实测**。
- 只统计路径含 `/Defs/` 的文件;`Patches/` 下的 PatchOperation 不在内。
