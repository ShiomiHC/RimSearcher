using System.Text.Json;

namespace RimSearcher.Tests;

/// <summary>
/// 继承层。快照里唯一**不是**「游戏内存里的对象」的一层:它是打补丁之前的 XML,
/// 这份时间差只许逐条申报,不许写成常驻免责声明。
/// </summary>
public class InheritanceTests
{
    private static string[] Kinds(params string[] argv)
    {
        var (json, _, _) = Fixture.Run([.. argv, "--json"]);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("notes", out var notes)
            ? notes.EnumerateArray().Select(n => n.GetProperty("kind").GetString()!).ToArray()
            : [];
    }

    private static string Text(params string[] argv)
    {
        var (stdout, _, _) = Fixture.Run(argv);
        return stdout;
    }

    /// <summary>
    /// 这一层存在的全部理由:抽象父节点从头到尾没有 Def 实例,<c>get</c> 永远找不到它。
    /// 两边同时成立,才说明补的是**另一层**而不是往 defs 里塞假记录。
    /// </summary>
    [Fact]
    public void 抽象节点不在defs里但在继承层里()
    {
        var (_, _, getCode) = Fixture.Run("get", "BaseBullet");
        Assert.Equal(1, getCode);

        var (_, _, inheritCode) = Fixture.Run("inherit", "BaseBullet");
        Assert.Equal(0, inheritCode);
    }

    /// <summary>
    /// patch 差异逐条申报,三支各说一件不同的事 —— **包括 0 那一支**。
    ///
    /// 此前这里钉的是「没被点名的一个字都不说」,理由是「恒在的免责声明不提供信息」。
    /// 那条理由建立在「0 = 没什么可说的」上,而 <c>Human</c> 是反例:它声明了 Name=、
    /// patch_ops 是 0,同时被 HAR 换掉 class、插进两个 comp —— 那些补丁按 defName 定位,
    /// 不点 Name=。沉默的 0 于是断言了一件假事,而旧断言里那个变量就叫 clean。
    ///
    /// **这条改动与原纪律是真冲突,待盲测裁决**:反方的顾虑(这一支覆盖 named 节点里的
    /// 多数,效果上接近恒在,会淹掉真正有据的那几条)没有被证伪,只是被「那个 0 是假话」
    /// 压过。裁决判据是**绕道率**不是答对率 —— 实测里所有运行都不信任这个 0、一致改用
    /// 双快照全字段 diff,而这句话省不掉那次 diff(单快照里这件事本就不可判定),
    /// 它省的是「先把 0 当答案用一遍」。
    /// </summary>
    [Fact]
    public void patch差异按条申报三支各说各的()
    {
        // BaseBullet 有 2 条 xpath 点名 —— 该说,并且要说出数字。
        var patched = Text("inherit", "BaseBullet");
        Assert.Contains("targeted by name by 2 patch operations", patched, StringComparison.Ordinal);

        // BaseProjectile 一条都没有:说破这个 0 数的是什么,而不是沉默。
        var unpatched = Text("inherit", "BaseProjectile");
        Assert.Contains("that is what the 0 counts", unpatched, StringComparison.Ordinal);
        Assert.Contains("with @Name= in this snapshot", unpatched, StringComparison.Ordinal);
        Assert.Contains("any other way leaves no trace", unpatched, StringComparison.Ordinal);
        // 不许举 defName 当遗漏面:这一支的对象可以是抽象节点,而它没有 defName。
        Assert.DoesNotContain("by defName", unpatched, StringComparison.Ordinal);
        // 不许串到 ops>0 那一支的话上:那句说的是「这一层与游戏最终读到的不同」,
        // 而这里没有任何已知的补丁让它不同 —— 只是这个计数看不见另一类。
        Assert.DoesNotContain("before patches", unpatched, StringComparison.Ordinal);
    }

    /// <summary>
    /// patch 计数的口径在**两处**出声:identity 块那三支,与 <c>inherit --help</c> 的 Remarks。
    /// 两处必须同形 —— r14 抓到一个受测者读了输出的新句、再引 help 的旧句把它降格成
    /// 「通用免责措辞」驳回,而 help 那句还多带一句更强的正面断言(「0 就是游戏读到的原样」)。
    /// 一句话在两个信道上强度不同时,读者取强的那个。
    ///
    /// 钉的三件事各是那次失效的一级台阶:遗漏面不许举 defName 当代表(抽象节点根本没有
    /// defName,举它等于说「这个漏检面对我不存在」)、口径半句 @Name= 两处都要在、
    /// 「0 = 原样」这类正面断言不许回来。
    /// </summary>
    [Fact]
    public void patch计数的口径在输出与help两处同形()
    {
        foreach (var text in new[] { Text("inherit", "--help"), Text("inherit", "BaseProjectile") })
        {
            Assert.Contains("@Name=", text, StringComparison.Ordinal);
            Assert.Contains("any other way", text, StringComparison.Ordinal);
            Assert.DoesNotContain("by defName", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("exactly what the game read", Text("inherit", "--help"),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// 「链到根了」与「父节点所在的 mod 没启用」必须分得开。两者在祖先表上长得一模一样:
    /// 都是表格到此为止。不说破,读的人会把「看不见」读成「没有」。
    /// </summary>
    [Fact]
    public void 断链与到根分得开()
    {
        var broken = Text("inherit", "TestModGun");
        Assert.Contains("BaseFromSomeDisabledMod", broken, StringComparison.Ordinal);
        Assert.Contains("not enabled", broken, StringComparison.Ordinal);

        // Bullet_Revolver 一路走到 BaseProjectile(它没有 ParentName),是真的到根了。
        var whole = Text("inherit", "Bullet_Revolver");
        Assert.Contains("BaseProjectile", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("not enabled", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 零结果的三种成因互斥分流:名字打错 / 不参与继承 / 这一层里真没有。
    /// 混报会把「你问错了层」说成「不存在」。
    /// </summary>
    [Fact]
    public void 不参与继承与不存在分得开()
    {
        var notInLayer = Text("inherit", "Apparel_ShieldBelt");
        Assert.Contains("takes part in no inheritance", notInLayer, StringComparison.Ordinal);
        Assert.DoesNotContain("No XML node named", notInLayer, StringComparison.Ordinal);

        var absent = Text("inherit", "NoSuchNode");
        Assert.Contains("No XML node named", absent, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>get</c> 落空时那句抽象父节点的话只许在继承层真命中时出现,不许无条件挂。
    /// </summary>
    [Fact]
    public void get落空时不再无条件谈抽象父节点()
    {
        var plain = Text("get", "NoSuchDefAtAll");
        Assert.DoesNotContain("Abstract", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("inherit", plain, StringComparison.Ordinal);

        // 真是具名节点时才点名说,并且指出去哪一层看。
        var node = Text("get", "BaseBullet");
        Assert.Contains("never becomes a def", node, StringComparison.Ordinal);
        Assert.Contains("rimsearcher inherit BaseBullet", node, StringComparison.Ordinal);
    }

    /// <summary>
    /// 有父节点才出 <c>inherits_from</c> 那一行 —— 否则就是给多数 def 平白多一行恒空值。
    /// </summary>
    [Fact]
    public void get只在有父节点时才多那一行()
    {
        Assert.Contains("inherits_from", Text("get", "Bullet_Revolver"), StringComparison.Ordinal);
        Assert.DoesNotContain("inherits_from", Text("get", "Meat_Muffalo"), StringComparison.Ordinal);
    }

    /// <summary>声明区的 kind 分类要对得上:计数是 count,patch 差异是 boundary。</summary>
    [Fact]
    public void 声明区分类正确()
    {
        var kinds = Kinds("inherit", "BaseBullet");
        Assert.Contains("count", kinds);
        Assert.Contains("boundary", kinds);
    }
}
