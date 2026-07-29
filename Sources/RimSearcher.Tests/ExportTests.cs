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
