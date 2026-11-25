using System;
using System.Collections.Generic;
using System.Xml;
using RimSearcher.Contract;
using Verse;

namespace RimSearcher.DataMod
{
    public static class XmlNodeExporter
    {
        private const string DefsFolder = "Defs/";
        private const string PatchesFolder = "Patches/";

        public static IEnumerable<string> BuildLines()
        {
            var patchOps = CountPatchTargets();
            var post = PatchedXml.Nodes;
            var pre = post == null ? null : new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var preByMod = post == null ? null : new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var inheritance = PatchedXml.Inheritance;
            var preOrigin = inheritance == null ? null
                : new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);

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

                        string nodeKey;
                        bool keyIsName;
                        if (PatchedXmlNodes.NodeKey(name, defName, isAbstract, out nodeKey, out keyIsName))
                        {
                            List<string> texts, noInherit;
                            var written = XmlFieldPaths.Collect(el, DefExporter.Limits.MaxInstanceFieldDepth,
                                                                DefExporter.Limits.MaxCollectionItems,
                                                                out texts, out noInherit);
                            if (pre == null)
                            {
                                yield return XmlWrittenLine(el.Name, nodeKey, keyIsName, written, texts, null,
                                                            noInherit);
                            }
                            else
                            {
                                HashSet<string> before;
                                var key = PatchedXmlNodes.Key(el.Name, nodeKey, keyIsName, isAbstract);
                                if (!pre.TryGetValue(key, out before))
                                    pre[key] = before = new HashSet<string>(StringComparer.Ordinal);
                                foreach (var p in written) before.Add(p);
                                var modKey = key + " " + mod.PackageId;
                                if (!preByMod.TryGetValue(modKey, out before))
                                    preByMod[modKey] = before = new HashSet<string>(StringComparer.Ordinal);
                                foreach (var p in written) before.Add(p);
                            }
                        }

                        if (name.Length == 0 && parentName.Length == 0 && !isAbstract) continue;

                        if (preOrigin != null)
                        {
                            if (nodeKey != null)
                            {
                                var key = PatchedXmlNodes.Key(el.Name, nodeKey, keyIsName, isAbstract);
                                if (!preOrigin.ContainsKey(key))
                                    preOrigin[key] = new KeyValuePair<string, string>(mod.PackageId, asset.name ?? "");
                            }
                            continue;
                        }

                        yield return XmlNodeLine(el.Name, name, parentName, isAbstract, defName, label,
                                                 mod.PackageId, asset.name ?? "", patchOps);
                    }
                }
            }

            if (inheritance != null)
            {
                foreach (var node in inheritance)
                {
                    var mod = node.SourceMod;
                    var file = node.SourceFile;
                    if (mod == null)
                    {
                        var key = PatchedXmlNodes.Key(node);
                        KeyValuePair<string, string> was;
                        if (key != null && preOrigin.TryGetValue(key, out was)) { mod = was.Key; file = was.Value; }
                        else { mod = ""; file = ""; }
                    }
                    yield return XmlNodeLine(node.DefType, node.Name, node.ParentName, node.IsAbstract,
                                             node.DefName, node.Label, mod, file, patchOps);
                }
            }

            if (post == null) yield break;
            foreach (var node in post)
            {
                HashSet<string> before;
                var key = PatchedXmlNodes.Key(node.DefType, node.NodeKey, node.KeyIsName, node.IsAbstract);
                if (node.SourceMod == null || !preByMod.TryGetValue(key + " " + node.SourceMod, out before))
                    pre.TryGetValue(key, out before);
                var patched = PatchedXmlNodes.PatchedFlags(node.Paths, before);

                yield return XmlWrittenLine(node.DefType, node.NodeKey, node.KeyIsName, node.Paths, node.Texts,
                                            patched, node.NoInherit);
            }
        }

        private static string XmlWrittenLine(string defType, string nodeKey, bool keyIsName, List<string> paths,
                                             List<string> texts, List<bool> patched, List<string> noInherit)
        {
            var line = new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlWritten)
                .Str(IntermediateFormat.KeyDefType, defType)
                .Str(IntermediateFormat.KeyNodeKey, nodeKey)
                .Bool(IntermediateFormat.KeyKeyIsName, keyIsName)
                .Strs(IntermediateFormat.KeyPaths, paths)
                .Strs(IntermediateFormat.KeyTexts, texts);
            if (patched != null) line.Bools(IntermediateFormat.KeyPatched, patched);
            if (noInherit.Count > 0) line.Strs(IntermediateFormat.KeyNoInherit, noInherit);
            return line.ToString();
        }

        private static string XmlNodeLine(string defType, string name, string parentName, bool isAbstract,
                                          string defName, string label, string sourceMod, string sourceFile,
                                          PatchTargetCounts patchOps)
        {
            int ops;
            if (name.Length == 0 || !patchOps.ByName.TryGetValue(name, out ops)) ops = 0;
            int opsDef;
            if (defName.Length == 0 || !patchOps.ByDefName.TryGetValue(defName, out opsDef)) opsDef = 0;
            int opsLabel;
            if (label.Length == 0 || !patchOps.ByLabel.TryGetValue(label, out opsLabel)) opsLabel = 0;

            return new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlNode)
                .Str(IntermediateFormat.KeyDefType, defType)
                .Str(IntermediateFormat.KeyName, name)
                .Str(IntermediateFormat.KeyParentName, parentName)
                .Bool(IntermediateFormat.KeyAbstract, isAbstract)
                .Str(IntermediateFormat.KeyDefName, defName)
                .Str(IntermediateFormat.KeyLabel, label)
                .Str(IntermediateFormat.KeySourceMod, sourceMod)
                .Str(IntermediateFormat.KeySourceFile, sourceFile)
                .Int(IntermediateFormat.KeyPatchOps, ops)
                .Int(IntermediateFormat.KeyPatchOpsDefName, opsDef)
                .Int(IntermediateFormat.KeyPatchOpsLabel, opsLabel)
                .ToString();
        }

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
