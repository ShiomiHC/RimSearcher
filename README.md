# RimSearcher

回答关于 RimWorld 的 def 与 C# 的问题：一个 def 在补丁与继承之后到底是什么、
哪些 def 用了某个类或某个值、一个字段能取哪些值、一个符号在游戏代码的哪里。

## 组成

| | |
|---|---|
| `Sources/RimSearcher.Cli` | `rimsearcher` 命令本体（net10.0，单文件发布） |
| `Sources/RimSearcher.Core` | 命令、快照库、反编译树检索 |
| `Sources/RimSearcher.DataMod` | 游戏内导出器 |
| `skills/rimsearcher` | skill：什么问题走哪条命令，以及输出怎么读 |

## 装机

```bash
dotnet publish Sources/RimSearcher.Cli/RimSearcher.Cli.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o <安装目录>
```

把安装目录放进 PATH，然后：

```bash
rimsearcher --help
```

配置与数据同住 `~/.rimsearcher/`：`config.toml`（游戏路径、导出器位置、scope 分组）、
`snapshots/*.db`（文件名即别名，自动发现）。`rimsearcher snapshot status` 当前用的是哪一份、以及和现在游戏的差。

## 一次完整的取数

```bash
rimsearcher export             # 无人值守跑一遍游戏，导出并导入
rimsearcher sources sync       # 反编译游戏实际加载的程序集，供 code-search / read
```

`datamod status / attach / detach` 为手工检视。

## License

MIT，见 [LICENSE](LICENSE)。
