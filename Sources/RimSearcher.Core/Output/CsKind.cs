namespace RimSearcher.Output;

public static class CsKind
{
    private static readonly Dictionary<string, string> Text = new(StringComparer.Ordinal)
    {
        ["method"] = "方法",
        ["constructor"] = "构造函数",
        ["property"] = "属性",
        ["field"] = "字段",
        ["event"] = "事件",
        ["indexer"] = "索引器",
        ["operator"] = "运算符",
        ["destructor"] = "析构函数",
        ["enum-member"] = "枚举成员",
        ["class"] = "类",
        ["struct"] = "结构",
        ["interface"] = "接口",
        ["record"] = "记录",
        ["enum"] = "枚举",
        ["delegate"] = "委托",
        ["extension"] = "扩展块",
        ["namespace"] = "命名空间",
    };

    private static readonly Dictionary<string, string> Back =
        Text.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public static string Of(string kind) => Text.TryGetValue(kind, out var t) ? t : kind;

    public static string? Parse(string text) => Back.TryGetValue(text, out var k) ? k : null;
}
