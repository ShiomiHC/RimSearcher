# RimSearcher

给 RimWorld mod 开发用的查询命令行，主要供 LLM 编码助手通过 skill 调用，也可以直接在终端里使用。

它查的是游戏**实际加载**之后的状态，而不是磁盘上的 XML：补丁全部应用，继承全部展开，
游戏运行时用代码生成的 def（尸体、肉、蓝图、配方等）也都在内。

## 能回答什么

- **一个 def 最终的内容**：应用补丁、展开继承之后的全部字段，以及它的译文。每个值都标有来源：
  这个 def 自己的 XML、父节点、别的 mod 的补丁、代码里的默认值，还是加载时由代码改写。
- **继承关系**：一个 XML 节点继承自谁、被谁继承，抽象父节点也算在内。
- **反查**：哪些 def 用到某个 C# 类，或者某个字段取某个值。
- **一个字段能取哪些值**：一个 def 类型实际用到哪些字段，每个字段出现过哪些值。
- **经济数据**：游戏算出来的物品价值、制作成本和工时。
- **界面文本**：一段界面文字对应哪个译文键，反过来也能查。
- **C# 侧**：在反编译出来的游戏与 mod 代码里查类型、成员、调用方和引用点，搜正则，读源码和 IL。
- **快照对比**：同一份 mod 名单在游戏或 mod 更新前后的两份快照之间，def 与字段的差异。

## 怎么得到这些数据

CLI 以指定的 mod 名单无人值守地启动游戏，游戏内的导出器把加载结果写成文件，CLI 再把它导入成一份 SQLite 快照。
快照可以有多份，每份对应一份 mod 名单。C# 侧由 CLI 反编译游戏实际加载的程序集，写成源码树。

面向 RimWorld 1.7。导出与反编译需要装好的游戏，只支持 Windows；Linux 上可以查询别处建好的快照与源码树。

## 构建

需要 Windows、Steam 版 RimWorld 1.7、创意工坊的 Harmony、.NET SDK 10.0.404 或更新的 10.x、PowerShell 7，
以及 Native AOT 所用的 MSVC 工具链（Visual Studio 的「使用 C++ 的桌面开发」）。

[RimTooling](https://github.com/ShiomiHC/RimTooling) 是 git 子模块。游戏目录按 Steam 安装信息找，不在 Steam 库里的设环境变量 `RimWorldDir`。

```powershell
git clone --recursive https://github.com/ShiomiHC/RimSearcher.git
cd RimSearcher
pwsh build.ps1
```

克隆时没有带 `--recursive` 的，先执行 `git submodule update --init`。GitHub 的「Download ZIP」不含子模块。
没有 MSVC 工具链的用 `pwsh build.ps1 -Jit`，产物需要装机的 .NET 10 运行时，启动较慢。

## 安装与首次使用

1. 把 `dist/` 的内容放到一个目录，并把该目录加进 `PATH`。导出器 `RimSearcher.DataMod.dll` 须与 `rimsearcher.exe` 同目录。
2. 写 `%USERPROFILE%\.rimsearcher\config.toml`，路径用单引号：

   ```toml
   game_dir = 'D:\SteamLibrary\steamapps\common\RimWorld'
   decompiled_dir = 'D:\rimsearcher\decompiled'
   mod_roots = [
     'D:\SteamLibrary\steamapps\common\RimWorld\Data',
     'D:\SteamLibrary\steamapps\common\RimWorld\Mods',
     'D:\SteamLibrary\steamapps\workshop\content\294100',
   ]
   ```

3. 把游戏当前启用的 mod 存成名单，导出一份快照，再反编译程序集：

   ```powershell
   rimsearcher modlist save mylist
   rimsearcher export --modlist mylist
   rimsearcher sources sync
   ```

4. 用支持 skill 的助手时，把 `skills/rimsearcher` 复制到它的 skill 目录（Claude Code 是 `~/.claude/skills/rimsearcher`）。

命令的用法见 `rimsearcher --help` 与 [skills/rimsearcher](skills/rimsearcher/SKILL.md)。

## 目录

| 路径 | 内容 |
| --- | --- |
| `Sources/RimSearcher.Cli/` | `rimsearcher` 命令的入口 |
| `Sources/RimSearcher.Core/` | 命令、快照库、反编译树检索 |
| `Sources/RimSearcher.DataMod/` | 游戏内导出器 |
| `RimTooling/` | 子模块：命令行框架、平台与 RimWorld 基础库、mod 工程的构建口径 |
| `skills/rimsearcher/` | 给 LLM 编码助手的 skill：哪个问题用哪条命令，输出怎么读 |

## 来源

fork 自 [kearril/RimSearcher](https://github.com/kearril/RimSearcher)，之后整体重写，与上游不共享代码路线。

## 许可

[MIT](LICENSE)
