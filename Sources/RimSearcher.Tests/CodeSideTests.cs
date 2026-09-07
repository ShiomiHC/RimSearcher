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

    /// <summary>
    /// 代码侧四条命令的多名形态:一次问几个,行等于分几次问再拼起来。
    ///
    /// 这四条的行里本来就带着归属列(types 的 type、members 的 type+assembly、
    /// il 与 callers 的 to_type/to_member),所以多名不需要新增任何一列,也不切块 ——
    /// 这条断言钉的就是「只是少了几次进程启动,拿到的行一行不多一行不少」。
    ///
    /// 落空的名字不吃掉别的名字的行:中间夹一个查不到的符号,退出码仍是 0。
    /// </summary>
    [Theory]
    [InlineData("types", "types", "Verse.ThingComp", "RimWorld.CompShield")]
    [InlineData("members", "members", "Verse.ThingComp", "RimWorld.CompShield")]
    [InlineData("il", "il", "RimWorld.CompShield.PostSpawnSetup", "Verse.Widgets.Label")]
    [InlineData("callers", "calls", "Verse.Widgets.Label", "RimWorld.CompShield.PostSpawnSetup")]
    public void 代码侧多名的行等于分别问再拼起来(string command, string key, string first, string second)
    {
        // 比的是那条命令**声明过的**行数组。il 另外还发 il_1 / il_2 这样一块一份的反汇编
        // 正文,它们的键名带序号,拼起来比会因为编号重排而必然不等 —— 而那不是数据差异。
        List<string> Rows(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty(key).EnumerateArray().Select(r => r.GetRawText()).ToList();
        }

        var (a, _, _) = Fixture.Run(command, first, "--json");
        var (b, _, _) = Fixture.Run(command, second, "--json");
        var (both, _, code) = Fixture.Run(command, first, second, "--json");

        // 拼起来再按整行去重:两个名字指到同一个东西时,多名那边只印一遍。
        var expected = Rows(a).Concat(Rows(b)).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Rows(both));

        var (withMiss, _, missCode) = Fixture.Run(command, first, "No.Such.Symbol.At.All", "--json");
        Assert.Equal(code, missCode);
        Assert.Equal(Rows(a), Rows(withMiss));
    }
}
