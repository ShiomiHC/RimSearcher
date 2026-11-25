using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Metadata;

public sealed record IlListing
{
    public required IReadOnlyList<string> Lines { get; init; }

    public required int TotalLines { get; init; }

    public required int FirstOffset { get; init; }
    public required int LastOffset { get; init; }

    public required int? ShownFrom { get; init; }
    public required int? ShownTo { get; init; }

    public bool Trimmed => Lines.Count < TotalLines;
}

public static class IlText
{
    public static IlListing Disassemble(PEFile file, MethodHit method, int? from, int? to, int? maxLines)
    {
        var output = new PlainTextOutput();
        var rd = new ReflectionDisassembler(output, default)
        {
            ShowMetadataTokens = true,
        };
        rd.DisassembleMethod(file, method.Handle);

        var all = output.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var offsets = all.Select(OffsetOf).ToArray();

        var present = offsets.Where(o => o >= 0).ToList();
        var first = present.Count > 0 ? present[0] : 0;
        var last = present.Count > 0 ? present[^1] : 0;

        var lowerBound = from ?? first;
        var upperBound = to ?? last;

        var kept = new List<string>();
        var shownFrom = -1;
        var shownTo = -1;

        for (var i = 0; i < all.Length; i++)
        {
            var off = offsets[i];
            if (off < 0)
            {
                kept.Add(all[i]);
                continue;
            }

            if (off < lowerBound || off > upperBound) continue;
            if (shownFrom < 0) shownFrom = off;
            shownTo = off;
            kept.Add(all[i]);
        }

        if (maxLines is > 0 && kept.Count > maxLines)
        {
            kept = kept.Take(maxLines.Value).ToList();
            var lastKept = kept.Select(OffsetOf).Where(o => o >= 0).ToList();
            (shownFrom, shownTo) = lastKept.Count > 0 ? (lastKept[0], lastKept[^1]) : (-1, -1);
        }

        return new IlListing
        {
            Lines = kept,
            TotalLines = all.Length,
            FirstOffset = first,
            LastOffset = last,
            ShownFrom = shownFrom < 0 ? null : shownFrom,
            ShownTo = shownTo < 0 ? null : shownTo,
        };
    }

    private static int OffsetOf(string line)
    {
        var s = line.AsSpan().TrimStart();
        if (s.Length < 8 || !s.StartsWith("IL_")) return -1;
        var colon = s.IndexOf(':');
        if (colon < 4) return -1;
        return int.TryParse(s[3..colon], System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
    }
}
