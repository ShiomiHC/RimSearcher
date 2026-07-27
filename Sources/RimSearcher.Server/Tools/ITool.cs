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

    // schema 里没声明、但各 getter 实际吸收的别名。声明出来是为了让「未知参数名」的检查
    // 不把合法别名误报成被忽略——服务端对参数名极宽容（别名 + 大小写/下划线归一），调用方
    // 由此学到「这台服务器对名字不挑」，于是把别的工具的参数类推过来是必然行为，而那些键
    // 一律被静默丢弃、返回却逐字正常，调用方会以为自己加的过滤/分页生效了。
    IEnumerable<string> ExtraAcceptedKeys => [];

    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null);
}
