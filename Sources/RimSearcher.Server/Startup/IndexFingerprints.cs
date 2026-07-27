using System.Security.Cryptography;
using System.Text;
using RimSearcher.Core;

namespace RimSearcher.Server;

// 同一份配置要算出两个指纹，而两者对「内容敏感」的要求正好相反。这个区分是索引共享
// 机制的成败所在，曾经因为一值两用而失效过，所以单独成一处，好被测试直接钉住。
public static class IndexFingerprints
{
    // 宿主管道名用。两条互相拉扯的要求都要满足：
    //
    // 1) 不认内容。管道名是进程间的会合点，而宿主的名字在启动时算一次就冻住。掺进内容之后，
    //    源一变（Steam 更新、编辑器保存一下、乃至 sync_sources 自己重写反编译产物）新进程就
    //    算出另一个门牌号，找不到正在跑的宿主，转头再建一份 1 GB 索引——共享机制恰好在最该
    //    生效的时候失效。
    //    代价是新进程可能挂上一个索引已陈旧的宿主。那条链路另有人管：SourceChangeProbe
    //    探到变化并提示，sync_sources 原地重建。
    //
    // 2) 认全部会改变「宿主替代理做出的回答」的配置。代理只把报文原样转发，工具实例、scope
    //    目录、PathSecurity 状态全是宿主那一份：路径相同而配置不同的两个 client 若算出同一个
    //    管道名，后启动的那个就被静默换成了别人的配置。最狠的是 skip_path_security——一个
    //    关掉了路径校验的宿主会把「已关闭」传染给明确要求开启的 client。
    public static string ForHost(AppConfig config, ResolvedSources sources)
    {
        var builder = new StringBuilder();

        // 路径集合本身。下面的 [sources] 段管的是归属（哪条路径算哪个源、跟哪些程序集），
        // 这一行管的是「一共索引了哪些目录」，两者都不能少。
        builder.AppendLine(PathPrint(
            sources.Csharp.Select(entry => entry.Path),
            sources.Xml.Select(entry => entry.Path)));
        builder.Append(DescribeSharedBehavior(config, sources));

        return $"host:sha256:{Sha256(builder.ToString())}";
    }

    // 缓存键用。必须对内容敏感：mod 更新不改路径集合，纯路径键会让磁盘上那份陈旧索引
    // 一直命中且毫无提示。verifySourceFreshness=false 是用户明确接受这个风险，
    // 换每次启动省下几万次元数据枚举（约 100~300ms）。
    // 遮蔽集合只进缓存键，不进宿主名：它随游戏版本和 loadFolders.xml 而变，而那正是
    // 「同一批路径、不同索引内容」的情形——缓存必须区分，宿主会合点则不该被它挤开。
    public static string ForCache(ResolvedSources sources, bool verifySourceFreshness)
        => IndexCacheService.ComputeConfigFingerprint(
            sources.Csharp.Select(entry => entry.Path),
            sources.Xml.Select(entry => entry.Path),
            includeContentDigest: verifySourceFreshness,
            excludedPaths: sources.Shadowed);

    // 规范化是这里的正事：同义写法必须收敛到同一行，否则本该共享的两个进程各建一份 1 GB
    // 索引，而这种失效是静默的——两边都工作正常，只是内存翻倍。
    //
    // 拿不准的差异宁可让它分开：多一份索引是可恢复的浪费，把配置不同的进程合到一起则是
    // 回答与安全边界被悄悄换掉。所以这里只收敛确定同义的写法，不去推演「这个差异到了
    // ScopeCatalog 会不会被丢掉」——那等于在此再抄一份它的规则，两份规则跑偏就没人发现了。
    private static string DescribeSharedBehavior(AppConfig config, ResolvedSources sources)
    {
        var builder = new StringBuilder();

        // 安全边界。PathSecurity 是全进程一份状态，代理无从覆盖，故这一位必须把会合点劈开。
        builder.AppendLine($"skipPathSecurity:{config.SkipPathSecurity}");

        // 每一次没带 scope 的工具调用都落到宿主的这个表达式上
        builder.AppendLine($"defaultScope:{NormalizeScopeExpression(config.DefaultScope)}");

        // 组名按序排：TOML 里的书写顺序只影响工具说明里罗列组名的次序，不影响任何一次解析结果。
        // 组内成员顺序相反，它就是 ScopeCatalog 给出的 rank（同分命中谁排前面），必须原样保留。
        builder.AppendLine("[scopeGroups]");
        foreach (var group in config.ScopeGroups.OrderBy(pair => Fold(pair.Key), StringComparer.Ordinal))
        {
            var members = (group.Value ?? [])
                .Where(member => !string.IsNullOrWhiteSpace(member))
                .Select(Fold)
                .ToList();

            // 一个成员都不剩的组等于没写：ScopeCatalog 也是这么丢掉它的，
            // 让它留下一行空记录只会把「没配这个组」和「配了个空组」算成两个宿主。
            if (members.Count == 0) continue;

            builder.AppendLine($"{Fold(group.Key)}={string.Join(",", members)}");
        }

        // 只取显式配置的版本号，不取 ResolvedSources.GameVersion（那是「配置 ?? 从 Version.txt 探得」）。
        // 探得的那个会随游戏更新在我们脚下变掉，纳入之后一次 Steam 更新就换了管道名——正是上面
        // 第 1 条要躲开的失效。代价是一个显式写 1.6、一个靠探测也得 1.6 的 client 会各建一份索引，
        // 这个方向的错宁可犯。
        builder.AppendLine($"gameVersion:{Fold(config.GameVersion)}");

        // 历史深度是宿主那份 SourceHistoryStore 的构造参数：深度 0 的宿主让明确要求留历史的
        // client 在 sync 后拿不到 diff，反过来则替不要历史的 client 写下了成倍的旧文件副本。
        builder.AppendLine($"sourceHistoryDepth:{Math.Max(0, config.SourceHistoryDepth)}");

        // 源变更提示由宿主的探针产生、附在每个会话的返回里。关掉它的 client 关不掉别人的探针，
        // 开着它的 client 挂到关掉的宿主上则再也收不到「你的索引已陈旧」。
        builder.AppendLine($"checkSourceUpdates:{config.CheckSourceUpdates}");

        // 源名不只是标签：它就是 scope 表达式里能写的词，也是 sync_sources 的选择单位。
        // 路径集合相同而归属的源名不同，两个进程要的 scope 词表就不同，不能共用一份目录。
        builder.AppendLine("[sources]");
        var names = sources.Csharp.Concat(sources.Xml)
            .Select(entry => Fold(entry.Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var name in names)
        {
            var csharp = sources.Csharp.Where(entry => Fold(entry.Name) == name).ToList();
            var xml = sources.Xml.Where(entry => Fold(entry.Name) == name).ToList();

            builder.AppendLine($"{name}={PathPrint(
                csharp.Select(entry => entry.Path),
                xml.Select(entry => entry.Path))}");

            // 程序集路径决定 sync_sources 从哪些 dll 反编译、覆盖进哪个源码目录。路径相同而
            // 程序集不同的两个 client 若共用宿主，一次 sync 就会用别人的 dll 改写这份源码。
            builder.AppendLine($"{name}#assemblies={PathPrint(
                csharp.SelectMany(entry => entry.AssemblyPaths),
                [])}");
        }

        return builder.ToString();
    }

    // 借 ComputeConfigFingerprint 做路径规范化（全路径、统一分隔符、去尾分隔符、去重、排序），
    // 不在这里另写一份：两份规范化一旦跑偏，症状就是本该共享的两个进程各建一份索引，
    // 而没人会把它联想到这里。
    //
    // 唯一要自己补的是大小写：它内部按 OrdinalIgnoreCase 去重，但保留首次出现的那个写法，
    // 于是同一个目录写成 S:\Src 与 s:\src 会算出两段不同的文本。缓存键不在乎（同一进程内
    // 写法是固定的），宿主会合点在乎——两个 client 的路径抄自不同地方、大小写不同，
    // 就各建一份 1 GB 索引。故先把大小写折平，且只在 Windows 上折：Unix 的路径确实区分大小写，
    // 那边 ComputeConfigFingerprint 用的也是 Ordinal。
    private static string PathPrint(IEnumerable<string> csharpPaths, IEnumerable<string> xmlPaths)
        => IndexCacheService.ComputeConfigFingerprint(
            FoldPaths(csharpPaths),
            FoldPaths(xmlPaths),
            includeContentDigest: false);

    private static IEnumerable<string> FoldPaths(IEnumerable<string> paths)
        => OperatingSystem.IsWindows() ? paths.Select(path => path.ToLowerInvariant()) : paths;

    // scope 表达式的同义写法：分隔符三选一（ScopeCatalog 按 , ; | 切）、token 大小写不敏感、
    // 排除前缀 '-' 与 '!' 等价、空白无意义、空串与缺省都落到「全域」。
    // token 顺序必须保留——它就是 rank。
    private static string NormalizeScopeExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return string.Empty;

        var tokens = new List<string>();
        foreach (var rawToken in expression.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Fold(rawToken);
            if (token.Length == 0) continue;

            var isExclusion = token[0] is '-' or '!';
            if (isExclusion) token = token[1..].Trim();
            if (token.Length == 0) continue;

            tokens.Add(isExclusion ? $"-{token}" : token);
        }

        return string.Join(",", tokens);
    }

    // 名字类字段的同义收敛：null / 空串 / 纯空白是一回事，大小写也是（ScopeCatalog 全程
    // 按 OrdinalIgnoreCase 匹配组名与源名）。路径不走这里——路径归 ComputeConfigFingerprint。
    private static string Fold(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
