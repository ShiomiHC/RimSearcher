namespace RimSearcher.Output;

public static class WrittenIn
{
    public const string Column = "written_in";

    public static string Render(bool generated) => generated ? "代码" : "XML";
}
