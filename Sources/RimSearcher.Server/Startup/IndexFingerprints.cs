using RimSearcher.Core;

namespace RimSearcher.Server;

// 同一份配置要算出两个指纹，而两者对「内容敏感」的要求正好相反。这个区分是索引共享
// 机制的成败所在，曾经因为一值两用而失效过，所以单独成一处，好被测试直接钉住。
public static class IndexFingerprints
{
    // 宿主管道名用。只认路径：管道名是进程间的会合点，而宿主的名字在启动时算一次就冻住。
    // 掺进内容之后，源一变（Steam 更新、编辑器保存一下、乃至 sync_sources 自己重写反编译
    // 产物）新进程就算出另一个门牌号，找不到正在跑的宿主，转头再建一份 1 GB 索引——
    // 共享机制恰好在最该生效的时候失效。
    //
    // 代价是新进程可能挂上一个索引已陈旧的宿主。那条链路另有人管：SourceChangeProbe
    // 探到变化并提示，sync_sources 原地重建。
    public static string ForHost(ResolvedSources sources)
        => Compute(sources, includeContentDigest: false);

    // 缓存键用。必须对内容敏感：mod 更新不改路径集合，纯路径键会让磁盘上那份陈旧索引
    // 一直命中且毫无提示。verifySourceFreshness=false 是用户明确接受这个风险，
    // 换每次启动省下几万次元数据枚举（约 100~300ms）。
    public static string ForCache(ResolvedSources sources, bool verifySourceFreshness)
        => Compute(sources, includeContentDigest: verifySourceFreshness);

    private static string Compute(ResolvedSources sources, bool includeContentDigest)
        => IndexCacheService.ComputeConfigFingerprint(
            sources.Csharp.Select(entry => entry.Path),
            sources.Xml.Select(entry => entry.Path),
            includeContentDigest);
}
