using RimSearcher.Sources;

namespace RimSearcher.Metadata;

/// <summary>一份程序集副本的来路。</summary>
public enum AssemblyOrigin
{
    /// <summary>树里抄着的那一份,与这棵树的 .cs 同源。</summary>
    Copy,

    /// <summary>树里没抄,读的是安装目录里的原件 —— 它与树的 .cs 未必是同一个版本。</summary>
    Installed,
}

/// <summary>
/// 一个程序集,以及这次是从哪儿读到它的。
///
/// <see cref="OriginalChanged"/> 只在 <see cref="Origin"/> 为 <see cref="AssemblyOrigin.Copy"/> 时有意义:
/// 抄的那份永远与树同源,而安装目录里的原件可能已经换了版本。两件事都成立时,同一个方法
/// 在这里读到的 IL 与在游戏里跑的 IL 不是一回事,输出必须说。
/// </summary>
public sealed record ResolvedAssembly
{
    public required string Tree { get; init; }

    /// <summary>程序集名(不含 .dll)。它也是落盘树里那一层目录的名字。</summary>
    public required string Name { get; init; }

    /// <summary>这次真正打开的文件。</summary>
    public required string Path { get; init; }

    public required AssemblyOrigin Origin { get; init; }

    /// <summary>安装目录里那一份的路径。副本模式下用来判断原件有没有变;<c>null</c> = 清单里没记。</summary>
    public string? InstalledPath { get; init; }

    /// <summary>抄进来之后原件变过。<c>null</c> = 没比(没抄副本,或原件已不在)。</summary>
    public bool? OriginalChanged { get; init; }

    /// <summary>原件已经不在安装目录里了(mod 卸载、游戏移动)。</summary>
    public bool OriginalMissing { get; init; }
}

/// <summary>
/// 反编译树里抄着的那份程序集。
///
/// 抄它的理由是 IL 没有副本:.cs 是把内容抄下来了,边表是把派生结果抄下来了,而 IL 每次都要
/// 回原件读 —— 原件一被覆盖,旧版本的 IL 就不存在了。于是不抄的话,同一棵树上
/// <c>read</c> 给的是上次同步那一刻的 C#,<c>il</c> 给的是磁盘此刻的指令,两个答案各自完整、
/// 都不报错,而它们来自不同的版本。抄一份(全部来源合起来 39MB,比它们的 .cs 还小)之后,
/// C# / IL / 边表是同一份 dll 的三种投影。
/// </summary>
public static class AssemblyStore
{
    /// <summary>副本目录名。以点开头,于是 <c>SourcesShared.TreeNames</c> 不会把它当成一棵树。</summary>
    public const string CopyDir = ".assemblies";

    /// <summary>树根 .gitignore 里该有的条目。副本与边表都是可重建的派生物,不进版本历史。</summary>
    public static readonly IReadOnlyList<string> IgnoreEntries = [CopyDir + "/", CallGraphStore.FileName];

    public static string CopyDirectory(string treeDir) => Path.Combine(treeDir, CopyDir);

    /// <summary>
    /// 副本落在哪。<paramref name="relativePath"/> 是清单里记的那个路径,**目录结构照抄**。
    ///
    /// 不能只拿文件名:一个 mod 里出现两个同名 dll 是常事(实测 keeptpa.nivarianrace 有两个
    /// <c>LumiParticle.dll</c>,一个在 <c>Assemblies/LumiParticle/</c> 下,一个在别处)。
    /// 按文件名抄的话后一个盖掉前一个,清单里两条记录指向同一个文件 —— 于是那个被盖掉的
    /// 程序集里的每一个类型都查不到,而输出说的是「没有这个类型」。
    /// </summary>
    public static string CopyPath(string treeDir, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        // 清单外的路径(算不出相对位置)退回文件名 —— 抄到树外面去比同名相撞更糟。
        if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal))
            rel = Path.GetFileName(rel);
        return Path.Combine(CopyDirectory(treeDir), rel);
    }

    /// <summary>把这批 dll 抄进树里,按它们相对 <paramref name="root"/> 的位置摆。返回抄成功的个数。</summary>
    public static int CopyInto(string treeDir, string root, IEnumerable<string> assemblies)
    {
        Directory.CreateDirectory(CopyDirectory(treeDir));
        var n = 0;
        foreach (var dll in assemblies)
        {
            try
            {
                var target = CopyPath(treeDir, Relative(root, dll));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(dll, target, overwrite: true);
                n++;
            }
            catch
            {
                // 抄不动一个 dll 不该让整棵树失败 —— 少一份副本只是让它退回读原件,
                // 而那条路本来就要走得通。
            }
        }
        return n;
    }

    /// <summary>一个 dll 相对 mod 根目录的位置。算不出来就只剩文件名。</summary>
    public static string Relative(string root, string assemblyPath)
    {
        try { return Path.GetRelativePath(root, assemblyPath); }
        catch { return Path.GetFileName(assemblyPath); }
    }

    /// <summary>
    /// 根 <c>.gitignore</c> 里补上副本与边表两条。已经有的不重复写。
    ///
    /// 非做不可:反编译树按设计是个 git 仓(版本间差异交给 git),而副本是二进制、
    /// 边表是几 MB 的派生数据,进了历史就再也拿不出来。
    /// </summary>
    public static void EnsureIgnored(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        List<string> lines;
        try { lines = File.Exists(path) ? [.. File.ReadAllLines(path)] : []; }
        catch { return; }

        var missing = IgnoreEntries.Where(e => !lines.Any(l => l.Trim() == e)).ToList();
        if (missing.Count == 0) return;

        try
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("# 抄进来的来源程序集与它们的调用边表 —— 可重建的派生物,不进历史。");
            lines.AddRange(missing);
            File.WriteAllLines(path, lines);
        }
        catch { /* 只读的仓不该让 sync 失败 */ }
    }

    /// <summary>
    /// 一棵树里所有能打开的程序集。副本优先;没有副本就回退到清单记的安装路径。
    ///
    /// 回退是有意的:老树没有副本,而「这台机器上装着的那一份」总比什么都不答强 ——
    /// 代价是它可能不是这棵树的来源,所以 <see cref="ResolvedAssembly.Origin"/> 把这件事带出去。
    ///
    /// <paramref name="installRoots"/> 是本机配置的安装根(game_dir 与 mod_roots)。原件不在盘上时,
    /// 只有清单记的来源落在其中某个之下才算卸载;来源不在任何安装根之下,「原件还在不在」在本机
    /// 无从回答 —— 树是别的机器上建的,或者这是只装了代码树的环境 —— 于是不报卸载。
    /// </summary>
    public static IReadOnlyList<ResolvedAssembly> Resolve(string treeDir, IReadOnlyCollection<string> installRoots)
    {
        var tree = Path.GetFileName(treeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        var state = SourceTreeState.Read(treeDir);
        var result = new List<ResolvedAssembly>();
        var reachable = state is not null && UnderAny(state.Root, installRoots);

        // 清单是唯一的名单来源 —— 副本目录里多出来的文件不算数(换过 mod 版本后
        // 旧 dll 的副本会留在那儿,把它当成这棵树的一部分会凭空多出一个程序集)。
        if (state is null)
        {
            foreach (var f in SafeFiles(CopyDirectory(treeDir)))
                result.Add(new ResolvedAssembly
                {
                    Tree = tree,
                    Name = Path.GetFileNameWithoutExtension(f),
                    Path = f,
                    Origin = AssemblyOrigin.Copy,
                });
            return result;
        }

        foreach (var entry in state.Assemblies)
        {
            var rel = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var installed = Path.GetFullPath(Path.Combine(state.Root, rel));
            var name = Path.GetFileNameWithoutExtension(rel);

            // 目录结构照抄的那条路先试;老树里的副本是平铺的,退回文件名再试一次,
            // 于是它们不必重抄一遍就还能用。
            var copy = CopyPath(treeDir, entry.Path);
            if (!File.Exists(copy))
                copy = Path.Combine(CopyDirectory(treeDir), Path.GetFileName(rel));

            if (File.Exists(copy))
            {
                var originalHere = File.Exists(installed);
                result.Add(new ResolvedAssembly
                {
                    Tree = tree,
                    Name = name,
                    Path = copy,
                    Origin = AssemblyOrigin.Copy,
                    InstalledPath = installed,
                    OriginalMissing = !originalHere && reachable,
                    OriginalChanged = originalHere ? !SameHash(installed, entry.Sha256) : null,
                });
            }
            else if (File.Exists(installed))
            {
                result.Add(new ResolvedAssembly
                {
                    Tree = tree,
                    Name = name,
                    Path = installed,
                    Origin = AssemblyOrigin.Installed,
                    InstalledPath = installed,
                    OriginalChanged = !SameHash(installed, entry.Sha256),
                });
            }
        }

        return result;
    }

    /// <summary>本机配置的安装根:game_dir 加 mod_roots,没配的不算。</summary>
    public static IReadOnlyList<string> InstallRoots(RimSearcher.Config.RimConfig config)
        => [.. new[] { config.GameDir }.Concat(config.ModRoots).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!)];

    private static bool UnderAny(string path, IReadOnlyCollection<string> roots)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = WithSeparator(Path.GetFullPath(path));
        return roots.Any(r => full.StartsWith(WithSeparator(Path.GetFullPath(r)), comparison));
    }

    private static string WithSeparator(string dir)
        => dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    /// <summary>全部树里的程序集。<paramref name="only"/> 非空时只看这些树。</summary>
    public static IReadOnlyList<ResolvedAssembly> ResolveAll(
        string root, IReadOnlyCollection<string> installRoots, IReadOnlyCollection<string>? only = null)
    {
        var all = new List<ResolvedAssembly>();
        if (!Directory.Exists(root)) return all;

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir)!;
            if (name.Length == 0 || name.StartsWith('.')) continue;
            if (only is { Count: > 0 } && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            all.AddRange(Resolve(dir, installRoots));
        }
        return all;
    }

    /// <summary>
    /// 快判:大小与存在性。整套哈希是每个 15MB 的文件 37ms,而重编译几乎必然改大小,
    /// 于是常态走这条 —— 代价是少提醒一次,不是给出错答案。
    /// </summary>
    private static bool SameHash(string path, string recorded)
    {
        if (recorded.Length == 0) return false;
        try { return string.Equals(AssemblyFilter.Sha256(path), recorded, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories) : []; }
        catch { return []; }
    }
}
