namespace RimSearcher.Tests;

// 有些用例断言的就是 Windows 的路径语义本身——盘符、`\` 分隔、目录名非法字符。这些概念在
// Unix 上不存在（`C:\mods\X` 只是一个普通文件名，`a:b*c` 是合法目录名），断言失败不代表被测
// 逻辑有问题。xunit 2.x 没有运行期 skip，条件跳过只能在发现期决定，故走自定义 attribute。
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows()) Skip = reason;
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows()) Skip = reason;
    }
}
