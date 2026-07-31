using System.Diagnostics;
using RimSearcher.Cli;
using RimSearcher.Config;

namespace RimSearcher.Commands;

/// <summary>
/// 导出器在游戏 Mods 目录下的**接挂点** —— 一个用完就断的目录联接(junction)。
///
/// 不常驻:导出器是工具不是内容,平常玩游戏时它不该出现在 mod 列表里。用联接而不是
/// 「拷进去、拷完删掉」:后者中途一崩会留下半个 mod(About.xml 在而 Assemblies 空了),
/// 游戏会为此报错;联接只有一个目录项,建与断都是原子的。
///
/// 三条不许越的线:
///
/// 1. **只删 reparse point,绝不删真目录。**接挂点上若是一份真目录,那是使用者自己手工装的
///    导出器 —— 那时什么都不做。删除一律走 <c>recursive: false</c>,于是误删使用者的东西
///    在实现层不可能发生,而不是靠调用处记得判断。
/// 2. **断开是 export 的后置条件,不是尽力而为。**跑完之后接挂点一定不在,不管开始时它在不在。
/// 3. **不碰真实 ModsConfig。**接挂只让游戏「看得见」这个 mod;「启用」它是临时 savedata
///    副本里那份 ModsConfig 的事(<see cref="ExportCommand"/>)。所以哪怕断开失败留下了残骸,
///    使用者的游戏里它也是未启用状态。
/// </summary>
public static class DataModLink
{
    /// <summary>接挂点的目录名。游戏认的是 About.xml 里的 packageId,目录名只给人看。</summary>
    public const string FolderName = "RimSearcher";

    public enum LinkState
    {
        /// <summary>没配 <c>datamod_dir</c> —— 接挂这件事不归 CLI 管,导出器得自己装。</summary>
        NotManaged,
        /// <summary>归 CLI 管,而此刻没接着 —— 这是平常该有的样子:游戏里看不见导出器。</summary>
        Detached,
        /// <summary>接挂点上是一份真目录:使用者手工装的常驻导出器。不碰。</summary>
        Installed,
        /// <summary>联接已接上,由本次调用负责断开。</summary>
        Attached,
    }

    /// <summary>接挂点的完整路径。<c>game_dir</c> 没配时为 null。</summary>
    public static string? Path(RimConfig config)
        => config.GameDir is { Length: > 0 } g
            ? System.IO.Path.Combine(System.IO.Path.GetFullPath(g), "Mods", FolderName)
            : null;

    /// <summary>
    /// 这个路径是不是一个链接。判据是 reparse point 属性本身,不是「能不能解析到目标」——
    /// 指向已删除目标的坏联接照样是联接,而它恰恰最需要被清理。
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    /// <summary>接挂点当前是什么。<c>datamod status</c> 与 export 的开场都读这一份。</summary>
    public static LinkState Inspect(RimConfig config)
    {
        var link = Path(config);
        if (link is not null && Directory.Exists(link))
            return IsLink(link) ? LinkState.Attached : LinkState.Installed;

        // 接挂点不在时,「归不归 CLI 管」才有区别:没配就是「导出器没装」,配了就是「平常态」。
        return string.IsNullOrWhiteSpace(config.DataModDir) ? LinkState.NotManaged : LinkState.Detached;
    }

    /// <summary>
    /// 接上。返回值负责断开 —— 用 <c>using</c> 接住,于是中途任何一条 throw 都还是会断。
    ///
    /// 已经接着的情况**照样接管**:成因(上次没断干净 / 手动 attach 过)这里分不出来,
    /// 分不出来时选可预测的那一侧 —— 跑完一定是断的,并把「它本来就在」说出去。
    /// </summary>
    public static Attachment Attach(RimConfig config)
    {
        var source = config.DataModDir;
        if (string.IsNullOrWhiteSpace(source)) return Attachment.NotManaged;

        source = System.IO.Path.GetFullPath(source);
        var about = System.IO.Path.Combine(source, "About", "About.xml");
        if (!File.Exists(about))
            throw new CliUsageException(
                $"'datamod_dir' points at '{source}', which is not a built mod: it has no About/About.xml. " +
                "Build Sources/RimSearcher.DataMod first — the build stages the mod folder that this setting " +
                "should point at.");

        var link = Path(config)
            ?? throw new CliUsageException(
                "'datamod_dir' is set but 'game_dir' is not, so there is nowhere to attach the exporter. " +
                "Set 'game_dir' in the config file to the folder holding RimWorldWin64.exe.");

        var mods = System.IO.Path.GetDirectoryName(link)!;
        if (!Directory.Exists(mods))
            throw new CliUsageException(
                $"The game's mod folder '{mods}' does not exist, so the exporter cannot be attached. " +
                "Check 'game_dir' in the config file.");

        // 真目录 = 使用者手工装的那份。不碰。
        if (Directory.Exists(link) && !IsLink(link))
            return new Attachment(LinkState.Installed, link, source, wasAlreadyThere: true);

        var wasAlreadyThere = Directory.Exists(link);
        if (wasAlreadyThere) Break(link);

        CreateJunction(link, source);
        return new Attachment(LinkState.Attached, link, source, wasAlreadyThere);
    }

    /// <summary>
    /// 断开。返回是否真的断了 —— false 表示本来就没接着,或者接挂点上是不该碰的真目录。
    /// </summary>
    public static bool Detach(RimConfig config)
    {
        var link = Path(config);
        if (link is null || !IsLink(link)) return false;
        Break(link);
        return true;
    }

    /// <summary>
    /// 删链接本身。<c>recursive: false</c> 是**安全性质而不是优化**:对联接,
    /// <c>RemoveDirectory</c> 删的是 reparse point,目标内容不掉;而万一这里其实是个
    /// 非空真目录,它会抛。
    /// </summary>
    private static void Break(string link)
    {
        if (!IsLink(link)) return;
        Directory.Delete(link, recursive: false);
    }

    /// <summary>
    /// .NET 没有建目录联接的 API。<c>Directory.CreateSymbolicLink</c> 建的是符号链接,
    /// 要管理员权限或开发者模式;联接不要任何特权,代价是得借 <c>mklink /J</c>。
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var proc = Process.Start(psi)
            ?? throw new CliUsageException("Could not run 'mklink' to attach the exporter.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // 退出码不够:mklink 失败时也可能给 0。判据是接挂点真的出现了,而且是个联接。
        if (!IsLink(link))
            throw new CliUsageException(
                $"Could not attach the exporter at '{link}'. " +
                (string.IsNullOrWhiteSpace(stderr + stdout)
                    ? "'mklink' said nothing."
                    : "'mklink' said: " + (stderr + stdout).Replace("\r", " ").Replace("\n", " ").Trim()));
    }

    /// <summary>
    /// 一次接挂的生命周期。<see cref="Dispose"/> 是**断开的唯一产地**。
    /// </summary>
    public sealed class Attachment(LinkState state, string? path, string? source, bool wasAlreadyThere) : IDisposable
    {
        public static Attachment NotManaged { get; } = new(LinkState.NotManaged, null, null, false);

        public LinkState State { get; } = state;
        public string? LinkPath { get; } = path;
        public string? Source { get; } = source;

        /// <summary>接之前它就在。上次没断干净,或者是手动接上的 —— 这里分不出来。</summary>
        public bool WasAlreadyThere { get; } = wasAlreadyThere;

        private bool _keep;

        /// <summary>
        /// 别在离开作用域时断开 —— <c>datamod attach</c> 要的就是「接上之后留着」。
        /// 它仍该用 <c>using</c> 接住,留不留是成功之后的一次显式动作。
        /// </summary>
        public Attachment Keep() { _keep = true; return this; }

        public void Dispose()
        {
            if (_keep || State != LinkState.Attached || LinkPath is null) return;
            // 断不掉不该把一次已经成功的导出翻成失败:残骸的代价只是 mod 列表里多一行未启用的。
            try { Break(LinkPath); } catch { /* 下一次 Attach 会清掉它 */ }
        }
    }
}
