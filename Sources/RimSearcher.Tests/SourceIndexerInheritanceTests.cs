using RimSearcher.Core;

namespace RimSearcher.Tests;

// 继承数据分两份：主基类链（一对一，向上走）与直接超类型（一对多，反查实现者）。
// 原先两份共用「基类型列表第一项」，接口实现者因此全部丢失。
public class SourceIndexerInheritanceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private (SourceIndexer Indexer, ScopeSelection Scope) Index(string fileName, string code)
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", fileName), code);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null).Resolve("vanilla"));
    }

    private static List<string> Inheritors(SourceIndexer indexer, ScopeSelection scope, string baseType)
        => indexer.GetInheritors(baseType, scope).Items.Select(entry => entry.Item).ToList();

    // 回归：`class Worker : BaseWorker, IDisposable` 只记了 BaseWorker，
    // 按 IDisposable 查实现者恒为空 —— 而按接口找实现是这个工具的主要用途之一。
    [Fact]
    public void Inheritors_IncludeEveryDirectSuperType()
    {
        var (indexer, scope) = Index("Worker.cs", """
            namespace RimWorld
            {
                public class BaseWorker { }
                public interface IDisposable { }
                public interface IExposable { }
                public class Worker : BaseWorker, IDisposable, IExposable { }
            }
            """);

        Assert.Contains("RimWorld.Worker", Inheritors(indexer, scope, "IDisposable"));
        Assert.Contains("RimWorld.Worker", Inheritors(indexer, scope, "IExposable"));
        // 基类那条边不能因此丢掉
        Assert.Contains("RimWorld.Worker", Inheritors(indexer, scope, "BaseWorker"));
    }

    // 只实现接口、没有基类的类型也要能被接口查到
    [Fact]
    public void Inheritors_FindInterfaceOnlyImplementors()
    {
        var (indexer, scope) = Index("Marker.cs", """
            namespace RimWorld
            {
                public interface IExposable { }
                public class Marker : IExposable { }
            }
            """);

        Assert.Contains("RimWorld.Marker", Inheritors(indexer, scope, "IExposable"));
    }

    // 泛型实参剥掉后才查得到：类型名索引里从来没有 `List<Thing>` 这种键
    [Fact]
    public void Inheritors_IgnoreGenericArguments()
    {
        var (indexer, scope) = Index("Repo.cs", """
            namespace RimWorld
            {
                public class Repo : Store<Thing>, IComparable<Repo> { }
            }
            """);

        Assert.Contains("RimWorld.Repo", Inheritors(indexer, scope, "Store"));
        Assert.Contains("RimWorld.Repo", Inheritors(indexer, scope, "IComparable"));
    }

    // 主基类链不回退：仍然沿基类一路向上走
    [Fact]
    public void InheritanceChain_StillWalksTheBaseClassChain()
    {
        var (indexer, _) = Index("Chain.cs", """
            namespace RimWorld
            {
                public class ThingComp { }
                public class CompShield : ThingComp { }
                public class CompShieldPlus : CompShield, IExposable { }
            }
            """);

        var chain = indexer.GetInheritanceChain("CompShieldPlus");

        Assert.Equal(
            [("RimWorld.CompShieldPlus", "CompShield"), ("RimWorld.CompShield", "ThingComp")],
            chain);
    }

    // 只实现接口的类型没有主基类：不能把 IExposable 记成它的基类
    [Fact]
    public void InheritanceChain_TreatsInterfaceOnlyBaseListAsNoBase()
    {
        var (indexer, _) = Index("Marker.cs", """
            namespace RimWorld
            {
                public class Marker : IExposable { }
            }
            """);

        Assert.Empty(indexer.GetInheritanceChain("Marker"));
    }

    // 接口自身例外：`interface IFoo : IBar` 的第一项就是它扩展的接口，那条边有意义
    [Fact]
    public void InheritanceChain_KeepsInterfaceExtensionEdges()
    {
        var (indexer, _) = Index("Interfaces.cs", """
            namespace RimWorld
            {
                public interface IBar { }
                public interface IFoo : IBar { }
            }
            """);

        Assert.Equal([("RimWorld.IFoo", "IBar")], indexer.GetInheritanceChain("IFoo"));
    }

    // partial 类可以把基类型列表拆到多处，合并时取并集才不丢边
    [Fact]
    public void Inheritors_MergePartialDeclarations()
    {
        var (indexer, scope) = Index("Partial.cs", """
            namespace RimWorld
            {
                public partial class Worker : BaseWorker { }
                public partial class Worker : IExposable { }
            }
            """);

        Assert.Contains("RimWorld.Worker", Inheritors(indexer, scope, "BaseWorker"));
        Assert.Contains("RimWorld.Worker", Inheritors(indexer, scope, "IExposable"));
        Assert.Equal([("RimWorld.Worker", "BaseWorker")], indexer.GetInheritanceChain("Worker"));
    }

    // 快照往返后两份索引都要还原（缓存命中时走的是这条路）
    [Fact]
    public void Snapshot_RoundTripsBothInheritanceIndexes()
    {
        var (indexer, scope) = Index("Worker.cs", """
            namespace RimWorld
            {
                public class BaseWorker { }
                public class Worker : BaseWorker, IExposable { }
            }
            """);

        var restored = new SourceIndexer();
        restored.ImportSnapshot(indexer.ExportSnapshot());
        restored.FreezeIndex();

        Assert.Contains("RimWorld.Worker", Inheritors(restored, scope, "IExposable"));
        Assert.Contains("RimWorld.Worker", Inheritors(restored, scope, "BaseWorker"));
        Assert.Equal([("RimWorld.Worker", "BaseWorker")], restored.GetInheritanceChain("Worker"));
    }

    // 短名对应多个全名时（跨命名空间/跨源同名类，实测里很常见）原先只试数组第一项，
    // 而那份数组的次序由索引期的并发写入决定：撞上没有基类的那个同名类，整条链就成了空——
    // inspect 于是在 Outline 明明列着 `X : Y` 的同一次返回里不画继承图。
    [Fact]
    public void InheritanceChain_FindsTheOverloadThatActuallyHasABase()
    {
        var (indexer, _) = Index("Ambiguous.cs", """
            namespace Other
            {
                public class CompShield { }
            }

            namespace RimWorld
            {
                public class ThingComp { }
                public class CompShield : ThingComp { }
            }
            """);

        Assert.Equal([("RimWorld.CompShield", "ThingComp")], indexer.GetInheritanceChain("CompShield"));
    }
}
