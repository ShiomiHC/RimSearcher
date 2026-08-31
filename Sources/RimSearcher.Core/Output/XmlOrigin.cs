using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Output;

/// <summary>
/// get 字段表上「这一行 XML 写没写」那一列 —— 产地唯一。
///
/// <c>here</c> = 这个 def 自己的 XML 写了这条路径,Replace 找得到节点;
/// <c>parent</c> = 只有祖先写了,指向这个 def 的 xpath 上这条节点不在,Add 才找得到;
/// <c>no</c> = 这一格没写(含:列表项已按 defName 标签归位,但这一格对应的行不在);
/// <c>under X</c> = XML 在容器 X 下写过东西,值回连之后仍对不到这一项。
/// 缺层时这一列根本不出现,由能力位那句通知说,不许印成 <c>no</c>。
///
/// RimWorld 允许拿 defName 当列表元素的标签名(<c>costList.Steel</c>、<c>things.AncientAmmoStack.chance</c>),
/// 索引按 <c>costList[0].thingDef</c>。值回连拿同一元素各格的值 V 去对 XML 的 <c>容器.V</c>
/// (自己写的和祖先写的都算);对上了就按能否落到具体行分成 here/parent 或确定的 no。
/// </summary>
public static class XmlOrigin
{
    public const string Column = "xml";
    public const string Here = "here";
    public const string Parent = "parent";
    public const string No = "no";

    public static string Under(string container) => $"under {container}";

    /// <summary>
    /// 按带下标的元素前缀把同一 def 的格分组。一次算完,查询侧按格查表,不按格扫库。
    /// </summary>
    public static Dictionary<string, List<string>> ValuesByElement(
        IEnumerable<FieldRow> cells)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (string.IsNullOrEmpty(cell.Value)) continue;
            foreach (var element in PathSegments.IndexPrefixes(cell.Path))
            {
                if (!d.TryGetValue(element, out var list))
                    d[element] = list = [];
                list.Add(cell.Value);
            }
        }
        return d;
    }

    /// <summary>
    /// 这一格的 xml 列取值。精确路径优先;否则值回连;再否则看容器前缀是不是还在。
    /// </summary>
    public static string Resolve(
        string path,
        IReadOnlyDictionary<string, string> xmlMarks,
        HashSet<string> containers,
        IReadOnlyDictionary<string, List<string>> valuesByElement)
    {
        if (xmlMarks.TryGetValue(path, out var exact))
            return exact;

        if (TryFirstIndex(path, out var container, out var element, out var leaf)
            && valuesByElement.TryGetValue(element, out var values))
        {
            var located = false;
            string? landed = null;
            foreach (var v in values)
            {
                var cv = container + "." + v;
                if (xmlMarks.ContainsKey(cv) || containers.Contains(cv))
                    located = true;
                if (leaf.Length > 0 && xmlMarks.TryGetValue(cv + "." + leaf, out var mLeaf))
                    landed = PreferHere(landed, mLeaf);
                if (xmlMarks.TryGetValue(cv, out var mV))
                    landed = PreferHere(landed, mV);
                if (landed == Here) break;
            }
            if (landed is not null) return landed;
            if (located) return No;
        }

        var bracket = path.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0 && containers.Contains(path[..bracket]))
            return Under(path[..bracket]);
        return No;
    }

    /// <summary>
    /// 第一条 <c>容器[N].叶</c>。<c>things[0].positions[1]</c> 的容器是 things,叶是 positions[1]
    /// —— defName 标签就挂在这一层,再往里的下标是标签底下的列表。
    /// </summary>
    internal static bool TryFirstIndex(
        string path, out string container, out string element, out string leaf)
    {
        var open = path.IndexOf('[', StringComparison.Ordinal);
        if (open <= 0)
        {
            container = element = leaf = "";
            return false;
        }
        var close = path.IndexOf(']', open + 1);
        if (close < 0)
        {
            container = element = leaf = "";
            return false;
        }
        container = path[..open];
        element = path[..(close + 1)];
        leaf = close + 1 < path.Length && path[close + 1] == '.'
            ? path[(close + 2)..]
            : "";
        return true;
    }

    private static string PreferHere(string? current, string mark)
        => current == Here || mark == Here ? Here : mark;
}
