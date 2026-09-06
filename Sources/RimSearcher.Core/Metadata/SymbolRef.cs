namespace RimSearcher.Metadata;

/// <summary>
/// 调用方写的那个符号名,拆成「类型那截」与「成员那截」。
///
/// 收哪些形态不是设计出来的,是 Vethara 那 4386 次外包调用里实际出现过的:文档注释式
/// (<c>M:Verse.Thing.DoTick</c>)、带参数表的签名式(<c>Verse.FreezeManager.DoWaterFreezing(Verse.IntVec3)</c>)、
/// IL 风格的双冒号,以及嵌套类型的 <c>/</c> 与 <c>.</c> 两种分隔符 —— 同一次调查里
/// <c>CompProperties_Serum/&lt;SpecialDisplayStats&gt;d__1.MoveNext</c> 与它的点号版各试了一次,
/// 那是调用方在试语法。构造函数的 <c>..ctor</c> 与 <c>.#ctor</c> 同理,两种都被写过。
/// </summary>
public sealed record SymbolRef
{
    /// <summary>类型那截。可能带 namespace,可能只是个裸类型名,也可能为空(只写了成员名)。</summary>
    public required string TypeName { get; init; }

    /// <summary>成员那截。为空表示这次问的是一个类型。</summary>
    public string? MemberName { get; init; }

    /// <summary>参数表里写的东西,原样留着。<c>null</c> = 没写括号,不据此挑重载。</summary>
    public string? Parameters { get; init; }

    /// <summary>调用方原样写的那个串。落空时的措辞要贴回它,不是贴回解析结果。</summary>
    public required string Raw { get; init; }

    public bool HasMember => MemberName is { Length: > 0 };

    /// <summary>IL 里构造函数叫 <c>.ctor</c>;调用方写的是 <c>.ctor</c> / <c>#ctor</c> / <c>ctor</c>。</summary>
    public static bool IsConstructorName(string name)
        => name is ".ctor" or "#ctor" or "ctor" or ".cctor" or "#cctor";

    public static SymbolRef Parse(string raw)
    {
        var s = raw.Trim();

        // 文档注释 ID 的种类前缀。M: 方法、T: 类型、P: 属性、F: 字段、E: 事件。
        if (s.Length > 2 && s[1] == ':' && "MTPFEN".Contains(char.ToUpperInvariant(s[0])))
            s = s[2..];

        string? parameters = null;
        var paren = s.IndexOf('(');
        if (paren >= 0)
        {
            var close = s.LastIndexOf(')');
            parameters = close > paren ? s[(paren + 1)..close] : s[(paren + 1)..];
            s = s[..paren];
        }

        s = s.Trim();

        // 双冒号是 IL 的写法,它把类型与成员分得毫不含糊 —— 有它就不必猜最后一个点在哪。
        var dc = s.IndexOf("::", StringComparison.Ordinal);
        if (dc >= 0)
            return new SymbolRef
            {
                TypeName = Normalize(s[..dc]),
                MemberName = EmptyToNull(s[(dc + 2)..]),
                Parameters = parameters,
                Raw = raw,
            };

        // 构造函数:`Verse.ThingDef..ctor` / `Verse.ThingDef.#ctor`。先摘掉它,
        // 否则下面按最后一个点切会把 `.ctor` 切成类型名的一部分。
        foreach (var suffix in new[] { "..ctor", ".#ctor", "..cctor", ".#cctor" })
            if (s.EndsWith(suffix, StringComparison.Ordinal))
                return new SymbolRef
                {
                    TypeName = Normalize(s[..^suffix.Length]),
                    MemberName = suffix.Contains("cctor") ? ".cctor" : ".ctor",
                    Parameters = parameters,
                    Raw = raw,
                };

        var lastDot = s.LastIndexOf('.');
        if (lastDot < 0)
            // 一个词。它既可能是类型也可能是成员 —— 这里当类型,查不到时由调用侧改当成员再试一次。
            return new SymbolRef { TypeName = Normalize(s), Parameters = parameters, Raw = raw };

        var head = s[..lastDot];
        var tail = s[(lastDot + 1)..];

        // 尾巴以大写开头、且前面那截的末段也以大写开头时,两种读法都通(嵌套类型 vs 成员)。
        // 不在这里猜:两截都留着,由查找那一侧按元数据里的实情决定,查不到才轮到另一种读法。
        return new SymbolRef
        {
            TypeName = Normalize(head),
            MemberName = EmptyToNull(tail),
            Parameters = parameters,
            Raw = raw,
        };
    }

    /// <summary>嵌套类型的两种分隔符统一成元数据侧的那种;泛型的反引号后缀留着。</summary>
    private static string Normalize(string s) => s.Replace('/', '+').Trim();

    private static string? EmptyToNull(string s) => s.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>
    /// 整串当成一个类型名再读一次 —— <c>A.B.C</c> 在上面被切成了类型 <c>A.B</c> 加成员 <c>C</c>,
    /// 而它也可能本来就是个三段的类型全名。
    /// </summary>
    public SymbolRef AsWholeType()
    {
        if (!HasMember) return this;
        return new SymbolRef { TypeName = $"{TypeName}.{MemberName}", Parameters = Parameters, Raw = Raw };
    }

    /// <summary>
    /// 反过来:整串当成一个裸成员名 —— 只写了一个词时的另一种读法。
    /// </summary>
    public SymbolRef AsBareMember()
        => new() { TypeName = "", MemberName = HasMember ? MemberName : TypeName, Parameters = Parameters, Raw = Raw };
}
