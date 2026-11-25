using System.Text.RegularExpressions;

namespace RimSearcher.Snapshot;

public static class InjectionKey
{
    private static readonly Regex NumericSegment = new(@"\.(\d+)(?=\.|$)", RegexOptions.Compiled);

    private static readonly Regex AnyIndex = new(@"\[\d+\]", RegexOptions.Compiled);

    public static string ToFieldPath(string key) =>
        NumericSegment.Replace(key, m => "[" + m.Groups[1].Value + "]");

    public static string CanonicalIndex(string fieldPath) => AnyIndex.Replace(fieldPath, "[0]");

    public static class State
    {
        public const string Resolved = "resolved";

        public const string NoSlot = "no-slot";

        public const string Refused = "refused";
    }
}
