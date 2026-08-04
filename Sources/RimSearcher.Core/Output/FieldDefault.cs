using RimSearcher.Contract;
using RimSearcher.Search;

namespace RimSearcher.Output;

/// <summary>
/// 「这一行是不是 C# 声明默认值」在输出里的措辞 —— 产地唯一。
///
/// R1 的四个错结论全部来自同一件事:作者写的值、C# 字段声明的初始值、ResolveReferences
/// 填的兜底值,在字段表里长成一模一样的行。所以这一列的取值必须**读一眼就明白**,
/// 而不是靠读者去查文档:三个词各自说的就是它自己的意思。
///
/// 列名叫 <see cref="Column"/> 而不是 <c>origin</c>:origin 已经是译文表的列名(且语义不同),
/// 同名不同义的列会让照着一次输出写解析器的人在下一张表上拿到别的东西。
/// </summary>
public static class FieldDefault
{
    public const string Column = "code_default";

    /// <summary>
    /// <c>no</c> = 与新 new 的值不同,所以**一定**有人设过它(XML / 补丁 / ResolveReferences);
    /// <c>yes</c> = 与新 new 的值一模一样。注意这**不等于**「没人设过它」—— XML 里照着默认值
    /// 再写一遍是常事,而两种情形在快照里完全同形;能证的只有「无从区分」这一句;
    /// <c>unknown</c> = 那个类型 new 不出来,没比成 —— 不许并进上面任何一边。
    /// </summary>
    public static string Render(int state) => state switch
    {
        DefaultState.Same => "yes",
        DefaultState.Unknown => "unknown",
        _ => "no",
    };

    /// <summary>构造函数惯常自己填的那几个字段 —— 那里的 yes 连「异常」都算不上。</summary>
    private static readonly string[] ConstructorAssigned = ["compClass", "thingClass", "workerClass"];

    /// <summary>
    /// `yes` 行在场时,这一列的**双向**释义。
    ///
    /// R10 的 C1 是八条契约里唯一**两组都答错**的一条,而且是在 SKILL.md 写着这条规则的
    /// 情况下答错的:当时的措辞只堵一个方向(「别把 yes 读成『这个 def 设了 X』」),
    /// 两组栽的都是反方向 ——「所以不是 def 设的,是 C# 构造函数的默认值」。
    /// **只堵半边的规则写在哪条信道上都不管用**,所以这段话双向,且跟着那张表走。
    ///
    /// 不是恒定横幅:`yes` 行默认就不列,只有 `--defaults` 或 `--path-contains` 指名字段
    /// 时才进表 —— 与 <c>Completeness.NoteIndexedPathsOnly</c> 同一条位置判据。
    ///
    /// <returns>表里一条 `yes` 都没有时 <c>null</c>。</returns>
    /// </summary>
    public static string? Legend(IEnumerable<(string Path, int State)> rows)
    {
        var listed = rows.ToList();
        if (!listed.Any(r => r.State == DefaultState.Same)) return null;

        // 「C# 构造函数里是什么」是模型看完 yes 之后的下一步动作,而那一步**回答不了**
        // 本问题 —— C1 两组栽的正是这一步。所以出路与陷阱写在同一句里。
        var text = "A `yes` in " + Column + " means the value is identical to what a freshly constructed " +
                   "instance carries, and this snapshot cannot tell those two apart: it supports neither " +
                   "'the def sets this' nor 'the def leaves it to the class default'. Reading the C# " +
                   "constructor shows where a default could come from, never whether the XML repeats it. " +
                   "Only `no` decides: something set that value.";

        if (listed.Any(r => r.State == DefaultState.Unknown))
            text += " `unknown` is not a third answer: the type could not be constructed, so nothing was compared.";

        // 消费侧提过「第三方以 Class= 挂上去的会是 no」—— 恰恰相反,而 vanilla 自己就有
        // 一千多条 no。两种取值在这几个字段上同时是常态,所以这半句两边都说。
        if (listed.Any(r => r.State == DefaultState.Same &&
                            PathSegments.IsWholeSegment(r.Path, ConstructorAssigned)))
            text += " On " + string.Join("/", ConstructorAssigned) + " both values are ordinary — those are " +
                    "usually constructor-assigned — so neither one says who mounted the comp. The `mod` " +
                    "column and the block's `Class` row do.";

        return text;
    }
}
