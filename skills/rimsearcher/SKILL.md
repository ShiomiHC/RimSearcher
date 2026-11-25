---
name: rimsearcher
description: 回答关于 RimWorld 的 def 与 C# 的问题：一个 def 在 patch 与继承之后实际含有什么，哪些 def 用到某个 Class 或某个值，一个字段可以设为什么，以及某个符号在游戏代码中的位置。凡涉及 RimWorld mod 开发、Def XML 或游戏程序集的任务都应使用。
---

# RimSearcher

有两个事实来源。**快照**：导出时游戏内存中全部 def 的数据库，patch 已应用、继承已解析、由代码生成的 def 也在其中，用 `rimsearcher` CLI 查询。**程序集**：游戏编译出的 C#，由同一个 CLI 经反编译源码树读取，其源码文本、IL 与调用图出自同一次构建。另有一层来自 mod 的 XML 而非内存：继承，游戏在导出前已将其丢弃。`inherit` 把它当作一棵树来遍历；`get --defaults` 的 `取自` 列（JSON 键 `from`）沿同一条继承链上溯，说明一行是由本 def 写的还是由祖先写的。

每个命令见 `rimsearcher --help`，每个命令的全部选项见 `<命令> --help`；示例与边界情形见 [references/usage-notes.md](references/usage-notes.md)。本页写的是契约：一个答案意味着什么，以及查询在哪里无法回答。

## 按问题选工具

| 问题 | 答案所在 |
|---|---|
| 这个 def 实际含有什么？ | `rimsearcher get <defName>`：给出多个名字时每个 def 输出一块，按给出顺序排列 |
| 多个 def 的同几个字段并排比较？ | `rimsearcher get <defName> <defName> --fields <路径>,<路径>`（或用 `--type <DefType>` 取某个类型的全部 def）：每个 def 一行，每条按整条路径匹配的路径一列；`statBases.MarketValue` 读作 `statBases[stat=MarketValue].value` |
| 某个类型的全部 def，完整输出 | `rimsearcher get --type <DefType> --json`，**不给 defName**：每个 def 一块，按 defName 排序，只用一个进程 |
| 这个 def 实际运行哪个 C# 类？ | `rimsearcher get <defName>`：查看 `*Class` 各行 |
| 原版 XML 写有这一行吗？用 Replace 还是 Add？ | `rimsearcher get <defName> --defaults`：查看 `取自` 列。`XML`（以及 `XML=parent` / `XML=缺省`，这两种虽写有此行，但不改变任何值）表示本 def 写有这一行，Replace 能定位到；`parent` 表示只有祖先写有；`缺省` / `运行时` / `缺省\|运行时` 表示没有 XML 写它；`在 <container> 下` 表示尚无定论。`+patch` 后缀表示这一行由 patch 放在这里（Replace 仍能定位到）。表上会注明读的是哪份 XML。其余内容见 `get --help`：列表条目如何回连到 defName 标签，以及 `在 <container> 下` 留下哪些未定。 |
| 这个东西叫什么？我只知道一部分。 | `rimsearcher search <query>` |
| 哪些 def 用到这个 Class / 这个值？ | `rimsearcher where <fieldPath> <value>` |
| 哪些 def 用 `Class="…"` 选用这个 Class？ | `rimsearcher where Class <ClassName>` |
| 同上，但只限一个 def 类型 | 加 `--type <DefType>`：对 `where`、`values`、`search` 与 `get` 都有效。def 类型写在命令本来要收的参数之前（`where HediffDef compClass X`）时，按 `--type` 读，输出里会写明；位置参数恰好是命令本来要收的那些（`where HediffDef X`）时，第一个按字段路径读。 |
| 一批 def 的同一个字段 | `rimsearcher where <fieldPath>`，**不给值**：每条命中的字段路径一条平铺的行。不要先 `list` 再对每个名字各 `get` 一次：`get` 的输出按 def 嵌套，`where` 不嵌套。确需多个 def 的完整字段表时，把全部名字传给同一个 `get`，或用 `get --type <DefType>` 取该类型；绝不要一个名字开一个进程。 |
| 这个字段可以设成什么？ | `rimsearcher values <fieldPath>...` |
| 某个 stat（或任何带键的列表条目）在各 def 上设成什么 | 取值分布用 `rimsearcher values "statBases[stat=Mass].value"`，各 def 的值用同一路径的 `where`。stat 名是一个*值*（`statBases[N].stat`），所以 `values Mass` 够不到它；`statBases.Mass` 读作 `statBases[stat=Mass].value`。键可以是条目的任一字段：`equippedStatOffsets[stat=…].value`、`costList[thingDef=Steel].count` |
| 这个 def 类型有哪些字段？ | `rimsearcher fields <DefType>...` |
| 某一类的全部 def | `rimsearcher list <DefType>...` 加 `--find <文本>`；不给类型则列出 def 类型 |
| 哪些已保存的 mod 名单点名这个 mod？ | `rimsearcher modlist show --find <文本>` |
| 什么继承自它 / 它继承自什么？ | `rimsearcher inherit <name>...` |
| 它值多少 / 造它要多少成本？ | `rimsearcher economy <defName>...`：这不是 def 的字段，`get` 无法回答。多个名字进入同样的三张表，各行可以对齐比较。不给名字则一次列出整个定价层：每行的键都相同，只是没有 `costChain` 与 `recipes`，这两项只有给名字的形式才计算 |
| 界面文本 ↔ 译文键 | `rimsearcher keyed <键或短语>...` |
| 哪些界面文本没有译文？ | `rimsearcher keyed --empty-translation`，不给查询词 |
| 一个 C# 类型在哪里，它派生自什么 | `rimsearcher types <Name>...`：加 `--derived`、`--bases`、`--transitive` |
| 类型里有什么，哪些成员是虚的或被重写 | `rimsearcher members <Type>...`：筛选依据是元数据位，不是文本里的关键字 |
| 哪些类型声明有这个名字的成员 | `rimsearcher members --name <Member>`，不给类型：遍历全部类型的元数据读取，而 `code-search` 要扫描每个文件的文本 |
| 哪些派生类型重写这个成员 | `rimsearcher types <Base> --derived --transitive --declares <Member>` |
| 谁调用这个方法 | `rimsearcher callers <Type>.<Member>...`：加 `--callees` 查另一个方向 |
| 谁读或写这个字段 / 属性，谁用到这个类型 | `rimsearcher usages <Type>.<field>...` 或 `usages <Type>`：只查看写入加 `--writes` |
| 一个方法的指令（transpiler 用） | `rimsearcher il <Type>.<Member>...`：迭代器或 async 方法加 `--state-machine` |
| 所有文件中、或一个文件或目录内的某种代码*形状* | `rimsearcher code-search <regex>`：`--in <文件或目录>` 按 `read` 称呼文件的方式点名 |
| 一棵源码树或一个目录里有哪些文件，各有多少行 | `rimsearcher code-search --files-only --source <源码树>`：用 `--in <目录>` 或 `--file-glob` 收窄；再给一个正则，则只列该正则命中的文件 |
| 一个文件的文本、其中某些成员、或一段行区间 | `rimsearcher read <文件>... --member <名称>`（要读多个就重复 `--member`，或写成 `A\|B`） |

**凡按名字查询的命令都能在一次调用里给多个名字**：`get`、`economy`、`inherit`、`types`、`members`、`il`、`callers`、`usages`、`read`、`list`、`fields`、`values`、`keyed`。例如 `rimsearcher values compClass thingClass`、`rimsearcher fields ThingDef HediffDef`、`rimsearcher read A.cs B.cs --outline`。每个名字各有一行计数说明，`--limit`（以及接受它的命令上的 `--offset`）分别作用于每个名字，而不是整批。各行进入同一张表，由一列写明每行回答的是哪个参数（`get` 与 `read` 的正文是例外：与单个名字时一样，每个名字输出一块）。某个名字落空不影响其余名字：命令仍返回退出码 0，只有全部落空才返回非零。**不要一个名字开一个进程。**

代码侧另有一页，写明哪个命令回答什么，以及各种陷阱：[references/code-side.md](references/code-side.md)。

## 想 grep XML 时，先停下

PatchOperation 会改写 mod 文件中的 XML，继承会把它合并，而且成千上万个 def（`Meat_*`、`Corpse_*`、蓝图）不存在于任何文件中。把意图换成下面的问法：

| 不要 | 改问 |
|---|---|
| grep `<defName>Bullet_` | `rimsearcher search Bullet_` |
| grep `<li Class="CompProperties_AmbientSound">` | `rimsearcher where compClass CompAmbientSound` |
| grep 某个 `Class="…"` 查看哪些 def 选用它 | `rimsearcher where Class <ClassName>` |
| grep 某个 `<thingClass>` 查看谁在用 | `rimsearcher where thingClass <ClassName>` |
| grep 查找某个标签有哪些取值 | `rimsearcher values <tag>` |
| grep `Name="BaseBullet"` 找 `Abstract` 父节点 | `rimsearcher inherit BaseBullet` |

第二行：XML 写的是 **properties** 类，def 字段里存的是解析后的 **comp** 类；用 properties 的名字去问，要多绕一趟转向。

## 范围之外

- **「这段 XML 该怎么写？」** 从运行时对象回到可编写的源头没有路径：读声明它的 C# 类，XML 解析器绑定的就是该类的字段。`get` 的 `文件` 行（JSON 键 `source`）只是一个未经核实的裸文件名。
- **文件系统中的文件。** `values texPath` 可用（路径是 def 的字段），但没有任何命令查看它指向的贴图、音频等文件，那是 `ls` 的问题。

## 容易踩的缺省

下面每一条都是这样的情形：显而易见的做法会返回一个干净、看似完整的答案，而它回答的是另一个问题。

- **`where` 的路径从末尾起匹配；每个 `--path-contains` 筛选都是子串匹配。** 裸名按整段匹配最后一段（`where label` 不会收进 `cannotEnterLabel`；`fields ThingDef --path-contains label` 会，因为它是子串匹配），带点的路径同样按整段匹配：`where graphicData.shaderType` 不会收进 `swimmingGraphicData.shaderType`。它仍是后缀匹配，所以其上方可以还有别的段：输出会点出这些路径形状；改用 `where <该形状> --exact-path` 重新运行，就只保留那一种。`[]` 在 `where` 与 `values` 的位置路径上代表任意下标，在每个 `--path-contains` 上也一样：`comps[].props.energyMax` 匹配每一个 `comps[N].props.energyMax`。
- **`get --path-contains` 与 `where --value` 同样是子串匹配**：`--path-contains soundImpact` 也会返回 `soundImpactDefault`，含义相反。`where --value` 不给字段路径时，这两类分处两列，`整值 def 数` 与 `内含 def 数`（JSON 键 `defs_exact` 与 `defs_other`），因此某一行可能全是子串命中；`--exact` 去掉它们，只留一列 `def 数`。
- **同一个 defName 可以属于多个 def 类型，此时 `get` 为每个 def 输出一块**：`HospitalBed` 既是 ThingDef 又是 ResearchProjectDef。在 `--json` 下，这是一个含多个条目的 `defs[]`，`defs[0].fields` 读到的可能是*另一个 def* 的字段，表现为「这个 def 没有这个字段」，而不是报错。按 `defs[].def.def_type` 选条目，或用 `--type <DefType>` 钉死。错误的 `--type` 绝不会返回另一个 def：给一个名字时以退出码 `1` 退出并带一行 `empty_because`（筛选条件为所给的 `--type`，下一步为不带它的同一条 `get`）；给多个名字时则输出一句话，写明该名字实际所属的类型。`边界` 类提示每次都会说明这种重名，带 `--type` 时也一样。没有 defName 时把运行时的 `Class` 当作 def 类型传入（`get --type <Class>`、`list <Class>`），以退出码 `3` 退出，并带一行 `found_as`，其 `实为` 列是 `Class`；应粘贴它的 `下一步`（`list <DefType> --class <Class>`），不要读作「没有这个 def」。带 defName 时则是上面所说的错误 `--type`。
- **字段名要反查，不要猜。** `where --value <value>` 报告哪些路径持有这个值。猜出的字段名恰好存在时，会返回一张干净、看似完整的表，但那是另一个字段的表，这是这里代价最高的失误。
- **`--scope vanilla`**（也写作 `core` / `base` / `official`）是 Ludeon 发行的全部模块，**不是**一份名为 `vanilla` 的快照。在快照命令上，它选的是快照内的 mod。
- **代码侧按源码树收窄**：`rimsearcher code-search <regex> --source <源码树>`，用 `rimsearcher sources list` 查看源码树名。`--mod` 在 def 侧收 packageId（`--mod ludeon.rimworld.biotech`）或配置组，在代码侧收 `sources list` 里的源码树名，多个就重复（`read` 只接受一个）。`code-search --scope` 接受 def 侧的表达式，在已同步的源码树上展开：DLC 的 id 会读整棵 `vanilla` 树，写出的 packageId 与 `--source` 点名它相同。`code-search` 不搜索 `unitystubs`（游戏里 Unity 的替身）、`waylith`（游戏引擎）、`corelib`（游戏 .NET 运行时的核心库）或 `gamelibs`（游戏附带的其他库），除非点名。按名字查找的命令无论如何都读 `unitystubs`，只在没有别的源码树含该名字时才读 `waylith`、`corelib` 与 `gamelibs`。`callers` 与 `usages` 不搜索 `corelib` 与 `gamelibs` 中的调用处与使用处，除非点名。在一棵源码树内，任何代码命令上的 `--assembly <名称>` 只保留所点名的程序集；`*` 为通配符；多个就重复；点名某个程序集即点名其源码树。
- **代码侧回答的是快照里游戏已加载的代码**（与 def 侧共用同一个 `--snapshot`）。快照之外的 mod，或快照内某个 mod 中游戏未加载的程序集，都被收掉；`范围外有` 那一句点名被收掉的部分在哪些源码树。`--include-unloaded` 把它们也读进来（每个代码命令上是同一个开关），点名其源码树或程序集同样可以（以源码树名开头的路径、`--source`、`code-search --scope` 里写出的 packageId、不带 `*` 的 `--assembly`）；两种方式读进来的内容都带标记。

## 输出说不出的事

CLI 在打印时会自行解释它的表、无结果的成因与边界。下面是它无从表述的几条：

- **`取自` 列决定一个值有多大分量。** `XML` = 本 def 的 XML 写有它。`XML=parent` = 它写的文本与最近一个写有它的祖先相同，`XML=缺省` = 没有祖先写它，且值等于新建实例的值；这两种写法都不改变任何值。`parent` = 只有祖先写有它。`缺省` = 没有 XML 写它，且值等于新建实例的值。`运行时` = 没有 XML 写它，且值不同，所以是 def 加载时由代码设定的。`缺省|运行时` = 声明类型无法构造出来作比较。在读取 patch 运行之前 XML 的快照上，后三种都输出 `未写`；表上会注明读的是哪份 XML。读 C# 构造函数只能判断出缺省值*可能*来自哪里，看不出 XML 是否也写有同一个值。豁免两头都成立：*读取*该值的规则（阈值、比较）从 `缺省` 行取值即可回答，因为无论哪种情形，这个值都是真实的；`compClass` / `thingClass` / `workerClass` 通常由构造函数赋值，所以那里的 `缺省` **在两个方向上都不是信号**，旁边出现 `运行时` 同样寻常，经由不止一条路径到达。这两个值都不说明是谁挂上这个 comp，要查看 `声明于` 列与该块的 `Class` 行。值等于新建实例的行默认隐藏（有一句话说明隐藏多少行）；`get --defaults` 显示它们；`--path-contains` 始终显示被点名的字段。
- **该类型的多数 def 也带有的值，来自继承或引擎填充的次数远多于人工编写**，所以其中的 `运行时` 仍不是 def 作者的决定。`get` 的表下方那一行会点出这些值；它对作者归属意味着什么，那一行没有写。
- **不给 `--limit` 就得到每一行**；`read` 不给 `--lines` 就读整个文件。传一个数是有意缩短答案；此时计数说明行会写明总共有多少，并且在 `list`、`search`、`where`、`values`、`fields`、`keyed` 与 `economy` 上，`--offset` 可以翻到其余部分。`code-search` 同样读取并打印每一行：`--limit`、`--max-per-file` 与 `--max-files` 都没有缺省值，其中只有 `--max-files` 能让答案不完整，给它一个数会使计数变成 `至少 N`。它不经询问就缩短的只有过长的行：反编译出的代码会把整张表写在一行里，所以 `--max-line-chars`（240）会把这样的行输出为其命中处的邻近文字，其余以 `…` 表示。这缩短的是宽度而不是行数：`--max-line-chars 0` 输出完整行，`--json` 无论如何都带完整行。
- **退出码**：`0` 有结果，`1` 无结果且已实测，`3` 无结果是因为这个名字另有所指（`found_as` 表写明所指对象与位置；只是一条无 def 设置的已声明字段路径，或从 `where` / `values` / `get` 找到的 C# 成员，仍保持 `1`），`4` 无结果是因为缺层（`absent` 表点名该层），`2` 用法错误，`69` 由 `export` 启动的游戏没有交回导出文件，`70` 工具自身缺陷。对查询而言的 `1` 包括三种情形：这份快照里没有任何东西带它、某个筛选使结果为空（`empty_because`）、`--offset` 超出末尾；输出会写明是哪一种；`3` 与 `4` 附带下一步要运行的命令。**用 `;` 串联命令，不要用 `&&`**：否则一个有信息量的非零退出码会丢掉排在其后的命令。`;` 串联只报告最后一个退出码，所以要读输出。一个 `get` 给多个名字时，`0` 的含义是至少有一个名字有输出，不是全部都有输出：没有匹配的名字有自己的提示，`defs` 里没有它的对象，所以要用所问的名字核对该数组，而不是只依据退出码。全部落空则为非零。**除用法错误、导出失败与工具缺陷外，一切都写到 stdout**，包括空结果的原因。`2`、`69` 与 `70` 的消息在 stderr 上，所以 `2>/dev/null` 会把拼错的选项变成一个无声的空结果。
- **批量形式保留每一块各自的句子，所以也不要用管道截取。** `get A B C` 与 `get --type <DefType>` 每个 def 输出一块，每块都带自己的计数、截断警告与脚注；`head` 只保留第一个 def 的，丢掉其余，这会被读成「其他 def 没有要声明的」。要取整份输出；输出很长时用 `--json`：提示在其中仍可按位置访问，各 def 自己的提示点明所属的 def。管道只要数据行时，用 `--quiet` 有意丢掉叙述，而不是让 `head` 无意中切掉。
- **`--quiet`**：stdout 只输出数据块，别无其他：没有提示、没有脚注、没有快照标签。管道要数行或切列、而叙述会落进它们中间时用它。查无结果的查询于是没有结果行；`found_as`、`absent`、`empty_because` 与快照不匹配的表属于数据块，照常输出。在 `--json` 下，标签仍保留为 `snapshot` 键。
- **叙述另有去处。** `RIMSEARCHER_RUN_LOG` 指向一个路径时，每次运行都向那里追加一行 JSON：每条提示连同它的种类、计数与前后两侧的块，外加退出码、是否指定 `--quiet`、以及用法错误的消息（它不经过报告，所以另有自己的键）。未设置时什么都不写，运行也没有额外 IO。管道或 `--quiet` 挡在 stdout 之外的内容，仍可从那里查到；读它的工具可以分辨叙述是被过滤掉的，还是调用方要求丢掉的。
- **`--json`**：根为一个对象；叙述移入 `notes`，每条为 `{kind, text}`；数据键随命令而异，但始终存在，没有匹配时为空数组。与它**并列**的键可能是有条件的，各命令的 `--help` 写明条件。键表见 usage-notes。当范围内有 def 的导出被截断时，`where`、`values` 与 `fields` 会加上 `completeness`：它用文字写出范围、计数、每个 def 类型一行，以及一条可直接运行的列出命令。它不存在就表示范围内没有 def 丢过字段。
- **凡由程序读取的输出都用 `--json`。** 文本表是为人类读者排版的：列按宽度补齐，每一行都重复同一个值的列会被提到一行 `各行相同，下表不再逐行列出：` 里，之后**不再出现在各行中**；当该值与传给命令的内容逐字相同时（def 类型、字段路径、值或查询词；真假值不论大小写），那一行也把它省去。按空白切分这些行，得到的字段数随数据而变，被提走的列读起来像缺失而不是恒定，两种失败产生的都是看似合理的值，而不是错误。
- **同一个键名并不是同一种行的形状。** 多数键存平铺的行；`get` 的 `defs` 是每个 def 一个嵌套对象（`{def, fields, empty_in_xml, translations}`），`inherit` 的 `nodes` 是每个 XML 节点一个（`{node, ancestors?, children?, reference?, witnesses?}`）。所以 `search` 与 `get` 都在 `defs` 下作答，嵌套方式却不同；一个 def 的字段表是 `defs[i].fields`，绝不是根上的 `fields` 键。形状以命令自己的 `--help` 为准，并且**按实际路径索引，不要探测**：写成 `j.fields || j.rows || []` 的脚本会把猜错变成一个空数组，而空数组恰恰就是「该 def 没有这个字段」的样子。这种失败会写出一份看似完整的文件，并以退出码 0 退出。

## 查询越不过的层

- **`search`** 覆盖 def 名、label、description，以及注入到 def 上的译文；没有一处命中时，再搜被译文替换的原文，所以英文词能在中文快照上找到它的 def。它**不**覆盖 C# 类名（→ `where compClass <Class>`）与 `Languages/*/Keyed` 下的界面字符串（→ `keyed <短语>`）；零结果会点明你撞上的是哪一种：写出该名字实际所在的层，并填好查询词，而不是把这份清单复述一遍。
- **def 的译文与它的字段共用同一套字段路径**，所以从字段表里抄出的字段路径，例如 `stages[0].label`，在两张表中选中同一处。语言文件把同一个槽位另有两种写法：`stages.0.label`，`get <defName> --path` 会把它折回，因此同样能到达两张表；以及 `stages.<handle>.label`，它只能到达译文表，因为 handle 不是字段路径。键是语言文件必须写的内容，它只在**与路径不同时**才出现在该行的 `键` 单元格里。
- **`keyed` 是通向屏幕文字的唯一道路**：标题、警报、工具提示都是不属于任何 def 的 Keyed 译文，`search`/`get`/`where` 都够不到。两个方向都可以：键 → 显示的文字，任一语言的短语 → 键。只有 `生效` 行是游戏显示的内容；`文件` 行大多来自已安装但未启用的 mod。不给查询词的 `--empty-translation` 列出每一个没有译文的键；`""`、`*`、`.` 都不是通配符。
- **价格是算出来的，不是存下来的**：`economy` 是通向它们的唯一道路。市场价值可由 recipe 推出，成本是递归展开的 costList，所以这些数没有一个是 def 的字段，`get` 对它们什么也不返回。单元格为空，读作*游戏算不出这个值*，绝不要读作零：`计算状态`（JSON 键 `calcState`）区分各种原因；`costList 为空` 表示 costList 加总为零，该 thing 并非免费。`链末占比` 为 1，表示该行的 `profit` 是一个手写的数减去另外几个数。快照可能根本没有这一层：此时 `economy` 会写明是哪一种情形而不作答，并且**没有绕过的办法**：已索引的 `marketValue` 是 XML 的基础值，不是算出的价格，没有任何字段持有成本或利润。
- **`Abstract` 节点不是 def**：`get` 够不到它们，它会点名 `inherit`。`inherit` 从 XML 层回答四件事：谁继承自谁，哪些节点是 `Abstract` 节点，哪些层带有某个字段（`--path-contains`），以及有多少 patch 按 `Name=` 指向某个节点。这棵树是 PatchOperation 运行**之后**的 XML，与游戏解析出的一致（没有该 XML 的快照会在 `absent` 下列出 `post_patch_inheritance`）；patch 数统计的是 patch 文件里的 xpath。数见证者不等于找到字段在哪里声明：`inherit --help` 写明这个计数能解决什么、不能解决什么。
- **`list` 的 def 类型是存放分组，不是运行时的 `Class`。** 含多个 `Class` 的分组带 `Class` 列与 `--class`。多数分组只含一个 `Class`：此时 `--class` 起不到任何收窄作用，行为写在嵌套的 `Class="…"` 字段上，那是 **`where Class` 的范围，不是 `--class` 的**。
- **`where Class`** 到达那个嵌套的运行时类型，但只限于它**不同于声明类型**的情形：字段运行的恰是其 C# 所声明的类型时，根本不会以 `Class` 编入索引。`code-search "class <Name>\b"` 无论经哪条路到达都能找到这个类，包括没有任何 def 驱动、由代码直接 `new` 出来的类，那时构造处就是答案。
- **`values <字段>` 已经回答「哪些 def 类型有这个字段」**：它的 `def 类型` 一行给出这些类型，每个类型各写明有多少个 def 在该路径上有值、该类型共有多少个 def。`fields <DefType>` 则反过来，需要先给出类型。
- **null 字段与空列表从不进入索引**：连 `--defaults` 也不会出现。成因可判定时，`get --path-contains` 与 `fields --path-contains` 会输出一张 **`index_gap` 表**（所查 / 状态 / 下一步），而不是让你去问声明它的类。`get` 输出的状态是 `本 def 无值`（同类型的其他 def 带有该路径）、`XML 空元素`（该 def 的 XML 写出该元素但其中为空；`get` 在 `empty_in_xml` 表里列出这类元素，取值同为 `XML` / `XML=parent` / `parent` / `+patch`）或 `是值非路径`；`fields` 输出 `全类型无值`（该类型声明有它，没有 def 有值）、`类型未声明`（该类型没有叫这个名字的字段）或 `是值非路径`。`是值非路径` 表示这段文本是某个字段持有的值，`where --value` 读的就是那一列。下一步是一条可粘贴的命令。`全类型无值` / `类型未声明` 背后的已声明集合只收集到有限的嵌套深度，提示会说明读到多深。`fields` 没有找到任何值时同样会说明。

## 快照

一次导出 = 一个游戏版本、一份有序的 mod 名单、一种语言；多份可以并存。`snapshot list` 列出它们，`--snapshot <名称>` 按命令选择，`snapshot use <名称>` 持久固定；`snapshot status` 把它与已安装的游戏比对，点名哪些 mod 的 Defs/Patches XML、游戏加载的程序集或 Keyed/DefInjected 语言文件存在改动；`snapshot diff <old> <new>` 比较两份快照中解析后的 def 与字段。

制作一份快照、同一名字的各世代如何轮换、以及不匹配检查漏掉什么：[references/snapshots.md](references/snapshots.md)。

**完整的计数对快照完整，对已安装的游戏则不然**：在只含 Core 的快照上，`1 个 def` 指 Core 里有一个，而**没有任何一句话说明这一点**。游戏里有而快照里没有的 def，说明导出时该 mod 没有启用；`rimsearcher mods` 列出覆盖范围。

**文本搜索放到最后用**，两侧都是如此。对 def 数据，`where`/`values` 在解析后的数据上是精确的。对代码，`types`/`members`/`callers` 读元数据：它们匹配上的名字就是那个符号，而 `code-search` 会匹配不同类型下的同名之物，并全部计入。
