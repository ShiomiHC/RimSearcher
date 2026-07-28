using RimSearcher.Core;

namespace RimSearcher.Tests;

// 倒排表的值装在 ConcurrentBag 里，而 bag 的枚举序由**并发写入顺序**决定。这本来只是排版问题，
// 但有三处拿它当结论：
//   - FirstPathOfType 取 files[0] 判**归属**（继承树、类型搜索的 scope 归属都走它）；
//   - GetPath 取 OrderBy(Rank) 的第一条当「读哪份文件」，同源多份时 Rank 并列，兜底就是这个次序；
//   - GetPathsByType 的候选分数恒为 100，同源之间同样只剩这个次序（同 F47 的形状）。
// README 承诺「换个进程、换个索引重建轮次都给同一批结果」，而那句话此前只对打分路径成立。
//
// **并发写入序是造不出来的**，所以这里断言的不是「某个特定的错序」，而是那条不变量本身：
// 多份文件必须按 Ordinal 全序回来。八个文件时，未定序的实现恰好撞上正序的概率是 1/8!。
public class IndexOrderDeterminismTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // 同一个类型摊在八个文件里（partial）。名字刻意让「写入序」与「字母序」不同解。
    private static readonly string[] Parts =
        ["Zulu", "Alpha", "Mike", "Bravo", "Yankee", "Charlie", "Xray", "Delta"];

    private SourceIndexer BuildPartialType(string dir)
    {
        var root = _workspace.Dir(dir);
        foreach (var part in Parts)
            _workspace.WriteFile(Path.Combine(dir, $"ZzMulti_{part}.cs"),
                $"namespace Zz\n{{\n    public partial class ZzMulti\n    {{\n"
                + $"        public int Zz{part};\n    }}\n}}\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        return indexer;
    }

    [Fact]
    public void PathsOfAType_ComeBackInATotalOrder()
    {
        var indexer = BuildPartialType("Core");

        var paths = indexer.GetPathsByType("ZzMulti");

        Assert.Equal(Parts.Length, paths.Count);
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToList(), paths);
    }

    // 上一条的要害不在「有序」而在「可复现」：同一份语料重扫一遍，第一条必须还是同一条——
    // 因为 FirstPathOfType 正是拿 files[0] 去判这个类型属于哪个源的。
    [Fact]
    public void RebuildingTheIndex_KeepsTheSameFirstPath()
    {
        var first = BuildPartialType("Core");
        var second = BuildPartialType("Core2");

        var a = Path.GetFileName(first.GetPathsByType("ZzMulti")[0]);
        var b = Path.GetFileName(second.GetPathsByType("ZzMulti")[0]);

        Assert.Equal(a, b);
        Assert.Equal("ZzMulti_Alpha.cs", a);
    }

    // 冻结前后行为逐字相同，是这个类的既有约定（见 BuildMemberKeyLookups 的注释）。
    // 定序只写在 FreezeIndex 里的话，冻结前那条路会留着旧的随机序。
    [Fact]
    public void TheOrderIsTheSame_BeforeAndAfterFreezing()
    {
        var root = _workspace.Dir("Unfrozen");
        foreach (var part in Parts)
            _workspace.WriteFile(Path.Combine("Unfrozen", $"ZzMulti_{part}.cs"),
                $"namespace Zz\n{{\n    public partial class ZzMulti\n    {{\n"
                + $"        public int Zz{part};\n    }}\n}}\n");

        var unfrozen = new SourceIndexer();
        unfrozen.Scan(root);
        var before = unfrozen.GetPathsByType("ZzMulti").Select(Path.GetFileName).ToList();

        unfrozen.FreezeIndex();
        var after = unfrozen.GetPathsByType("ZzMulti").Select(Path.GetFileName).ToList();

        Assert.Equal(before, after);
    }

    // 同名文件散在多个源里时，read_code 读的是「scope 表达式里排在前面的那个源」（R81）。
    // 那条保证靠 Rank，而**同一个源里**有多份同名文件时 Rank 并列——次序就只剩这里守着。
    [Fact]
    public void SameNamedFilesInOneSource_ResolveToAStablePick()
    {
        var root = _workspace.Dir("Src");
        foreach (var sub in new[] { "Zulu", "Alpha", "Mike" })
            _workspace.WriteFile(Path.Combine("Src", sub, "ZzTwin.cs"),
                $"namespace Zz.{sub}\n{{\n    public class ZzTwin{sub} {{ }}\n}}\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var scope = ScopeCatalog.Build([("vanilla", root)], null, null).Resolve("vanilla");

        var picked = indexer.GetPath("ZzTwin", scope, out var outOfScope);
        var all = indexer.GetPathsByName("ZzTwin", scope);

        Assert.False(outOfScope);
        Assert.Equal(3, all.Count);
        // 选中的那份必须是候选列表的第一条，而候选列表必须是全序的
        Assert.Equal(all[0], picked);
        Assert.Equal(all.OrderBy(p => p, StringComparer.Ordinal).ToList(), all);
    }
}
