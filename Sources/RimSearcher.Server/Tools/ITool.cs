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

    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null);
}
