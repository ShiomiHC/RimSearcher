# 07 · 实证:历史调用分布(2026-07-29)

**边界**:master 形态 MCP 工具在真实会话里的实际调用数据。这是 04「盲测方法论」的
回溯版——不用重搭观察点,历史会话就是现成观察数据。结论供 06 设计点引用(编号 ①~⑥)。

## 方法与口径

扫描 `~/.claude/projects/` 全部会话 jsonl,提取 `mcp__rimsearcher` 前缀的 tool_use 块,
统计工具名、参数键、低基数键取值。脚本 `count-tools.mjs`(scratchpad,不进库;口径:
逐行 JSON 解析、按项目目录前缀分组、pattern 标识符判据
`^(\\b)?[A-Za-z_][A-Za-z0-9_]*(\.[…])*(\\b)?$`)。

数据主体 = **Vethara 组 1197 次**(真实 mod 开发消费方);RimSearcher 组 222 次为
自研测试会话,仅参考不入结论。

## 分布(Vethara)

| 工具 | 次数 | 占比 |
|---|---:|---:|
| read_code | 624 | 52% |
| search_regex | 342 | 29% |
| inspect | 134 | 11% |
| locate | 66 | 6% |
| list_directory | 20 | 2% |
| trace | 11 | 0.9% |

## 事实清单

- **参数键发明**:search_regex 的文件过滤意图出现 **9 种拼法**(fileFilter 253 /
  file_filter 43 / fileGlob 21 / glob 6 / filePattern 5 / file_glob 2 /
  fileExtension、fileType、pathFilter 各 1);read_code 观测到 **16 个不同参数键**,
  近半为发明(query/className/symbol/lineStart/lineEnd/member…);
  maxResults / max_results / limit 三种混用;`limit: "all"` 高频出现(20+ 次)。
- **pattern 分类**:367 条中纯标识符型仅 28.1%,其余为真正则(交替、锚点、
  类声明模式);样本含 `class Alert_\w+ : Alert`(**用正则手搓的继承查询**)。
- **fileFilter 取值**:`.cs`×172、`.xml`×75 —— 约三成正则搜索的对象是 Defs XML
  (`<defName>Bullet_`、`<li Class="CompProperties_AmbientSound">` 一类)。
- **误用实录**:pattern 出现 HTML 转义形态 `&lt;defName&gt;`(必然零命中);
  locate query 有打字错误 `CompTikRare`/`CompTickRar`,有 kind 前缀习惯
  `method:CompTick`/`field:id`,有文件名式查询 `CompShield.cs`。
- **scope**:locate 37% / trace 64% / read_code 3% / search_regex 6% 的调用带 scope;
  取值以 base/all 为主,**排除语法 `all,-vanilla` 有真实使用记录**(×2)。
- trace 消费方仅 11 次(mode 分布含 usages 11 全体);inheritors 的 25 次全部
  出自自研测试组。

## 结论(供 06 引用)

1. **read_code 占 52%** → 新架构中外包给 DecompilerServer 的正是主干道;
   「树上定位 → DecompilerServer 精查」两段式的 skill 教学与盲测覆盖为最高优先级。
2. **参数名发明是常态** → 未知 flag 严格报错+候选提示有硬数据支撑;高频拼写变体
   按声明政策「接受的不许禁」有意接受(别名收一个产地);`--limit all` 设为正式取值。
3. **继承图洞小于预期** → 消费方几乎不用 trace,且已在用正则手搓继承查询;
   code-search 文本近似即自然习惯,InheritorsMap 自建优先级下调。
4. **三成正则流量搜 XML** → 新架构下这批意图迁移到 db 查询(名字前缀/`find`/`values`);
   skill 决策树必须显式引导旧习惯,否则调用方会拿 code-search 搜已不存在的 Defs XML。
5. **typo 与 kind 前缀真实存在** → FuzzyMatcher 容错在野外兑现;kind 前缀语法
   (`method:`/`field:`)值得进类型定位模式。
6. **转义 pattern 检测** → pattern 含 `&lt;`/`&gt;` 时提示,错误消息一等公民的现成落点。

## 限制

- 单一消费方项目(Vethara)样本,意图分布可信,绝对数不外推。
- 观察对象是 master 的 MCP 形态;**调用意图**(想问什么)与形态无关可迁移,
  **参数形状**(怎么拼)会随 CLI 形态变化,后者仅作方向证据。
