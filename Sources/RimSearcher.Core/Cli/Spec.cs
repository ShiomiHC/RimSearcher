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
    /// 有意接受的别名(07-② 实证:一个意图被真实调用方拼出 9 种写法)。
    /// 大小写与 <c>-</c>/<c>_</c> 差异由解析器归一化统一吃掉,**不需要**列进这里;
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
}

/// <summary>位置参数声明。</summary>
public sealed record PositionalSpec
{
    public required string Name { get; init; }
    public required string Help { get; init; }
    public bool Required { get; init; } = true;
    /// <summary>吞掉其余全部位置参数。</summary>
    public bool Variadic { get; init; }
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

    /// <summary>用法示例,每条一行。盲测实证 07-④:旧习惯迁移靠示例带,比散文有效。</summary>
    public string[] Examples { get; init; } = [];

    /// <summary>是否吃全局参数(--snapshot/--db/--json 等)。维护型命令可以关掉。</summary>
    public bool UsesGlobals { get; init; } = true;
}
