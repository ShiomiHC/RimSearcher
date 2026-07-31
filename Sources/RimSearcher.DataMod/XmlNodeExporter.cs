using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    /// xpath 点了它的名(<c>patch_ops</c>)。
    /// </summary>
    public static class XmlNodeExporter
    {
        private const string DefsFolder = "Defs/";
        private const string PatchesFolder = "Patches/";

        /// <summary>
        /// xpath 里点名一个具名节点的写法,如 <c>/Defs/ThingDef[@Name="BaseBullet"]</c>。
        /// 这是**文本**判据而不是语义判据:patch 到这一步还没跑,真实目标集合不存在。
        /// 取宁可多报的一侧 —— 漏报会让人以为这条没被动过。
        /// </summary>
        private static readonly Regex NameInXPath =
            new Regex("@Name\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.Compiled);

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

                        // 三样都没有 = 一条不参与继承的普通 def,它在 defs 表里已经完整存在
                        // (且带着 patch 与代码生成的结果)。
                        if (name.Length == 0 && parentName.Length == 0 && !isAbstract) continue;

                        int ops;
                        if (name.Length == 0 || !patchOps.TryGetValue(name, out ops)) ops = 0;

                        yield return new JsonLine()
                            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlNode)
                            .Str(IntermediateFormat.KeyDefType, el.Name)
                            .Str(IntermediateFormat.KeyName, name)
                            .Str(IntermediateFormat.KeyParentName, parentName)
                            .Bool(IntermediateFormat.KeyAbstract, isAbstract)
                            .Str(IntermediateFormat.KeyDefName, ChildText(el, "defName"))
                            .Str(IntermediateFormat.KeyLabel, ChildText(el, "label"))
                            .Str(IntermediateFormat.KeySourceMod, mod.PackageId)
                            .Str(IntermediateFormat.KeySourceFile, asset.name ?? "")
                            .Int(IntermediateFormat.KeyPatchOps, ops)
                            .ToString();
                    }
                }
            }
        }

        /// <summary>
        /// 每个具名节点被多少条 xpath 点了名。跨 mod 统计:补丁最常见的用法正是
        /// 一个 mod 改另一个 mod(或官方)的基节点。
        /// </summary>
        private static Dictionary<string, int> CountPatchTargets()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

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
                        foreach (Match m in NameInXPath.Matches(text))
                        {
                            var target = m.Groups[1].Value;
                            int n;
                            counts[target] = counts.TryGetValue(target, out n) ? n + 1 : 1;
                        }
                    }
                }
            }
            return counts;
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
