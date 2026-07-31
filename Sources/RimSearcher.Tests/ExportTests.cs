using RimSearcher.Commands;

namespace RimSearcher.Tests;

/// <summary>
/// 导出编排里**不起游戏也判得了**的那部分:命令行怎么拼。
/// 起一次游戏要几十秒并且会动真实机器,所以这里判的不是「跑起来对不对」。
/// </summary>
public class ExportTests
{
    private const string Temp = "/tmp/rs-export-test";
    private const string Out = "/tmp/rs-export-test.rsx.jsonl.gz";

    /// <summary>
    /// 默认无头。导出全程零渲染,窗口是纯副作用:抢焦点,随手一关就毁掉一次几十秒的加载。
    /// 实测两种模式产出的 defs / field_values / translations 逐项相同。
    /// </summary>
    [Fact]
    public void 默认不开窗口()
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow: false);
        Assert.Contains("-batchmode", argv);
        Assert.Contains("-nographics", argv);
    }

    /// <summary>
    /// 加载期碰 GUI 的 mod 在无头下起不来,而那时唯一的补救就是这个开关。
    /// </summary>
    [Fact]
    public void 给了show_window就真的带图形起()
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow: true);
        Assert.DoesNotContain("-batchmode", argv);
        Assert.DoesNotContain("-nographics", argv);
    }

    /// <summary>
    /// 隔离不许有缺口。<c>-screen-width</c>/<c>-screen-height</c> 也能跑通导出,但它把窗口
    /// 尺寸写进 <c>HKCU\…\Screenmanager*</c> —— 注册表是 <c>-savedatafolder</c> 隔离不到的地方,
    /// 而「真实配置永不触碰」这条约定里注册表也算真实配置。
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
    /// 日志任何模式下都要落在我们指定的地方:Unity 默认那份 Player.log 这次跑可能一个字都不写
    /// (实测一次挂死的导出,那个文件的时间戳停在半小时前)。
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
    /// 依赖按声明补齐,而且**插在需要它的 mod 之前**:前置必须先加载。
    /// 实测代价:races 列表漏了 Ancot.AncotLibrary,游戏在读定义之前弹了一个点不掉的
    /// 对话框,无头模式下挂到人工中止。
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

        // 传递依赖也要跟上来。补的**顺序**不构成契约,补进列表里的**位置**才是。
        Assert.Equal(["ancot.ancotlibrary", "brrainz.harmony"], added.Order().ToList());
        Assert.True(ids.IndexOf("ancot.ancotlibrary") < ids.IndexOf("ancot.milirarace"),
            $"Dependency loads after its dependent: {string.Join(", ", ids)}");
        Assert.True(ids.IndexOf("brrainz.harmony") < ids.IndexOf("ancot.ancotlibrary"),
            $"Transitive dependency loads after its dependent: {string.Join(", ", ids)}");
    }

    /// <summary>
    /// 没装的依赖不许被悄悄跳过 —— 那会造一次隐形挂起。这里只判「不当成补上了」,
    /// 报错由调用方统一出。
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

    /// <summary>
    /// 阶段停顿是**软限制**:到点只说话,不停进程。阈值注定选不准 —— 「读定义」那一段
    /// 随 mod 数量放大,20 个 mod 实测 35 秒,上百个 mod 要几分钟是正常的。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("mod-classes")]
    public void 阶段停太久只警告不停进程(string? stage)
    {
        Assert.Equal(ExportCommand.WaitAction.Warn,
            ExportCommand.Decide(pastDeadline: false, stage, ExportCommand.StageStallSeconds + 1, warned: false));
    }

    /// <summary>硬停只有 --timeout 一个来源。</summary>
    [Fact]
    public void 只有超时才停进程()
    {
        Assert.Equal(ExportCommand.WaitAction.GiveUp,
            ExportCommand.Decide(pastDeadline: true, "mod-classes", 1, warned: false));
    }

    /// <summary>同一个阶段只说一遍。每 500ms 重复同一句话是把终端刷成噪音。</summary>
    [Fact]
    public void 同一阶段不重复警告()
    {
        Assert.Equal(ExportCommand.WaitAction.KeepWaiting,
            ExportCommand.Decide(pastDeadline: false, "mod-classes", 9999, warned: true));
    }

    /// <summary>
    /// 导出阶段不报:那一段本来就长,而这里对它没有任何下一步可说。
    /// </summary>
    [Fact]
    public void 导出阶段不报停顿()
    {
        Assert.Equal(ExportCommand.WaitAction.KeepWaiting,
            ExportCommand.Decide(pastDeadline: false, "exporting", 9999, warned: false));
    }

    /// <summary>没到点就闭嘴。</summary>
    [Fact]
    public void 没到阈值不说话()
    {
        Assert.Equal(ExportCommand.WaitAction.KeepWaiting,
            ExportCommand.Decide(pastDeadline: false, "mod-classes", ExportCommand.StageStallSeconds - 1, false));
    }

    /// <summary>
    /// 停在读定义之前那一步,必须点名 <c>--show-window</c> —— 那是唯一能看见对话框的路。
    /// </summary>
    [Fact]
    public void 停在读定义之前要指向能看见对话框的路()
    {
        Assert.Contains("--show-window", ExportCommand.StageDiagnosis("mod-classes"));
    }

    /// <summary>
    /// 三种停法说三句不同的话:下一步动作完全不同。
    /// </summary>
    [Fact]
    public void 三种停法各说各的()
    {
        string?[] stages = [null, "mod-classes", "exporting"];
        var said = stages.Select(ExportCommand.StageDiagnosis).ToList();
        Assert.Equal(said.Count, said.Distinct().Count());
        Assert.All(said, s => Assert.NotEmpty(s));
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
