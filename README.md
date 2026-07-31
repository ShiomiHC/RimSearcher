# RimSearcher

回答关于 RimWorld 的 def 与 C# 的问题：一个 def 在补丁与继承之后到底是什么、
哪些 def 用了某个类或某个值、一个字段能取哪些值、一个符号在游戏代码的哪里。

数据来自**游戏自己加载完的那一份**——导出器随游戏跑一遍，把运行时的 def 树写成快照，
之后所有查询都在快照上进行，不再猜 XML 合并的结果。

```bash
rimsearcher --help
```

## 形态换代（2026-07-31）

本仓已从 **MCP server** 换代为 **CLI + skill**。旧世系停在 tag `V2_A`
（`git checkout V2_A` 取得回来），当前 master 是重建后的 CLI 世系。

设计与调查的产地在 [`Docs/`](Docs/)：`00-decision` → `01-assets` →
`06-architecture` → `04-workflow` 是新会话的阅读顺序。

> 本 README 是换代时的最小占位，命令面与装机流程待补。
