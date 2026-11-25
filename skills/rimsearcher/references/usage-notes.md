# rimsearcher 使用说明

SKILL.md 写契约；本页写契约背后的机制、边界情形与示例。`rimsearcher --help` 列出每个命令，`<命令> --help` 列出一个命令的全部选项。

## 快照管理

**`snapshot status` 比对什么。** 共六项：mod 相同、顺序相同、游戏构建相同，以及（快照记录有它们时）它能找到的每个 mod 的 `Defs/` 与 `Patches/` 下每个 XML 文件的大小与时间戳、游戏加载的程序集的大小与时间戳，和 `Languages/` 下每个 Keyed 与 DefInjected 文件的大小与时间戳（快照的语言与英文）。`XML 指纹` 一行写明这涵盖多少个 mod，只含它能找到、且带有 XML 的，所以可能比快照自己的 mod 数少。这些文件比对能发现内容变化而 `About.xml` 的版本没有动的 mod，这正是 Steam 创意工坊更新的常见形态。这些 mod 列在一张两列的表里，每个 mod 一行：`packageId`，以及 `状态`，写明 XML、程序集、语言文件中哪几份 `自导出后存在改动`，或该 mod `未找到`。`rimsearcher export --modlist <快照名>` 重新导出即可消除前者。普通查询也输出这张表，答案涉及这些 mod 时放在答案之上，否则放在其下；只有输出译文的命令才比对语言文件。`--json` 按文件种类分成 `xml`、`assemblies` 与 `languages` 三个数组，每行另带重新导出的命令 `next`（`未找到` 的为 null）。`snapshot status` 还把只出现在已启用名单一侧的每个 packageId 列在一张 `mod_list` 表里；`snapshot status --json` 把这四张表都以行数组给出，没有差异时为空。

**重命名快照。** 名字是共用它的三个文件：`snapshots/<名称>.db`、配置文件旁的 `modlists/<名称>.toml`，以及 `exports/<名称>.rsx.jsonl.gz`。`rimsearcher snapshot rename <old> <new>` 移动其中存在的那些，并写明查找过哪些而没有找到；缺其中一个不会使重命名中止。新名字在这三处任一处已被占用时，命令拒绝执行，什么也不移动。`snapshot use` 固定着旧名字时，固定随之更新，输出会说明这一点。先前的数据库世代（`<名称>.prev`、`<名称>.prev2` 等）随数据库一起移动。此命令不删除任何东西。

**`snapshot diff` 比对什么。** 两份已导入的快照，按 `snapshot list` 中的名字给出。它不接受 `--scope`。用不同的有序 mod 名单导出的快照会被拒绝：这种比较是 `snapshot status` 及其 `mod_list` 表的事。比较有任何差异时，对没有差异的一侧输出 `0 个新增的 def。` / `0 个删除的 def。` / `0 个字段。`，而不是省略它；只有全零的比较才收成一句话。`--json` 始终带这三个数组。重新导出同一名字，旧文件轮换为 `<名称>.prev`，再之前的轮换为 `<名称>.prev2`，依此类推：每个世代都是一份独立的快照，所以 `snapshot diff <名称>.prev2 <名称>` 可用。配置文件中的 `snapshot_keep` 或 `--keep <n>` 决定该名字保留多少世代（计入正在写入的那一份）；超出该数的会被删除，输出说明删除的是哪些。`--replace-prev` 即 `--keep 1`。新到的快照只有与现有的是同一个数据库、逐行相同（包括布局，以及导出所记录的一切，运行本身留下的痕迹除外）时，才会让每个世代都保持原样。

**指纹的边界**：输出写明其中大部分，但只是写在「所比对的一切都匹配」这个结论旁边：大小与时间戳不是文件内容，所以重新下载同样的字节会被读作改动，而保持两者不变的编辑是它漏掉的唯一情形。纹理与音频完全在它之外（`Languages/` 与各 mod 的 dll 有各自的指纹），已安装的游戏版本不加载的目录也在其外，最后这项排除输出从不提及。导入时一个 mod 也找不到的快照（没有配置 `mod_roots` 或 `game_dir`）没有指纹，`XML 指纹` 显示为 `未记录`，并且在该快照其他各项比对都匹配时，会写明这些 mod 里面的文件没有比对。重新导出，或对导出文件重新执行 `rimsearcher snapshot import`，即可开始记录这一层。

**游戏版本从哪里来。** 已配置 `game_dir` 时来自 `Assembly-CSharp.dll`。没有配置时回退到 `ModsConfig.xml`，游戏只在你于 mod 页面保存改动时才重写它，所以同一个 1.x 版本线内的一次 Steam 更新不会使它变化。`snapshot status` 只在回退的情形下点明来源：一条提示写明该版本号来自 `ModsConfig.xml`，没有提示就表示读的是 DLL。

**查询会警告什么，不会警告什么。** 每次查询都把快照与已安装的游戏比对，只报告会使快照自身数据出错的情形：游戏构建发生变化，快照所描述的 mod 的 `Defs/`–`Patches/` XML 存在改动或未找到，或这些 mod 现在的加载顺序不同（加载顺序决定哪个 patch 生效，所以值可能是错的，而不只是不完整）。游戏碰巧启用哪些 mod，则**从不**报告：多启用五个 mod 或停用一个，每次查询都保持沉默。这些数字要问 `snapshot status`；零结果已经会点名另一份含有你所查 def 的快照。点名快照的 `--snapshot <名称>` 同样会得到这些警告。

**改动 XML 的那一行会移动，所以不要只读表头。** 答案点名某个已改动的 mod、完全没有答案、或输出没有 `声明于` 列可以排除它时（`fields`、`values`、`code-search`），它位于最上方。每一行都可证明来自其他 mod 时，它落到表*之下*。它只是被移位，从不被压掉：因某个已改动的 mod 删掉它而缺失的一行，看起来与零结果一模一样，所以零结果把它保留在最上方。另外三行（构建发生变化、加载顺序重排、mod 名单无法读取）始终在最上方。

**`modlist show --find <文本>`** 搜索它能打开的每一份已保存名单，并点名打不开的。它回答的是**哪些已保存的名单点名某个 mod**，不是这个 mod 是否已安装。名单成员资格严格来自已保存的 `.toml` 文件（零结果只反映这些名单）；`名称` 列的显示名在可用时从已安装的 mod 动态查得，否则留空，所以 `--find` 对未安装的 mod 只按 `packageId` 匹配。其他命令确实读取那个文件夹：`snapshot status` 背后的 XML 指纹，以及每次查询里的不匹配提示，都是对它的一次扫描。

## 各命令的 `--json` 数据键

根是一个对象；每一句叙述都移入 `notes`，形如 `{kind, text}`，文本形式的「各行相同」前导句除外，其值改为在每一行上重复；数据在各自的键之下，键随命令而异。`<命令> --help` 列出每个命令的键。

| 命令 | 数据键 |
|---|---|
| `search` | `defs`：每个 def 一条平铺的行 |
| `get` | `defs`：**不是行**，而是每个 def 一个嵌套对象，形如 `{def, fields, empty_in_xml, translations}`。一个 def 的字段表是 `defs[i].fields`；`defs[i].empty_in_xml`（empty_element、from）列出其 XML 写出而内部为空、因此没有字段行的元素；根上没有 `fields` 键。即使只有一个 def，它也仍是数组，因为一个名字可以属于多个 def 类型。给多个名字时，对象按名字的给出顺序排列；`--type` 不带名字时，按 defName 顺序排列该类型的每个 def。没有匹配的名字是一条提示，不是一个对象，所以要用 `defs[i].def.def_name` 对照你所问的名字。此外根上还有 `absent`：这些 def 本应取用、而本快照没有的数据层，每层一行；每个这样的层都完整时为空（见下）。此外根上还有 `unloaded_translations`：对所问名字、没有任何已加载的 def 带有的每条 DefInjected 记录一行（name、def_type、path、translated、original、source），旁边另有一行 `found_as`，写着 `无对应 def 的译文`；始终存在，否则为空。在 `--fields` 下，答案改为 `rows`：每个 def 一条平铺的行（def_name，各行含有多于一个 def 类型时再加 def_type，然后每列一个键，以它所读的路径命名），而 `defs` 是空数组；其他时候没有 `rows` |
| `list` | `defs`（给出 def 类型时）或 `types`（没给时），绝不两者同时存在；两种都是平铺的行 |
| `where`（带字段路径） | `matches`，同时给出 `--value` 时也一样 |
| `where --value`（不带字段路径） | `paths`。每行的 def 数分成两个：`defs_exact`（值恰是所问的值）与 `defs_other`（它嵌在更长的值里）。一行可以两者都不为零。带 `--exact` 时只有一种含义，所以该列是单独的 `defs` |
| `values` | `values`，外加 `field`：一个数组，所问的每条路径一个条目，按给出顺序排列，每个条目形如 `{field: {asked, matched_paths, def_types, defs_with_field}}`，写明该路径的取值空间取自哪些完整路径与哪些 def 类型。两个键始终存在，即使只有一条路径，`field` 也是数组；结果为空时，该条目的成员为空或为零，而不是键消失 |
| `fields` | `fields` |
| `search` / `get` / `list` / `where` / `values` / `keyed` / `types` / `members` / `economy` / `read` | 外加 `empty_because`：该次调用中给出的每个选项各一行，凡单独就使结果为空的选项都在内；列为 filter（原样写出的选项）、hidden（只去掉那个选项时，会有多少行回来，按该命令数的东西计：def、键、成员等）、next（去掉它的同一次调用，可直接粘贴）。始终存在；结果不为空，或没有任何单个选项能解释空结果时为空。当路径筛选（`--path-contains` 或 `--exact-path`）使某个 def 的译文表为空时，`get` 会在 `defs[i]` 之内再输出一份，并且仅在此时输出 |
| `search` / `get` / `list` / `where` / `inherit` / `keyed` / `economy` | 外加 `found_as`：每个所问的名字一行，该名字不是命令所查的对象，却以别的身份出现；列为 name、is（def / mod 范围外的 def / XML 节点 / Abstract 节点 / 无对应 def 的译文 / def 类型 / Class / 已声明的字段路径 / C# 类型、字段、属性、方法等 / 界面文本 / 本快照的 mod / 本快照外的 mod / 字段值 / 其他快照中的 def / 其他快照中的 XML 节点）、in（具体在哪里）、next（能到达它的命令，可直接粘贴）。始终存在；名字已找到，或哪里都没有出现时为空 |
| `fields` | 外加 `index_gap`：所问的路径文本在值索引下没有内容、且成因可判定时，每段文本一行；列为 asked（原样给出的文本）、state（全类型无值 / 类型未声明 / 是值非路径；`get` 中为 本 def 无值 / XML 空元素 / 是值非路径）、next（能到达现有内容的命令，可直接粘贴）。始终存在；路径有匹配，或成因无法判定时为空。路径筛选在某个 def 上没有匹配到任何东西时，`get` 会在 `defs[i]` 之内输出同样的表，并且仅在此时输出 |
| `where` / `values` / `fields` | 外加 `completeness`，当范围内有 def 的导出被截断时出现：`scope`（这涵盖哪些 def 类型，用文字写出）、`defs_cut_short`、`types`（每个 def 类型一行，带各自的计数）、`verify`（一条列出它们的现成命令）。范围内没有 def 丢过字段时，该键不存在 |
| `mods` | `mods` |
| `inherit` | `nodes`：**不是行**，而是每个 XML 节点一个嵌套对象，形如 `{node, ancestors?, children?, reference?, witnesses?}`；`absent`：继承层读自 mod 的 XML 文件、而不是 patch 运行之后的 XML 时为一行（节点仍然在），否则为空 |
| `keyed` | `keys`；查询词本身就是一个键、另有少数几个键以它开头时，外加 `siblings`，这些键的译文，形状与 `keys` 相同 |
| `code-search` | `matches`，外加 `ui_text`，当某条输出的命中行对一个快照能解析的字面键调用 `.Translate()` 时；在 `--files-only` 下则改为 `files`，每个文件一行 |
| `read` | `source`，或带 `--outline` 时的 `declarations`，绝不两者同时存在 |
| `types` | `types`：每个类型一行，无论问的是三种问题中的哪一种 |
| `members` | `members` |
| `il` | `il`：每个反汇编的方法一行；指令本身是一个文本块，不是一列 |
| `callers` | `calls`：两个方向共用一个键，用 `from_*` 与 `to_*` 命名两端 |
| `economy` | `things`，外加带 defName 时的 `costChain` 与 `recipes`；`absent`：本快照的经济层不足时为一行（此时 `things` 为空），否则为空 |
| `snapshot status` | `snapshot`（对象），外加 `xml`、`assemblies`、`languages` 与 `mod_list`：每个 packageId 一行；四个数组都存在，没有差异时为空；`layers`：数据层的完整账目，每层一行（layer、state、next、why），包括状态为 齐备 的行；`export_timings` 与 `import_timings`，当那一侧记录有它们时（文本输出没有这两张表） |
| `snapshot diff` | `defs_added`、`defs_removed`、`fields`：三个都存在，没有差异时为空；`notation`，仅当一份快照导出于 1.6 或更早、另一份导出于 1.7 或更晚时 |
| `snapshot rename` | `renamed`（对象）：from、to、snapshot、modlist、export、pin。snapshot / modlist / export 即使那个文件不在也存在 |

本快照没有的层是一张表，不是一句话。查询需要这样的层时，它输出 `absent`，列为 layer、state、next，每个不足的层一行；它需要的每个层都齐备时则什么也不输出。state 是一个封闭的词：已跳过（导出被告知略过它）、无法采集（导出器在那个游戏构建上无法测量它）、未采集或未配置（导入没有扫描它：被关掉，或没有可扫描的东西）、空、未建（没有可读的东西：源码树的 `call_graph:<tree>` 表，或某棵源码树没有保留某个程序集的副本而改读已安装 dll 的元数据时的 `assembly_copy:<tree>/<dll>`，两者的 next 都是 `sources sync`）。next 列是补齐该层的命令，可直接运行，若需要改配置，则配置改动写在命令之前；对于无法采集的经济层，它是导出器自己给出的原因，因为没有命令能补齐它。一个层为什么不足，只写在 `snapshot status` 里，它的 `layers` 表是完整账目。

代码输出同样是行，所以不必从 `path:line:text` 里再解析出任何东西：`code-search` 的行是 `{file, line, is_match, group, text}`，`read` 的行是 `{file, line, text}`，行来自 `--member` 或 `--type` 时则是 `{file, line, kind, declaration, text}`。在 `--outline` 下，行改为 `{kind, modifiers, name, in, lines, at}`，其中最后一个是形如 `"817-951"` 的字符串，可直接交还给 `--lines`，不是一对整数。不要凭这些修饰符断定「这个类型是否重写它」：重写的成员与该类型自己引入的虚成员，在文本里读起来一样，而它们指向相反的下一步。`members <Type> --overrides` 改按元数据位来断定。`--json` 从不折叠列：每一行都带每一列。截断提示带 `kind: "截断"`，但只限于本次查询自己做的那一刀。导出器丢掉的字段改为 `kind: "边界"`，所以写成 `notes | where kind == "截断"` 的扫描，抓到你已经要求的分页截断，却漏掉你没有要求的导出期截断。在接受 `--path-contains` 的 def 侧命令（`get`、`inherit`、`fields`）上，筛选所匹配行数的计数带 `kind: "筛选"`：那是你要求的筛选，不是被截掉；代码命令则改用 `--file-glob` 或 `--in` 收窄文件。

## keyed：各层详解

`生效` 行排在最前；Keyed 译文按 mod 加载顺序相互覆盖，快照保留胜出者。`文件` 行是某个 mod 的语言文件里带有的译文，不一定是胜出的那条：多数来自已安装但未启用的 mod，它们从未进入覆盖的角逐。`占位符` 是单独的「是 / 否」列，而不是第三种出处；没有占位条目时，文本表不印这一列（`--json` 的行总带着）。`占位符` 为 `是` 的行，在它的语言文件里声明这个键却没有译文，所以游戏在那里回退到英文，这正是译文覆盖率问题所问的，`--empty-translation` 也只列这些。这样的行仍带 `生效` 或 `文件` 这个出处。

只要已配置 `mod_roots`，`文件` 层就在导入时扫描，所以只有某个未启用的 mod 翻译的键，仍能凭它的文字找到。没有做过这一扫描的快照，会为 `disk_translations` 输出一行 `absent`，`出处` 列此时只有 `生效`。并且，如果导出时的游戏根本没有加载语言数据，快照里就没有 Keyed 译文：`keyed` 会说明这一点，而不是报告你的键不存在。

`--empty-translation` 不需要查询词：单独使用时它筛选整个层，所以 `rimsearcher keyed --empty-translation` 就是「列出每一条未翻译的字符串」；给出查询词，则改为收窄这个结果集。不带该开关而省略查询词，列出的是层本身。没有任何一条是占位条目时，答案是对整个层的覆盖情况说明，退出码为 `1`。

从代码到屏幕文字不需要第二步：`code-search` 会解析它所输出各行中传给 `.Translate()` 的字面键，并在命中旁追加一张 `ui_text` 表（`--no-resolve-keys` 关闭它）：截断命中的 `--limit` 同样会截断这张表，而没有传给 `.Translate()` 的字面量不会得到行。代码在运行时拼出来的键（`"Stat_" + x`）没有字面量可解析，答案会写明有多少行是这样的，而不是留成空白。从键找到打印它的代码：`rimsearcher code-search '"TheKey"'`。

## 反编译源码树

反编译出的 C# 是一棵目录树，**每个带有程序集的 mod** 一个目录，以其小写的 **packageId** 命名；游戏自己的代码与官方 DLC 合并成同一棵 `vanilla` 树。`rimsearcher sources list` 写明有哪些源码树，以及哪些已与已安装的内容不匹配：`程序集已改动`（某个程序集文件已变化）、`引用的程序集已改动`（程序集文件匹配，但其引用的某个程序集已被另一版本替换）、`反编译器不匹配`；`rimsearcher sources sync` 按快照的 mod 实际加载的内容重建这三类。在那之前，这样的源码树仍能回答问题，答的是它被反编译时那个构建的情况。

`code-search` 读完所选的全部文件时，只报命中数与文件数，不报范围：范围就是 `--source` / `--scope` / `--assembly` / `--in` / `--file-glob` 选中的文件，都不给时是当前快照加载的每棵源码树中的 `*.cs`（`unitystubs`、`waylith`、`corelib` 与 `gamelibs` 除外）。`--in` 所选的文件或目录与你键入的不同时，会点名它们。被 `--max-files` 停下的扫描写明读取了几个文件、哪些源码树没有读到。有哪些源码树见 `sources list`；它列为 `空树` 且在快照之外的源码树，是 `sources sync` 没有计划任何工作的那种。读取到一半就停下的扫描得到零结果时，会说明这一点，并且**不会**把你指向快照。

两个命令都不比较版本。反编译根目录是一个 **git 仓库**，所以两次构建之间有何改动，是 `git show` / `git log -p` 的问题，这还带来重命名检测与逐文件的历史。`sources sync` 对它重写的每棵树各自提交一次，提交信息为 `<tree>: sources sync (<原因>)`。不要给它添加远端：它保存的是反编译出的游戏代码，只应留在本地。`sources sync` 不会覆盖仍有未提交改动的树（手工编辑，或提交失败的一次同步）。`--force` 不能越过这一条。先提交或还原，再重新同步。

## 示例与推导

**为什么绝不要凭 defName 猜类。** 多数 `GenStepDef` 运行的类，名字并不是 `GenStep_<defName>`：`rimsearcher where genStep.Class --type GenStepDef --scope vanilla` 把每一个都列在其 defName 旁边。有些确实一致，所以 `code-search "class GenStep_<defName>"` 命中与否，并不能说明这个 def 的任何情况。类名要从 `get` 的 `*Class` 各行取得：身份块里的 `Class` 行是该 def **自己的**类型，同一类型的每个 def 都一样，除非 XML 在该 def 自己的标签上选用一个派生类型，`list <DefType>` 会写明该分组含有多少个 `Class`；而 `*.Class` 与 `*Class` 各行，如 `genStep.Class`、`comps[0].compClass`、`thingClass`，才是实际运行的。

**`genSteps[N]` 的位置不是运行顺序。** `MapGenerator` 先按 `GenStepDef.order` 升序排列各步骤，再按 def 的载入序号排列，所以 `get <MapGeneratorDef> --path-contains genSteps` 输出的编号是 XML 里的，只有在该列表已经按 `order` 排好的地方才与执行一致，一些原版地图生成器如此，另一些不是。下结论谈顺序之前，先读每一步自己的 `order`。快照里没有任何东西把两者配对起来：列表位置与 order 值存在于不同的 def 上。

**`where Class` 这一维覆盖什么。** 嵌套的 `Class="…"` 对象的运行时类型，可以用字段名 `Class` 查询，分两部分：列表元素（`<li Class="…">`）与单个选择类的字段（`GenStepDef.genStep`、`ThinkTreeDef.thinkRoot`）。零结果会写明它搜索多少个值、在哪些路径之下。

**逐步走一遍锚定。** `code-search MapPortal` 找到每一处提及：分布在几十个文件里的几十处命中，全部输出，声明被渲染得与其他每一处命中一模一样。把模式锚定（`code-search "class MapPortal\b"`）就收窄到一处，而 `types MapPortal` 省去猜测：它读元数据，所以无须一个可能写错的模式，就能点出程序集与基类型。无论哪种方式，确定类型之后，`members MapPortal` 列出其中有什么，`read <文件> --outline` 从文本侧给出同样的内容。单独一处 `code-search` 命中意味着该模式找到*这个东西在哪里定义*，而不是*它做什么*。

**`search` 容忍什么。** 词、部分名字、译文、复合名字内部的匹配：`search shield` 能查到 `Apparel_ShieldBelt`。没有一处命中时，它再搜被译文替换的原文，所以在中文快照上 `search "brain damage"` 照样可用；仍没有时返回拼写上最接近的名字，这使拼错（`shild belt`）或 CamelCase 首字母缩写（`psl` → `PsychicShockLanceCast`）也能命中：这些行会写明这一点，并且 `命中于` 列为 `近似拼写`，所以回退的行并不是你所问的那个 def。`命中于` 列写明每行匹配于*哪里*：`label` 为空的行并不是匹配失败，而是匹配在别处，该列点出那个位置。

**`缺省` 豁免，具体而言。** 像 `HarvestDestroys => harvestAfterGrowth <= 0f` 这样的属性读取字段的值；`缺省` 行里的值是真实的，所以该属性的结果可以由它回答。

**跨 def 类型的同名。** `PsychicSensitivity` 既是 `StatDef` 又是 `TraitDef`；`--type <DefType>` 选其一，`--json` 则无论如何都把每一个放在各自的槽位里。

**同一个索引块中的兄弟字段。** `minFuelCost` 与 `fuelPerTile` 位于同一个 `comps[N]` 块里，回答「这能走多远」的两半。`--path-contains` 筛选会把兄弟字段切掉，所输出的行与新建实例不同时，输出会点出同一块里其余与新建实例不同的字段。

**文件夹不是命名空间。** `HealthCardUtility` 在 `RimWorld/` 下，`HealthUtility` 在 `Verse/` 下。猜出的 `RimWorld/HealthUtility.cs` 仍能读到该文件：`read` 回退到唯一一个同名的文件，并用文字写明所给路径并不存在，但裸名 `HealthUtility.cs` 无须绕这一圈就能到达。

**裸字段名上的 `values`。** `values <字段>` 给出完整的取值空间，并写明哪些完整路径与 def 类型提供它：像 `damageAmountBase` 这样的裸名可以匹配几条不相干的路径。

**存放分组。** 游戏把派生类型归在其基类型的数据库之下，所以 `CreepJoinerAggressiveDef` 的实例存放在 `CreepJoinerBaseDef` 之下。`list <SomeClass>` 告诉你该去哪里查找，而不是声称这个类型不存在。多数分组只含一个 `Class`；此时 `--class` 起不到任何收窄作用，这样的 def 类型把它的全部行为存放在嵌套的 `Class="…"` 字段上：快照里每个 `GenStepDef` 都是 `Verse.GenStepDef`，每个 def 运行哪个 `GenStep` 写在 `genStep` 上，索引为 `genStep.Class`。

## 分页与报错详解

`get` 上的 `--limit` 数的是**每个 def 的**字段，不是 def：给多个名字、或给 `--type` 而不给名字时，每一块都缩短相同的数，而 def 的数量就是你所问的那么多。没有办法缩短 def 的列表，那是 `list <DefType>` 的用途，它的 `--limit` 与 `--offset` 数的确实是 def。

没有任何命令自行扣下行：省略 `--limit`，答案就是整个结果集，`read` 也不例外，`--lines`、`--outline` 或 `--member` 无论多长都全部输出，而裸的 read 给出整个文件。`--limit` 只接受正数，别无其他：`all`、`none` 与 `0` 都是用法错误。分页从你传 `--limit <n>` 时开始，分页的答案写明这一页含有多少行、总共有多少行、从哪里开始；下一页是这个起点加上 `--limit`。最后一页会说明它是最后一页；`--offset` 超出末尾时，报告的是越界，而不是「什么也没找到」。把 `--offset` 传给 `get`、`inherit` 或 `code-search` 是用法错误，会列出该命令所接受的选项，而不是无声地忽略这个开关。

未知选项会被拒绝而不是忽略，并给出最接近的合法拼写；没有接近的时，则给出该命令接受的全部选项，这同时回答「这里叫什么」与「这里到底有没有」。

`read` 拒绝去猜的两件事，因为猜错读起来与猜对一模一样：裸文件名匹配多个文件时，它把它们列出来而不是挑一个；`--lines` 与 `--member`/`--type` 同时给出是用法错误，而不是无声地偏向其一。

`where` 猜错时：给出的是一个不是字段路径的单词，`where` 会算出这个词实际是什么，是字段值、def 名、def 类型还是 mod，并点出能到达它的查询。没有任何东西引用这个名字时，它就这样说，而不是交还一条结果会为空的查询。

## 导出机制

`rimsearcher export --modlist <名称>` 在没有窗口的情况下运行游戏，工作期间什么也不输出。某个加载阶段停着不动时，它在 stderr 上说明，并**继续等待**；让游戏停下的是 `--timeout`，或者游戏自己的日志说数据加载失败，那种情形任何等待都无法恢复。（在导出之外，查询的 stderr 保持为空，除非命令以 `2`（用法错误）或 `70` 退出：真正的落空，或超出末尾的 `--offset`，都报告在 stdout 上，见 SKILL.md 里的退出码契约。）等待时长用 `--timeout` 加长。`<名称>` 必填，来源是 `rimsearcher modlist list`：已保存的名单是导出能依据的唯一对象。由 `modlist save` 写出的、由 RimRunner 编写的、以及手工键入的，都出现在 `modlist list` 里，读回的结果相同：显示名是从已安装的 mod 动态查得的，而不是存在名单文件里。

### 为脚本取整层数据

每次调用就是一个进程，所以要遍历数百个 def 的脚本，不应该一个 def 花一个进程。有三种形式可用，每种返回的都恰是单个名字的调用所返回的：

- `get A B C [--type <DefType>]`：所点名的 def，`defs` 中每个 def 一个对象，按名字的给出顺序排列。
- `get --type <DefType>` 不带名字（或带 `--all`，意思相同）：该类型的每个 def，按 defName 顺序排列。展开为空的名字列表（对空文件执行 `$(cat names.txt)`）也落到这里；没有给名字时，第一行会说明。
- 不带 defName 的 `economy`：整个定价层，`things` 下每个 thing 一行，键与带名字的调用所给的相同，共 22 个。`costChain` 与 `recipes` 只为点名的 thing 计算，所以只要价格、别无所求的脚本，要用不带名字的形式。

`--defaults` 与 `--path-contains` 对每一块同样适用。

其他每个查找也是如此：`inherit`、`keyed`、`read`、`list`、`fields`、`values`、`types`、`members`、`il`、`callers` 与 `usages` 都在一次调用里接受多个参数。它们的行进入同一张表，由一列写明每行回答的是哪个参数，所以脚本按那一列分组，而不是按它来自哪个进程分组；`--limit` 与 `--offset` 分别作用于每个参数，所以一批不会在批的层面被无声地截断。

### 确认某条路径没有被截断掉

每一份截断报告都是一个计数，从不是被丢弃内容的清单，所以问「`statBases` 本身在这个 def 上被截断吗？」没有直接的答案。但 `get` 确实会说明是**哪一种上限**停下导出器，而各种成因的后果不同：先读这一句。

| `get` 写的内容 | `snapshot truncated` 的列 | 实际缺失什么 |
|---|---|---|
| *N 个值按长度上限截断* | 值截断数 | **不缺任何路径。** 这些行都在表里，只是文本的尾部缺失。 |
| *N 个列表在条数上限处截断* | 列表截断数 | 这 N 个列表中超出上限的条目，以及它们之下的一切。 |
| *N 个嵌套对象超出深度上限而未展开* | 超深度上限数 | N 整棵子树，大小未知。 |
| *N 个字段在游戏里读取时抛出异常而略去* | 无法读取数 | N 个值，或者在遍历时抛出异常的列表字段中未走完的其余部分。 |
| *N 个字段超出该 def 的字段上限而丢弃* | 超字段上限数 | N 条路径，在该 def 上的任何位置。 |

仅属第一行所述情形的 def 没有缺失任何内容，`get` 会如此说明。它仍会被 `snapshot truncated` 列出，列上带同样的计数。`snapshot status` 在整份快照上对同样的拆分求和，得到 `丢失路径的 def 数` / `值被截断的 def 数`。其余各行各丢一种不同的东西，这就是计数是下界而不是总数的原因：一个停下的列表或一棵未展开的子树，无论其下有多少内容，都只算一次。

只有最后一行会因导出时启用较少的 mod 而改善：每个 def 的上限是唯一会把补丁加入的字段计入的上限。遇到的正是这一种上限时，把两份快照上 `get <def> --json` 的路径集合做差，先把 `[0]`、`[1]` 等规范化为 `[]`：路径在较小的快照里有、在较大的里没有，就是被截掉，差集为空即是确认。对其余三种上限，缩小 mod 名单不会改变任何东西，所以在那里差集为空什么也不能确认。
