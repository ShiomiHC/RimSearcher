# 05 · C# 侧调查结论(2026-07-29)

01 把「C# 源码阅读能力按决定外包给 DecompilerServer MCP」一句话带过。本篇是那句话背后的
事实底账:外包能拿到什么、拿不到什么、若某天自建要付什么。**不含选型倾向** —— 选型的产地是 00。

来源:`git show` 实查 `upstream/master` @ `8a0a4f7` 与 `master` @ `f2afe08`;成本数字为
master 构建产物实测(标注了哪些是估算)。上游 24 个提交里已被 02 吸收的条目(噪声清单双写、
`1fe397e` 版本位数、CJK bigram、FTS 前缀 hack、导出非原子、编译产物进库)不在此复述。

## 1. 前提澄清:两边都是反编译,同一个引擎

「上游的 C# 是 IL、本地的是源码」是误解,两边都不是。

- 上游 skill 的主路径是 `search_symbols` → `get_decompiled_source`,返回**反编译出的 C#**。
  `get_il` 是旁支工具,SKILL.md 里只在一个场景要求它 —— 写 Harmony patch 之前。
- master 的 `Sources/RimSearcher.Core/Core/DecompileService.cs` 用 `ICSharpCode.Decompiler`
  8.2 的 `WholeProjectDecompiler` 整包反编译落盘。RimWorld 不开源,所谓「源码」也是反编译产物。
- pardeike 的 DecompilerServer 底层同为 ILSpy。

**真正的差别是产物的存放与索引方式,不是「IL vs 源码」。**

## 2. 两种用法的能力差

| | master 形态(整包落盘) | DecompilerServer(按需单成员) |
|---|---|---|
| 产物 | 反编译一次,落盘 | 按 memberId 现场反编译一个成员 |
| 全局文本检索 | `search_regex` 跨文件正则 | 无,只能按符号名 `search_symbols` |
| 引用分析 | `trace mode:'usages'`,全词文本匹配(同名成员会混) | `find_callers`/`find_callees`,走元数据 |
| 版本 diff | `sync_sources` 重新反编译后比对 | `compare_symbols(compareMode:"body")` |
| IL | 无 | `get_il` |
| 覆盖面 | scope 体系:本体 + DLC + 全部已配 mod | 一次 `load_assembly` 一个程序集 |
| 前置代价 | 一次整包反编译 + 磁盘 + 需 sync | 无预处理,每次现算 |

一条易被忽略的差异:master 的 diff 依赖「同一 dll 两次反编译字节一致」这个基线,
它由 `DecompileService.CreateSettings()` 锁死 `LanguageVersion.CSharp9_0` 换来
(注释里的理由是让产物贴近 Ludeon 真实能写出的形态)。调用外部 MCP 时,其
`DecompilerSettings` 不由本方控制、且会随它自身版本漂移,该基线不存在。

## 3. IL 的不可替代场景只有一个

**写 transpiler 时。** 反编译器为产出可读 C#,会把 IL 的实际形态抹掉:迭代器状态机还原成
`yield`、闭包还原成 lambda、`switch` 跳转表还原成 `switch` 语句、编译器生成的临时变量被合并。
transpiler 匹配的是抹掉之前的指令序列,照反编译 C# 猜 opcode 对不上。

除此之外读逻辑一律用 C#:IL 不含更多语义信息,只是更啰嗦。

## 4. 精确调用图的两条实现路径(若自建)

- **IL token 扫描(可行)**:`MethodBodyBlock.GetILBytes()` 拿裸字节,自写一个轻量 opcode
  步进器(约 100 行 + 一张指令长度表),只在 `call` / `callvirt` / `newobj` / `ldftn` 处取那
  4 字节 token,解析成目标成员。这与 DecompilerServer 的 `find_callers` 是同一层精度。
- **Roslyn 语义模型(不可行)**:需建 `Compilation` 后用 `SemanticModel.GetSymbolInfo` 解析
  每个调用点,而反编译产物**不保证能编译**(不可访问成员、编译器生成名、泛型约束丢失),
  在错误树上大量调用点解析不出符号,得到的图是残的。master 全程只用
  `CSharpSyntaxTree.ParseText`(纯语法树,见 `RoslynHelper`)不是偶然。

避雷:别用 `ICSharpCode.Decompiler.IL` 的 `ILReader` 建图 —— 那是反编译用的重型路径,会构建
ILAst。这个选择决定构建时间是「几秒」还是「几十秒」量级(见 6)。

## 5. IL 调用图的固有缺陷(两种形态都有)

`callvirt` 的操作数 token 指向**声明类型**的方法。因此查一个被重写的方法,找不到那些通过
基类引用打进来的调用点;反过来查基类虚方法,能找到调用点但不知道运行时实际跑的是哪个重写。
DecompilerServer 的 `find_callers` 同样受此限制。

补法是与继承关系交叉。master 侧有现成的 `InheritorsMap`(`trace mode:'inheritors'` 走它);
运行时导出方案下继承关系可由导出对象直接得到,精度更高。

## 6. 成本量化

master 构建产物实测(`.cache/index/manifest.json`,schemaVersion 5):

- `index.bin` **27.8 MB**,gzip 且 `CompressionLevel.SmallestSize`
- 索引 **14452** 个 C# 文件 / **2697** 个 XML 文件
- 构建耗时 **7.3 秒**

调用图规模为**估算**(方法总数未实测):按 14452 个 .cs 文件、每文件均 10~15 个成员计,
约 15~20 万个方法;每方法去重后 5~10 个调用目标,则边数约 100~150 万。据此:

- 边用字符串 key 存 → 几百 MB 级,不可行
- 边用整数 ID(member 表可与现有 `MemberIndex` 合并)、且只存 callees 单向、callers 于启动时
  在内存反转 → 落盘 gzip 后约 **+5~8 MB**,相对 27.8 MB 是 **+20~30%**
- 构建时间:轻量 opcode 扫描为纯字节扫描,增量在几秒;`ILReader` 路径为数十秒
- **IL 本身按需现算,不进索引,磁盘零增长** —— 三项里最不肥的是 IL,肥的是调用图

## 7. 面积膨胀的三个维度(若在既有工具上扩,非新增工具)

- **工具数:0 增长**。callers/callees 作为 `trace` 的 mode(2 → 4);IL 作为 `read_code` 的
  `format:'il'`。
- **参数:净增 1**(`format`)。`trace` 参数表不变,只是 mode 的 enum 变长。
  注意 `direction` 在 `TraceTool` 的 ArgSpec 里**已经是 `mode` 的别名**,不能再拿它做方向参数。
  真正代价不在数量,而在它加重「schema 与取参别名两份手写靠人对齐」这条债;好在 tools/list
  有字节级基线,description 变长会落在 diff 里,是可测量的膨胀而非暗账。
- **数据**:见 6。可选降载 —— 调用图不进主索引,单独一个文件、首次用到才加载,则不使用该
  功能的调用方磁盘外零成本(启动内存与加载时间不变)。

## 8. 文本引用与语义调用图是并列关系,不互相替代

`usages` 的全词文本匹配能命中 IL 调用图永远看不见的东西:XML 里的 `<compClass>` 一类类名
引用、反射用的字符串字面量、注释中的提及。IL 调用图只覆盖 C# 方法调用。任何形态下二者并列。

对运行时导出方案:XML 侧的类名引用不再需要文本匹配 —— SQLite 的 `field_values` 可直接精确回答,
上游 CLI 的 `find <fieldPath> <value>`(路径后缀匹配、值精确)就是这个能力,SKILL.md 把它
列为「C# 类 → 所有使用该类的 Def」的反查主路径。这与 00 决定性论据第 4 条(引用已解析)同源。

## 9. DecompilerServer 实测(2026-07-29,v1.3.7 · 本机装配后)

本节**推翻了本篇此前若干读码推断**。装配:`C:\Users\CCH\tools\decompiler-server\`
(GitHub release v1.3.7 win-x64,MIT,依赖 .NET 10 运行时,本机 10.0.10 已在);
MCP 名 `decompiler`,project-local scope。复核:直连 stdio 发 JSON-RPC
(`probe*-mcp.mjs`,scratchpad,不进库)。

### 工具面:44 个,上游 reference 只教了 13 个

reference 提到的**一个不缺**,另有 31 个未提。与本篇结论相关的关键增补:

- `find_derived_types`(**支持 `transitive`**)/ `find_base_types` / `get_overrides` /
  `get_implementations` —— **继承关系全套具备**
- `search_types` / `search_members` / `search_symbols` 均支持 `regex` 与丰富过滤
  (kind / namespace / declaringType / accessibility / isStatic / genericArity…)
- `search_string_literals`(IL 字面量正则)、`find_usages`、`get_ast_outline`、
  `get_xml_doc`、`get_overloads`、`get_member_signature`
- Harmony 专用:`suggest_transpiler_targets`、`generate_harmony_patch_skeleton`、
  `generate_detour_stub`、`generate_extension_method_wrapper`
- 上下文管理:`list_contexts` / `select_context` / `unload` / `set_decompile_settings` /
  `warm_index` / `clear_caches` / `status` / `get_server_stats`
- 批量与分块:`batch_get_decompiled_source`、`plan_chunking`、`get_source_slice`

### 实测数据

| 项 | 实测 |
|---|---|
| `load_assembly(gameDir)` | **0.4 秒**,自动发现 Assembly-CSharp.dll;16158 types / 76770 methods,`warmed:true`(对照 master:整包反编译落盘 + 7.3 秒索引构建) |
| `find_derived_types(Verse.ThingComp, transitive)` | 378 个派生类型,分页(`hasMore`/`totalEstimate`) |
| `get_overrides` | 返回 `baseDefinition` + `overrides[]`,走元数据 |
| 多 assembly | mod dll 可作独立 context 与本体并存(实测 0Harmony.dll),各查询按 `contextAlias` 路由 |
| `search_string_literals` | 有效(`"Shield"` 命中 3 条 IL 字面量;`"ShieldBelt"` 0 条 = 确实无此字面量) |

### 对本篇既有结论的修订

1. **§2 表「引用分析」与继承**:「DecompilerServer 无继承图对应物」的判断**错误** ——
   `find_derived_types(transitive)` + `get_overrides` + `get_implementations` 齐全,
   且走元数据。§5 记的 `callvirt` 缺陷补法(与继承关系交叉)在外包侧**可直接完成**。
2. **§2「覆盖面:一次 load_assembly 一个程序集」**:精确但需补充 —— 多 context **可并存**,
   `list_contexts` 枚举、各查询按 alias 路由。残余差异是**单次查询不跨 context**
   (要遍历 alias),属迭代成本非能力缺失。
3. **§2/§6「前置代价」**:load 实测 0.4 秒且自带 warm,「无预处理」比原表述更强。
4. **§2 末段 diff 基线(不变,且已由实测坐实)**:`set_decompile_settings` 只暴露 7 个开关
   (usingDeclarations / showXmlDocumentation / namedArguments / makeAssignmentExpressions /
   alwaysUseBraces / removeDeadCode / introduceIncrementAndDecrement),
   **`LanguageVersion` 不在其中**;传入未知键(`LanguageVersion`/`languageVersion`)
   返回 `status:ok` 却**静默忽略**。故 master 那条「锁 C#9 换来两次反编译字节一致」的
   diff 基线在外包侧**确认无法复刻**(此前是推断,现为实测)。
   附带教训:该工具正是本方设计明令禁止的「未知参数静默吞掉」形态(06 输出契约),
   skill 里要提醒调用方 set 完读返回值核对。
5. **§8 文本引用**:`search_string_literals` 覆盖了「反射用字符串字面量」这一支;
   仍未覆盖的是**任意正则匹配方法体文本**(如 `public\s+(?:virtual\s+)?void\s+Notify_\w+\(`
   之类跨全源的形状搜索)—— 这是 `code-search` 保留的独立价值。

## 复核途径

- 上游提交:`git show <sha>`,上游 worktree 位置见 CLAUDE.local.md(只读)
- master 侧实现:`git show master:Sources/RimSearcher.Core/Core/DecompileService.cs` 等
- 成本数字:master worktree 下 `Sources/.build/bin/RimSearcher.Server/Debug/net10.0/win-x64/.cache/index/`
  的 `manifest.json` 与 `index.bin`(构建产物,不在库内;重建后数字会变)
