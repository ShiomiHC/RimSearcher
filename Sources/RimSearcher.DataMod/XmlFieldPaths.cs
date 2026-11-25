#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace RimSearcher.DataMod
{
    internal static class XmlFieldPaths
    {
        public static List<string> Collect(XmlElement def, int maxDepth, int maxItems)
        {
            List<string> texts;
            return Collect(def, maxDepth, maxItems, out texts);
        }

        public static List<string> Collect(XmlElement def, int maxDepth, int maxItems,
                                           out List<string> texts)
        {
            List<string> noInherit;
            return Collect(def, maxDepth, maxItems, out texts, out noInherit);
        }

        public static List<string> Collect(XmlElement def, int maxDepth, int maxItems,
                                           out List<string> texts, out List<string> noInherit)
        {
            var into = new List<string>();
            texts = new List<string>();
            noInherit = new List<string>();
            if (NoInherit(def)) noInherit.Add("");
            WalkChildren(def, "", 0, maxDepth, maxItems, into, texts, noInherit);
            return into;
        }

        private static bool NoInherit(XmlElement el)
        {
            var attr = el.Attributes["Inherit"];
            return attr != null && string.Equals(attr.Value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static void WalkChildren(XmlElement el, string prefix, int depth,
                                         int maxDepth, int maxItems,
                                         List<string> into, List<string> texts,
                                         List<string> noInherit)
        {
            var li = 0;
            foreach (XmlNode n in el.ChildNodes)
            {
                var child = n as XmlElement;
                if (child == null) continue;

                string path;
                if (child.LocalName == "li")
                {
                    if (li >= maxItems) continue;
                    path = prefix + "[" + li.ToString(CultureInfo.InvariantCulture) + "]";
                    li++;
                }
                else
                {
                    path = prefix.Length == 0 ? child.LocalName : prefix + "." + child.LocalName;
                }
                if (path.IndexOf('[') < 0 && NoInherit(child) && !noInherit.Contains(path))
                    noInherit.Add(path);
                WalkElement(child, path, depth, maxDepth, maxItems, into, texts, noInherit);
            }
        }

        private static void WalkElement(XmlElement el, string path, int depth,
                                        int maxDepth, int maxItems,
                                        List<string> into, List<string> texts,
                                        List<string> noInherit)
        {
            var classAttr = el.GetAttribute("Class");
            if (classAttr != null && classAttr.Length > 0)
                Add(into, texts, path + ".Class", classAttr);
            foreach (XmlAttribute a in el.Attributes)
                if (!NotData(a.Name)) Add(into, texts, path + ".@" + a.Name, a.Value);

            var hasChildEls = false;
            foreach (XmlNode n in el.ChildNodes)
            {
                if (n is XmlElement) { hasChildEls = true; break; }
            }

            if (!hasChildEls)
            {
                Add(into, texts, path, el.InnerText ?? "");
                return;
            }

            if (depth >= maxDepth) return;
            WalkChildren(el, path, depth + 1, maxDepth, maxItems, into, texts, noInherit);
        }

        private static bool NotData(string name)
            => name == "Class" || name == "Inherit" || name == "MayRequire" || name == "MayRequireAnyOf"
               || name == "TKey" || name == "IgnoreIfNoMatchingField"
               || name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal);

        private static void Add(List<string> into, List<string> texts, string path, string text)
        {
            into.Add(path);
            texts.Add(text ?? "");
        }
    }
}
