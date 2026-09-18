namespace RimSearcher.Cli;

public enum Arity
{
    /// <summary>无值开关。</summary>
    Flag,
    /// <summary>取一个值,重复出现后者覆盖前者。</summary>
    Single,
    /// <summary>取一个值,可重复,累积成列表。</summary>
    Multi,
}

/// <summary>
/// 一个参数的完整声明。这是该参数在全系统里的唯一产地:--help 的一行、cli-reference.md
/// 的一行、解析器认哪些拼法、报错时的候选池,全部读这一份。
/// </summary>
public sealed record OptionSpec
{
    public required string Name { get; init; }
    public char? Short { get; init; }

    /// <summary>
    /// 有意接受的别名。大小写与 <c>-</c>/<c>_</c> 差异由解析器归一化吃掉,**不需要**列进这里;
    /// 这里只列同义词(fileGlob / glob / pathFilter 之类换词不换意的写法)。
    /// </summary>
    public string[] Aliases { get; init; } = [];

    public Arity Arity { get; init; } = Arity.Single;

    /// <summary>取值占位符,如 <c>&lt;text&gt;</c>。Flag 无。</summary>
    public string? Placeholder { get; init; }

    /// <summary>散文说明。数字一律从 <see cref="Limits"/> 插值。</summary>
    public required string Help { get; init; }

    /// <summary>默认值的展示文本(不参与解析)。</summary>
    public string? Default { get; init; }

    public bool Required { get; init; }

    /// <summary>取值的枚举集合;非空时解析器校验并在报错里回传候选。</summary>
    public string[] Choices { get; init; } = [];

    /// <summary>
    /// 这个参数一给,结果集就**变小** —— 计数句要把它念回去,否则完整式计数
    /// 会被读成「一个不漏」,而实际是「在用户自己划的范围内完整」。
    /// <see cref="Output.Tally"/> 的三态只覆盖工具造成的收窄(行数上限、扫描没跑完)。
    ///
    /// 只标**过滤**性质的参数。<c>--limit</c> / <c>--offset</c> 不标:三态文法已经说清了。
    /// </summary>
    public bool Narrows { get; init; }
}

/// <summary>位置参数声明。</summary>
public sealed record PositionalSpec
{
    public required string Name { get; init; }
    public required string Help { get; init; }
    public bool Required { get; init; } = true;
    /// <summary>吞掉其余全部位置参数。</summary>
    public bool Variadic { get; init; }
    /// <summary>
    /// 同一格的选项拼法:填这个名字的选项就是给这一格。`where --field X --value Y` 与 `where X Y`
    /// 是同一条命令。解析后落进 <c>Positionals</c> 同一格,命令侧不用两头读。
    /// </summary>
    public string? Option { get; init; }
}

/// <summary>
/// <c>--json</c> 输出里一个顶层数据键的声明。
///
/// 键名必须进声明层,与参数走同一条产地:否则消费方要先猜键名,而猜错拿到的 null/空
/// 与「查到了但确实没有」同形 —— 这套输出里「错的与对的同形」是不许留的形状。
/// </summary>
public sealed record JsonKeySpec
{
    /// <summary>顶层键名。</summary>
    public required string Key { get; init; }

    /// <summary>这个键装着什么:一句话说清它是数组还是对象、每一项是什么。</summary>
    public required string What { get; init; }

    /// <summary>
    /// 这个键是**行数组**,而且这条命令一跑就该有它 —— 于是零行时它是 <c>[]</c>,不是整个消失。
    ///
    /// 认领由 <see cref="Runner"/> 读这个标记、在开查之前统一发,**不**交给每条命令
    /// 自己在 Run 里调 <see cref="Output.Report.Promises"/>:漏掉一条的表现是那个键不在,
    /// 与「查过了确实没有」同形。
    ///
    /// **只在某个开关下才产出的键不标**(<c>where --value</c> 的 paths、<c>read --outline</c>
    /// 的 declarations、<c>sources sync --dry-run</c> 的 plan):它们互斥,凭空多一个空数组
    /// 在机器侧读作「这一路也查过了,没有」。那几条由命令在自己那条分支上认领。
    /// 「an object: …」那类键同理不标:空数组不是它们的空形状。
    /// </summary>
    public bool Rows { get; init; }
}

/// <summary>一条命令(或子命令)的完整声明。</summary>
public sealed record CommandSpec
{
    /// <summary>命令名。子命令写成 <c>"snapshot import"</c> 这样的两段式。</summary>
    public required string Name { get; init; }

    public string[] Aliases { get; init; } = [];

    /// <summary>一句话摘要,进命令总表。</summary>
    public required string Summary { get; init; }

    /// <summary>展开说明,进 <c>--help</c> 与 markdown 的正文段落。</summary>
    public string? Remarks { get; init; }

    public PositionalSpec[] Positionals { get; init; } = [];
    public OptionSpec[] Options { get; init; } = [];

    /// <summary>用法示例,每条一行。</summary>
    public string[] Examples { get; init; } = [];

    /// <summary>
    /// <c>--json</c> 下这条命令可能产出的顶层数据键。<c>notes</c> 是全局的,不在这里列。
    /// 有闸对着实测输出验:实际出现过而没声明的键会红。
    /// </summary>
    public JsonKeySpec[] JsonKeys { get; init; } = [];

    /// <summary>是否吃全局参数(--snapshot/--db/--json 等)。维护型命令可以关掉。</summary>
    public bool UsesGlobals { get; init; } = true;

    /// <summary>
    /// 这条命令**有意不收**、而且说得出为什么的拼法。它是声明层的一部分:进 <c>--help</c> 与
    /// cli-reference,解析器撞上时印它的那句话而不是近似候选。
    ///
    /// 只放**边界**上的词 —— 那件事住在别的命令里、而读者在这条命令的框里伸手去拿的
    /// (<c>read --grep</c>:按模式找行是 code-search 的事)。拼错的词不进来,那是近似候选的活。
    /// </summary>
    public RefusedSpec[] Refused { get; init; } = [];
}

/// <summary>一个有意不收的拼法,以及那件事住在哪(见 <see cref="CommandSpec.Refused"/>)。</summary>
public sealed record RefusedSpec
{
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    /// <summary>
    /// 那句话。写成能粘的整条命令;里面的 <c>&lt;第一个位置参数名&gt;</c> 与
    /// <see cref="ValuePlaceholder"/> 由解析器在读者已经写了那个词时替换成他写的。
    /// </summary>
    public required string Where { get; init; }
    /// <summary>读者跟在这个拼法后面给的值,该填进 <see cref="Where"/> 的哪个占位符。</summary>
    public string? ValuePlaceholder { get; init; }
}
