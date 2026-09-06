namespace RimSearcher.Tests;

/// <summary>
/// 代码侧四条命令印出来的**否定句**。它们各自有一条「查空」路径,而查空是常态 ——
/// 每条路径上那句话点名的东西必须是读者接着要去问的那个,否则他会顺着走岔。
/// </summary>
public class CodeSideTests
{
    /// <summary>
    /// 「继承来的成员在基类上」这句话,点名的得是基类。
    ///
    /// 从全体树里捞同名成员的话,捞到的是别的 mod 里毫不相干的类型 —— 句子说的是继承,
    /// 举的例子却与这条继承链无关,而读者会照着去问它们。
    /// </summary>
    [Fact]
    public void 成员落在基类上时点名的是基类()
    {
        var (stdout, _, code) = Fixture.Run("il", "RimWorld.CompShield.parent");

        Assert.Equal(1, code);
        Assert.Contains("Verse.ThingComp", stdout);
        Assert.Contains("--inherited", stdout);
    }

    /// <summary>
    /// 覆写方法上的零个调用者要点名基类链。callvirt 记的是调用点写的那个名字,
    /// 于是「没人调用它」几乎总是「调用点写的是基类那个名字」—— 不点名的话,
    /// 这个 0 与「这个方法确实没人用」逐字同形。
    /// </summary>
    [Fact]
    public void 覆写方法上的零个调用者要指向基类()
    {
        var (stdout, _, code) = Fixture.Run("callers", "RimWorld.CompShield.PostSpawnSetup");

        Assert.Equal(1, code);
        Assert.Contains("Verse.ThingComp", stdout);
        Assert.Contains("overrides", stdout);
    }

    /// <summary>
    /// <c>--callees</c> 方向的边界句不许说漏了「调用者」。没有边表的树在两个方向漏掉的
    /// 不是同一件事:查调用者时漏的是住在那里的调用点,查被调用者时只有被点名的方法自己
    /// 住在那里才受影响,而那时整份答案都是空的。
    /// </summary>
    [Fact]
    public void 被调用方向不说漏了调用者()
    {
        var (stdout, _, _) = Fixture.Run("callers", "RimWorld.CompShield.PostSpawnSetup", "--callees");

        // 先钉住这句话确实发了 —— 否则底下那条否定断言在「一句都没印」时也是绿的。
        Assert.Contains("Searched without a call-graph table", stdout);
        Assert.Contains("everything it calls is missing", stdout);
        Assert.DoesNotContain("A call site in one of them", stdout);
    }
}
