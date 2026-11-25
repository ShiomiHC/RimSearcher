# 代码侧

游戏的 C#。`rimsearcher sources sync` 为每个含程序集的 mod 写一棵源码树（Ludeon 的模块同属 `vanilla`），在 C# 之外还附一份它所出自的程序集副本，以及这些程序集中调用处的一张表。下面每个命令读的都是这同一次构建，所以 C#、IL 与调用图彼此不会矛盾。

源码树按 mod 而不是按快照建立，所以代码命令会把当前快照中游戏没有加载的部分排除在外：快照之外的 mod，或快照内某个 mod 中游戏未加载的程序集。`范围外有` 那一句点名被排除的部分在哪些源码树，没有命中的不提；落空时，它代替「没找到」，因为该名字在那里确实存在。`--include-unloaded` 把这些代码也读进来并加标记。在调用中点名它也一样：以源码树名开头的 `read` 路径、`--source <源码树>`、`code-search --scope` 里写出的 packageId、不带 `*` 的 `--assembly <名称>`，或以源码树名开头的 `code-search --in` / `--file-glob`。单独一个文件名、一个类型名或路径的末段不算点名，仍然被排除。

`rimsearcher sources list` 写明哪些源码树是 `匹配`，其他的为什么不是（`程序集已改动`、`引用的程序集已改动`、`反编译器不匹配`、`未建`、`无清单`、`空树`，或 `不在快照`），以及哪些带有程序集副本与调用表。用 `--source` 点名一棵从未建过的源码树是用法错误；`sources sync` 负责建它。

从 RimWorld 1.7 起，有四棵源码树不属于任何 mod，`sources sync` 由 `game_dir` 建出它们：`unitystubs`，游戏里 Unity 的替身，mod 的 `UnityEngine.*` 引用最终落在这里；`waylith`，游戏引擎（`Waylith*.dll` 及其下的 `Brutal.*.dll` 库）；`corelib`，游戏 .NET 运行时的核心库（`System.Private.CoreLib.dll`）；`gamelibs`，游戏文件夹里其余的全部库（压缩、音频、Steamworks 之类）。游戏自己的 `RimWorld.dll` 与它旁边的 `RimWorldWaylith.*` 程序集（`RimWorldWaylith.PrepatcherCompat`，它代替 mod 的 `0PrepatcherAPI.dll` 提供 `Prepatcher.*` 类型）属于 `vanilla`。`types` / `members` / `il` / `callers` / `usages` / `read` 在 `unitystubs` 中查找名字，与在任何别的源码树中一样；只有没有别的源码树含该名字时，才在 `waylith` 中查找，再没有才在 `corelib` 与 `gamelibs` 中查找：引擎有它自己的 `Color`、`Material`、`Mesh` 等，所以 `types Color` 答的是 Unity 的。要查引擎的，写全名（`Waylith.Math.Color`）或用 `--source waylith`。`code-search` 把这四棵都排除在外，除非 `--source`、`--scope` 或 `--assembly` 点名其中一棵，或传给 `--in` 或 `--file-glob` 的路径指进该源码树。没有点名源码树又一无所获的搜索，会写明其中哪几棵有匹配、各有多少处。`callers` 与 `usages` 不搜索 `corelib` 与 `gamelibs` 中的调用处与使用处，除非 `--source` 或 `--assembly` 点名它；所问的成员就在其中时，输出会写明没有搜索哪一棵。

一棵源码树可以含有多个程序集（`vanilla` 含有 `Assembly-CSharp`、`RimWorld` 与 `RimWorldWaylith.PrepatcherCompat`）。`--assembly <名称>` 把任何代码命令收窄到所点名的程序集，名字取 `types` 在程序集一列中输出的那个。`*` 为通配符。多个就重复。点名一个程序集即点名其源码树，所以 `--assembly 'Waylith*'` 不需要 `--source` 就读 `waylith`。`callers` 与 `usages` 保留由该程序集发出的调用处（`callers --callees`：调入该程序集的调用）。要问 RimWorld 中谁调用某个引擎方法，`--source vanilla` 即可：`callers UnityEngine.Mathf.Clamp01 --source vanilla`。`--callees` 读该方法所在源码树的调用表，给了 `--source` 则只读点名的，无论 `--assembly` 写什么。`read` 要求这些程序集位于同一棵源码树中。

回答 def 数据的问题，这些一概用不到。

## 哪个命令回答什么

| 问题 | 命令 |
|---|---|
| 这个类型在哪里，它是什么 | `rimsearcher types <Name>` |
| 什么派生自它 / 什么实现这个接口 | `rimsearcher types <Name> --derived`（整棵子树加 `--transitive`） |
| 它派生自什么 | `rimsearcher types <Name> --bases` |
| 哪些派生类型重写这个成员 | `rimsearcher types <Base> --derived --transitive --declares <Member>` |
| 这个类型里有什么 | `rimsearcher members <Type>`：`--kind`、`--static`、`--virtual`、`--abstract`、`--overrides`、`--access` |
| 一个继承来的成员出自哪里 | `rimsearcher members <Type> --inherited`：各行写明由哪个类型声明 |
| 一个或几个成员的方法体 | `rimsearcher read <File>.cs --member <name> --member <other>`：各块按给出的顺序输出 |
| 分散在几个文件中的成员 | `rimsearcher read A.cs B.cs --member 'X\|Y'`：每个名字都在每个文件里查找：在几个文件里都有声明的，每个文件各输出一次，没有它的文件会得到一行，写明哪个文件有它 |
| 一个方法的指令 | `rimsearcher il <Type>.<Member>`：迭代器或 async 方法加 `--state-machine` |
| 谁调用这个方法 | `rimsearcher callers <Type>.<Member>` |
| 谁读或写这个字段或属性 | `rimsearcher usages <Type>.<field>`：`--writes` 只查看存储、`ref` 传递与 setter 调用 |
| 谁用到这个类型 | `rimsearcher usages <Type>`：`is` / 强制转换 / `typeof` / 泛型实参，以及对它所声明的每个成员的全部使用 |
| 这个方法调用什么 | `rimsearcher callers <Type>.<Member> --callees` |
| 所有文件中的某种代码*形状* | `rimsearcher code-search <regex>` |

`types` / `members` / `il` / `callers` / `usages` 读程序集的元数据，所以是精确的：`members --virtual` 是元数据位，不是对文本里 `virtual` 一词的猜测。

`read` 与 `code-search` 读的则是反编译出的文本，像 `public\s+(?:virtual\s+)?void\s+Notify_\w+\(` 这样横跨所有文件的形状，只有它们答得出来，别的都不行。

## 陷阱

- **`code-search` 区分大小写，除非传 `-i`**：`orbitalDebris` 与 `OrbitalDebris` 是两次搜索，错的那次得到的零看起来像不存在。
- **含 `/` 的 `--file-glob` 按整条路径匹配**，整条路径是 `<packageId>/<命名空间目录>/<文件>.cs`：即使带有 `--source`，它也以源码树名开头。一个文件都匹配不到时按路径尾段匹配，`RimWorld/*.cs` 读作 `**/RimWorld/*.cs`，输出会写明。`*` 止于 `/`：`vanilla/Verse/*.cs` 不含 `Verse.AI`，`vanilla/Verse/**` 才含。不含 `/` 的，按任意深度的文件名匹配。`{a,b}` 匹配其中任一写法，各写法里仍可用 `*`（`**/{Verb_*,Projectile}.cs`）。要搜索一个文件或目录，`--in` 更简单：它接受路径的任意末段（`--in Verse/HediffSet`、`--in Verse/AI`）。
- **`--member`/`--type`/`--outline` 未命中的名字不能证明不存在**：用读元数据的 `members` 复核。
- **编译器生成的类型在源码树里没有文件。** 迭代器状态机与闭包持有者被反编译器还原成 `yield` 与 lambda，所以 `read` 够不到它们，只有 `il` 能进去。
- **对重写方法的 `callers` 通常为零，这并不表示不存在。** `callvirt` 记录的是调用处所写的那个方法，所以针对基类型写的调用算在基类型名下。零结果会说明这一点，并点出该改问的基类型。

## `code-search` 怎么计数

- **`code-search` 搜索反编译出的 C#，从不搜索 Def。** 它把命中数与文件数报告为两个数：问「有多少个方法」要的是前者。在缩短答案的三个开关中，`--limit` 与 `--max-per-file` 只决定输出多少（计数保持精确）；**只有 `--max-files` 缩短扫描**，使计数变成 `至少 N`。这三个都没有缺省值：每个 glob 选中的文件都会读取，每个命中都会输出，直到你给其中一个传一个正数，并且它们都不接受 `all`。源码原有的行内注释不在反编译出的文本里，多数局部变量名也不是原名（参数与成员保留）；找不到的成员通常是继承来的：`members <Type> --inherited` 会说明它声明在哪里。源码树以 packageId 命名（`vanilla` = 游戏）；`sources list` 是名册。
