namespace RimSearcher.Output;

public readonly record struct Tally
{
    private Tally(int shown, int? total, bool lowerBound)
    {
        Shown = shown; Total = total; LowerBound = lowerBound;
    }

    public int Shown { get; }
    public int? Total { get; }
    public bool LowerBound { get; }

    public static Tally Complete(int shown) => new(shown, shown, false);

    public static Tally Of(int shown, int total) => new(shown, total, false);

    public static Tally AtLeast(int shown) => new(shown, null, true);

    public bool IsTruncated => LowerBound || (Total is { } t && t > Shown);

    public string Render(string noun, string scope = "", string aside = "")
    {
        if (LowerBound) return Cjk.Join(scope, "至少", NounRegistry.Count(noun, Shown), aside);
        var lead = scope.Length == 0 ? "" : Cjk.Join(scope, "的");
        if (Total is { } t && t > Shown)
            return Cjk.Join(lead, NounRegistry.Count(noun, t), aside, "中的", Shown.ToString(), NounRegistry.Measure(noun));
        return Cjk.Join(lead, NounRegistry.Count(noun, Shown), aside);
    }

    public string RenderTotalFirst(string noun, bool fromStart = true, string scope = "", string aside = "")
    {
        if (LowerBound) return Cjk.Join(scope, "至少", NounRegistry.Count(noun, Shown), aside);
        if (Total is { } t && t > Shown)
            return Cjk.Join(scope, "共", NounRegistry.Count(noun, t), aside,
                            fromStart ? "，列出前" : "，列出", Shown.ToString(), NounRegistry.Measure(noun));
        return Render(noun, scope, aside);
    }
}

public static class NounRegistry
{
    private readonly record struct Entry(string Noun, string Measure);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal)
    {
        ["def"] = new("def", "个"),
        ["def added"] = new("新增的 def", "个"),
        ["def removed"] = new("删除的 def", "个"),
        ["def type"] = new("def 类型", "个"),
        ["def class"] = new("Class", "个"),
        ["field"] = new("字段", "个"),
        ["field path"] = new("字段路径", "条"),
        ["empty element"] = new("空元素", "个"),
        ["path shape"] = new("路径形状", "种"),
        ["value"] = new("值", "个"),
        ["match"] = new("命中", "处"),
        ["file"] = new("文件", "个"),
        ["C# file"] = new("C# 文件", "个"),
        ["mod"] = new("mod", "个"),
        ["mod list"] = new("mod 名单", "份"),
        ["saved mod list"] = new("已保存的 mod 名单", "份"),
        ["translation"] = new("译文", "条"),
        ["keyed translation"] = new("Keyed 译文", "条"),
        ["matching keyed translation"] = new("匹配的 Keyed 译文", "条"),
        ["DefInjected key"] = new("DefInjected 键", "个"),
        ["row"] = new("行", "行"),
        ["key"] = new("Keyed 键", "个"),
        ["translation key"] = new("代码中的翻译键", "个"),
        ["source tree"] = new("源码树", "棵"),
        ["XML node"] = new("XML 节点", "个"),
        ["direct child"] = new("直接子节点", "个"),
        ["ancestor"] = new("祖先", "层"),
        ["assembly"] = new("程序集", "个"),
        ["type"] = new("类型", "个"),
        ["namespace"] = new("命名空间", "个"),
        ["derived type"] = new("派生类型", "个"),
        ["member"] = new("成员", "个"),
        ["caller"] = new("调用方", "个"),
        ["callee"] = new("被调用方", "个"),
        ["call edge"] = new("调用边", "条"),
        ["usage"] = new("使用处", "处"),
        ["access edge"] = new("访问边", "条"),
        ["directory"] = new("目录", "个"),
        ["name"] = new("名字", "个"),
        ["path and def-type pair"] = new("路径与 def 类型的组合", "个"),
        ["line"] = new("行", "行"),
        ["printed line"] = new("输出行", "行"),
        ["declaration"] = new("声明", "处"),
        ["thing"] = new("thing", "个"),
        ["recipe"] = new("recipe", "个"),
        ["nested object"] = new("嵌套对象", "个"),
        ["list"] = new("列表", "个"),
        ["patch xpath"] = new("patch xpath", "条"),
        ["generation"] = new("世代", "个"),
    };

    public static IReadOnlyCollection<string> Known => Entries.Keys;

    public static bool IsRegistered(string noun) => Entries.ContainsKey(noun);

    public static string Noun(string noun) => Get(noun).Noun;

    public static string Measure(string noun) => Get(noun).Measure;

    public static string Phrase(string noun)
    {
        var e = Get(noun);
        if (e.Noun == e.Measure) return e.Measure;
        if (e.Noun.Length > 1 && e.Noun.EndsWith(e.Measure, StringComparison.Ordinal))
            return Cjk.Join(e.Measure, e.Noun[..^e.Measure.Length]);
        return Cjk.Join(e.Measure, e.Noun);
    }

    public static string Count(string noun, int count) => Cjk.Join(count.ToString(), Phrase(noun));

    private static Entry Get(string noun)
        => Entries.TryGetValue(noun, out var e)
            ? e
            : throw new InvalidOperationException(
                $"计数名词 '{noun}' 未在 NounRegistry 登记。须先登记，不在调用点自行计数。");
}
