using RimSearcher.Contract;

namespace RimSearcher.Output;

public static class FieldFrom
{
    public const string Column = "from";

    public const string Xml = "XML";
    public const string XmlParent = "XML=parent";
    public const string XmlDefault = "XML=缺省";
    public const string Parent = XmlOrigin.Parent;
    public const string Default = "缺省";
    public const string Runtime = "运行时";
    public const string DefaultOrRuntime = "缺省|运行时";
    public const string NotWritten = "未写";

    public static string Under(string container) => $"在 {container} 下";

    public static string Render(string mark, int? defaultState, bool postPatchXml)
    {
        var patched = mark.EndsWith(XmlOrigin.PatchSuffix, StringComparison.Ordinal);
        var bare = patched ? mark[..^XmlOrigin.PatchSuffix.Length] : mark;
        var suffix = patched ? XmlOrigin.PatchSuffix : "";
        switch (bare)
        {
            case XmlOrigin.Here:
                return (defaultState == DefaultState.Same ? XmlDefault : Xml) + suffix;
            case XmlOrigin.HereOver:
                return Xml + suffix;
            case XmlOrigin.HereSame:
                return XmlParent + suffix;
            case XmlOrigin.No:
                if (!postPatchXml || defaultState is null) return NotWritten;
                return defaultState switch
                {
                    DefaultState.Same => Default,
                    DefaultState.Unknown => DefaultOrRuntime,
                    _ => Runtime,
                };
            default:
                return bare.StartsWith(XmlOrigin.UnderPrefix, StringComparison.Ordinal)
                    ? Under(bare[XmlOrigin.UnderPrefix.Length..]) + suffix
                    : mark;
        }
    }
}
