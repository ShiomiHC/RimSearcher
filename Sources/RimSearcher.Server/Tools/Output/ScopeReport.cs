using System.Text;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 跨段累加落在 scope 之外的命中，最后汇成一行提示。
// 防的是「按默认 scope 搜不到 → 断言该符号不存在」这类错误结论。
public sealed class ScopeReport
{
    private readonly Dictionary<string, int> _outOfScope = new(StringComparer.OrdinalIgnoreCase);

    // 合计是由哪些段凑出来的。locate 的这份脚注跨五段累加，而**哪几段参与**跟着这次查询的
    // 命中形态变：`method:CompTick` 在 scope 'base' 下只有 Members 段，报 miho 7；同一条查询
    // 在 scope 'HAR' 下 Members 段空了、触发 Files 段模糊查名，同一个 miho 就报 8。
    // 调用方看到同一个源两次计数不一致，只能对整份脚注打折使用——R48 花力气建起来的合计
    // 就此变成一个不可复现的数。构成一列出来，两个数立刻都对得上。
    private readonly Dictionary<CountedNoun, int> _byNoun = [];

    public void Add<T>(ScopedResult<T> result, CountedNoun? noun = null)
    {
        foreach (var (source, count) in result.OutOfScope)
        {
            _outOfScope[source] = _outOfScope.GetValueOrDefault(source) + count;
            if (noun != null) _byNoun[noun] = _byNoun.GetValueOrDefault(noun) + count;
        }
    }

    public void Add(string sourceName, int count)
    {
        if (count <= 0) return;
        _outOfScope[sourceName] = _outOfScope.GetValueOrDefault(sourceName) + count;
    }

    public bool HasOutOfScope => _outOfScope.Count > 0;

    // noun：合计的名词槽。locate 的这份脚注跨四段累加（类型 / 成员 / def / 内容命中），
    // 只有 "matches" 说得准；trace inheritors 那边全是子类，故由调用方点名。
    // extra：调用方独有的、只有把落选那批算进来才成立的一句话（trace inheritors 用它给出
    // 全域树的形状）。挂在逐源列表之后、出路那句之前。
    public string? Render(ScopeSelection scope, CountedNoun? noun = null, string? extra = null)
    {
        if (_outOfScope.Count == 0) return null;

        var parts = _outOfScope
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} {kv.Value}");

        // 多源时先给合计。同一份返回里 scope **内**的量在表头是加总好的（`144 members`），
        // 这一行句式并列却只给分项，读者得临时切换成心算——整份输出里唯一一处要做算术的地方，
        // 且紧挨着一个不必做算术的同型数字。盲测里 7 个分项被加成 41（真值 47）。
        // 单源时不加：那时合计逐字等于那一个数（同「推得出来就不印」）。
        // 只有一段参与时就用那一段自己的名词。泛称 "matches" 是为跨段累加准备的，而单段时
        // 它凭空造出第二个计数词：正文写着 `4 files`、脚注紧跟着写 `3 matches`，同一屏两个
        // 名词指的是同一类东西，读者得先确认它们不是两个量。
        var summaryNoun = _byNoun.Count == 1 ? _byNoun.Keys.First() : noun ?? CountedNoun.Matches;
        var total = _outOfScope.Count > 1
            ? $"{summaryNoun.Quantity(_outOfScope.Values.Sum())}{Composition()} — "
            : string.Empty;

        var sb = new StringBuilder();
        sb.Append($"\n_Outside scope '{scope.Expression}': {total}");
        sb.Append(string.Join(", ", parts));
        if (extra != null) sb.Append($"; {extra}");
        sb.Append(". Pass scope to include them (e.g. scope:'all')._");
        return sb.ToString();
    }

    // 只有一种构成时不印：那时它逐字等于前面那个合计（同「推得出来就不印」）。
    // 次序按数量降序，与后面的逐源列表同序。
    private string Composition()
    {
        if (_byNoun.Count < 2) return string.Empty;

        var parts = _byNoun
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Plural, StringComparer.Ordinal)
            .Select(kv => kv.Key.Quantity(kv.Value));

        return $" ({string.Join(" + ", parts)})";
    }
}
