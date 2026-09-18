using RimSearcher.Cli;
using RimSearcher.Commands;
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
        Assert.Equal(Runner.ExitFoundElsewhere, getCode);   // found_as:它是 xml node

        var (_, _, inheritCode) = Fixture.Run("inherit", "BaseBullet");
        Assert.Equal(0, inheritCode);
    }

    /// <summary>
    /// 补丁计数的口径由列名自陈,不再靠句子(Docs/25 丁2,2026-09-18)。此前 identity 块后跟三支
    /// 散文(被点名 N 次 / 0 数的是什么 / 无名没量),来历是 <c>Human</c> 那个反例:它声明了 Name=、
    /// 那一格是 0,同时被 HAR 按 defName 换掉 class —— 沉默的 0 断言了一件假事。现在三格并排
    /// (patch_ops_name / patch_ops_defname / patch_ops_label),0 / 2 / 1 自己读得出来;
    /// 只在旧库缺后两格时才出声,形态是 absent 表一行(层 patch_ops_defname_label,pre-measure,
    /// 出路重导)。thingClass 与通配符哪一格都不算,是这一层的口径,住在 --help。
    /// </summary>
    [Fact]
    public void patch计数三格并排_旧库缺两格时absent表说破()
    {
        // 主 fixture 是旧口径(只数 @Name=):有 Name= 的节点一行 absent,不再有任何解释句。
        var unpatched = Text("inherit", "BaseProjectile");
        Assert.Contains("patch_ops_defname_label  pre-measure  rimsearcher export --modlist ", unpatched,
                        StringComparison.Ordinal);
        Assert.Contains(InheritCommand.PatchOpsName + "  0", unpatched, StringComparison.Ordinal);
        Assert.DoesNotContain("@Name=", unpatched, StringComparison.Ordinal);
        Assert.DoesNotContain("leaves no trace", unpatched, StringComparison.Ordinal);

        // 新口径的库:三格都在,absent 表不出。
        var counted = Text("inherit", "BaseGun", "--db", Fixture.PresenceDb);
        Assert.DoesNotContain("patch_ops_defname_label", counted, StringComparison.Ordinal);
        Assert.Contains("patch_ops_defname", counted, StringComparison.Ordinal);
        Assert.Contains("patch_ops_label", counted, StringComparison.Ordinal);
    }

    /// <summary>
    /// patch 计数的口径只在 <c>inherit --help</c> 的 Remarks 里出声(输出面 2026-09-18 起只有列名
    /// 与 absent 表)。r14 抓到一个受测者读了输出的新句、再引 help 的旧句把它降格成「通用免责
    /// 措辞」驳回 —— 两处不再各说一遍,help 就是唯一那一处,它得把三格与遗漏面都说全:
    /// 遗漏面不许举 defName 当代表(抽象节点根本没有 defName)、「0 = 原样」这类正面断言不许回来。
    /// </summary>
    [Fact]
    public void patch计数的口径只住help且说全三格与遗漏面()
    {
        var help = Text("inherit", "--help");
        Assert.Contains("@Name=", help, StringComparison.Ordinal);
        Assert.Contains("by thingClass", help, StringComparison.Ordinal);
        Assert.Contains("by a wildcard", help, StringComparison.Ordinal);
        Assert.Contains(InheritCommand.PatchOpsName, help, StringComparison.Ordinal);
        Assert.Contains("patch_ops_defname", help, StringComparison.Ordinal);
        Assert.Contains("patch_ops_label", help, StringComparison.Ordinal);
        Assert.DoesNotContain("exactly what the game read", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// 祖先被 patch 点名 = 这个节点从它继承来的字段跟着变,而 identity 块那一格看不见这件事 ——
    /// 它只数点名本节点自己的 xpath。r15 抓到的正是这个缺口:真快照里 <c>BaseMechanoid</c> 自己
    /// patch_ops 是 0、父 <c>BasePawn</c> 是 1(全部机械族的 comps 都被那条改了),六个受测者里
    /// 只有两个想到再往上跑一次 <c>inherit BasePawn</c>,其余四个答「节点自身未被改」——
    /// 字面不错,漏掉的是真正生效的那条。判据当场算得出来:那些数就在祖先表已经查出的行里。
    ///
    /// **列条件化不是为省地方**:全零时渲染器会把它折进「Same in every row」那句、印成
    /// <c>patch_ops=0</c>,而那正是本文件另外两条闸一直在修的形态 —— 一个沉默的 0 断言假事。
    /// 条件化之后,沉默只发生在「祖先侧确实没有已知 patch」时,而那时沉默推不出错结论。
    /// </summary>
    [Fact]
    public void 祖先被点名时在祖先表上就看得见()
    {
        // Bullet_Revolver 自己 n/a,父 BaseBullet 是 2 —— 与 BaseMechanoid → BasePawn 同构。
        var viaParent = Text("inherit", "Bullet_Revolver");
        // 带上数字才省得掉「再往上跑一次」那个动作;只说「有祖先被改过」等于把活推回去。
        // 「def 继承祖先的字段、补丁经继承到达」是机制,住 Remarks(Docs/25 丁2);句里只剩数与列名。
        Assert.Contains("Patches also name 1 ancestor above (patch_ops_name column)", viaParent, StringComparison.Ordinal);

        // Firefoam 的整条链全 0:那句话一个字不许出现。列的在场与否由字节闸
        // inherit-def / inherit-ancestors-clean 两份基线对照钉住。
        Assert.DoesNotContain("Patches also name", Text("inherit", "Firefoam"),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// 「链到根了」与「父节点所在的 mod 没启用」必须分得开。此前是表下一句散文说破;
    /// 2026-09-18 起断链的那个名字自己占 ancestors 一行,declared_in = not-in-snapshot ——
    /// 分不开的两态在表里分开了,那句话就不需要了(Docs/25 §19)。
    /// </summary>
    [Fact]
    public void 断链与到根分得开()
    {
        var broken = Text("inherit", "TestModGun");
        Assert.Contains("BaseFromSomeDisabledMod", broken, StringComparison.Ordinal);
        Assert.Contains(InheritCommand.NotInSnapshot, broken, StringComparison.Ordinal);
        Assert.DoesNotContain("not enabled", broken, StringComparison.Ordinal);

        // Bullet_Revolver 一路走到 BaseProjectile(它没有 ParentName),是真的到根了。
        var whole = Text("inherit", "Bullet_Revolver");
        Assert.Contains("BaseProjectile", whole, StringComparison.Ordinal);
        Assert.DoesNotContain(InheritCommand.NotInSnapshot, whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 零结果的三种成因互斥分流:名字打错 / 不参与继承 / 这一层里真没有。
    /// 混报会把「你问错了层」说成「不存在」。
    /// </summary>
    [Fact]
    public void 不参与继承与不存在分得开()
    {
        // 「是个 def,只是不在这一层」是 found_as 的一行(is = def,next = get);2026-09-18 之前是
        // 一句「takes part in no inheritance」加一句 PatchOperation 的情景假设。
        var notInLayer = Text("inherit", "Apparel_ShieldBelt");
        Assert.Contains("No XML node named 'Apparel_ShieldBelt'", notInLayer, StringComparison.Ordinal);
        Assert.Contains("Apparel_ShieldBelt  def  ThingDef in ", notInLayer, StringComparison.Ordinal);
        Assert.Contains("rimsearcher get Apparel_ShieldBelt", notInLayer, StringComparison.Ordinal);

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
        Assert.Contains("abstract xml node", node, StringComparison.Ordinal);
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

    /// <summary>声明区的 kind 分类要对得上:计数是 count。(patch 差异那句 boundary 2026-09-18 退成列名。)</summary>
    [Fact]
    public void 声明区分类正确()
    {
        var kinds = Kinds("inherit", "BaseBullet");
        Assert.Contains("count", kinds);
    }
}
