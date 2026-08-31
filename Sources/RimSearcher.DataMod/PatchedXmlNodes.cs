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
            /// <summary>身份的一部分,不出到中间格式 —— 那边的 abstract 长在 xml_nodes 上。</summary>
            public bool IsAbstract;
            public List<string> Paths;
            public List<string> Texts;
        }

        /// <summary>
        /// 合并文档里同一个 defName 可以出现多次,但**多数不是重复定义**,塌缩前得先认清身份。
        ///
        /// 身份 = 类型 + 键 + 键取自哪(<c>Name=</c> 还是 <c>defName</c>)+ 抽不抽象。后两样
        /// 不进键就会踩两种官方写法:抽象节点 <c>Name="BabyPlay"</c> 与具体 def
        /// <c>&lt;defName&gt;BabyPlay&lt;/defName&gt;</c> 同名;抽象节点同时带 <c>Name=</c> 和
        /// <c>&lt;defName&gt;</c>(<c>Mercenary_Slasher</c>)。这两种情形里父子两份路径**都算数**
        /// —— 子 def 继承了父节点写的那些行 —— 塌缩掉一份就会让它们变成确定的 no。
        ///
        /// 身份真撞上时**前者胜**:<c>DefDatabase.Add</c> 对同名的第二份要么跳过(同 mod 内),
        /// 要么给它改名(跨 mod),活下来的叫这个名字的始终是第一份。逐 mod 遍历那一路给的是
        /// 并集,于是路径表里会混进那份根本没生效的。
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
                var isAbstract = string.Equals(el.GetAttribute("Abstract"), "true",
                                               StringComparison.OrdinalIgnoreCase);

                var key = Key(el.Name, nodeKey, keyIsName, isAbstract);
                if (byKey.ContainsKey(key)) continue;   // 前者胜
                order.Add(key);

                List<string> texts;
                var paths = XmlFieldPaths.Collect(el, maxDepth, maxItems, out texts);
                byKey[key] = new Node
                {
                    DefType = el.Name,
                    NodeKey = nodeKey,
                    KeyIsName = keyIsName,
                    IsAbstract = isAbstract,
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

        /// <summary>
        /// 节点身份。四样都进键 —— 只用类型+键会把抽象父节点和同名的具体 def 认成一个。
        /// def 类型与节点键都不含空格,拿空格当分隔安全。
        /// </summary>
        public static string Key(string defType, string nodeKey, bool keyIsName, bool isAbstract)
            => defType + " " + nodeKey + (keyIsName ? " N" : " D") + (isAbstract ? "A" : "C");

        private static string ChildText(XmlElement el, string childName)
        {
            var child = el[childName];
            return child == null ? "" : child.InnerText;
        }
    }
}
