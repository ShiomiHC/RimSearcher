using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Metadata;

/// <summary>一段反汇编出来的 IL,以及这次给出的是其中哪一截。</summary>
public sealed record IlListing
{
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>这个方法体一共多少行。</summary>
    public required int TotalLines { get; init; }

    /// <summary>指令覆盖的偏移区间。方法体为空时两端都是 0。</summary>
    public required int FirstOffset { get; init; }
    public required int LastOffset { get; init; }

    /// <summary>本次给出的区间。与上面两个相同则是整段。</summary>
    public required int ShownFrom { get; init; }
    public required int ShownTo { get; init; }

    public bool Trimmed => Lines.Count < TotalLines;
}

/// <summary>
/// 方法 → IL 文本。
///
/// 用反编译器自带的反汇编器,不自己拼指令 —— 它就是 ILSpy 的 IL 视图印出来的那一份。
/// 实测一个 16158 个类型的程序集全部 90840 个方法反汇编 2.5 秒,单个方法不到 1 毫秒,
/// 所以这一层不落盘:现算比读盘还快,而落盘要多付 382MB。
/// </summary>
public static class IlText
{
    /// <summary>
    /// 反汇编一个方法。
    ///
    /// <paramref name="from"/> / <paramref name="to"/> 是 IL 偏移,不是行号 —— IL 的坐标系是偏移,
    /// 而反汇编文本的行与指令不是一一对应(局部变量表、异常处理块各占若干行)。方法头
    /// (签名、maxstack、locals)不参与裁剪:没有它,一段指令读不出自己在操作什么。
    /// </summary>
    public static IlListing Disassemble(PEFile file, MethodHit method, int? from, int? to, int? maxLines)
    {
        var output = new PlainTextOutput();
        var rd = new ReflectionDisassembler(output, default)
        {
            // 元数据 token 是写 transpiler 时要往 Harmony 里填的东西,不印出来就得再查一次。
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
                // 没有偏移的行:方法头、局部变量表、结束的大括号。它们不属于任何区间,
                // 裁剪时一律留着。
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
            if (lastKept.Count > 0) shownTo = lastKept[^1];
        }

        return new IlListing
        {
            Lines = kept,
            TotalLines = all.Length,
            FirstOffset = first,
            LastOffset = last,
            ShownFrom = shownFrom < 0 ? lowerBound : shownFrom,
            ShownTo = shownTo < 0 ? lowerBound : shownTo,
        };
    }

    /// <summary>行首的 <c>IL_00a4:</c> → 164。没有就是 -1。</summary>
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
