using System.Globalization;
using System.Text.RegularExpressions;
using RimSearcher.Sources;

namespace RimSearcher.Snapshot;

public enum UnityEraSide { Neither, Old, New }

public static class EngineNotation
{
    private const string F2 = @"-?\d+\.\d\d|-?Infinity|NaN";

    private static readonly Regex UnityVector = new(
        $@"^\((?<c>{F2})(?:, (?<c>{F2})){{1,3}}\)$", RegexOptions.CultureInvariant);

    private static readonly Regex UnityRect = new(
        $@"^\(x:(?<c>{F2}), y:(?<c>{F2}), width:(?<c>{F2}), height:(?<c>{F2})\)$", RegexOptions.CultureInvariant);

    private static readonly Regex WaylithTuple = new(
        @"^\((?<c>[^,()]+)(?:, (?<c>[^,()]+)){1,3}\)$", RegexOptions.CultureInvariant);

    public static UnityEraSide Side(string oldGameVersion, string newGameVersion)
        => (IsUnityEra(oldGameVersion), IsUnityEra(newGameVersion)) switch
        {
            (true, false) => UnityEraSide.Old,
            (false, true) => UnityEraSide.New,
            _ => UnityEraSide.Neither,
        };

    public static bool? IsUnityEra(string gameVersion)
        => Version.TryParse(SourcePlanner.NormalizeGameVersion(gameVersion), out var v)
            ? v < new Version(1, 7)
            : null;

    public static bool SameValue(string unity, string waylith)
    {
        var u = UnityVector.Match(unity);
        if (!u.Success) u = UnityRect.Match(unity);
        if (!u.Success) return false;
        var w = WaylithTuple.Match(waylith);
        if (!w.Success) return false;

        var uc = u.Groups["c"].Captures;
        var wc = w.Groups["c"].Captures;
        if (uc.Count != wc.Count) return false;
        for (var i = 0; i < uc.Count; i++)
        {
            if (!TryParseWaylith(wc[i].Value, out var f)) return false;
            if (MonoF2(f) != uc[i].Value) return false;
        }
        return true;
    }

    private static bool TryParseWaylith(string s, out float f)
    {
        switch (s)
        {
            case "∞": f = float.PositiveInfinity; return true;
            case "-∞": f = float.NegativeInfinity; return true;
            default: return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }
    }

    public static string MonoF2(float f)
    {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsInfinity(f)) return f > 0 ? "Infinity" : "-Infinity";
        if (Math.Abs(f) >= 1e15f) return f.ToString("F2", CultureInfo.InvariantCulture);
        var s = ((decimal)f).ToString("F2", CultureInfo.InvariantCulture);
        return s[0] == '-' && s.Trim('-', '0', '.').Length == 0 ? s[1..] : s;
    }
}
