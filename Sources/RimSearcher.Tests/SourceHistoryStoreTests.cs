using RimSearcher.Core;

namespace RimSearcher.Tests;

public class SourceHistoryStoreTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private SourceHistoryStore Store(int depth) => new(_workspace.Dir("cache"), depth);

    [Fact]
    public void Capture_ClassifiesAddedModifiedRemoved()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");

        _workspace.WriteFile(Path.Combine("src", "Kept.cs"), "same");
        _workspace.WriteFile(Path.Combine("src", "Changed.cs"), "old");
        _workspace.WriteFile(Path.Combine("src", "Gone.cs"), "bye");

        _workspace.WriteFile(Path.Combine("staging", "Kept.cs"), "same");
        _workspace.WriteFile(Path.Combine("staging", "Changed.cs"), "new");
        _workspace.WriteFile(Path.Combine("staging", "Fresh.cs"), "hi");

        var changes = Store(depth: 2).Capture("Core", source, staging);

        Assert.Equal(1, changes.Added);
        Assert.Equal(1, changes.Modified);
        Assert.Equal(1, changes.Removed);
        Assert.True(changes.Any);
        Assert.DoesNotContain(changes.Changes, c => c.RelativePath == "Kept.cs");
    }

    [Fact]
    public void DepthZero_ComputesChangesButWritesNothing()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        _workspace.WriteFile(Path.Combine("src", "A.cs"), "old");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "new");

        var store = Store(depth: 0);
        var changes = store.Capture("Core", source, staging);

        Assert.False(store.Enabled);
        Assert.Equal(1, changes.Modified);
        Assert.Empty(store.ListVersions("Core"));
    }

    // 反向增量：只归档被改写和被删除的旧文件，新增文件在旧版本里本就不存在
    [Fact]
    public void Capture_ArchivesOnlyOverwrittenAndRemovedFiles()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");

        _workspace.WriteFile(Path.Combine("src", "Changed.cs"), "old-content");
        _workspace.WriteFile(Path.Combine("src", "Gone.cs"), "removed-content");
        _workspace.WriteFile(Path.Combine("staging", "Changed.cs"), "new-content");
        _workspace.WriteFile(Path.Combine("staging", "Fresh.cs"), "added-content");

        var store = Store(depth: 2);
        store.Capture("Core", source, staging);

        var version = Assert.Single(store.ListVersions("Core"));
        Assert.Equal("v0001", version.Id);

        Assert.Equal("old-content", store.ReadArchived("Core", "v0001", "Changed.cs"));
        Assert.Equal("removed-content", store.ReadArchived("Core", "v0001", "Gone.cs"));
        Assert.Null(store.ReadArchived("Core", "v0001", "Fresh.cs"));
        Assert.True(version.ArchivedBytes > 0);
    }

    [Fact]
    public void Rotate_KeepsOnlyTheConfiguredDepth()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        var store = Store(depth: 1);

        _workspace.WriteFile(Path.Combine("src", "A.cs"), "v1");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "v2");
        store.Capture("Core", source, staging);

        // 模拟一次转正，再同步一版
        _workspace.WriteFile(Path.Combine("src", "A.cs"), "v2");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "v3");
        store.Capture("Core", source, staging);

        var versions = store.ListVersions("Core");
        Assert.Single(versions);
        Assert.Equal("v0002", versions[0].Id);

        // 被轮转掉的那一版连同归档目录一起消失
        Assert.Null(store.ReadArchived("Core", "v0001", "A.cs"));
        Assert.Equal("v2", store.ReadArchived("Core", "v0002", "A.cs"));
    }

    [Fact]
    public void DiffAgainst_ComparesArchivedSnapshotWithCurrentDisk()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        var store = Store(depth: 3);

        _workspace.WriteFile(Path.Combine("src", "A.cs"), "v1");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "v2");
        store.Capture("Core", source, staging);

        // 归档记的是同步前的树；把磁盘改成同步后的样子再比
        _workspace.WriteFile(Path.Combine("src", "A.cs"), "v2");
        _workspace.WriteFile(Path.Combine("src", "B.cs"), "brand-new");

        var diff = store.DiffAgainst("Core", source);

        Assert.NotNull(diff);
        Assert.Equal(1, diff!.Modified);
        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void DiffAgainst_WithoutHistory_ReturnsNull()
        => Assert.Null(Store(depth: 2).DiffAgainst("Core", _workspace.Dir("src")));

    [Fact]
    public void DiffAgainst_WithUnknownVersion_ReturnsNull()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        var store = Store(depth: 2);

        _workspace.WriteFile(Path.Combine("src", "A.cs"), "v1");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "v2");
        store.Capture("Core", source, staging);

        Assert.Null(store.DiffAgainst("Core", source, "v9999"));
    }

    // 源名可含 / 或 :，直接拿来做目录名会炸
    [Fact]
    public void SourceNamesWithPathSeparators_AreSanitized()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        var store = Store(depth: 2);

        _workspace.WriteFile(Path.Combine("src", "A.cs"), "old");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "new");

        store.Capture(@"Vendor/Mod:1.6", source, staging);

        Assert.Single(store.ListVersions(@"Vendor/Mod:1.6"));
    }

    [Fact]
    public void IdenticalTrees_ProduceNoChanges()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        _workspace.WriteFile(Path.Combine("src", "A.cs"), "same");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "same");

        var store = Store(depth: 2);
        var changes = store.Capture("Core", source, staging);

        Assert.False(changes.Any);
        Assert.Empty(store.ListVersions("Core"));
    }

    // 只比较 .cs：反编译产物之外的东西（工程文件、资源）不该进历史
    [Fact]
    public void NonCSharpFiles_AreIgnored()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        _workspace.WriteFile(Path.Combine("src", "A.cs"), "same");
        _workspace.WriteFile(Path.Combine("src", "notes.txt"), "old");
        _workspace.WriteFile(Path.Combine("staging", "A.cs"), "same");
        _workspace.WriteFile(Path.Combine("staging", "notes.txt"), "new");

        Assert.False(Store(depth: 2).Capture("Core", source, staging).Any);
    }

    [Fact]
    public void NestedDirectories_AreTrackedByRelativePath()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");
        _workspace.WriteFile(Path.Combine("src", "RimWorld", "CompShield.cs"), "old");
        _workspace.WriteFile(Path.Combine("staging", "RimWorld", "CompShield.cs"), "new");

        var store = Store(depth: 2);
        var changes = store.Capture("Core", source, staging);

        var change = Assert.Single(changes.Changes);
        Assert.Equal(Path.Combine("RimWorld", "CompShield.cs"), change.RelativePath);
        Assert.Equal(FileChangeKind.Modified, change.Kind);
        Assert.Equal("old", store.ReadArchived("Core", "v0001", change.RelativePath));
    }
}
