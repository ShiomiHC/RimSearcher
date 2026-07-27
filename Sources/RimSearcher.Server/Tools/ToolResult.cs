namespace RimSearcher.Server.Tools;

// Content 在构造时统一去掉结尾空行。各工具是「表头 → 若干可选段 → 若干可选脚注」
// 的拼装，缺段时就在结尾留下一到三个空行；inspect 的类型模式恒以 "\n\n\n" 收尾。空行本身
// 不致命，但对 LLM 调用方它是一个信号——「后面本来还有、被截断了」——于是引出一次多余的
// 重查。收口放在这里而不是各工具末尾，是因为漏一处就等于没做。
public record ToolResult
{
    public ToolResult(string Content, bool IsError = false)
    {
        this.Content = Content.TrimEnd();
        this.IsError = IsError;
    }

    public string Content { get; init; }

    public bool IsError { get; init; }

    public void Deconstruct(out string Content, out bool IsError)
    {
        Content = this.Content;
        IsError = this.IsError;
    }
}
