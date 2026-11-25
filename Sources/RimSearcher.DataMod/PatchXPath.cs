#nullable disable

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RimSearcher.DataMod
{
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
