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
    /// 没有边表的树,在两个方向上不是同一件事。查调用者时,哪棵树没表都可能藏着调用点;
    /// 查被调用者时,边全出自被点名的方法自己那棵树 —— 别的树没表与这次答案无关,报出来
    /// 是让读者去疑一件不影响结论的事。
    ///
    /// fixture 里 CompShield 住在 vanilla,而 vanilla 有表,另外两棵没有。
    /// </summary>
    [Fact]
    public void 被调用方向不报与答案无关的空树()
    {
        var (callers, _, _) = Fixture.Run("callers", "Verse.Widgets.Label");
        var (callees, _, _) = Fixture.Run("callers", "RimWorld.CompShield.PostSpawnSetup", "--callees");

        // 查调用者那一侧必须照报 —— 否则底下那条否定断言在「两侧都不报」时也是绿的。
        Assert.Contains("Searched without a call-graph table", callers);
        Assert.DoesNotContain("Searched without a call-graph table", callees);
    }
}
