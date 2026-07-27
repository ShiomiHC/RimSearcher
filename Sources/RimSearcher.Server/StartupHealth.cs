namespace RimSearcher.Server;

// 启动期就已知、但调用方永远看不见的事实。
//
// 存在的理由：这些诊断过去只走 ServerLogger，而 ServerLogger 在启动阶段的 OnLogAsync
// 还没接上 MCP 通道，实际只落到 stderr。stderr 是给盯着终端的人看的，而这个服务的
// 调用方是 LLM——它读到的只有工具返回的那段文本。于是配置加载失败时，服务照常起来、
// 索引空空如也，每一条查询都回一句体面的 "No results for 'X' in scope 'all'"，
// 调用方据此告诉用户「这个类型不存在」。这不是搜不到，是搜了个空。
//
// 分两级，因为两者的正确重复频率不同：
//   Blocking  —— 索引不可信，每一次工具调用都要带上（每一条「没找到」都可能是假的）
//   Advisory  —— 索引可用，但工具替调用方做过它看不见的取舍（谁遮蔽了谁、互斥分支选了哪支），
//                每会话提醒一次即可，重复只是噪音
public static class StartupHealth
{
    public static string? BlockingReason { get; private set; }

    public static IReadOnlyList<string> Advisories { get; private set; } = [];

    public static void Record(string? blockingReason, IEnumerable<string>? advisories = null)
    {
        BlockingReason = string.IsNullOrWhiteSpace(blockingReason) ? null : blockingReason;
        Advisories = advisories?.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray() ?? [];
    }

    public static void ResetForTests() => Record(null, null);

    // 每会话一份，负责「忠告只说一次、阻塞级每次都说」这条节奏
    public sealed class SessionNotice
    {
        private int _advisoriesSent;

        public string? Consume()
        {
            var blocking = BlockingReason;
            var advisories = Advisories;

            if (blocking != null)
            {
                // 阻塞级不做「只说一次」：每一条返回都可能被读成权威的「不存在」，
                // 而这个会话里的每一条都同样不可信
                return "\n\n---\n**Warning: this server has no usable index, so the result above is not "
                     + $"evidence of absence.** {blocking} Nothing is indexed, so every lookup returns "
                     + "\"no results\" regardless of whether the symbol exists. Fix the server "
                     + "configuration before trusting any answer from this session.";
            }

            if (advisories.Count == 0) return null;
            if (Interlocked.Exchange(ref _advisoriesSent, 1) != 0) return null;

            var body = string.Join("\n", advisories.Select(a => "- " + a));
            return "\n\n---\n**Note: the indexed source layout involved choices you cannot see from the "
                 + $"results.**\n{body}\n"
                 + "These were decided at startup from the mod folder layout, not from what is actually "
                 + "enabled in-game, so a hit may come from the copy that does not load at runtime.";
        }
    }
}
