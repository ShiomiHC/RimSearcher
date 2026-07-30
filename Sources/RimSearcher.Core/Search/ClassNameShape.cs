namespace RimSearcher.Search;

/// <summary>
/// 「这段文本长得像不像一个 C# 类名」。
///
/// 它决定的是**要不要说一句猜测**,而猜错的代价在第五轮盲测里量过:落空时把未经验证的
/// 「如果 X 是抽象基类……」摆在输出位置,读的人会当结论用。所以判据从严 —— 拿不准就
/// 一个字都不说,而不是说一句可能对的。
///
/// 三处旧判据各写各的,而且各自都会误判:
///   · <c>value.Contains('.')</c> —— <c>.ogg</c>、<c>1.5</c> 全中
///   · <c>char.IsUpper(v[0])</c> —— <c>True</c>、<c>False</c> 全中
/// 这两条正是共识里点名不许复用的那一个 <c>looksLikeType</c>。归到这里一处,判据写在
/// 注释里能被人反驳,而不是散在三个调用点上各自演化。
///
/// 判据只有两条,别的都是它俩的推论 —— 写第三条只会写出一段永远跑不到的代码,
/// 而跑不到的代码带着一句确信的注释比没有更坏(空白字符、`/`、`True` 都曾各占一条)。
/// </summary>
public static class ClassNameShape
{
    public static bool Looks(string? value)
    {
        // 两个字符的全大写缩写(`AB`)在下面两条里是合法的,但它不值得为之猜一句。
        if (value is not { Length: > 2 }) return false;

        // 一、按 `.` 切开之后,每一段都得是首字母大写的标识符。
        //   `.ogg`          第一段是空的
        //   `Foo.ogg`       第二段小写开头
        //   `1.5` / `12`    数字开头
        //   `Sounds/Impact` 段里有 `/`,不是标识符字符 —— 资源路径在值里比类名还常见
        //   `Comp Shield`   同理,空白也不是标识符字符
        var segments = value.Split('.');
        foreach (var s in segments)
        {
            if (s.Length == 0 || !char.IsUpper(s[0])) return false;
            if (!s.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
        }

        // 二、不带命名空间的单段还要求段内**再有一个**大写字母或下划线,也就是至少两个词。
        // PascalCase 的类名几乎全是复合词,而单词的 defName 几乎全不是 ——
        // `True` / `False` / `Never` 这些字面量就落在这一条上,`Steel` `Meat` 同理。
        // 代价是漏掉 `Bullet` 这种单词类名:少说一句猜测,比多说一句错猜便宜。
        if (segments.Length == 1)
            return segments[0].Skip(1).Any(c => char.IsUpper(c) || c == '_');

        return true;
    }

    /// <summary>去掉命名空间,只留最后一段 —— <c>code-search</c> 要搜的是这一段。</summary>
    public static string Tail(string value)
    {
        var i = value.LastIndexOf('.');
        return i < 0 ? value : value[(i + 1)..];
    }
}
