using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Config;

namespace RimSearcher.Tests;

/// <summary>
/// 接挂点的闸。判的是**文件系统上真的发生了什么**,不用假 IO 抽象 ——
/// 换成桩,「真目录不许被删」那条就退化成自证。
/// </summary>
public class DataModLinkTests
{
    /// <summary>
    /// 最坏结局的闸:接挂点上是使用者自己拷进去的一份 mod 时,谁也不许删它 ——
    /// 本功能唯一**不可逆**的失手方式。
    /// </summary>
    [Fact]
    public void 真目录不许被当成联接删掉()
    {
        using var env = new Env();
        env.InstallRealFolder("something the user put here");

        Assert.Equal(DataModLink.LinkState.Installed, DataModLink.Inspect(env.Config));

        using (var attachment = DataModLink.Attach(env.Config))
            Assert.Equal(DataModLink.LinkState.Installed, attachment.State);

        Assert.False(DataModLink.Detach(env.Config));
        Assert.True(File.Exists(Path.Combine(env.LinkPath, "About", "About.xml")));
        Assert.Equal("something the user put here",
            File.ReadAllText(Path.Combine(env.LinkPath, "mine.txt")));
    }

    /// <summary>
    /// 断开是**后置条件**,不是尽力而为:跑完之后接挂点一定不在,平常玩游戏时看不见导出器。
    /// </summary>
    [Fact]
    public void 接上再离开作用域就断干净()
    {
        using var env = new Env();

        using (var attachment = DataModLink.Attach(env.Config))
        {
            Assert.Equal(DataModLink.LinkState.Attached, attachment.State);
            Assert.True(Directory.Exists(env.LinkPath));
            // 游戏读不到 About.xml 就静默忽略这个目录,表现成一次跑到超时的导出。
            Assert.True(File.Exists(Path.Combine(env.LinkPath, "About", "About.xml")));
        }

        Assert.False(Directory.Exists(env.LinkPath));
        Assert.Equal(DataModLink.LinkState.Detached, DataModLink.Inspect(env.Config));
    }

    /// <summary>断开只拆链接。目标目录里的构建产物一根汗毛都不许掉。</summary>
    [Fact]
    public void 断开不碰目标目录里的东西()
    {
        using var env = new Env();
        using (var _ = DataModLink.Attach(env.Config)) { }

        Assert.True(File.Exists(Path.Combine(env.Source, "About", "About.xml")));
        Assert.True(File.Exists(Path.Combine(env.Source, "Assemblies", "RimSearcher.DataMod.dll")));
    }

    /// <summary>
    /// 上一次被强杀留下的残骸要能自愈:<c>--timeout</c> 杀进程是设计内行为,那条路上
    /// <c>Dispose</c> 跑不到,下一次 Attach 必须接管已在的联接并照旧断掉。
    /// </summary>
    [Fact]
    public void 接管上次留下的联接并照样断开()
    {
        using var env = new Env();
        DataModLink.Attach(env.Config).Keep();   // 模拟「断开那一步没跑到」
        Assert.Equal(DataModLink.LinkState.Attached, DataModLink.Inspect(env.Config));

        using (var attachment = DataModLink.Attach(env.Config))
        {
            Assert.Equal(DataModLink.LinkState.Attached, attachment.State);
            Assert.True(attachment.WasAlreadyThere);   // 说出去,让人判是残骸还是手动接的
        }

        Assert.False(Directory.Exists(env.LinkPath));
    }

    /// <summary>
    /// 指向已删除目标的坏联接照样是联接,清理必须够得着它:目录在但 About.xml 读不到,
    /// 游戏会静默忽略。
    /// </summary>
    [Fact]
    public void 坏联接也认得出来并被清掉()
    {
        using var env = new Env();
        DataModLink.Attach(env.Config).Keep();
        Directory.Delete(env.Source, recursive: true);

        Assert.True(DataModLink.IsLink(env.LinkPath));
        Assert.False(File.Exists(Path.Combine(env.LinkPath, "About", "About.xml")));
        Assert.True(DataModLink.Detach(env.Config));
        Assert.False(Directory.Exists(env.LinkPath));
    }

    /// <summary>
    /// <c>datamod_dir</c> 指着一个不是 mod 的目录时当场报错:接上去游戏会**静默忽略**它,
    /// 表现成一次跑到超时的导出。
    /// </summary>
    [Fact]
    public void 目标不是mod结构就当场报错()
    {
        using var env = new Env();
        File.Delete(Path.Combine(env.Source, "About", "About.xml"));

        var ex = Assert.Throws<CliUsageException>(() => DataModLink.Attach(env.Config));
        Assert.Contains("About.xml", ex.Message);
        Assert.False(Directory.Exists(env.LinkPath));
    }

    /// <summary>没配 datamod_dir 就什么也不做 —— 常驻安装方式照样能用。</summary>
    [Fact]
    public void 没配就不接不断()
    {
        using var env = new Env(manage: false);

        using var attachment = DataModLink.Attach(env.Config);
        Assert.Equal(DataModLink.LinkState.NotManaged, attachment.State);
        Assert.False(Directory.Exists(env.LinkPath));
    }

    /// <summary>
    /// <c>datamod attach</c> 接上之后要留着 —— 它存在的理由就是让人能进游戏用设置页那个按钮。
    /// </summary>
    [Fact]
    public void 显式留下的联接不随作用域断开()
    {
        using var env = new Env();
        using (var attachment = DataModLink.Attach(env.Config)) attachment.Keep();
        Assert.True(DataModLink.IsLink(env.LinkPath));
        DataModLink.Detach(env.Config);
    }

    /// <summary>
    /// 一次性造齐:一个装模作样的游戏目录 + 一份构建好的导出器 mod。
    /// </summary>
    private sealed class Env : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "rimsearcher-datamod-tests", Guid.NewGuid().ToString("N"));

        public RimConfig Config { get; }
        public string Source { get; }
        public string LinkPath { get; }

        public Env(bool manage = true)
        {
            var gameDir = Path.Combine(_root, "game");
            Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));

            Source = Path.Combine(_root, "staging");
            Directory.CreateDirectory(Path.Combine(Source, "About"));
            Directory.CreateDirectory(Path.Combine(Source, "Assemblies"));
            File.WriteAllText(Path.Combine(Source, "About", "About.xml"),
                "<ModMetaData><packageId>rimsearcher.datamod</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(Source, "Assemblies", "RimSearcher.DataMod.dll"), "not a real assembly");

            Config = new RimConfig { GameDir = gameDir, DataModDir = manage ? Source : null };
            LinkPath = DataModLink.Path(Config)!;
        }

        /// <summary>使用者手工拷进去的那一份。测试里它必须活着离开。</summary>
        public void InstallRealFolder(string marker)
        {
            Directory.CreateDirectory(Path.Combine(LinkPath, "About"));
            File.WriteAllText(Path.Combine(LinkPath, "About", "About.xml"), "<ModMetaData />");
            File.WriteAllText(Path.Combine(LinkPath, "mine.txt"), marker);
        }

        public void Dispose()
        {
            try { if (DataModLink.IsLink(LinkPath)) Directory.Delete(LinkPath, recursive: false); } catch { }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
