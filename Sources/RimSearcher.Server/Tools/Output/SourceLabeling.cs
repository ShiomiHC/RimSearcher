using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 来源标签 `[vanilla]`：印在哪儿、描述的是哪个量、以及那条读法的契约。
//
// 判据的**位置**是这个类型存在的全部理由：标签本身只是一个方括号加一个源名，难的是
// 「同源就提到段头印一次、混源才逐行印」以及「段头那个方括号描述的是这一段的**总数**而不是
// 印出来的那几行」。两条都是第十一轮盲测的产物，且都不是某个工具的事——三个工具共用一份。
public readonly struct SourceLabeling
{
    private readonly bool _perRow;
    private readonly string? _common;
    private readonly IReadOnlyList<(string Source, int Count)>? _scopeTotals;

    private SourceLabeling(
        bool perRow, string? common, IReadOnlyList<(string Source, int Count)>? scopeTotals)
    {
        _perRow = perRow;
        _common = common;
        _scopeTotals = scopeTotals;
    }

    // 一批结果行的来源标签该印在哪儿。ScopeCatalog.ShowLabels 只回答「scope 选中了几个源」；
    // scope 是 'all' 而结果恰好全落在一个源里时，每行仍挂着同一个 ` [vanilla]`——实测
    // locate 一次 200 条的返回里 412 个标签约 4120 字，占正文 14%。ScopeCatalog 自己的注释
    // 早写着「单源时来源标签是纯噪音（每行都一样）」，这里把那条判据从 scope 挪到**实际列出
    // 的行**上：同源就提到表头印一次，混源才逐行印。标签是移位，不是删除。
    //
    // 但「提到表头印一次」这个动作本身造出了第十一轮的缺陷：段头恰好也是**总数**所在的那一行，
    // 而标签是按印出来的那几行算的。`10 of 36 members` + `**Members** [vanilla]` 里，36 条中有
    // 2 条来自 Cinders；`511 in scope 'all'` 后面挂着 `[vanilla]`，511 横跨五个源。三条盲测链
    // （locate 成员段、trace 继承树、locate 折叠掉的 def 段）全都只能靠自费多调一次才没答反。
    // 这与 R42 是同一型：那次是 direct/deepest 按切片算却排在描述全树的总数后面。**来源标签是
    // 同一个表头上最后一个仍按切片算的量。**
    //
    // 收口判据：段头的方括号恒描述**这一段的总数**——全集单源就印那个源名（与此前逐字相同），
    // 全集混源就印构成。列表没被截断时不印构成，那时行本身就是构成，再印一遍是 R19 删掉的噪音。
    // 这条读法此前在任何一处描述里都没写过——`[vanilla]` 是个无契约的记号，调用方只能自己发明
    // 一个读法，而最自然的那个（「这一段都是 vanilla」）恰好是假的。补进 locate / trace /
    // search_regex 三处描述，返回侧一个字不多。
    public const string Contract =
        "A `[source]` tag on a section header describes that section's total, not just the rows listed "
        + "under it: a bare name means the whole total comes from that source, and a truncated listing "
        + "whose total spans several sources carries the breakdown instead, as `[vanilla 34, Cinders 2]`. "
        + "That breakdown covers the section's whole total, near-name rows included, so it does not tell "
        + "you which sources the '(K at 100%)' subset comes from. "
        + "Rows carry their own `[source]` tag only when the listed rows span more than one source. "
        // 十一个源里九个的来源名与命名空间恰好相同，这个巧合把「方括号 = 命名空间」教成了规则，
        // 而 trace 的行是「全名 + [来源]」，方括号正落在命名空间之后那个位置。两个例外是
        // Cinders → Embergarden、kiiroEvent → Kiiro_Event。第十三轮盲测里被测方据此拼出了
        // `Cinders.CompVehicleWeapon` 与 `kiiroEvent.CompFishCatcher` 两个**不存在的标识符**并
        // 写进了交给用户的答案——拿去 inspect / read_code 一律解析不到，而返回里没有任何一处
        // 能让它察觉。这是本轮唯一一处「输出直接导致伪造标识符流向用户」。
        + "A `[source]` tag is a configured source name, never a namespace — the two coincide for most "
        + "sources on this server but not all, so never build a qualified name out of one.";

    public static string Label(string? sourceName)
        => string.IsNullOrEmpty(sourceName) ? string.Empty : $" [{sourceName}]";

    // 截断了才传 scopeTotals（见 Of<T> 重载）。传了就由它定表头，行级规则一字不动。
    public static SourceLabeling Of(
        IEnumerable<string?> rowSources,
        IReadOnlyList<(string Source, int Count)>? scopeTotals = null)
    {
        string? common = null;
        var seen = false;
        var perRow = false;

        foreach (var name in rowSources)
        {
            // 有一行说不出来源，说明 scope 已经把源钉死了（ShowLabels=false 时 SourceName
            // 恒为 null），这批本来就一个标签都不该印。
            if (string.IsNullOrEmpty(name)) return new SourceLabeling(false, null, null);

            if (!seen) { common = name; seen = true; }
            else if (!string.Equals(common, name, StringComparison.OrdinalIgnoreCase))
            {
                perRow = true;
                common = null;
            }
        }

        return new SourceLabeling(perRow, common, scopeTotals);
    }

    // 未截断时展示切片就是全集，Of(rowSources) 的老口径本来就对总数为真——故只在截断时
    // 把全集构成交给表头。
    public static SourceLabeling Of<T>(ScopedResult<T> result)
        => Of(
            result.Items.Select(e => e.SourceName),
            result.Items.Count < result.TotalInScope ? result.SourcesInScope : null);

    public string Header
    {
        get
        {
            if (_scopeTotals is { Count: > 0 })
                return _scopeTotals.Count == 1
                    ? $" [{_scopeTotals[0].Source}]"
                    : $" [{string.Join(", ", _scopeTotals.Select(t => $"{t.Source} {t.Count}"))}]";

            return _common == null ? string.Empty : $" [{_common}]";
        }
    }

    public string Row(string? sourceName) => _perRow ? Label(sourceName) : string.Empty;
}
