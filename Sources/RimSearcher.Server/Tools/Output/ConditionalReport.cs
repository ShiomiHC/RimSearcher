using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

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
    //
    // 反面那半句要写进**契约**而不是只写进脚注：脚注只在这一份返回里真有标记时才印
    // （见 Render），于是 inspect 一条不在条件目录里的 def、整份返回一个字都不提条件目录，
    // 「没标记」到底是「查过了、不在」还是「这个工具压根不查」就无从分辨。写进契约是最省的
    // 一处——它对每一次调用都成立，不必为此在每份返回里挂一句常亮的「本文件无条件」。
    public const string Contract =
        "Mod folders that loadFolders.xml loads only under a condition are indexed whatever is installed, "
        + "and results from inside one carry a `[conditional: <folder>]` tag naming that condition; "
        + "every result is checked, so one without the tag is not inside such a folder. "
        // 这句原先到上一行为止，而 OfAll 把它说绝了（己-3）：一个散在多份文件里的符号，只有
        // **每一处**声明都落在条件目录里才打标。代码是有意的（有一处是无条件的，那这个符号在
        // 任何实机上都在，打标反而是假警报），错的是契约漏了这半句——照上一句读，一个半数声明
        // 在条件目录里的类型会被当成「查过了、不在」。
        + "A symbol declared in several files is tagged only when every one of them is inside such a folder: "
        + "one unconditional declaration means the symbol is present on any install. "
        + "Nothing here evaluates the condition.";

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
             + "condition, so a result from inside one is not evidence that it takes effect at runtime; "
             // 单向说完就停的话，「这条 def 到底在不在我的游戏里」这个正面问题在整份返回里
             // 没有一句话回答得了——要答出来得自己接上 loadFolders 的语义。接不上的调用方
             // 会把一道答得出的题答成「这套工具判不出来」。补上反方向，读者拿自己的 mod 列表
             // 就能收口：条件成立与否，答案分别是什么。
             + "when the condition does not hold the folder is not loaded at all and its contents are "
             + "absent from the game. "
             // 记号在场时最容易被读成「门就这一道」。目录条件与文件自己的门是两层，第十轮
             // 盲测里那条链差点据此断言「装了 CE 就会换成这个弹丸」——真正的门是同一份补丁里
             // 一条按显示名写的 PatchOperationFindMod，与目录条件是两回事。
             + "The tag names the folder-level condition only: a patch or def inside may carry its own gate "
             + "(PatchOperationFindMod, MayRequire) that this index does not report. "
             // 否定式放在最末、还要读者逆推一步。倒过来让作用对象在前，并把「查过了」明说
             // 出来——这句唯一要防的误读就是把「没标记」读成「没查过」。
             + "Rows without that tag were checked and are not inside such a folder._";
    }

    // 单目标那一形（read_code 读一个文件、inspect 讲一条 def）。整份返回只落在一个文件上时
    // 没有「哪些行带标记」的问题，故键与成因合在一句里说完，不再分行内 + 脚注两处。
    // 不带前缀也不带句末标点：read_code 要把它包成 `// note: …`，inspect 要包成 `_Note: …._`，
    // 两边的外壳是各自版面的既有惯例，不该由这里替它们决定。
    // 双向与边界两句在这里同样要有：单目标形是**唯一**一处脚注不在场的地方，缺了这两句，
    // read_code 一个条件目录里的补丁文件就只剩「不算生效证据」这半句可用。
    public static string? Explain(ConditionalArea? area)
        => area == null
            ? null
            : $"[conditional: {area.Folder}] loadFolders.xml loads this folder only with {area.Condition}; "
              + "the condition is not evaluated here, so this is not evidence that it takes effect at "
              + "runtime, and when it does not hold this folder is not loaded at all. That is the "
              + "folder-level condition only — contents inside may carry their own gate "
              + "(PatchOperationFindMod, MayRequire), which is not reported here";
}
