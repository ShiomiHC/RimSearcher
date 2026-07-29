using System.Text.Json;

namespace RimSearcher.Tests;

/// <summary>
/// 继承层。快照里唯一**不是**「游戏内存里的对象」的一层,所以它自带的风险也和别处不同:
/// 它是打补丁之前的 XML,而这份时间差一旦被说成一句常驻免责声明,就正好复活了
/// 00 论据 3 淘汰掉的那个东西。
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
    /// 两边同时成立才说明补的是**另一层**而不是往 defs 里塞了几条假记录。
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
    /// patch 差异是**逐条**申报的:被点名的节点说,没被点名的一个字都不说。
    ///
    /// 这条闸挡的是把它写成常驻声明的那种改法 —— 那样每一次 inherit 都挂一句
    /// 「这可能不准」,而 00 论据 3 淘汰掉的正是这个:恒在的免责声明既不提供信息,
    /// 又让真正有据的那几条淹没在噪声里。
    /// </summary>
    [Fact]
    public void patch差异按条申报而不是常驻声明()
    {
        // BaseBullet 有 2 条 xpath 点名 —— 该说,并且要说出数字。
        var patched = Text("inherit", "BaseBullet");
        Assert.Contains("targeted by name by 2 patch operations", patched, StringComparison.Ordinal);

        // BaseProjectile 一条都没有 —— 一个字都不许说。
        var clean = Text("inherit", "BaseProjectile");
        Assert.DoesNotContain("patch operation", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("before patches", clean, StringComparison.Ordinal);
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
    /// 零结果的三种成因互斥分流(C 那条的继承层版本):名字打错了 / 这个 def 不参与继承 /
    /// 这一层里真没有。混报最强的那种,就是把「你问错了层」说成「不存在」。
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
    /// <c>get</c> 落空时那句抽象父节点的话曾经是**无条件**的 —— 十有八九问的根本不是抽象节点,
    /// 于是它每次都在花预算说一件不相干的事。继承层落地之后这件事可判定了,
    /// 那句话就必须只在真命中时才出现。
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
    /// 有父节点才出 <c>inherits_from</c> 那一行。没父节点的那批 def 平白多一行空值,
    /// 就是 F13 删掉的那个恒 null 的 parent 字段换个名字回来了。
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
