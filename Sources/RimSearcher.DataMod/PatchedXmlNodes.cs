// DataMod 整个工程是 Nullable=disable(net472,游戏进程内)。这个文件另被编进测试工程,
// 而那边开着 —— 不钉住的话同一份源码两边语义不同,测试工程先炸。
#nullable disable

using System;
using System.Collections.Generic;
using System.Xml;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 从一份合并 XML 里抽 def 节点,以及算「这一行是补丁加的」那个标记。
    ///
    /// 单独一个文件是为了**能测** —— 拿那份文档的两条路线(Harmony / 重放)都要启动游戏
    /// 才走得到,而这里的两条规则(合并文档里后者胜、标记怎么算)恰恰是最容易错的部分。
    /// 所以这个文件跟 <see cref="XmlFieldPaths"/> 一样:<b>不许引用任何 RimWorld 类型</b>,
    /// 于是测试工程能把它整个编进来。
    /// </summary>
    internal static class PatchedXmlNodes
    {
        /// <summary>一个 def 节点在打完补丁的文档里写出来的东西。</summary>
        internal sealed class Node
        {
            public string DefType;
            public string NodeKey;
            public bool KeyIsName;
            public List<string> Paths;
            public List<string> Texts;
        }

        /// <summary>
        /// 合并文档里同一个 defName 可以有多份(谁覆盖谁是后面 ParseAndProcessXML 的事),
        /// 而文档是按加载顺序拼的 —— 所以这里**后者胜**,位置仍留在第一次出现的地方。
        ///
        /// 逐 mod 遍历那一路做不到这件事:它把多份并成一个并集,于是一个被后来的 mod 整个
        /// 重定义过的 def,路径表里混着已经不生效的那一份的路径。
        /// </summary>
        public static List<Node> Extract(XmlDocument doc, int maxDepth, int maxItems)
        {
            var order = new List<string>();
            var byKey = new Dictionary<string, Node>(StringComparer.Ordinal);

            var root = doc == null ? null : doc.DocumentElement;
            if (root == null) return new List<Node>();

            foreach (XmlNode child in root.ChildNodes)
            {
                var el = child as XmlElement;
                if (el == null) continue;

                var name = el.GetAttribute("Name");
                var defName = ChildText(el, "defName");
                string nodeKey;
                bool keyIsName;
                if (defName.Length > 0) { nodeKey = defName; keyIsName = false; }
                else if (name.Length > 0) { nodeKey = name; keyIsName = true; }
                else continue;

                List<string> texts;
                var paths = XmlFieldPaths.Collect(el, maxDepth, maxItems, out texts);
                var key = Key(el.Name, nodeKey);
                if (!byKey.ContainsKey(key)) order.Add(key);
                byKey[key] = new Node
                {
                    DefType = el.Name,
                    NodeKey = nodeKey,
                    KeyIsName = keyIsName,
                    Paths = paths,
                    Texts = texts,
                };
            }

            var result = new List<Node>(order.Count);
            foreach (var key in order) result.Add(byKey[key]);
            return result;
        }

        /// <summary>
        /// 这条路径在磁盘上的原文里有没有出现过。<paramref name="before"/> 是 <c>null</c>
        /// 时全算补丁加的 —— 那说明整个节点在原文里都不存在,它确实整个是补丁造的。
        /// </summary>
        public static List<bool> PatchedFlags(List<string> paths, HashSet<string> before)
        {
            var flags = new List<bool>(paths.Count);
            foreach (var p in paths) flags.Add(before == null || !before.Contains(p));
            return flags;
        }

        /// <summary>xml_written 的节点身份:def 类型 + 节点键。两者都不含空格。</summary>
        public static string Key(string defType, string nodeKey) => defType + " " + nodeKey;

        private static string ChildText(XmlElement el, string childName)
        {
            var child = el[childName];
            return child == null ? "" : child.InnerText;
        }
    }
}
