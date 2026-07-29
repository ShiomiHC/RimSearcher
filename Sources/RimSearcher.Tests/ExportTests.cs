using RimSearcher.Commands;

namespace RimSearcher.Tests;

/// <summary>
/// 导出编排里**不起游戏也判得了**的那部分:命令行怎么拼。
///
/// 起一次游戏要几十秒并且会动真实机器,所以这里判的不是「跑起来对不对」——
/// 那由实测记在 06 里 —— 而是那条实测结论有没有被后来的改动悄悄推翻。
/// </summary>
public class ExportTests
{
    private const string Temp = "/tmp/rs-export-test";
    private const string Out = "/tmp/rs-export-test.rsx.jsonl.gz";

    /// <summary>
    /// 默认无头。导出全程零渲染,窗口是纯副作用:它抢焦点,而且随手一关就毁掉一次
    /// 几十秒的加载。实测两种模式产出的 defs / field_values / translations 逐项相同。
    /// </summary>
    [Fact]
    public void 默认不开窗口()
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow: false);
        Assert.Contains("-batchmode", argv);
        Assert.Contains("-nographics", argv);
    }

    /// <summary>
    /// 逃生口要真的逃得出去。加载期碰 GUI 的 mod 在无头下起不来,而那时唯一的
    /// 补救就是这个开关 —— 它若名存实亡,报错消息指的那条路就是死路。
    /// </summary>
    [Fact]
    public void 给了show_window就真的带图形起()
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow: true);
        Assert.DoesNotContain("-batchmode", argv);
        Assert.DoesNotContain("-nographics", argv);
    }

    /// <summary>
    /// 隔离不许有缺口。<c>-screen-width</c>/<c>-screen-height</c> 那一档也能跑通导出
    /// (实测 27 秒、产物正常),但它把窗口尺寸写进 <c>HKCU\…\Screenmanager*</c> ——
    /// 而注册表是 <c>-savedatafolder</c> **隔离不到**的地方,实测确实被改了一个键。
    /// 「真实配置永不触碰」这条约定里,注册表也算真实配置。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 任何模式都不设分辨率(bool showWindow)
    {
        foreach (var a in ExportCommand.BuildGameArguments(Temp, Out, showWindow))
            Assert.False(a.StartsWith("-screen-", StringComparison.Ordinal),
                $"'{a}' writes to the display settings the game keeps outside its save-data folder.");
    }

    /// <summary>
    /// 日志任何模式下都要落在我们指定的地方。Unity 默认那份 Player.log **这次跑可能一个字
    /// 都不写**(实测:一次挂死的导出,那个文件的时间戳停在半小时前)。没有日志,
    /// 「游戏卡住了」就是一句没有下文的话,而这正是那次排查里最贵的一段。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 任何模式都把游戏日志落到临时目录(bool showWindow)
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow);
        Assert.Contains(argv, a => a.StartsWith("-logfile=", StringComparison.Ordinal) &&
                                   a.Contains(ExportCommand.GameLogName, StringComparison.Ordinal) &&
                                   a.Contains(Temp, StringComparison.Ordinal));
    }

    /// <summary>
    /// 依赖按声明补齐,而且**插在需要它的 mod 之前**。补在后面等于没补:前置必须先加载。
    ///
    /// 实测代价:手写的 races 列表漏了 Ancot.AncotLibrary,游戏在读定义之前弹了一个
    /// 点不掉的对话框,无头模式下挂到人工中止。
    /// </summary>
    [Fact]
    public void 声明的依赖被补进列表且排在前面()
    {
        var installed = Mods(
            ("brrainz.harmony", []),
            ("ancot.ancotlibrary", ["brrainz.harmony"]),
            ("ancot.milirarace", ["ancot.ancotlibrary"]));

        var ids = new List<string> { "ancot.milirarace" };
        var added = ExportCommand.ResolveDependencies(ids, installed);

        // 传递依赖也要跟上来:AncotLibrary 自己还要 Harmony。补的**顺序**不构成契约,
        // 补进列表里的**位置**才是 —— 那两条在下面判。
        Assert.Equal(["ancot.ancotlibrary", "brrainz.harmony"], added.Order().ToList());
        Assert.True(ids.IndexOf("ancot.ancotlibrary") < ids.IndexOf("ancot.milirarace"),
            $"Dependency loads after its dependent: {string.Join(", ", ids)}");
        Assert.True(ids.IndexOf("brrainz.harmony") < ids.IndexOf("ancot.ancotlibrary"),
            $"Transitive dependency loads after its dependent: {string.Join(", ", ids)}");
    }

    /// <summary>
    /// 没装的依赖不许被悄悄跳过 —— 那正好又造一次隐形挂起。这里只判「不当成补上了」,
    /// 报错由调用方那段统一出,消息里能跟缺失的 mod 一起列。
    /// </summary>
    [Fact]
    public void 没装的依赖不算补上了()
    {
        var installed = Mods(("ancot.milirarace", ["ancot.ancotlibrary"]));
        var ids = new List<string> { "ancot.milirarace" };

        Assert.Empty(ExportCommand.ResolveDependencies(ids, installed));
        Assert.Equal(["ancot.milirarace"], ids);
    }

    /// <summary>循环依赖不许把补全转成死循环。</summary>
    [Fact]
    public void 循环依赖不死循环()
    {
        var installed = Mods(("a", ["b"]), ("b", ["a"]));
        var ids = new List<string> { "a" };
        ExportCommand.ResolveDependencies(ids, installed);
        Assert.Contains("b", ids);
    }

    private static Dictionary<string, InstalledMod> Mods(params (string Id, string[] Deps)[] mods)
        => mods.ToDictionary(m => m.Id, m => new InstalledMod(m.Id, m.Id, "/nowhere") { Dependencies = m.Deps },
                             StringComparer.OrdinalIgnoreCase);

    /// <summary>两件事在任何模式下都必须在:数据往哪写,以及真配置别碰。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 隔离与产出路径任何模式都在(bool showWindow)
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow);
        Assert.Contains(argv, a => a == $"-savedatafolder={Temp}");
        Assert.Contains(argv, a => a.EndsWith("=" + Out, StringComparison.Ordinal) && a != $"-savedatafolder={Temp}");
    }
}
