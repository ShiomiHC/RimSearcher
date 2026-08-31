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
/// (自己写的和祖先写的都算),那个 V 就是标签名。
///
/// 归位之后还得分清标签底下写了什么,否则会给出错的确定答案:短形式只写标签名加一段
/// 文本,文本落哪一格由该类型的 LoadDataFromXmlCustom 决定,而 xml_written 只记路径不记
/// 内容 —— 元素里的候选格多于一个时(costList 的 count 与 quality)就是说不准,报 here
/// 会把 Replace 指向一个从没写过的节点。
/// </summary>
public static class XmlOrigin
{
    public const string Column = "xml";
    public const string Here = "here";
    public const string Parent = "parent";
    public const string No = "no";

    public static string Under(string container) => $"under {container}";

    /// <summary>元素里的一格。<c>Leaf</c> 是相对该元素前缀的余段,<c>costList[0]</c> 下的 <c>count</c>。</summary>
    public readonly record struct ElementCell(string Leaf, string Value);

    /// <summary>
    /// 按带下标的元素前缀把同一 def 的格分组。一次算完,查询侧按格查表,不按格扫库。
    /// </summary>
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

    /// <summary>
    /// 这一格的 xml 列取值。精确路径优先;否则值回连;再否则看容器前缀是不是还在。
    /// </summary>
    public static string Resolve(
        string path,
        IReadOnlyDictionary<string, string> xmlMarks,
        HashSet<string> containers,
        IReadOnlyDictionary<string, List<ElementCell>> cellsByElement)
    {
        if (xmlMarks.TryGetValue(path, out var exact))
            return exact;

        if (TryFirstIndex(path, out var container, out var element, out var leaf)
            && cellsByElement.TryGetValue(element, out var cells))
        {
            // anchor:元素里某一格的值 V 使 `容器.V` 出现在 XML 里 —— 那个 V 就是标签名。
            string? anchor = null, anchorValue = null, anchorMark = null;
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

            if (anchor is not null)
            {
                // XML 把这个字段自己拼了出来
                if (leaf.Length > 0 && xmlMarks.TryGetValue(anchor + "." + leaf, out var mLeaf))
                    return mLeaf;

                // 键格:这一格的值就是那个标签名。标签在,它就在。
                foreach (var c in cells)
                    if (c.Leaf == leaf && c.Value == anchorValue)
                        return anchorMark ?? ChildMark(xmlMarks, anchor);

                // 标签底下拼着别的字段(长形式 <Widget><chance>…</chance></Widget>),
                // 没拼到这一格 —— 确定没写。
                if (anchorMark is null) return No;

                // 短形式 <Steel>75</Steel> 只写两件事:标签名,和一段文本。文本落哪一格由
                // 类型的 LoadDataFromXmlCustom 决定,XML 侧只记了路径没记内容,这里读不出来。
                // 候选只剩一格时没得选(statBases 的 value);剩多格时(costList 的 count 与
                // quality)哪一格都可能,报 here 就是给了个错的确定答案。
                var candidates = 0;
                foreach (var c in cells)
                {
                    if (c.Leaf.Length == 0 || c.Value == anchorValue) continue;
                    if (xmlMarks.ContainsKey(anchor + "." + c.Leaf)) continue;
                    if (xmlMarks.ContainsKey(element + "." + c.Leaf)) continue;
                    if (++candidates > 1) break;
                }
                return candidates == 1 ? anchorMark : Under(container);
            }
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

    /// <summary>
    /// 标签自己没占一行(长形式)时,它的 here/parent 只能从底下那些行看出来。
    /// 只在键格这一条路上走,量小。
    /// </summary>
    private static string ChildMark(IReadOnlyDictionary<string, string> xmlMarks, string anchor)
    {
        string? mark = null;
        foreach (var kv in xmlMarks)
            if (kv.Key.Length > anchor.Length
                && kv.Key[anchor.Length] == '.'
                && kv.Key.StartsWith(anchor, StringComparison.Ordinal))
            {
                mark = PreferHere(mark, kv.Value);
                if (mark == Here) break;
            }
        return mark ?? Here;
    }
}
