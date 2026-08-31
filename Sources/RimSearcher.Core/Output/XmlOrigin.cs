namespace RimSearcher.Output;

/// <summary>
/// get 字段表上「这一行 XML 写没写」那一列 —— 产地唯一。
///
/// <c>here</c> = 这个 def 自己的 XML 写了这条路径,Replace 找得到节点;
/// <c>parent</c> = 只有祖先写了,指向这个 def 的 xpath 上这条节点不在,Add 才找得到;
/// <c>no</c> = 本节点和祖先都没写。
/// 缺层时这一列根本不出现,由能力位那句通知说,不许印成 <c>no</c>。
/// </summary>
public static class XmlOrigin
{
    public const string Column = "xml";
    public const string Here = "here";
    public const string Parent = "parent";
    public const string No = "no";
}
