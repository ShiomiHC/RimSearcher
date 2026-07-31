namespace RimSearcher.Search;

/// <summary>
/// 「这段文本长得像不像一个 C# 类名」。
///
/// 它决定的是**要不要说一句猜测**,而落空的猜测会被读者当结论用,所以判据从严 ——
/// 拿不准就一个字都不说。判据只有两条,别的都是它俩的推论。
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
        // PascalCase 的类名几乎全是复合词,而单词的 defName 几乎全不是
        // (`True` / `Steel` 落在这一条上)。代价是漏掉 `Bullet` 这种单词类名。
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
