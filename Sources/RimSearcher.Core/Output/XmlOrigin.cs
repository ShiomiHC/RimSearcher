namespace RimSearcher.Output;

/// <summary>
/// get 字段表上「这一行 XML 写没写」那一列 —— 产地唯一。
///
/// <c>here</c> = 这个 def 自己的 XML 写了这条路径,Replace 找得到节点;
/// <c>parent</c> = 只有祖先写了,指向这个 def 的 xpath 上这条节点不在,Add 才找得到;
/// <c>no</c> = 本节点和祖先都没写;
/// <c>under X</c> = XML 在容器 X 下写过东西,但两边路径写法对不上,这一项对不到。
/// 缺层时这一列根本不出现,由能力位那句通知说,不许印成 <c>no</c>。
///
/// <c>under</c> 这一档不是保守起见:XML 拿 defName 当标签名(<c>costList.Steel</c>、
/// <c>statBases.MarketValue</c>),索引按列表下标(<c>costList[0].thingDef</c>),
/// 同一件事两种写法。缺了这一档,join 落空就印 no,而 no 的出路是 Add ——
/// 节点其实在,Add 插出第二份。baseline 快照上 ThingDef 的 6086 条路径有 4880 条带下标,
/// 仅 statBases 一项就是 44 条路径 / 1967 个 def。
/// </summary>
public static class XmlOrigin
{
    public const string Column = "xml";
    public const string Here = "here";
    public const string Parent = "parent";
    public const string No = "no";

    public static string Under(string container) => $"under {container}";
}
