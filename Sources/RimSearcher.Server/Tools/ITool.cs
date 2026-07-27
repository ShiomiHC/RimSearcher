using System.Text.Json;

namespace RimSearcher.Server.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    object JsonSchema { get; }

    // 自己就要触发索引重建的工具不能被查询读锁挡住——它要拿写锁，会和自己等到超时。
    // 代价是这类工具必须自行保证不在重建窗口里读索引。
    bool BypassIndexGate => false;

    // 返回内容里已经自带变更摘要的工具，再追加一条「源已过期」提示纯属重复；
    // 那条提示留给之后的查询，那时它才提供新信息。
    bool SuppressStalenessNotice => false;

    // MCP 的 tools/list 注解。client 靠 readOnlyHint 决定要不要在调用前征求用户同意——
    // 本服务器除 sync_sources 外全是只读查询，不标出来会让每次查询都被当成有副作用。
    bool ReadOnlyHint => true;

    // 面向人的短名。规范里 name 是标识符、title 才是展示名，界面上列 7 个
    // 'rimworld-searcher__xxx' 只是把同一个前缀重复 7 遍。
    string Title => Name.Contains("__") ? Name[(Name.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : Name;

    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null);
}
