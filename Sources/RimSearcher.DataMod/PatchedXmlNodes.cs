#nullable disable

using System;
using System.Collections.Generic;
using System.Xml;

namespace RimSearcher.DataMod
{
    internal static class PatchedXmlNodes
    {
        internal sealed class Node
        {
            public string DefType;
            public string NodeKey;
            public bool KeyIsName;
            public bool IsAbstract;
            public List<string> Paths;
            public List<string> Texts;
            public List<string> NoInherit;
            public string SourceMod;
        }

        public static List<Node> Extract(XmlDocument doc, int maxDepth, int maxItems,
                                         HashSet<XmlNode> shadowed = null,
                                         Func<XmlNode, KeyValuePair<string, string>?> origin = null)
        {
            var order = new List<string>();
            var byKey = new Dictionary<string, Node>(StringComparer.Ordinal);

            var root = doc == null ? null : doc.DocumentElement;
            if (root == null) return new List<Node>();

            foreach (XmlNode child in root.ChildNodes)
            {
                var el = child as XmlElement;
                if (el == null || (shadowed != null && shadowed.Contains(el))) continue;

                var name = el.GetAttribute("Name");
                var defName = ChildText(el, "defName");
                var isAbstract = string.Equals(el.GetAttribute("Abstract"), "true",
                                               StringComparison.OrdinalIgnoreCase);
                string nodeKey;
                bool keyIsName;
                if (!NodeKey(name, defName, isAbstract, out nodeKey, out keyIsName)) continue;

                var key = Key(el.Name, nodeKey, keyIsName, isAbstract);
                if (byKey.ContainsKey(key)) continue;
                order.Add(key);
                var from = origin == null ? null : origin(el);

                List<string> texts, noInherit;
                var paths = XmlFieldPaths.Collect(el, maxDepth, maxItems, out texts, out noInherit);
                byKey[key] = new Node
                {
                    DefType = el.Name,
                    NodeKey = nodeKey,
                    KeyIsName = keyIsName,
                    IsAbstract = isAbstract,
                    Paths = paths,
                    Texts = texts,
                    NoInherit = noInherit,
                    SourceMod = from.HasValue ? from.Value.Key : null,
                };
            }

            var result = new List<Node>(order.Count);
            foreach (var key in order) result.Add(byKey[key]);
            return result;
        }

        public static HashSet<XmlNode> Shadowed(XmlDocument doc, Func<XmlNode, int?> rankOf)
        {
            var shadowed = new HashSet<XmlNode>();
            var root = doc == null ? null : doc.DocumentElement;
            if (root == null || rankOf == null) return shadowed;

            var winners = new Dictionary<string, KeyValuePair<XmlElement, int?>>(StringComparer.Ordinal);
            foreach (XmlNode child in root.ChildNodes)
            {
                var el = child as XmlElement;
                if (el == null) continue;
                if (string.Equals(el.GetAttribute("Abstract"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                var defName = ChildText(el, "defName");
                if (defName.Length == 0) continue;

                var key = Key(el.Name, defName, false, false);
                var rank = rankOf(el);
                KeyValuePair<XmlElement, int?> held;
                if (!winners.TryGetValue(key, out held))
                {
                    winners[key] = new KeyValuePair<XmlElement, int?>(el, rank);
                    continue;
                }
                var takesOver = !rank.HasValue || (held.Value.HasValue && rank.Value > held.Value.Value);
                if (takesOver)
                {
                    shadowed.Add(held.Key);
                    winners[key] = new KeyValuePair<XmlElement, int?>(el, rank);
                }
                else shadowed.Add(el);
            }
            return shadowed;
        }

        internal sealed class InheritNode
        {
            public string DefType;
            public string Name;
            public string ParentName;
            public bool IsAbstract;
            public string DefName;
            public string Label;
            public string SourceMod;
            public string SourceFile;
        }

        public static List<InheritNode> ExtractInheritance(XmlDocument doc,
                                                           Func<XmlNode, KeyValuePair<string, string>?> origin,
                                                           HashSet<XmlNode> shadowed = null)
        {
            var result = new List<InheritNode>();
            var root = doc == null ? null : doc.DocumentElement;
            if (root == null) return result;

            foreach (XmlNode child in root.ChildNodes)
            {
                var el = child as XmlElement;
                if (el == null) continue;

                var name = el.GetAttribute("Name");
                var parentName = el.GetAttribute("ParentName");
                var isAbstract = string.Equals(el.GetAttribute("Abstract"), "true",
                                               StringComparison.OrdinalIgnoreCase);
                if (name.Length == 0 && parentName.Length == 0 && !isAbstract) continue;
                var isShadowed = shadowed != null && shadowed.Contains(el);
                if (isShadowed && name.Length == 0) continue;

                var from = origin == null ? null : origin(el);
                result.Add(new InheritNode
                {
                    DefType = el.Name,
                    Name = name,
                    ParentName = parentName,
                    IsAbstract = isAbstract,
                    DefName = isShadowed ? "" : ChildText(el, "defName"),
                    Label = ChildText(el, "label"),
                    SourceMod = from.HasValue ? from.Value.Key : null,
                    SourceFile = from.HasValue ? from.Value.Value : null,
                });
            }
            return result;
        }

        public static string Key(InheritNode node)
        {
            if (node.DefName.Length > 0) return Key(node.DefType, node.DefName, false, node.IsAbstract);
            if (node.Name.Length > 0) return Key(node.DefType, node.Name, true, node.IsAbstract);
            return null;
        }

        public static List<bool> PatchedFlags(List<string> paths, HashSet<string> before)
        {
            var flags = new List<bool>(paths.Count);
            foreach (var p in paths) flags.Add(before == null || !before.Contains(p));
            return flags;
        }

        public static bool NodeKey(string name, string defName, bool isAbstract, out string nodeKey, out bool keyIsName)
        {
            if (defName.Length > 0 && !(isAbstract && name.Length > 0)) { nodeKey = defName; keyIsName = false; return true; }
            if (name.Length > 0) { nodeKey = name; keyIsName = true; return true; }
            nodeKey = null; keyIsName = false;
            return false;
        }

        public static string Key(string defType, string nodeKey, bool keyIsName, bool isAbstract)
            => defType + " " + nodeKey + (keyIsName ? " N" : " D") + (isAbstract ? "A" : "C");

        private static string ChildText(XmlElement el, string childName)
        {
            var child = el[childName];
            return child == null ? "" : child.InnerText;
        }
    }
}
