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
    /// 这一层是**打补丁之前**的 XML。每个 Name= 节点随行带出有多少条 PatchOperation 的
    /// xpath 点了它的名(<c>patch_ops</c>),以及按 defName / label 定位的两条计数。
    /// 全部 def 节点(含不参与继承的普通 def)另发 xml_written,收录实际写出来的字段路径
    /// 与叶子行内文本。
    /// </summary>
    public static class XmlNodeExporter
    {
        private const string DefsFolder = "Defs/";
        private const string PatchesFolder = "Patches/";

        public static IEnumerable<string> BuildLines()
        {
            var patchOps = CountPatchTargets();

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
                            yield return new JsonLine()
                                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlWritten)
                                .Str(IntermediateFormat.KeyDefType, el.Name)
                                .Str(IntermediateFormat.KeyNodeKey, nodeKey)
                                .Bool(IntermediateFormat.KeyKeyIsName, keyIsName)
                                .Strs(IntermediateFormat.KeyPaths, written)
                                .Strs(IntermediateFormat.KeyTexts, texts)
                                .ToString();
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
