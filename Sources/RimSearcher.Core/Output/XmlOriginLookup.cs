using RimSearcher.Storage;

namespace RimSearcher.Output;

public sealed class XmlOriginLookup(SnapshotDb db)
{
    private sealed class DefXml(
        DefRow def, Dictionary<string, string> marks, HashSet<string> containers,
        Dictionary<string, string> texts, HashSet<string>? patched,
        Dictionary<string, List<XmlOccurrence>> occurrences)
    {
        public readonly DefRow Def = def;
        public readonly Dictionary<string, string> Marks = marks;
        public readonly HashSet<string> Containers = containers;
        public readonly Dictionary<string, string> Texts = texts;
        public readonly HashSet<string>? Patched = patched;
        public readonly Dictionary<string, List<XmlOccurrence>> Occurrences = occurrences;
        public readonly Dictionary<string, (Dictionary<string, List<XmlOrigin.ElementCell>> Cells,
                                            Dictionary<string, int> Defaults)> ByPrefix = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, List<XmlOrigin.ElementCell>> NoCells = [];
    private readonly Dictionary<(string Type, string Name), DefXml?> _cache = [];

    public string? Resolve(string defType, string defName, string path)
        => Get(defType, defName, null) is { } x ? Resolve(x, path) : null;

    private string Resolve(DefXml x, string path)
    {
        var cells = x.Marks.ContainsKey(path) || !path.Contains('[') ? NoCells : Cells(x, path).Cells;
        return XmlOrigin.Resolve(path, x.Marks, x.Containers, cells, x.Texts, x.Patched, x.Occurrences);
    }

    public string? From(string defType, string defName, string path, int? defaultState = null)
        => From(Get(defType, defName, null), path, defaultState);

    public string? From(DefRow def, string path, int? defaultState = null)
        => From(Get(def.DefType, def.DefName, def), path, defaultState);

    private string? From(DefXml? x, string path, int? defaultState)
    {
        if (x is null) return null;
        var mark = Resolve(x, path);
        if (defaultState is null && Cells(x, path).Defaults.TryGetValue(path, out var d)) defaultState = d;
        return FieldFrom.Render(mark, defaultState, db.Meta.IndexesPostPatchXml);
    }

    private DefXml? Get(string defType, string defName, DefRow? known)
    {
        if (!_cache.TryGetValue((defType, defName), out var x))
            _cache[(defType, defName)] = x = Load(defType, defName, known);
        return x;
    }

    private (Dictionary<string, List<XmlOrigin.ElementCell>> Cells, Dictionary<string, int> Defaults)
        Cells(DefXml x, string path)
    {
        var open = path.IndexOf('[');
        var prefix = open < 0 ? path : path[..(open + 1)];
        if (x.ByPrefix.TryGetValue(prefix, out var hit)) return hit;
        var cells = db.AllFieldCells(x.Def.Id, prefix);
        var defaults = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in cells) defaults.TryAdd(c.Path, c.Default);
        return x.ByPrefix[prefix] = (XmlOrigin.CellsByElement(cells), defaults);
    }

    private DefXml? Load(string defType, string defName, DefRow? known)
    {
        var def = known ?? db.GetDefsNamed(defName)
                    .FirstOrDefault(d => string.Equals(d.DefType, defType, StringComparison.Ordinal));
        if (def is null) return null;
        var marks = db.XmlWrittenMarks(def.DefType, def.DefName, out var containers, out var texts, out var patched,
                                       out _, out var occurrences);
        return new DefXml(def, marks, containers, texts, patched, occurrences);
    }
}
