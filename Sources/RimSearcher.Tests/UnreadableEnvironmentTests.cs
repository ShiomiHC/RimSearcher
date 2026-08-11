using RimSearcher.Config;
using RimSearcher.Snapshot;

namespace RimSearcher.Tests;

/// <summary>
/// 环境读不到时,不许说成「你还没有」。
///
/// <c>File.Exists</c> 与 <c>Directory.Exists</c> 在 access-denied 上都返回 <c>false</c>,
/// 与「不存在」逐字同形。不分流的话,一个只读挂载(或权限不足的家目录)会一路走成
/// 「No snapshot is available,去 export 一个」—— 可信、可操作,而且照着做还会再撞一次。
/// 这里守的就是这条分流:**出路可以分支,否定不许跟着分支。**
/// </summary>
public class UnreadableEnvironmentTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "unreadable", tag);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 「存在但打不开」的可移植造法:拿一个**目录**当文件读。真实成因(只读挂载、ACL)
    /// 在测试进程里造不稳,但走的是同一条 catch —— 读失败而非「不存在」。
    /// </summary>
    [Fact]
    public void 配置文件打不开时报错而不是当成空配置()
    {
        var asFile = TempDir("config-is-a-dir");

        var ex = Assert.Throws<TomlError>(() => Toml.Load(asFile));

        Assert.Contains(asFile, ex.Message, StringComparison.Ordinal);
        Assert.Contains("unreadable", ex.Message, StringComparison.Ordinal);
        // 空配置那条路会让每个设置静默失效,消息必须说出这一点。
        Assert.Contains("no setting in it is in effect", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反向的闸:「不存在 = 空表」是既有语义,测试全靠 <c>--config &lt;不存在的路径&gt;</c>
    /// 隔离本机配置(见 <see cref="Fixture.NoConfigPath"/>),首次运行也走这条。
    /// 上面那条改动不许顺手把它一起收紧。
    /// </summary>
    [Fact]
    public void 配置文件不存在仍然是空表()
    {
        var missing = Path.Combine(TempDir("no-config"), "no-such-config.toml");

        var t = Toml.Load(missing);

        Assert.Null(t.String("game_dir"));
        Assert.Empty(t.Values);
    }

    /// <summary>
    /// 目录路径上坐着一个文件 —— <c>EnumerateFiles</c> 在这里失败,与「目录不存在」不同。
    /// </summary>
    [Fact]
    public void 快照目录读不进来时带出原因()
    {
        var notADir = Path.Combine(TempDir("snapdir-is-a-file"), "snapshots");
        File.WriteAllText(notADir, "");
        var config = new RimConfig { Path = Path.Combine(TempDir("snapdir-is-a-file"), "config.toml"), SnapshotDir = notADir };

        var entries = SnapshotCatalog.Enumerate(config, out var unreadable);

        Assert.Empty(entries);
        Assert.NotNull(unreadable);
        Assert.Contains(notADir, unreadable, StringComparison.Ordinal);
    }

    /// <summary>
    /// 目录**不存在**时空列表是真的空 —— 还没导出过是正常开局,那条出路照旧要给。
    /// </summary>
    [Fact]
    public void 快照目录不存在时没有原因可带()
    {
        var missing = Path.Combine(TempDir("no-snapdir"), "snapshots-that-are-not-there");
        var config = new RimConfig { Path = Path.Combine(TempDir("no-snapdir"), "config.toml"), SnapshotDir = missing };

        var entries = SnapshotCatalog.Enumerate(config, out var unreadable);

        Assert.Empty(entries);
        Assert.Null(unreadable);
    }

    /// <summary>
    /// 本轮真正要挡住的那句话:目录读不了时,不许回「去 export 一个」。
    /// </summary>
    [Fact]
    public void 快照目录读不进来时不叫人去export()
    {
        var notADir = Path.Combine(TempDir("resolve-unreadable"), "snapshots");
        File.WriteAllText(notADir, "");
        var config = new RimConfig { Path = Path.Combine(TempDir("resolve-unreadable"), "config.toml"), SnapshotDir = notADir };

        var ex = Assert.Throws<SnapshotFormatError>(() => SnapshotCatalog.Resolve(config, null, null));

        Assert.DoesNotContain("export --modlist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 目录不存在那一侧,原来那句出路必须原样还在 —— 分流不许把正常开局也改掉。
    /// </summary>
    [Fact]
    public void 快照目录不存在时照旧叫人去export()
    {
        var missing = Path.Combine(TempDir("resolve-missing"), "snapshots-that-are-not-there");
        var config = new RimConfig { Path = Path.Combine(TempDir("resolve-missing"), "config.toml"), SnapshotDir = missing };

        var ex = Assert.Throws<SnapshotFormatError>(() => SnapshotCatalog.Resolve(config, null, null));

        Assert.Contains("export --modlist", ex.Message, StringComparison.Ordinal);
    }
}
