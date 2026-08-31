#nullable disable

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// PatchOperation xpath 里三种能用正则从全文抓到的定位方式。
    ///
    /// <c>@Name=</c> 是属性;<c>defName=</c> / <c>label=</c> 通常写在谓词里点元素
    /// (可带或不带 <c>@</c>)。按类名和按通配符定位要语义解析,这里不做。
    ///
    /// 这个文件不许引用任何 RimWorld 类型:测试工程靠 Compile Link 编进来验正则。
    /// </summary>
    internal static class PatchXPath
    {
        public static readonly Regex Name =
            new Regex("@Name\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.Compiled);

        public static readonly Regex DefName =
            new Regex("@?defName\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.Compiled);

        public static readonly Regex Label =
            new Regex("@?label\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.Compiled);

        public static void AddMatches(string xpath,
                                      Dictionary<string, int> byName,
                                      Dictionary<string, int> byDefName,
                                      Dictionary<string, int> byLabel)
        {
            CountInto(byName, Name, xpath);
            CountInto(byDefName, DefName, xpath);
            CountInto(byLabel, Label, xpath);
        }

        private static void CountInto(Dictionary<string, int> counts, Regex rx, string xpath)
        {
            foreach (Match m in rx.Matches(xpath))
            {
                var target = m.Groups[1].Value;
                int n;
                counts[target] = counts.TryGetValue(target, out n) ? n + 1 : 1;
            }
        }
    }
}
