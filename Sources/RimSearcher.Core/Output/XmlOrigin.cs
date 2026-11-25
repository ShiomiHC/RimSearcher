using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Output;

public static class XmlOrigin
{
    public const string Here = "here";
    public const string HereOver = "here>";
    public const string HereSame = "here=";
    public const string Parent = "parent";

    public static bool IsHere(string mark) => mark.StartsWith(Here, StringComparison.Ordinal);
    public const string No = "not-written";
    public const string PatchSuffix = "+patch";

    private static string WithPatch(string mark, string source, IReadOnlySet<string>? patched)
        => patched is not null && patched.Contains(source) ? mark + PatchSuffix : mark;

    public const string UnderPrefix = "under ";

    public static string Under(string container) => UnderPrefix + container;

    public readonly record struct ElementCell(string Leaf, string Value);

    public static Dictionary<string, List<ElementCell>> CellsByElement(
        IEnumerable<FieldRow> cells)
    {
        var d = new Dictionary<string, List<ElementCell>>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (string.IsNullOrEmpty(cell.Value)) continue;
            foreach (var element in PathSegments.IndexPrefixes(cell.Path))
            {
                if (!d.TryGetValue(element, out var list))
                    d[element] = list = [];
                list.Add(new ElementCell(
                    cell.Path.Length > element.Length + 1 ? cell.Path[(element.Length + 1)..] : "",
                    cell.Value!));
            }
        }
        return d;
    }

    public static string Resolve(
        string path,
        IReadOnlyDictionary<string, string> xmlMarks,
        HashSet<string> containers,
        IReadOnlyDictionary<string, List<ElementCell>> cellsByElement,
        IReadOnlyDictionary<string, string> xmlTexts,
        IReadOnlySet<string>? patchedPaths = null,
        IReadOnlyDictionary<string, List<XmlOccurrence>>? occurrences = null)
    {
        if (xmlMarks.TryGetValue(path, out var exact))
            return WithPatch(exact, path, patchedPaths);

        var walked = Walk(path, 0, "", xmlMarks, containers, cellsByElement, xmlTexts, patchedPaths, occurrences);
        if (walked is not null) return walked;

        var bracket = path.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0 && containers.Contains(path[..bracket]))
            return Under(path[..bracket]);
        return No;
    }

    private static string? Walk(
        string path, int pos, string xmlPrefix,
        IReadOnlyDictionary<string, string> xmlMarks,
        HashSet<string> containers,
        IReadOnlyDictionary<string, List<ElementCell>> cellsByElement,
        IReadOnlyDictionary<string, string> xmlTexts,
        IReadOnlySet<string>? patchedPaths,
        IReadOnlyDictionary<string, List<XmlOccurrence>>? occurrences)
    {
        var open = path.IndexOf('[', pos);
        var close = open < 0 ? -1 : path.IndexOf(']', open + 1);
        if (open < 0 || close < 0)
        {
            var spelled = xmlPrefix + path[pos..];
            if (xmlMarks.TryGetValue(spelled, out var m))
                return WithPatch(m, spelled, patchedPaths);
            return containers.Contains(spelled) ? ElementMark(xmlMarks, spelled, patchedPaths) : null;
        }

        var containerIdx = path[..open];
        var container = xmlPrefix + path[pos..open];
        var element = path[..(close + 1)];
        var elementXml = container + path[open..(close + 1)];
        var leaf = close + 1 < path.Length && path[close + 1] == '.' ? path[(close + 2)..] : "";

        string? anchor = null, anchorValue = null, anchorMark = null;
        if (cellsByElement.TryGetValue(element, out var cells))
        {
            foreach (var c in cells)
            {
                if (c.Value.Length == 0) continue;
                var cv = container + "." + c.Value;
                if (xmlMarks.TryGetValue(cv, out var m))
                {
                    anchor = cv; anchorValue = c.Value; anchorMark = m;
                    break;
                }
                if (anchor is null && containers.Contains(cv))
                {
                    anchor = cv; anchorValue = c.Value;
                }
            }
        }

        if (anchor is null)
            return Walk(path, close + 1, elementXml, xmlMarks, containers, cellsByElement, xmlTexts, patchedPaths, occurrences)
                   ?? (occurrences is null ? null
                       : CurvePoint(path, containerIdx, container, leaf, cellsByElement, xmlMarks, occurrences, patchedPaths)
                         ?? TypeTagged(containerIdx, container, element, leaf, cells, cellsByElement, xmlMarks,
                                       containers, occurrences, patchedPaths));

        if (leaf.Length > 0 && xmlMarks.TryGetValue(anchor + "." + leaf, out var mLeaf))
            return WithPatch(mLeaf, anchor + "." + leaf, patchedPaths);

        if (leaf.Contains('['))
        {
            var deeper = Walk(path, close + 1, anchor, xmlMarks, containers, cellsByElement, xmlTexts, patchedPaths, occurrences);
            if (deeper is not null) return deeper;
        }

        foreach (var c in cells!)
            if (c.Leaf == leaf && c.Value == anchorValue)
                return anchorMark is not null
                    ? WithPatch(anchorMark, anchor, patchedPaths)
                    : ChildMark(xmlMarks, anchor, patchedPaths);

        if (xmlMarks.ContainsKey(anchor + ".@")) return Under(containerIdx);

        if (anchorMark is null) return No;

        if (xmlTexts.TryGetValue(anchor, out var ownText) && ownText.Length == 0)
            return cells.Exists(c => c.Leaf == leaf) ? No : Under(containerIdx);

        if (ShaderParameterCell(leaf, cells, anchorValue!, xmlTexts.GetValueOrDefault(anchor)) is { } written)
            return written ? WithPatch(anchorMark, anchor, patchedPaths) : No;

        var candidates = 0;
        foreach (var c in cells)
        {
            if (c.Leaf.Length == 0 || c.Value == anchorValue) continue;
            if (xmlMarks.ContainsKey(anchor + "." + c.Leaf)) continue;
            if (xmlMarks.ContainsKey(elementXml + "." + c.Leaf)) continue;
            if (++candidates > 1) break;
        }
        if (candidates <= 1)
            return candidates == 1
                ? WithPatch(anchorMark, anchor, patchedPaths)
                : Under(containerIdx);

        xmlTexts.TryGetValue(anchor, out var t);
        var matches = 0;
        var thisMatches = false;
        if (t is not null)
        {
            foreach (var c in cells)
            {
                if (c.Leaf.Length == 0 || c.Value == anchorValue) continue;
                if (xmlMarks.ContainsKey(anchor + "." + c.Leaf)) continue;
                if (xmlMarks.ContainsKey(elementXml + "." + c.Leaf)) continue;
                if (c.Value != t) continue;
                matches++;
                if (c.Leaf == leaf) thisMatches = true;
            }
        }
        if (matches == 1)
            return thisMatches ? WithPatch(anchorMark, anchor, patchedPaths) : No;
        if (matches == 0) return Under(containerIdx);
        return thisMatches ? Under(containerIdx) : No;
    }

    private static bool? ShaderParameterCell(string leaf, List<ElementCell> cells, string anchorValue, string? text)
    {
        if (text is null || leaf is not ("value" or "type")) return null;
        string? name = null, value = null, type = null;
        foreach (var c in cells)
            switch (c.Leaf)
            {
                case "name": name = c.Value; break;
                case "value": value = c.Value; break;
                case "type": type = c.Value; break;
                default: return null;
            }
        if (name != anchorValue || value is null || type is null) return null;

        var t = text.Trim();
        float[]? parsed;
        string expected;
        if (t.StartsWith('(')) { expected = "Vector"; parsed = Floats(t.TrimStart('(').TrimEnd(')'), pad: true); }
        else if (t.StartsWith('/')) { expected = "Texture"; parsed = null; }
        else { expected = "Float"; parsed = Floats(t, pad: false) is [var f] ? [f, f, f, f] : null; }
        if (type != expected) return null;
        if (expected == "Texture") return leaf == "type";
        if (parsed is null || Floats(value.TrimStart('(').TrimEnd(')'), pad: false) is not { Length: 4 } shown) return null;
        for (var i = 0; i < 4; i++)
            if (Math.Abs(shown[i] - parsed[i]) > 0.051f * Math.Max(1f, Math.Abs(parsed[i]))) return null;
        return true;

        static float[]? Floats(string s, bool pad)
        {
            var parts = s.Split(',');
            if (parts.Length is 0 or > 4) return null;
            var r = new float[pad ? 4 : parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out r[i])) return null;
            return r;
        }
    }

    private static string? CurvePoint(
        string path, string containerIdx, string container, string leaf,
        IReadOnlyDictionary<string, List<ElementCell>> cellsByElement,
        IReadOnlyDictionary<string, string> xmlMarks,
        IReadOnlyDictionary<string, List<XmlOccurrence>> occurrences,
        IReadOnlySet<string>? patchedPaths)
    {
        if (leaf.Length != 0) return null;
        var pointsPrefix = container + ".points[";
        var written = new List<(string Path, XmlOccurrence O, (float, float) P)>();
        foreach (var (p, occ) in occurrences)
            if (p.StartsWith(pointsPrefix, StringComparison.Ordinal) && p.IndexOf('.', pointsPrefix.Length) < 0)
                foreach (var o in occ)
                    if (ParsePoint(o.Text) is { } xy) written.Add((p, o, xy));
        if (written.Count == 0) return null;

        var mine = ParsePoint(CellValue(cellsByElement, path));
        if (mine is null) return null;

        var runtime = new List<(float, float)>();
        foreach (var e in DirectElements(containerIdx, cellsByElement))
            if (ParsePoint(CellValue(cellsByElement, e)) is { } xy) runtime.Add(xy);

        var anyMatched = runtime.Exists(r => written.Exists(w => w.P == r));
        var same = written.FindAll(w => w.P == mine.Value);
        if (same.Count == 0) return anyMatched ? No : null;
        if (runtime.FindAll(r => r == mine.Value).Count > same.Count) return Under(containerIdx);
        var hit = same.Find(w => IsHere(w.O.Mark));
        if (hit.Path is null) hit = same[0];
        return WithPatch(hit.O.Mark, hit.Path, patchedPaths);
    }

    private static string? TypeTagged(
        string containerIdx, string container, string element, string leaf,
        List<ElementCell>? cells,
        IReadOnlyDictionary<string, List<ElementCell>> cellsByElement,
        IReadOnlyDictionary<string, string> xmlMarks,
        HashSet<string> containers,
        IReadOnlyDictionary<string, List<XmlOccurrence>> occurrences,
        IReadOnlySet<string>? patchedPaths)
    {
        if (cells is null) return null;
        var prefix = container + ".";
        var texts = new List<(string Path, XmlOccurrence O)>();
        var attributed = false;
        foreach (var key in xmlMarks.Keys)
        {
            if (key.StartsWith(container + "[", StringComparison.Ordinal)) return null;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = key[prefix.Length..];
            if (rest.EndsWith(".@", StringComparison.Ordinal) && rest.IndexOf('.') == rest.Length - 2
                && rest.IndexOf('[') < 0)
            {
                attributed = true;
                continue;
            }
            if (rest.IndexOf('.') >= 0 || rest.IndexOf('[') >= 0) return null;
            if (containers.Contains(key)) return null;
            if (occurrences.TryGetValue(key, out var occ))
                foreach (var o in occ) texts.Add((key, o));
        }
        if (texts.Count == 0) return null;

        bool Written(string value) => texts.Exists(t => t.O.Text == value);
        var anyMatched = false;
        foreach (var e in DirectElements(containerIdx, cellsByElement))
            if (cellsByElement[e].Exists(c => c.Value.Length > 0 && Written(c.Value))) { anyMatched = true; break; }
        if (!anyMatched) return null;

        var key0 = cells.Find(c => c.Value.Length > 0 && Written(c.Value));
        if (key0.Value is null || key0.Leaf != leaf) return attributed ? Under(containerIdx) : No;

        var runtimeSame = 0;
        foreach (var e in DirectElements(containerIdx, cellsByElement))
            if (cellsByElement[e].Exists(c => c.Leaf == key0.Leaf && c.Value == key0.Value)) runtimeSame++;
        var same = texts.FindAll(t => t.O.Text == key0.Value);
        if (runtimeSame > same.Count) return Under(containerIdx);
        var hit = same.Find(t => IsHere(t.O.Mark));
        if (hit.Path is null) hit = same[0];
        return WithPatch(hit.O.Mark, hit.Path, patchedPaths);
    }

    private static IEnumerable<string> DirectElements(string containerIdx,
                                                      IReadOnlyDictionary<string, List<ElementCell>> cellsByElement)
    {
        foreach (var e in cellsByElement.Keys)
            if (e.Length > containerIdx.Length + 2 && e.StartsWith(containerIdx + "[", StringComparison.Ordinal)
                && e[^1] == ']' && e.IndexOf('[', containerIdx.Length + 1) < 0)
                yield return e;
    }

    private static string? CellValue(IReadOnlyDictionary<string, List<ElementCell>> cellsByElement, string element)
        => cellsByElement.TryGetValue(element, out var cs) ? cs.Find(c => c.Leaf.Length == 0).Value : null;

    private static (float, float)? ParsePoint(string? text)
    {
        if (text is null) return null;
        var t = text.Trim();
        if (t.Length < 5 || t[0] != '(' || t[^1] != ')') return null;
        var parts = t[1..^1].Split(',');
        if (parts.Length != 2) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles st = System.Globalization.NumberStyles.Float;
        return float.TryParse(parts[0].Trim(), st, inv, out var x) && float.TryParse(parts[1].Trim(), st, inv, out var y)
            ? (x, y) : null;
    }

    private static string ElementMark(IReadOnlyDictionary<string, string> xmlMarks, string element,
                                      IReadOnlySet<string>? patchedPaths)
    {
        string? mark = null;
        var allPatched = patchedPaths is not null;
        foreach (var kv in xmlMarks)
            if (kv.Key.Length > element.Length
                && kv.Key[element.Length] == '.'
                && kv.Key.StartsWith(element, StringComparison.Ordinal))
            {
                mark = PreferHere(mark, kv.Value);
                if (allPatched && !patchedPaths!.Contains(kv.Key)) allPatched = false;
            }
        mark ??= Here;
        return allPatched ? mark + PatchSuffix : mark;
    }

    public readonly record struct EmptyElement(string Path, string Mark);

    public static List<EmptyElement> EmptyElements(
        IReadOnlyDictionary<string, string> xmlMarks,
        IReadOnlyDictionary<string, string> xmlTexts,
        IReadOnlySet<string>? patchedPaths,
        IReadOnlySet<string> parentListPrefixes,
        IEnumerable<string> fieldPaths,
        IReadOnlyDictionary<string, List<ElementCell>> cellsByElement)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in fieldPaths)
        {
            covered.Add(p);
            for (var i = 1; i < p.Length; i++)
                if (p[i] is '.' or '[') covered.Add(p[..i]);
        }
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (element, cells) in cellsByElement)
        {
            var open = element.LastIndexOf('[');
            if (open <= 0) continue;
            foreach (var c in cells) tags.Add(element[..open] + "." + c.Value);
        }

        var list = new List<EmptyElement>();
        foreach (var (path, mark) in xmlMarks)
        {
            if (!xmlTexts.TryGetValue(path, out var t) || t.Length != 0) continue;
            if (xmlMarks.ContainsKey(path + ".Class") || xmlMarks.ContainsKey(path + ".@")) continue;
            if (covered.Contains(path) || UnderTag(path, tags)) continue;
            if (ListMergedWithAncestor(path, parentListPrefixes)) continue;
            list.Add(new EmptyElement(path, WithPatch(mark, path, patchedPaths)));
        }
        return list;
    }

    private static bool UnderTag(string path, HashSet<string> tags)
    {
        if (tags.Contains(path)) return true;
        for (var dot = path.IndexOf('.'); dot > 0; dot = path.IndexOf('.', dot + 1))
            if (tags.Contains(path[..dot])) return true;
        return false;
    }

    private static bool ListMergedWithAncestor(string path, IReadOnlySet<string> parentListPrefixes)
    {
        for (var open = path.IndexOf('['); open > 0; open = path.IndexOf('[', open + 1))
            if (parentListPrefixes.Contains(path[..(open + 1)])) return true;
        return false;
    }

    private static string PreferHere(string? current, string mark)
    {
        if (current is null) return mark;
        if (!IsHere(current)) return IsHere(mark) ? mark : current;
        if (!IsHere(mark)) return current;
        if (current == HereOver || mark == HereOver) return HereOver;
        return current == Here || mark == Here ? Here : HereSame;
    }

    private static string ChildMark(IReadOnlyDictionary<string, string> xmlMarks, string anchor,
                                    IReadOnlySet<string>? patchedPaths = null)
    {
        string? mark = null;
        string? source = null;
        foreach (var kv in xmlMarks)
            if (kv.Key.Length > anchor.Length
                && kv.Key[anchor.Length] == '.'
                && kv.Key.StartsWith(anchor, StringComparison.Ordinal))
            {
                var picked = PreferHere(mark, kv.Value);
                if (source is null || picked != mark) { mark = picked; source = kv.Key; }
                if (mark == HereOver) break;
            }
        return source is null ? Here : WithPatch(mark!, source, patchedPaths);
    }
}
