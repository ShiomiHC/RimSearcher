using System.Reflection;

namespace RimSearcher.Sources;

/// <summary>
/// **现在装在磁盘上的**游戏是哪个版本。
///
/// 这个问题原先是问 <c>ModsConfig.xml</c> 的 <c>&lt;version&gt;</c> 的,而那不是安装事实,
/// 是一条历史记录:游戏只在 <c>Page_ModsConfig.PostClose()</c> 且玩家保存了改动时写它
/// (<c>ModsConfig.Save()</c> 全游戏只有那一个调用点),外加静态构造里 major/minor 变了
/// 或有 mod id 迁移那两条。于是 1.6.4871 → 1.6.4900 这种同 minor 的更新之后,
/// 只要没动过 mod 列表,那个数就一直停在旧值 —— 而 Ludeon 在这种更新里照样改 Def XML。
///
/// 版本号的真产地是 <c>Assembly-CSharp.dll</c> 的 AssemblyVersion:游戏自己的
/// <c>VersionControl</c> 静态构造读的就是它,<see cref="Format"/> 复刻那几行算术。
/// Steam 换了 dll,这里立刻跟着变,不需要游戏跑过一次。
///
/// (<c>Version.txt</c> 不能用:本机实测它写着 rev590,而 dll 算出来是 rev591。)
/// </summary>
public static class GameBuild
{
    private const string CoreAssembly = "Assembly-CSharp.dll";

    /// <summary>
    /// AssemblyVersion → <c>CurrentVersionStringWithRev</c>,与游戏逐字一致。
    ///
    /// 两个魔数抄自 <c>VersionControl</c>:build 减 4805(1.0 那天的天数基线),
    /// revision 折算成分钟里的 30 秒格。别去化简 —— 它们要跟着上游走,不是我们的算法。
    /// </summary>
    public static string Format(Version assemblyVersion)
    {
        var build = assemblyVersion.Build - 4805;
        var revision = assemblyVersion.Revision * 2 / 60;
        return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{build} rev{revision}";
    }

    /// <summary>
    /// 装在 <paramref name="gameDir"/> 的游戏版本。读不到就是 <c>null</c> ——
    /// 没配 <c>game_dir</c>、目录搬了、或那份 dll 不给读,三种都退回问 ModsConfig。
    /// </summary>
    public static string? Installed(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;
        var dll = Path.Combine(SourcePlanner.ManagedPath(gameDir), CoreAssembly);
        if (!File.Exists(dll)) return null;
        try
        {
            var version = AssemblyName.GetAssemblyName(dll).Version;
            return version is null ? null : Format(version);
        }
        catch
        {
            return null;   // 坏 dll / 权限 / 非托管文件:少答一句,不是报错
        }
    }
}
