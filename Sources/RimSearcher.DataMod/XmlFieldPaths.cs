#nullable disable

using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 一个 def 节点里 XML **实际写出来的**字段路径,拼法对齐 <c>field_values.path</c>:
    /// 子元素用 <c>.</c> 连接,<c>li</c> 按下标写成 <c>[N]</c>,<c>Class=</c> 属性写成
    /// <c>.Class</c>。叶子(没有子元素的节点,含空元素)占一条路径,中间复合节点不占。
    ///
    /// 深度预算与导出器相同:只有往下钻进复合对象才 +1,叶子不占。
    ///
    /// 对不齐的形状(statBases 把 defName 当标签名、<c>costList</c> 同形、继承合并后下标
    /// 与补丁后 field_values 下标)不在这里修补,查询侧按字面 join。
    ///
    /// 这个文件不许引用任何 RimWorld 类型。
    /// </summary>
    internal static class XmlFieldPaths
    {
        public static List<string> Collect(XmlElement def, int maxDepth, int maxItems)
        {
            var into = new List<string>();
            var seen = new HashSet<string>();
            WalkChildren(def, "", 0, maxDepth, maxItems, into, seen);
            return into;
        }

        private static void WalkChildren(XmlElement el, string prefix, int depth,
                                         int maxDepth, int maxItems,
                                         List<string> into, HashSet<string> seen)
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
                WalkElement(child, path, depth, maxDepth, maxItems, into, seen);
            }
        }

        private static void WalkElement(XmlElement el, string path, int depth,
                                        int maxDepth, int maxItems,
                                        List<string> into, HashSet<string> seen)
        {
            var classAttr = el.GetAttribute("Class");
            if (classAttr != null && classAttr.Length > 0)
                Add(into, seen, path + ".Class");

            var hasChildEls = false;
            foreach (XmlNode n in el.ChildNodes)
            {
                if (n is XmlElement) { hasChildEls = true; break; }
            }

            if (!hasChildEls)
            {
                Add(into, seen, path);
                return;
            }

            if (depth >= maxDepth) return;
            WalkChildren(el, path, depth + 1, maxDepth, maxItems, into, seen);
        }

        private static void Add(List<string> into, HashSet<string> seen, string path)
        {
            if (seen.Add(path)) into.Add(path);
        }
    }
}
