namespace RimSearcher.Output;

/// <summary>
/// 「这一行的 def 有没有对应的 XML 节点」在表里的措辞 —— 产地唯一。
///
/// 快照里有一批 def 是游戏在加载期用 C# 造出来的(ImpliedDefs:每件家具的 Blueprint_ /
/// Frame_、每种肉与皮、每样尸体……),它们在表里与 XML 里写出来的 def 完全同形。
/// 差别只在下游:按 defName 寻址的 PatchOperation 打不到它们 —— 补丁跑在 XML 上,
/// 而这些 def 那时还不存在。
///
/// 这一列只在结果里**确有**这种行时才出现:没有的时候它每行都是同一个值,
/// 是纯噪声;有的时候它是那批行与其余行之间唯一的分界。
///
/// 列名不叫 <c>source</c> 也不叫 <c>origin</c>:前者是 `get` 里那个装文件路径的字段名,
/// 后者是译文表的列名,两个都已经指着别的东西。
/// </summary>
public static class DeclaredIn
{
    public const string Column = "declared_in";

    /// <summary>
    /// <c>code</c> = 加载期由 C# 造出,没有 XML 节点;<c>xml</c> = 有。
    /// </summary>
    public static string Render(bool generated) => generated ? "code" : "xml";
}
