using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 各道闸共用的**名单**：服务器到底注册了哪几个工具。判断不共用——每道闸各问各的问题
// （判据六：闸与产品只许共用名单，不许共用判断）。
//
// 此前这份名单在测试里有两份手写拷贝（参数层闸一份 `ITool[]`、输出层矩阵七个字符串短名），
// 与 Program.cs 那份真正注册的合计三份，且没有任何一处断言三份一致。后果是新加一个工具时
// 两道闸**全部保持绿**：它压根不在名单里，「格格有主」这条完整性判据照的是一张少了一列的表。
internal static class RegisteredTools
{
    // 短名（`rimworld-searcher__` 前缀之后那一截，即 ITool.Title）。
    //
    // 从 ToolRegistry 真的造一批实例来问，而不是再抄一份字符串——抄的那份在新加工具时不会红，
    // 那正是本文件要治的病。七个构造函数都只存引用、不碰磁盘也不读索引，故这里的依赖可以是
    // 空壳：这批实例只被问名字，不被执行。
    public static string[] Titles => [.. Nameless().Select(tool => tool.Title)];

    private static ITool[] Nameless()
    {
        var indexer = new SourceIndexer();
        var defIndexer = new DefIndexer();
        var catalog = ScopeCatalog.Build([], null, null);

        // GameVersion 显式给上：不给的话 SourceSyncService 会去探测，而这批实例不该碰盘。
        // cacheDirectory 只被 Path.Combine 拼一下存起来，从不创建。
        var sync = new SourceSyncService(
            new AppConfig { GameVersion = "1.6" },
            new ResolvedSources([], []),
            Path.Combine(Path.GetTempPath(), "rimsearcher-registry-names-never-created"));

        return ToolRegistry.Create(indexer, defIndexer, catalog, sync);
    }
}
