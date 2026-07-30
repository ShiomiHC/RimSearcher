using System.Text;

namespace RimSearcher.Tests;

/// <summary>
/// 字节级基线(04 的主闸)。跑一批固定调用,把 stdout 逐字节钉进 <c>Snapshots/</c>。
///
/// 这道闸看的是**输出契约**,不是某个断言想到的那几条性质。措辞、列宽、声明区位置、
/// 空行、行尾 —— 凡是调用方读得到的东西,改动都会在这里变红。它抓得住的正是断言抓不住的
/// 那一类回归:「谁也没想到要断言的那句话被顺手改了」。
///
/// 基线不对时用 <c>RIMSEARCHER_UPDATE_SNAPSHOTS=1 dotnet test</c> 重写,然后**读 diff**。
/// 重写是一个动作,不是一个默认值 —— 默认重写等于没有闸。
/// </summary>
public class OutputSnapshotTests
{
    /// <summary>
    /// 每条基线都是一次真实调用。名字既是文件名也是这条用例在说什么。
    /// 覆盖面按 07 的真实意图分布挑:查名字、看细节、反查、值域、代码搜索、报错路径。
    /// </summary>
    public static TheoryData<string, string[]> Cases => new()
    {
        { "search-hit",            ["search", "shield"] },
        { "search-miss",           ["search", "zzzznothing"] },
        { "search-miss-classlike", ["search", "CompShield"] },
        { "search-typo",           ["search", "Aparel_ShieldBelt"] },
        // 混合命中:两条 FTS + 一条只有子串扫描找得到,再加上一个小 limit ——
        // 「N of M 的 M 不随 limit 变」除了 GrammarTests 那条断言,这里也逐字节钉一份。
        { "search-substring",      ["search", "VoidNode"] },
        { "search-substring-cap",  ["search", "VoidNode", "--limit", "2"] },
        { "get-full",              ["get", "Apparel_ShieldBelt"] },
        { "get-path-filter",       ["get", "Apparel_ShieldBelt", "--path", "comps"] },
        { "get-path-no-match",     ["get", "Apparel_ShieldBelt", "--path", "zzzz"] },
        { "get-truncated-export",  ["get", "Bullet_Revolver"] },
        { "get-generated",         ["get", "Meat_Muffalo"] },
        { "get-missing",           ["get", "NoSuchDef"] },
        // R2:同名跨 def_type。两份基线的分工是「不带 --type 时提示在场」与「带 --type 时
        // 提示不许消失、且父节点/译文不许串味」—— 后者是本轮最恶劣的一处:按 SKILL 教的
        // 加了 --type 之后,同名提示反而没了,错行留下,对冲归零。
        { "get-name-collision",    ["get", "Firefoam"] },
        { "get-name-collision-typed", ["get", "Firefoam", "--type", "StatDef"] },
        // 桶名不一致(XML 根元素 TestVariantDef,def 落在 TestBaseDef 桶)时 inherits_from
        // 仍要在场 —— R2 的修法收窄了关联条件,这一份守的是它没有收窄过头。
        { "get-bucket-mismatch",   ["get", "VariantOne"] },
        { "find-hit",              ["find", "compClass", "RimWorld.CompShield"] },
        { "find-miss-compprops",   ["find", "compClass", "CompProperties_Shield"] },
        { "find-miss-field",       ["find", "noSuchField", "x"] },
        // 继承层的四条路各钉一份:抽象节点(有子、被 patch 点名)、具体 def(往上走)、
        // 断链(父不在快照里)、名字不在这一层。四条的措辞各说一件不同的事,
        // 而它们混起来正是「零结果一律报最强的那种」那类事故的温床。
        { "inherit-abstract",      ["inherit", "BaseBullet"] },
        { "inherit-def",           ["inherit", "Bullet_Revolver"] },
        { "inherit-broken-chain",  ["inherit", "TestModGun"] },
        { "inherit-not-in-layer",  ["inherit", "Apparel_ShieldBelt"] },
        { "inherit-missing",       ["inherit", "NoSuchNode"] },
        { "get-xml-node-only",     ["get", "BaseBullet"] },
        { "list-limited",          ["list", "ThingDef", "--limit", "2"] },
        { "list-scope-empty",      ["list", "HediffDef", "--scope", "test.mod"] },
        { "fields-filtered",       ["fields", "ThingDef", "--path", "comps"] },
        { "values-coverage",       ["values", "compClass"] },
        { "values-miss",           ["values", "noSuchField"] },
        { "types",                 ["types"] },
        { "mods",                  ["mods"] },
        { "json-mode",             ["get", "Apparel_ShieldBelt", "--limit", "3", "--json"] },
        { "usage-unknown-flag",    ["search", "shield", "--lmit", "5"] },
        { "usage-unknown-command", ["serach", "shield"] },
        { "help-overview",         ["--help"] },
        { "help-get",              ["get", "--help"] },
        { "help-code-search",      ["code-search", "--help"] },
        { "help-sources-sync",     ["sources", "sync", "--help"] },
        // 没配 decompiled_dir 时说的那句话。反编译树是**唯一**不在快照里的数据源,
        // 于是「没有它」这条路必然被走到,而它必须说清该往哪补一行配置。
        { "sources-not-configured", ["sources", "list"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 输出与基线逐字节一致(string name, string[] argv)
    {
        var (stdout, stderr, code) = Fixture.Run(argv);

        // stdout / stderr / 退出码是同一个契约的三面。只钉 stdout,一条命令从「有结果」
        // 变成「报错但仍打印点什么」也能悄悄溜过去。
        var actual = new StringBuilder()
            .Append("$ rimsearcher ").Append(string.Join(' ', argv)).Append('\n')
            .Append("exit ").Append(code).Append('\n')
            .Append("--- stdout ---\n").Append(stdout)
            .Append("--- stderr ---\n").Append(stderr)
            .ToString()
            .Replace("\r\n", "\n");

        var path = Path.Combine(SnapshotDir, name + ".txt");

        if (Environment.GetEnvironmentVariable("RIMSEARCHER_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(SnapshotDir);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path),
            $"No baseline for '{name}'. Run with RIMSEARCHER_UPDATE_SNAPSHOTS=1 to create it, then read the diff.");

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 基线目录里不许有没人认领的文件。删掉一条用例却留下它的基线,那份文件就成了
    /// 「看起来还在闸内、其实早没人跑」的东西 —— 比没有闸更坏。
    /// </summary>
    [Fact]
    public void 基线目录里没有孤儿文件()
    {
        if (!Directory.Exists(SnapshotDir)) return;
        var claimed = Cases.Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.EnumerateFiles(SnapshotDir, "*.txt")
                               .Select(Path.GetFileNameWithoutExtension)
                               .Where(n => n is not null && !claimed.Contains(n))
                               .ToList();
        Assert.True(orphans.Count == 0, $"Baselines with no case: {string.Join(", ", orphans)}.");
    }

    internal static string SnapshotDir => Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Tests", "Snapshots");
}
