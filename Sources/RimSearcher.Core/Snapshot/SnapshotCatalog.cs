using System.Xml.Linq;
using RimSearcher.Config;
using RimSearcher.Sources;
using RimSearcher.Storage;

namespace RimSearcher.Snapshot;

public sealed record SnapshotEntry(string Alias, string Path);

public enum SelectionSource { ExplicitDb, ExplicitAlias, Pinned, AutoDetected, OnlyOne }

public sealed record SnapshotSelection(string Path, string? Alias, SelectionSource Source);

/// <summary>
/// 「这份快照还等于它自己的来源吗」的判定。**只管过期,不管环境选择** ——
/// 「游戏现在启用着哪些 mod」不在这个枚举里,它走
/// <see cref="EnvironmentReport.Added"/> / <see cref="EnvironmentReport.Removed"/>
/// (成因见那里)。
/// </summary>
public enum EnvironmentMatch
{
    /// <summary>快照与当前环境在比过的每一项上都一致。</summary>
    Same,
    /// <summary>游戏 build 变了。</summary>
    VersionDrift,
    /// <summary>游戏版本对得上,但某些 mod 的 Defs/Patches XML 在导出之后动过。</summary>
    ContentDrift,
    /// <summary>读不到 ModsConfig.xml,无从比对。</summary>
    Unknown,
}

/// <summary>「当前游戏是哪个版本」这句话的产地。两者强度差一档,声明时要说清是哪一个。</summary>
public enum GameVersionSource
{
    /// <summary>没答案 —— 既没配 game_dir,ModsConfig.xml 也读不到。</summary>
    None,
    /// <summary>
    /// <c>ModsConfig.xml</c> 的 <c>&lt;version&gt;</c>。**弱**:那是游戏上次保存 mod 列表时
    /// 写下的历史记录,同 minor 的更新之后它不会变(成因见 <see cref="Sources.GameBuild"/>)。
    /// </summary>
    ModsConfig,
    /// <summary><c>Assembly-CSharp.dll</c> 的 AssemblyVersion。安装事实,Steam 一换就变。</summary>
    Assembly,
}

public sealed record EnvironmentReport(EnvironmentMatch Match, IReadOnlyList<string> ActiveMods, string? GameVersion)
{
    /// <summary>
    /// 集合差:游戏这边多开了几个 / 快照这边有几个已不再启用。
    ///
    /// **不进 <see cref="Match"/>,于是每次查询不为它发声** —— 它是环境选择,不是过期。
    /// 拿一份刻意精简的基线快照(本体 + 官方 DLC)当 pin,「你的环境和它不一样」每次查询
    /// 都成立、而且永远不会「修好」:那正是那份快照存在的理由。恒真的警告不携带信息,
    /// 却与真过期同形同位,久了会把真的那几条一起训练成噪声。
    ///
    /// 需要它的两处各自更准:<c>snapshot status</c> 逐条讲(它是被显式问的),
    /// 而零结果会点名「这个 def 不在你查的这份里,在 'modded' 那份里」。
    /// </summary>
    public int Added { get; init; }

    /// <inheritdoc cref="Added"/>
    public int Removed { get; init; }

    /// <summary>
    /// 快照描述的那批 mod,在当前列表里的**相对次序**变了。
    ///
    /// 这一条与集合差分开,因为它不是「另一个环境」—— 没人 pin 得出一个「次序变体」。
    /// 加载顺序决定同名 patch 谁赢,于是次序一动,快照里的值不是「不全」而是**直接错了**,
    /// 与 <see cref="EnvironmentMatch.VersionDrift"/> 同类。
    ///
    /// 判据只看两边都在的那些 mod:多开几个不算,禁用几个也不算(那是集合差),
    /// 只有剩下的那些互换了位置才算。
    /// </summary>
    public bool Reordered { get; init; }

    public GameVersionSource VersionSource { get; init; } = GameVersionSource.None;

    /// <summary>内容漂移的明细。<c>null</c> = 这一项没比过(快照没记指纹,或这次扫不了盘)。</summary>
    public ContentComparison? Content { get; init; }
}

/// <summary>
/// 快照寻址 —— **显式恒胜自动**(当前游戏启用的不一定是正在查询的目标环境)。
///
///   1. 本次调用显式指定:<c>--db &lt;path&gt;</c> 或 <c>--snapshot &lt;别名&gt;</c>
///   2. <c>snapshot use</c> 固定的活动快照
///   3. 都没有才走自动检测(读 ModsConfig.xml ↔ 各快照 meta 指纹比对)
///
/// 但无论选择来自哪一层,每次输出都要报告「所用快照 ↔ 当前 ModsConfig」的比对结果:
/// 寻址与过期自证是同一次比对的两个产出。**不一致只声明,不静默切换。**
/// </summary>
public static class SnapshotCatalog
{
    public static IReadOnlyList<SnapshotEntry> Enumerate(RimConfig config)
    {
        var result = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        var dir = config.ResolveSnapshotDir();

        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.db").OrderBy(f => f, StringComparer.Ordinal))
                result[Path.GetFileNameWithoutExtension(file)] =
                    new SnapshotEntry(Path.GetFileNameWithoutExtension(file), file);

        foreach (var (alias, target) in config.Snapshots)
        {
            var path = Path.IsPathRooted(target) ? target : Path.Combine(dir, target);
            result[alias] = new SnapshotEntry(alias, path);
        }

        return result.Values.OrderBy(e => e.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 显式点了名的快照当场校验,**不开库**。
    ///
    /// 寻址是懒的(命令第一次碰 <c>ctx.Db</c> 才走 <see cref="Resolve"/>),这本身是性能
    /// 考虑:<c>code-search</c> 只有印出来的行里带 <c>.Translate()</c> 才需要快照。可懒
    /// 寻址顺带把**「这个名字对不对」也变懒了** —— 同一个 <c>--snapshot vanilla</c>
    /// (查无此名),<c>search</c> 上是硬错、<c>code-search</c> 上一声不吭照常出结果。
    /// 一个参数在两条命令上两种命运,读的人只能得出「它在这条命令上生效了」。
    ///
    /// 只校验、不开库,是因为开了库 <c>AnnounceSnapshot</c> 那句「这次用的是哪份快照」
    /// 就会长到本来不碰快照的输出里去。没显式给就什么都不做:自动检测那一层要逐个开库
    /// 比指纹,不该由一条不查 def 的命令去付这笔钱。
    /// </summary>
    public static void ValidateExplicit(RimConfig config, string? explicitDb, string? explicitAlias)
    {
        if (explicitDb is { Length: > 0 })
        {
            if (!File.Exists(explicitDb)) throw new SnapshotFormatError(SnapshotDb.NoDatabaseAt(explicitDb));
            return;
        }

        // 名字不在册就在这里抛,措辞与懒路径逐字同源。
        if (explicitAlias is { Length: > 0 }) _ = Resolve(config, null, explicitAlias);
    }

    public static SnapshotSelection Resolve(RimConfig config, string? explicitDb, string? explicitAlias)
    {
        if (explicitDb is { Length: > 0 })
            return new SnapshotSelection(explicitDb, null, SelectionSource.ExplicitDb);

        var entries = Enumerate(config);

        if (explicitAlias is { Length: > 0 })
        {
            var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, explicitAlias, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                throw new SnapshotFormatError(
                    $"No snapshot named '{explicitAlias}'. " +
                    (entries.Count == 0
                        ? "No snapshots are registered yet; run 'rimsearcher export' to make one."
                        : $"Registered: {string.Join(", ", entries.Select(e => e.Alias))}."));
            return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.ExplicitAlias);
        }

        if (config.ActiveSnapshot is { Length: > 0 } pinned)
        {
            var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, pinned, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.Pinned);
        }

        if (entries.Count == 0)
            throw new SnapshotFormatError(
                "No snapshot is available. A snapshot is produced inside the game: run 'rimsearcher export --modlist <name>' " +
                "to drive it, or press the export button in the mod's settings page and then " +
                "'rimsearcher snapshot import <file>'.");

        // 第 3 层:自动检测。只在前两层都没说话时才轮到它。
        var env = ReadActiveMods(config);
        if (env.ActiveMods.Count > 0)
        {
            foreach (var entry in entries)
            {
                try
                {
                    using var db = SnapshotDb.Open(entry.Path);
                    if (db.Meta.ModlistFingerprint == ExportMeta.ComputeModlistFingerprint(env.ActiveMods))
                        return new SnapshotSelection(entry.Path, entry.Alias, SelectionSource.AutoDetected);
                }
                catch (SnapshotFormatError) { /* 坏库不该让寻址整个失败 */ }
            }
        }

        if (entries.Count == 1)
            return new SnapshotSelection(entries[0].Path, entries[0].Alias, SelectionSource.OnlyOne);

        throw new SnapshotFormatError(
            "More than one snapshot is registered and none matches the mods currently enabled in the game, " +
            $"so there is no safe default. Pick one with --snapshot: {string.Join(", ", entries.Select(e => e.Alias))}. " +
            "'rimsearcher snapshot use <name>' makes the choice stick.");
    }

    /// <summary>
    /// 当前环境:启用了哪些 mod,以及游戏是哪个版本。
    ///
    /// 两件事产地不同。mod 列表只有 <c>ModsConfig.xml</c> 答得了。版本先问
    /// <c>Assembly-CSharp.dll</c>(装在磁盘上的事实),问不到才退回同一份 xml 里那个数 ——
    /// 后者是游戏上次保存 mod 列表时写的历史记录,同 minor 的更新之后不会变
    /// (成因见 <see cref="GameBuild"/>)。
    /// </summary>
    public static EnvironmentReport ReadActiveMods(RimConfig config)
    {
        var installed = GameBuild.Installed(config.GameDir);

        var path = config.ModsConfigPath();
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) return Empty(installed);
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return Empty(installed);
            var declared = root.Element("version")?.Value?.Trim();
            var mods = root.Element("activeMods")?.Elements("li")
                           .Select(e => e.Value.Trim())
                           .Where(s => s.Length > 0).ToList() ?? [];
            return new EnvironmentReport(EnvironmentMatch.Unknown, mods, installed ?? declared)
            {
                VersionSource = installed is not null ? GameVersionSource.Assembly
                              : declared is { Length: > 0 } ? GameVersionSource.ModsConfig
                              : GameVersionSource.None,
            };
        }
        catch
        {
            return Empty(installed);
        }
    }

    private static EnvironmentReport Empty(string? installedVersion)
        => new(EnvironmentMatch.Unknown, [], installedVersion)
        {
            VersionSource = installedVersion is null ? GameVersionSource.None : GameVersionSource.Assembly,
        };

    private static List<string> WithoutExporter(IEnumerable<string> ids)
        => ids.Where(id => !string.Equals(id, Contract.IntermediateFormat.ExporterPackageId,
                                          StringComparison.OrdinalIgnoreCase))
              .ToList();

    /// <summary>寻址与过期自证的共同产出:所用快照 ↔ 当前 ModsConfig 的比对。</summary>
    public static EnvironmentReport Compare(SnapshotDb db, RimConfig config)
    {
        var env = ReadActiveMods(config);
        if (env.ActiveMods.Count == 0) return env with { Match = EnvironmentMatch.Unknown };

        // 导出器两侧都不算数:它是导出时临时塞进去的工具,快照里有、玩家的 ModsConfig 里
        // 通常没有,参与比对会造出「有 1 个 mod 不再启用」的假过期。
        var snapshotIds = WithoutExporter(db.Mods.Select(m => m.PackageId));
        var activeIds = WithoutExporter(env.ActiveMods);

        // mod 列表的三个产出都算出来,但**一个都不改 Match**:它们各自有各自的去处,
        // 而版本与内容那两层必须照旧跑到。此前这里在列表不一致时直接 return,于是
        // pin 着一份精简快照的人碰上 build 升级或 XML 改动,一个字都收不到 —— 那条恒真的
        // 列表警告不只是稀释信号,它在顶替信号。
        if (ExportMeta.ComputeModlistFingerprint(snapshotIds) !=
            ExportMeta.ComputeModlistFingerprint(activeIds))
        {
            var snapSet = snapshotIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var envSet = activeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            env = env with
            {
                Added = envSet.Except(snapSet, StringComparer.OrdinalIgnoreCase).Count(),
                Removed = snapSet.Except(envSet, StringComparer.OrdinalIgnoreCase).Count(),
                // 次序只在**两边都在**的那些 mod 之间判 —— 拿全表去判的话,多开一个
                // 或禁用一个都会让它响,而那两件事按上面的口径不发声。
                Reordered = !IsSubsequence([.. snapshotIds.Where(envSet.Contains)], activeIds),
            };
        }

        if (VersionDrifted(env, db.Meta.GameVersion))
            return env with { Match = EnvironmentMatch.VersionDrift };

        // 内容这一层排在最后:游戏换了版本的时候,「XML 也变了」是那件事的后果,
        // 不是第二条新闻。
        var content = CompareContent(db, config, env);
        if (content is { Drifted: true })
            return env with { Match = EnvironmentMatch.ContentDrift, Content = content };

        return env with { Match = EnvironmentMatch.Same, Content = content };
    }

    /// <summary>
    /// <paramref name="inner"/> 是否为 <paramref name="outer"/> 的子序列 —— 一个都不能少,
    /// 而且**相对顺序**要保持。
    /// </summary>
    private static bool IsSubsequence(IReadOnlyList<string> inner, IReadOnlyList<string> outer)
    {
        var i = 0;
        foreach (var id in outer)
        {
            if (i == inner.Count) break;
            if (string.Equals(inner[i], id, StringComparison.OrdinalIgnoreCase)) i++;
        }
        return i == inner.Count;
    }

    /// <summary>
    /// 版本比对。两个产地严格程度不同,判法也不同:
    ///
    /// dll 那一路两边都是 <c>CurrentVersionStringWithRev</c>(导出器写的也是它),逐字可比,
    /// 于是 rev 变一位就该说话。ModsConfig 那一路的值可能只有 <c>1.6</c> 那么粗,
    /// 所以维持双向前缀 —— 拿粗的去要求细的相等,只会得到一条永远在响的假过期。
    /// </summary>
    private static bool VersionDrifted(EnvironmentReport env, string snapshotVersion)
    {
        if (env.GameVersion is not { Length: > 0 } gv) return false;
        return env.VersionSource == GameVersionSource.Assembly
            ? !string.Equals(gv, snapshotVersion, StringComparison.OrdinalIgnoreCase)
            : !snapshotVersion.StartsWith(gv, StringComparison.OrdinalIgnoreCase) &&
              !gv.StartsWith(snapshotVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 参考侧 XML 比对。两处 <c>null</c> 各有各的意思,都表示**这一项没比过**:
    /// 快照没记(旧库),或这次扫不了盘(没配 mod 根目录)。
    /// </summary>
    private static ContentComparison? CompareContent(SnapshotDb db, RimConfig config, EnvironmentReport env)
    {
        if (db.Content is not { } recorded) return null;
        var version = env.GameVersion is { Length: > 0 } gv ? gv : db.Meta.GameVersion;
        var current = ContentFingerprint.Rescan(config, recorded, db.Mods.Select(m => m.PackageId), version);
        return current is null ? null : ContentFingerprint.Compare(recorded, current);
    }
}
