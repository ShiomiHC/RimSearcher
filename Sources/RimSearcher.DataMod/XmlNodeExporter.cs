using System;
using System.Collections.Generic;
using System.Xml;
using RimSearcher.Contract;
using Verse;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 继承层导出器 —— 快照里唯一一处**不是**「游戏内存里的对象」的数据。
    ///
    /// 为什么非得单独收一遍:游戏在 <c>LoadedModManager.LoadAllActiveMods</c> 末尾调
    /// <c>XmlInheritance.Clear()</c>,而导出跑在 <c>StaticConstructorOnStartup</c>,那时
    /// 「谁继承谁」已经应用完并丢弃 —— def 对象上一点痕迹都没有。抽象父节点更是从头到尾
    /// 没有对应的 Def 实例。于是这一层只能从 XML 原文再读一次。
    ///
    /// 不必自己写 XML 读取器:<c>DirectXmlLoader.XmlAssetsInModFolder</c> 任何时候都能调,
    /// 它走 <c>mod.foldersToLoadDescendingOrder</c> —— 游戏自己解析完的 loadFolders.xml、
    /// 版本目录、同名文件优先级去重。自己扫目录会读到游戏根本没加载的文件。
    ///
    /// <c>xml_nodes</c>(继承层)是**打补丁之前**的 XML:每个 Name= 节点随行带出有多少条
    /// PatchOperation 的 xpath 点了它的名(<c>patch_ops</c>),以及按 defName / label 定位的
    /// 两条计数。
    ///
    /// <c>xml_written</c>(全部 def 节点,含不参与继承的普通 def)不同:0.7.0 起它的路径全集
    /// 取自 <see cref="PatchedXml"/> —— 打完补丁的那份合并文档,每条路径另带一个「这一行是
    /// 补丁加的」标记。拿不到那份文档时才退回这里逐 mod 读到的原文,并在元数据里说明。
    /// </summary>
    public static class XmlNodeExporter
    {
        private const string DefsFolder = "Defs/";
        private const string PatchesFolder = "Patches/";

        public static IEnumerable<string> BuildLines()
        {
            var patchOps = CountPatchTargets();
            // 打完补丁的那份文档拿不到时是 null,这一路原样走 0.6.0 的老形状。
            var post = PatchedXml.Nodes;
            // 原文写过哪些路径 —— 拿到 post 时它只用来算「这一行是不是补丁加的」那个标记。
            var pre = post == null ? null : new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod == null) continue;
                foreach (var asset in AssetsIn(mod, DefsFolder))
                {
                    var root = asset.xmlDoc == null ? null : asset.xmlDoc.DocumentElement;
                    if (root == null) continue;

                    foreach (XmlNode child in root.ChildNodes)
                    {
                        var el = child as XmlElement;
                        if (el == null) continue;

                        var name = el.GetAttribute("Name");
                        var parentName = el.GetAttribute("ParentName");
                        var isAbstract = string.Equals(el.GetAttribute("Abstract"), "true",
                                                       StringComparison.OrdinalIgnoreCase);
                        var defName = ChildText(el, "defName");
                        var label = ChildText(el, "label");

                        // xml_written 覆盖全部 def 节点,包括不参与继承的普通 def ——
                        // Replace/Add 问的就是「这个节点的 XML 写没写这一行」。
                        // xml_nodes 的收录口径不变:三样都没有的普通 def 仍不进那张表。
                        string nodeKey = null;
                        var keyIsName = false;
                        if (defName.Length > 0) { nodeKey = defName; keyIsName = false; }
                        else if (name.Length > 0) { nodeKey = name; keyIsName = true; }
                        if (nodeKey != null)
                        {
                            List<string> texts;
                            var written = XmlFieldPaths.Collect(el, DefExporter.Limits.MaxFieldDepth,
                                                                DefExporter.Limits.MaxCollectionItems,
                                                                out texts);
                            if (pre == null)
                            {
                                yield return new JsonLine()
                                    .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlWritten)
                                    .Str(IntermediateFormat.KeyDefType, el.Name)
                                    .Str(IntermediateFormat.KeyNodeKey, nodeKey)
                                    .Bool(IntermediateFormat.KeyKeyIsName, keyIsName)
                                    .Strs(IntermediateFormat.KeyPaths, written)
                                    .Strs(IntermediateFormat.KeyTexts, texts)
                                    .ToString();
                            }
                            else
                            {
                                // 这里要并集而不是后者胜:问的是「磁盘上有没有哪份原文写过
                                // 这一行」,任何一份写过,它就不是补丁加的。
                                HashSet<string> before;
                                var key = PatchedXmlNodes.Key(el.Name, nodeKey, keyIsName, isAbstract);
                                if (!pre.TryGetValue(key, out before))
                                    pre[key] = before = new HashSet<string>(StringComparer.Ordinal);
                                foreach (var p in written) before.Add(p);
                            }
                        }

                        // 三样都没有 = 一条不参与继承的普通 def,它在 defs 表里已经完整存在
                        // (且带着 patch 与代码生成的结果)。
                        if (name.Length == 0 && parentName.Length == 0 && !isAbstract) continue;

                        int ops;
                        if (name.Length == 0 || !patchOps.ByName.TryGetValue(name, out ops)) ops = 0;
                        int opsDef;
                        if (defName.Length == 0 || !patchOps.ByDefName.TryGetValue(defName, out opsDef)) opsDef = 0;
                        int opsLabel;
                        if (label.Length == 0 || !patchOps.ByLabel.TryGetValue(label, out opsLabel)) opsLabel = 0;

                        yield return new JsonLine()
                            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlNode)
                            .Str(IntermediateFormat.KeyDefType, el.Name)
                            .Str(IntermediateFormat.KeyName, name)
                            .Str(IntermediateFormat.KeyParentName, parentName)
                            .Bool(IntermediateFormat.KeyAbstract, isAbstract)
                            .Str(IntermediateFormat.KeyDefName, defName)
                            .Str(IntermediateFormat.KeyLabel, label)
                            .Str(IntermediateFormat.KeySourceMod, mod.PackageId)
                            .Str(IntermediateFormat.KeySourceFile, asset.name ?? "")
                            .Int(IntermediateFormat.KeyPatchOps, ops)
                            .Int(IntermediateFormat.KeyPatchOpsDefName, opsDef)
                            .Int(IntermediateFormat.KeyPatchOpsLabel, opsLabel)
                            .ToString();
                    }
                }
            }

            // 打完补丁的那份文档在手时,xml_written 的路径全集出自它 —— 上面那一路只用来
            // 攒 pre。放在循环之后是因为 pre 得先攒齐:一个 def 的原文可能分散在多个 mod 里。
            if (post == null) yield break;
            foreach (var node in post)
            {
                HashSet<string> before;
                pre.TryGetValue(PatchedXmlNodes.Key(node.DefType, node.NodeKey, node.KeyIsName, node.IsAbstract),
                                out before);
                var patched = PatchedXmlNodes.PatchedFlags(node.Paths, before);

                yield return new JsonLine()
                    .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlWritten)
                    .Str(IntermediateFormat.KeyDefType, node.DefType)
                    .Str(IntermediateFormat.KeyNodeKey, node.NodeKey)
                    .Bool(IntermediateFormat.KeyKeyIsName, node.KeyIsName)
                    .Strs(IntermediateFormat.KeyPaths, node.Paths)
                    .Strs(IntermediateFormat.KeyTexts, node.Texts)
                    .Bools(IntermediateFormat.KeyPatched, patched)
                    .ToString();
            }
        }


        /// <summary>
        /// 每个被 xpath 点到的名字,按三种文本定位分开计。跨 mod 统计:补丁最常见的用法正是
        /// 一个 mod 改另一个 mod(或官方)的基节点。
        /// </summary>
        private static PatchTargetCounts CountPatchTargets()
        {
            var counts = new PatchTargetCounts();

            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod == null) continue;
                foreach (var asset in AssetsIn(mod, PatchesFolder))
                {
                    var root = asset.xmlDoc == null ? null : asset.xmlDoc.DocumentElement;
                    if (root == null) continue;

                    // 嵌套的 PatchOperationSequence / Conditional 里也有 xpath,所以取整棵子树的
                    // 全部 xpath 元素,而不是只看顶层 Operation。
                    foreach (XmlNode node in root.SelectNodes(".//xpath"))
                    {
                        var text = node.InnerText;
                        if (string.IsNullOrEmpty(text)) continue;
                        PatchXPath.AddMatches(text, counts.ByName, counts.ByDefName, counts.ByLabel);
                    }
                }
            }
            return counts;
        }

        private sealed class PatchTargetCounts
        {
            public readonly Dictionary<string, int> ByName = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> ByDefName = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> ByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 一个 mod 的文件读不了不该毁掉整次导出 —— 空结果在输出上跟
        /// 「这个环境里确实没有继承关系」分不开。
        /// </summary>
        private static LoadableXmlAsset[] AssetsIn(ModContentPack mod, string folder)
        {
            try { return DirectXmlLoader.XmlAssetsInModFolder(mod, folder); }
            catch (Exception ex)
            {
                Log.Warning("[RimSearcher] could not read " + folder + " of " + mod.PackageId + ": " + ex.Message);
                return new LoadableXmlAsset[0];
            }
        }

        private static string ChildText(XmlElement el, string childName)
        {
            var child = el[childName];
            return child == null ? "" : child.InnerText;
        }
    }
}
