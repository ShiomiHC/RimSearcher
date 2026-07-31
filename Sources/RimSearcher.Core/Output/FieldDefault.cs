using RimSearcher.Contract;

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
}
