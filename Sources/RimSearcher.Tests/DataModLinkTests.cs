using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Config;

namespace RimSearcher.Tests;

/// <summary>
/// 接挂点的闸。这里判的是**文件系统上真的发生了什么** —— 真建联接、真断开、真在旁边放一份
/// 假的「使用者手工装的 mod」看它会不会被删掉。用假 IO 抽象替掉的话,这一批里最要紧的那条
/// (真目录不许被删)就退化成「我写的判断和我写的桩一致」,而那什么也没证明。
/// </summary>
public class DataModLinkTests
{
    /// <summary>
    /// 最坏结局的闸:接挂点上是使用者自己拷进去的一份 mod 时,谁也不许删它。
    ///
    /// 这条排在最前面,因为它是本功能唯一**不可逆**的失手方式。其余每一种错都只是
    /// 「mod 列表里多一行」或「导出跑不起来」,而这一种是把别人的东西弄没了。
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
    /// 断开是**后置条件**,不是尽力而为:跑完之后接挂点一定不在。
    /// 用户要的就是这一条 —— 平常玩游戏时看不见导出器。
    /// </summary>
    [Fact]
    public void 接上再离开作用域就断干净()
    {
        using var env = new Env();

        using (var attachment = DataModLink.Attach(env.Config))
        {
            Assert.Equal(DataModLink.LinkState.Attached, attachment.State);
            Assert.True(Directory.Exists(env.LinkPath));
            // 接上之后游戏能读到 About.xml —— 否则它会静默忽略这个目录,
            // 而那表现成一次跑到超时的导出,不是一句报错。
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
    /// 上一次被强杀留下的残骸要能自愈。<c>--timeout</c> 杀进程是设计内的行为,
    /// 而那条路上 <c>Dispose</c> 跑不到 —— 所以下一次 Attach 必须能接管已经在的那个联接,
    /// 并且照旧在结束时断掉。
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
    /// 指向已删除目标的坏联接照样是联接。它是残骸里最毒的一种:目录在、About.xml 读不到,
    /// 于是游戏静默忽略,而「导出器没装」这句话永远不会被说出来。清理必须够得着它。
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
    /// <c>datamod_dir</c> 指着一个不是 mod 的目录时当场报错。接上去的话游戏会**静默忽略**它,
    /// 而那一路的表现是导出跑到超时 —— 一次几十秒到几分钟的加载换一句本来在毫秒级就能说的话。
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

    /// <summary>没配 datamod_dir 就什么也不做 —— 老的常驻安装方式必须继续能用。</summary>
    [Fact]
    public void 没配就不接不断()
    {
        using var env = new Env(manage: false);

        using var attachment = DataModLink.Attach(env.Config);
        Assert.Equal(DataModLink.LinkState.NotManaged, attachment.State);
        Assert.False(Directory.Exists(env.LinkPath));
    }

    /// <summary>
    /// <c>datamod attach</c> 接上之后要留着 —— 它存在的全部理由就是让人能进游戏用设置页那个
    /// 按钮。离开作用域就断的话,这条命令等于没跑。
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
