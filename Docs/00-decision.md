# 00 · 重建决定(2026-07-29)

## 决定

重建的指导核心是**三方向逐点择优**:每个设计点上把三个来源摆在一起按点裁决 ——
本地 2.x(master 世系)的做法、上游 3.x(kearril/RimSearcher)的做法、全新设计。
**不设默认基座,不整体站队**;01(本地资产 = 本地在该点胜出的证据)与
02(上游缺陷 = 上游在该点落败的证据)是择优的两侧证据库。

已按此裁决的唯一骨架级点:**数据获取采用运行时导出**(游戏内 DataMod 导出运行时 Def →
SQLite → CLI 查询 → skill 文档引导,即上游 3.x 的管线形状)。裁决依据是下节四条论据,
它在这一点上淘汰了本地 MCP server 形态与静态索引取数层 —— 但只淘汰这一点;
管线之内每个环节的具体做法(导出器、schema、CLI 文法、呈现、闸)仍逐点择优,
复用上游代码与全新实现之间没有预设答案。**沉没成本明确不作为考量**(用户拍板原话:
「如果说代价仅是沉没成本的话完全无所谓」)。

## 世系

- 上游 3.x:`upstream/master` @ `8a0a4f7`(kearril/RimSearcher)
- 本地旧世系:`master`(ShiomiHC/RimSearcher fork;本轮分析时点 `f2afe08`,与上游 merge-base `2ae4c35`,分叉 130/24)
- 两边已不可 merge:上游 `718a6c4` 删除了 Core/Server —— 即本地 130 个提交所改的全部代码
- 本分支(`rebuild`)为 orphan 世系。**写这一行时它只放迁移文档,现在整套重建的代码与
  skill 都在这里**(2026-07-30 起),Docs/ 是它的设计产地而不是它的全部内容;两边旧代码随取:
  `git show master:<path>` / `git show upstream/master:<path>`(同仓库共享 object store)

## 决定性论据(为何对象导出而非静态 XML 方案)

1. **ImpliedDefs**:`RimWorld.DefGenerator.GenerateImpliedDefs_PreResolve/PostResolve`
   在反序列化之后凭空生成数千 def(Corpse_X / Meat_X / Blueprint_X / Frame_X / Make_X 配方 /
   石材地毯地形 / Gene / Thought / 键位…),`generated=true`,来源标记是字符串 `"ImpliedDefs"`
   而非文件。XML 里一行都没有,任何静态方案结构性拿不到。证据见 03。
2. **PatchOperation 免费解决**:运行时导出时全部 patch(含 mod 自定义子类)已应用。
3. **启用歧义消失**:本地方案每次返回挂着的整段免责声明
   (conditional folders / shadowed files / decided at startup not in-game)不再需要。
4. **引用已解析**:反查(哪些 def 用了 `RimWorld.CompShield`)从文本匹配变为精确。

## 已知代价(接受,但必须在输出里声明)

- 数据 = 导出那一刻的快照;游戏/mod 更新后**静默过期** → 必须做过期自证(02-4、04)
- 用户需进一次游戏导出(体验成本;上游 `GUIDED_SETUP.md` 有现成说法可参考)
- 「谁 patch 过这个 def」在运行时对象上拿不到 → 溯源能力丢失;补救选项见 03 的 ApplyPatches 拦截点

## 途中否决的方案(勿再绕回)

- **自建 PatchOperation 应用器**(重写 15 个 vanilla 子类,约 400~600 行):可行,XPath 是 BCL
  `SelectNodes` 无私有方言;但被 mod 方案整体替代。调查结论保留在 03,以备溯源功能用。
- **引用 Assembly-CSharp 直接执行 PatchOperation**:不可行(Verse 静态初始化链 + Unity 依赖,
  `XmlContainer` 反序列化要走 DirectXmlToObject 全链)。
- **反射加载 mod DLL 执行自定义 operation**:同上不可行;但已装 mod 的 3 个自定义子类行为
  静态可判定(见 03),不需要执行。
- **整体保守(留在 MCP + 静态索引,只补 PatchOperation)**:前几轮的默认立场,被用户明确反转。
