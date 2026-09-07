using System.Text;
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
    /// 跳过经济面那个开关得是**无值**的:游戏侧读它走 <c>CommandLineArgPassed</c>,
    /// 而那一支不认 <c>key=value</c>(<c>TryGetCommandLineArg</c> 才认,它先判
    /// <c>Contains('=')</c>)。写成带值的形式不会报错,只会静默地永远为假 ——
    /// 于是「跳过了」在快照里长成「量过了、这个名单下没有可生产物」。
    /// </summary>
    [Fact]
    public void 跳过经济面的开关不带值()
    {
        var with = ExportCommand.BuildGameArguments(Temp, Out, showWindow: false, skipEconomy: true);
        Assert.Contains("-" + Contract.IntermediateFormat.SkipEconomySwitch, with);
        Assert.DoesNotContain(with, a => a.StartsWith(
            "-" + Contract.IntermediateFormat.SkipEconomySwitch + "=", StringComparison.Ordinal));

        // 不给就一个字都不传 —— 默认必须是「量」。
        var without = ExportCommand.BuildGameArguments(Temp, Out, showWindow: false);
        Assert.DoesNotContain(without, a => a.Contains(
            Contract.IntermediateFormat.SkipEconomySwitch, StringComparison.Ordinal));
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
    ///
    /// 开关与路径必须是**相邻的两个元素**。这个开关由 Unity 自己解析,它不认 <c>key=value</c>,
    /// 不认就整个静默忽略 —— 于是日志一个字都不落盘,而失败信息还在指着那个路径。
    /// 2026-09-07 四种写法各起一次游戏实测:带等号的两种都不建文件(1.4M 日志全从 stdout 出),
    /// 空格分隔的两种都写出 1.4M 的文件。大小写不影响。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 任何模式都把游戏日志落到临时目录(bool showWindow)
    {
        var argv = ExportCommand.BuildGameArguments(Temp, Out, showWindow);
        var i = argv.ToList().FindIndex(a => a.Equals("-logFile", StringComparison.OrdinalIgnoreCase));
        Assert.True(i >= 0 && i + 1 < argv.Count, "the log switch is not in the command line at all.");
        Assert.Equal(Path.Combine(Temp, ExportCommand.GameLogName), argv[i + 1]);

        Assert.DoesNotContain(argv, a => a.StartsWith("-logfile=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 失败信息里那句指路牌不许指空。它是**诊断路径上**的话:平时不发作,只在出事那一刻发作,
    /// 而那一刻人会照着它去找文件。曾有一轮 229 秒的失败导出,唯一的现场就是被这句话许诺、
    /// 实则从未存在的日志。
    /// </summary>
    [Fact]
    public void 日志文件不在时不说它被留下了()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-lognote-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var absent = ExportCommand.GameLogNote(dir);
            Assert.DoesNotContain("kept at", absent, StringComparison.Ordinal);
            Assert.Contains("no log file", absent, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, ExportCommand.GameLogName), "something\n");
            var present = ExportCommand.GameLogNote(dir);
            Assert.Contains("kept at", present, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(dir, ExportCommand.GameLogName), present, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// 「游戏最后的输出」要从日志文件里取。<c>-logFile</c> 一生效,stdout 就只剩 Unity 那三十行
    /// memorysetup 配置块(实测 1771 字节),照着它印等于把噪音当现场交出去。
    /// 文件读不到才退回 stdout —— 那是唯一还剩点东西的地方。
    /// </summary>
    [Fact]
    public void 最后几行优先取自日志文件()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-lastlines-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, ExportCommand.GameLogName);
        var stdout = new StringBuilder("memorysetup-temp-allocator-size-gfx=262144\n");
        try
        {
            // 文件不在:退回 stdout。
            Assert.Contains("memorysetup", ExportCommand.LastLines(log, stdout), StringComparison.Ordinal);

            File.WriteAllText(log, "loading defs\nNullReferenceException in PlayDataLoader\n");
            var fromFile = ExportCommand.LastLines(log, stdout);
            Assert.Contains("NullReferenceException", fromFile, StringComparison.Ordinal);
            Assert.DoesNotContain("memorysetup", fromFile, StringComparison.Ordinal);

            // 空文件也算读不到 —— 空着的现场不该把 stdout 那点东西挤掉。
            File.WriteAllText(log, "   \n");
            Assert.Contains("memorysetup", ExportCommand.LastLines(log, stdout), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
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

    /// <summary>
    /// 写进 <c>activeMods</c> 的字面量恒小写。
    ///
    /// 游戏的两套口径在这里分叉:加载走 <c>ModLister</c> 的 ignore-case 字典,而
    /// <c>ModsConfig.IsActive</c> 拿 <c>id.ToLower()</c> 去查一个**大小写敏感**的
    /// HashSet —— 那个集合原样收 <c>activeMods</c> 的字面量。于是带大写的那一行
    /// 让 mod 照常加载、照常进 <c>RunningMods</c>,而所有 <c>MayRequire</c> /
    /// <c>IfModActive</c> 门控的内容整批静默缺席:不报错、不缺 dll,只是 Def 少了一截。
    ///
    /// 游戏自己写 <c>ModsConfig</c> 恒走 <c>SetActive</c> 的 <c>ToLower()</c>,所以这条
    /// 只有脚本生成的名单踩得到 —— 而 <c>.rml</c> 允许手写,大写就是从那里进来的。
    ///
    /// **本仓已有的三道闸都照不到它**:指纹自校两侧都 <c>ToLowerInvariant</c> 后再比,
    /// 依赖解析用 <c>OrdinalIgnoreCase</c>,缺 mod 检查查的是安装表。全绿,数据缺一截。
    /// </summary>
    [Fact]
    public void 写进activeMods的id恒小写()
    {
        var ids = ExportCommand.ActiveModIds(["CETeam.CombatExtended", "ludeon.rimworld", "Solaris.RatkinRaceMod"]);
        Assert.Equal(["ceteam.combatextended", "ludeon.rimworld", "solaris.ratkinracemod"], ids);
    }

    /// <summary>顺序是加载顺序,小写化不许动它。</summary>
    [Fact]
    public void 小写化不改变加载顺序()
    {
        string[] input = ["Harmony.Mod", "b.second", "A.third"];
        Assert.Equal(input.Select(i => i.ToLowerInvariant()), ExportCommand.ActiveModIds(input));
    }

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
