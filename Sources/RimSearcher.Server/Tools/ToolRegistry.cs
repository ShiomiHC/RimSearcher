using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

// 服务器注册哪七个工具、按什么顺序、各自拿什么构造实参——**一处**。
//
// 此前这份名单有三份拷贝，且没有任何一处断言三份一致：
//   1. Program.cs 里真的注册的那份 `ITool[]`（带真实构造实参）；
//   2. 参数层闸（UnknownParameterNoticeTests）自己写的一份，**构造重载都不一样**
//      （`new ListDirectoryTool()` 对 `new ListDirectoryTool(scopeCatalog, conditionalFolders)`）；
//   3. 输出层矩阵（OutputGrammarGateTests）那七个字符串短名。
//
// 后果不是「三份可能对不上」而已，是**新加一个工具时两道闸全部保持绿**——它压根不在闸的名单里，
// 于是「格格有主」这条完整性判据照的是一张少了一列的表。这与 CountedNoun 治好的那件事同型：
// 名词的产地是调用点的实参，不是各处各抄一份的字面量。
//
// 闸与产品共用的是**名单**（谁在册、叫什么），不是判断——每道闸各自问自己那个问题，
// 只是不再各自维护一份「有哪些工具」。
public static class ToolRegistry
{
    // 顺序逐字沿用此前 Program.cs 里那份数组，且它是 tools/list 的呈现顺序——改这里就是改
    // 调用方看到的排序，不是内部细节。
    //
    // 后三个参数可选，是为了让只关心名单本身的调用点（比如闸）不必造出一整套真实依赖；
    // 前四个必需，是因为少了它们工具就不是它在产品里的那一份了。
    public static ITool[] Create(
        SourceIndexer sourceIndexer,
        DefIndexer defIndexer,
        ScopeCatalog scopeCatalog,
        SourceSyncService syncService,
        LocalizationIndex? localization = null,
        ConditionalFolders? conditional = null,
        IndexRebuilder? rebuilder = null)
        =>
        [
            new ListDirectoryTool(scopeCatalog, conditional),
            new LocateTool(sourceIndexer, defIndexer, scopeCatalog, localization, conditional),
            new InspectTool(sourceIndexer, defIndexer, scopeCatalog, localization, conditional),
            new TraceTool(sourceIndexer, scopeCatalog, conditional),
            new ReadCodeTool(sourceIndexer, scopeCatalog, conditional),
            new SearchRegexTool(sourceIndexer, scopeCatalog, conditional),
            new SyncSourcesTool(syncService, rebuilder)
        ];
}

