using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Sources;

namespace RimSearcher.Snapshot;

/// <summary>
/// 一个 mod 的参考侧 XML 指纹。<paramref name="Root"/> 是导出那一刻它的目录 ——
/// 记着它,下一次比对就不必再把全机每份 About.xml 解析一遍去找它在哪
/// (实测那一步比扫文件本身还贵)。目录不在了就退回全量寻址,见
/// <see cref="ContentFingerprint.Rescan"/>。
/// </summary>
public sealed record ModContent(string PackageId, int Files, string Hash, string? Root = null);

/// <summary>一次扫描的结果。存进 db meta 的就是它的 JSON。</summary>
public sealed record ContentScan(IReadOnlyList<ModContent> Mods)
{
    public int Files => Mods.Sum(m => m.Files);

    public string ToJson()
        => JsonSerializer.Serialize(Mods.Select(m => new Dto(m.PackageId, m.Files, m.Hash, m.Root)).ToList());

    public static ContentScan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<List<Dto>>(json);
            return dto is null
                ? null
                : new ContentScan(dto.Select(d => new ModContent(d.id, d.files, d.hash, d.root)).ToList());
        }
        catch (JsonException) { return null; }
    }

    private sealed record Dto(string id, int files, string hash, string? root);
}

/// <summary>
/// 两次扫描的差。<paramref name="Missing"/> 是导出时扫得到、现在定位不到的那些 ——
/// 与「内容改了」不同因,说法也就不同。
/// </summary>
public sealed record ContentComparison(IReadOnlyList<string> Changed, IReadOnlyList<string> Missing, int Scanned)
{
    public bool Drifted => Changed.Count > 0 || Missing.Count > 0;
}

/// <summary>
/// 「这些 mod 的 Defs / Patches 还是导出时那份吗」。
///
/// 快照的另外两条判据(mod 清单、游戏 build)问的都是**哪些东西被加载**,没有一条问
/// **它们里面是什么** —— 而 Steam 更新一个 mod 时,内容变了而 About.xml 的
/// <c>&lt;modVersion&gt;</c> 常常一动不动(本机实测:24 个启用 mod 里 20 个根本没有这个字段,
/// 有的那 4 个格式还各不相同)。那条缝正是这里堵的。
///
/// **判据是路径 + 长度 + mtime,不是内容哈希。** 代价差一个数量级(本机 23 个 mod、
/// 2390 个 XML、16.4 MB:19 ms 对 356 ms),而这条判据挂在每一次查询上。代价是它有
/// **假阳性**:Steam 重下一份逐字节相同的文件也会刷新 mtime,于是会多说一句「变了」。
/// 没有假阴性 —— 内容变了 mtime 必变,而那才是会让人读到错答案的方向。
///
/// **只扫 <see cref="Subdirs"/> 两个子目录**,不是整个 mod 目录:mod 里常年躺着
/// 1.4 / 1.5 的旧版本目录与互斥分支,把它们算进来就是白报警。哪些目录算数由
/// <see cref="ModFolders.LoadFolders"/> 答 —— 那是游戏自己的算法。
/// 代价是 <c>Languages/</c> 与贴图音频不在射程内:翻译层改了这里不响。
/// </summary>
public static class ContentFingerprint
{
    /// <summary>扫哪几个子目录。def 数据的两个产地,一个出原文一个出改写。</summary>
    public static readonly string[] Subdirs = ["Defs", "Patches"];

    private const char Sep = '\u0001';

    /// <summary>
    /// 扫一遍磁盘。<c>null</c> = 这次扫不了(没配 <c>mod_roots</c>/<c>game_dir</c>,
    /// 于是一个 mod 目录都定位不到)—— 与「扫过了,什么都没变」是两件事。
    /// </summary>
    public static ContentScan? Scan(RimConfig config, IEnumerable<string> packageIds, string gameVersion)
    {
        var installed = InstalledMods.Scan(config);
        if (installed.Count == 0) return null;

        var version = SourcePlanner.NormalizeGameVersion(gameVersion);
        var ids = packageIds
            .Where(id => !string.Equals(id, Contract.IntermediateFormat.ExporterPackageId,
                                        StringComparison.OrdinalIgnoreCase))
            .ToList();
        var active = ModFolders.NormalizeActive(ids);

        // 装不到磁盘上的 mod 不留条目。留一条 files:0 的话,「这个 mod 没装」与
        // 「这个 mod 的 Defs 是空的」在指纹里同形。
        var targets = ids.Where(installed.ContainsKey)
                         .Select(id => (Id: id, Root: installed[id].Directory))
                         .ToList();

        return Assemble(targets, version, active);
    }

    /// <summary>
    /// 比对用的重扫 —— 与 <see cref="Scan"/> 同一个算法,只是**跳过寻址**:每个 mod
    /// 在哪已经记在快照里了。
    ///
    /// 这一步是性能上的分水岭:寻址要把三个根目录下每一份 About.xml 解析一遍找 packageId
    /// (本机 263 份),而这条判据挂在每一次查询上。记下来的目录还在,就一份都不用读。
    ///
    /// **任何一个目录不在了就整体退回 <see cref="Scan"/>。** 只按旧路径找的话,
    /// 「mod 搬了个位置」会被报成「mod 不见了」,而那两件事的下一步完全不同。
    /// </summary>
    public static ContentScan? Rescan(RimConfig config, ContentScan recorded, IEnumerable<string> packageIds,
                                      string gameVersion)
    {
        if (recorded.Mods.Any(m => m.Root is not { Length: > 0 } r || !Directory.Exists(r)))
            return Scan(config, packageIds, gameVersion);

        var version = SourcePlanner.NormalizeGameVersion(gameVersion);
        var active = ModFolders.NormalizeActive(
            packageIds.Where(id => !string.Equals(id, Contract.IntermediateFormat.ExporterPackageId,
                                                  StringComparison.OrdinalIgnoreCase)));

        return Assemble(recorded.Mods.Select(m => (m.PackageId, m.Root!)).ToList(), version, active);
    }

    /// <summary>
    /// 每个 mod 扫一遍,**并行**。这条判据挂在每一次查询上,而它整个是 IO 等待 ——
    /// 本机 12 棵树、1866 个文件,串行与并行差一倍多。
    ///
    /// 结果按输入顺序回填(不是完成顺序):这份东西要序列化进 db,同样的磁盘状态
    /// 必须给出同样的字节,否则「指纹变了」会随线程调度随机成真。
    /// </summary>
    private static ContentScan? Assemble(IReadOnlyList<(string Id, string Root)> targets, string version,
                                         IReadOnlySet<string> active)
    {
        var slots = new ModContent?[targets.Count];
        Parallel.For(0, targets.Count, i => slots[i] = ScanOne(targets[i].Id, targets[i].Root, version, active));

        var mods = slots.OfType<ModContent>().ToList();
        return mods.Count == 0 ? null : new ContentScan(mods);
    }

    private static ModContent? ScanOne(string packageId, string rootDir, string version, IReadOnlySet<string> active)
    {
        List<string> folders;
        try { folders = ModFolders.LoadFolders(rootDir, version, active); }
        catch { return null; }

        // 键是**相对 mod 根**的路径:库搬了家不该让每个 mod 都变红(与 SourceTreeState 同口径)。
        // 同一个绝对路径只算一次 —— loadFolders.xml 允许把根目录再列一遍。
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var sub in Subdirs)
            {
                var dir = new DirectoryInfo(Path.Combine(folder, sub));
                if (!dir.Exists) continue;

                // 枚举 FileInfo 而不是路径字符串:长度与时间戳跟着目录项一起回来,
                // 一个文件一次系统调用。回头 new FileInfo(path) 再问一遍的话,
                // 本机 1866 个文件要多付一倍的 stat。
                IEnumerable<FileInfo> files;
                try { files = dir.EnumerateFiles("*.xml", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var file in files)
                {
                    if (!seen.Add(file.FullName)) continue;
                    var key = Path.GetRelativePath(rootDir, file.FullName).Replace('\\', '/');
                    try { entries[key] = file.Length + ":" + file.LastWriteTimeUtc.Ticks; }
                    catch { entries[key] = "?"; }   // 读不到属性:记一个恒定标记,不让它随机变红
                }
            }
        }

        if (entries.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var (path, stamp) in entries)
            sb.Append(path).Append(Sep).Append(stamp).Append(Sep);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
        return new ModContent(packageId, entries.Count, hash, Path.GetFullPath(rootDir));
    }

    /// <summary>
    /// 导出时那一份 ↔ 现在这一份。
    ///
    /// 只走记录那一侧:现在有而快照里没有的 mod 不点名,那是 modlist 判据的活儿,
    /// 两条都说等于同一件事报两遍。
    /// </summary>
    public static ContentComparison Compare(ContentScan recorded, ContentScan current)
    {
        var now = current.Mods.ToDictionary(m => m.PackageId, StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>();
        var missing = new List<string>();

        foreach (var m in recorded.Mods)
        {
            if (!now.TryGetValue(m.PackageId, out var cur)) { missing.Add(m.PackageId); continue; }
            if (!string.Equals(cur.Hash, m.Hash, StringComparison.Ordinal)) changed.Add(m.PackageId);
        }

        return new ContentComparison(changed, missing, recorded.Mods.Count);
    }
}
