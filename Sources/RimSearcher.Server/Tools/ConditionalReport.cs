using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

// 一次返回里出现过的条件加载目录。
//
// 分工与 ScopeReport 完全同型，理由也一样（R19）：**行内只放键，成因整份说一次**。
// 键是 `[conditional: 1.6/CE]`，成因是「loadFolders.xml 只在 CE 启用时加载这个目录」——
// 后者一句四十来字，挂在每一行上就是把同一句话说五十遍；只挂键又会让读者拿着键没处兑换。
// 两者共用 ConditionalArea.Folder 这一个字符串，那条线才指认得上（F33 规则甲）。
//
// 判据里最要紧的一条是**反面**：没打标不等于没查过。故 Render 的最后一句把「没标记 = 不在
// 条件目录里」明说出来——不说的话这个记号只能单向使用（看见了才有意义），而调用方要做的
// 判断恰恰是「我手上这条到底受不受影响」。
public sealed class ConditionalReport
{
    private readonly ConditionalFolders _folders;
    private readonly Dictionary<string, ConditionalArea> _seen = new(StringComparer.Ordinal);

    public ConditionalReport(ConditionalFolders? folders) => _folders = folders ?? ConditionalFolders.None;

    // tools/list 里那句常驻契约。六个工具逐字共用一份：措辞散开写的话，同一个记号在每个工具上
    // 都要重学一遍——第九轮的 `at least` 就是这么在两个工具上各错一次的。
    public const string Contract =
        "Mod folders that loadFolders.xml loads only under a condition are indexed whatever is installed, "
        + "and results from inside one carry a `[conditional: <folder>]` tag naming that condition; "
        + "nothing here evaluates it.";

    // 一条路径的行内标记。落在条件目录外（或者根本没有条件目录）时返回空串。
    public string Tag(string? path) => Mark(_folders.Of(path));

    // 一个符号散在多份文件里时的行内标记：全部落在条件目录里才算（见 ConditionalFolders.OfAll）。
    public string TagAll(IEnumerable<string>? paths) => Mark(_folders.OfAll(paths));

    public ConditionalArea? Area(string? path) => _folders.Of(path);

    private string Mark(ConditionalArea? area)
    {
        if (area == null) return string.Empty;
        _seen[area.Key] = area;
        return $" [conditional: {area.Folder}]";
    }

    // 整份返回末尾那条脚注。一处都没打标就返回 null——没发生的事不说（同 R9）。
    public string? Render()
    {
        if (_seen.Count == 0) return null;

        var listed = string.Join("; ", _seen.Values
            .OrderBy(a => a.Folder, StringComparer.Ordinal)
            .ThenBy(a => a.Source, StringComparer.Ordinal)
            .Select(a => a.Describe()));

        return "\n\n_`[conditional: …]` marks a mod folder that loadFolders.xml loads only under a condition: "
             + $"{listed}. This index reads those folders whatever is installed and never evaluates the "
             + "condition, so a result from inside one is not evidence that it takes effect at runtime. "
             + "Untagged results are not inside such a folder._";
    }

    // 单目标那一形（read_code 读一个文件、inspect 讲一条 def）。整份返回只落在一个文件上时
    // 没有「哪些行带标记」的问题，故键与成因合在一句里说完，不再分行内 + 脚注两处。
    // 不带前缀也不带句末标点：read_code 要把它包成 `// note: …`，inspect 要包成 `_Note: …._`，
    // 两边的外壳是各自版面的既有惯例，不该由这里替它们决定。
    public static string? Explain(ConditionalArea? area)
        => area == null
            ? null
            : $"[conditional: {area.Folder}] loadFolders.xml loads this folder only with {area.Condition}; "
              + "the condition is not evaluated here, so this is not evidence that it takes effect at runtime";
}
