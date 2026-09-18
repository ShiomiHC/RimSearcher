using RimSearcher.Config;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 层账(<see cref="DataLayers"/>)是「这份库没有这一层」的唯一产地。闸守两件事:
/// 账的形状(词表封闭、ok 行不带出路、缺席行必带成因)与几条判据的取值。
/// </summary>
public class DataLayerTests
{
    private static IReadOnlyList<LayerRow> FixtureLedger()
    {
        using var db = SnapshotDb.Open(Fixture.Db);
        return DataLayers.Ledger(db, new RimConfig(), "fixture");
    }

    [Fact]
    public void 账的形状_层名唯一_ok行不带出路_缺席行必带成因()
    {
        var rows = FixtureLedger();
        Assert.Equal(rows.Count, rows.Select(r => r.Layer).Distinct(StringComparer.Ordinal).Count());
        foreach (var r in rows)
        {
            // 词表封闭:渲染函数对每个枚举值都有一个拼法,拼法里没有大写与空格。
            var text = LayerStateText.Render(r.State);
            Assert.Matches("^[a-z-]+$", text);
            if (r.Complete)
            {
                Assert.Null(r.Next);
                Assert.Null(r.Why);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(r.Why), $"{r.Layer}: a short row must say why");
                Assert.False(string.IsNullOrWhiteSpace(r.Next), $"{r.Layer}: a short row must say what next");
            }
        }
    }

    /// <summary>
    /// fixture 的导入没配 <c>mod_roots</c> 而没关收割 —— 成因位记的是 no-roots,于是账上是
    /// unconfigured 而不是 unmeasured,出路先配根再导入,导入命令填的是建库时记下的文件名。
    /// </summary>
    [Fact]
    public void 磁盘语言层_状态读建库时记下的成因_出路看现机()
    {
        var row = FixtureLedger().Single(r => r.Layer == DataLayers.DiskTranslations);
        Assert.Equal(LayerState.Unconfigured, row.State);
        Assert.Equal("set 'mod_roots' in the config file, then rimsearcher snapshot import fixture.rsx.jsonl.gz --name fixture", row.Next);

        // 现机配了根:状态不改(库是没根的时候建的,那一次确实没地方扫),出路只剩导一次。
        using var db = SnapshotDb.Open(Fixture.Db);
        var withRoots = DataLayers.DiskTranslationsRow(db, new RimConfig { ModRoots = ["S:/nowhere"] }, "fixture");
        Assert.Equal(LayerState.Unconfigured, withRoots.State);
        Assert.Equal("rimsearcher snapshot import fixture.rsx.jsonl.gz --name fixture", withRoots.Next);
        Assert.Equal(SnapshotSchema.TranslationsHarvestNoRoots, db.TranslationsHarvest);
    }

    /// <summary>
    /// 经济层的三种缺席各是一态,第四态(键不在)是 pre-measure。这里拿 fixture 的取值当锚:
    /// 它的经济层是量过的。
    /// </summary>
    [Fact]
    public void 经济层_fixture量过_账上是ok()
    {
        var row = FixtureLedger().Single(r => r.Layer == DataLayers.Economy);
        Assert.Equal(LayerState.Ok, row.State);
    }
}
