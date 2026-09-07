using System.IO.Compression;
using System.Text;
using RimSearcher.Contract;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 截断成因这一路的闸:导出侧分四类 → 四个键 → 四列 → get 那一句说得出是哪一种。
///
/// 立这道闸的直接理由:本项目自己拿总数**连猜错两次**(先说「集合上限 200」、再说
/// 「每 def 条数上限可以当不存在」),两次都是拿一个总数配一条相关性当因果。总数答得出
/// 「被截了」,答不出「往哪个方向放开」,而四种截断的出路互不相同。
///
/// 不走主语料:那份语料里 Bullet_Revolver 的截断**不带**成因键(0.12.0 那代的形态),
/// 十来份基线正靠它钉着那句含糊的「for depth or size」。往它身上加成因等于把
/// 「旧库怎么读」这一档从语料里撤掉,而那一档恰恰是七个官方快照现在的样子。
/// </summary>
public class TruncationCauseTests
{
    private const string Vague = "dropped at export time for depth or size";

    /// <param name="Causes">null = 这一行不带成因键,即 0.13.0 之前的导出器写出来的形态。</param>
    private readonly record struct MiniDef(string Name, int Truncated, (int Cap, int Len, int Dep, int Items)? Causes);

    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "trunc-cause");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Build(string tag, params MiniDef[] defs)
    {
        var dir = Dir();
        var export = Path.Combine(dir, tag + IntermediateFormat.FileExtension);
        var db = Path.Combine(dir, tag + ".db");
        foreach (var p in new[] { export, db }) if (File.Exists(p)) File.Delete(p);

        using (var fs = File.Create(export))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
                .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
                .Str(IntermediateFormat.KeyExporterVersion, "0.13.0")
                .Str(IntermediateFormat.KeyExportedAtUtc, "2026-01-01T00:00:00.0000000Z")
                .Str(IntermediateFormat.KeyGameVersion, Fixture.GameVersion)
                .Str(IntermediateFormat.KeyLanguage, Fixture.Language)
                .Raw(IntermediateFormat.KeyMods,
                     "[" + new JsonLine().Str("package_id", "ludeon.rimworld").Str("name", "Core").Str("version", "1.6") + "]")
                .Raw(IntermediateFormat.KeyLimits, new JsonLine().Int("max_field_depth", 6).ToString())
                .Str(IntermediateFormat.KeyModSettingsHash, "")
                .ToString());

            foreach (var d in defs)
            {
                var line = new JsonLine()
                    .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDef)
                    .Str(IntermediateFormat.KeyDefType, "ThingDef")
                    .Str(IntermediateFormat.KeyDefName, d.Name)
                    .Str(IntermediateFormat.KeyLabel, d.Name)
                    .Str(IntermediateFormat.KeyDescription, "")
                    .Str(IntermediateFormat.KeySourceMod, "ludeon.rimworld")
                    .Str(IntermediateFormat.KeySourceFile, "T.xml")
                    .Bool(IntermediateFormat.KeyGenerated, false)
                    .Str(IntermediateFormat.KeyClass, "Verse.ThingDef")
                    .Fields(IntermediateFormat.KeyFields,
                            [new ExportedField("label", d.Name, DefaultState.Differs)])
                    .Int(IntermediateFormat.KeyFieldsTruncated, d.Truncated);
                if (d.Causes is { } c)
                    line.Int(IntermediateFormat.KeyTruncatedByCap, c.Cap)
                        .Int(IntermediateFormat.KeyTruncatedByLength, c.Len)
                        .Int(IntermediateFormat.KeyTruncatedByDepth, c.Dep)
                        .Int(IntermediateFormat.KeyTruncatedByItems, c.Items);
                w.WriteLine(line.ToString());
            }

            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
                .Int(IntermediateFormat.KeyRecords, defs.Length + 2)
                .Int(IntermediateFormat.KeyDefs, defs.Length)
                .Int(IntermediateFormat.KeyInjections, 0)
                .Int(IntermediateFormat.KeyXmlNodes, 0)
                .ToString());
            w.Flush();
        }

        new SnapshotImporter().Import(export, db);
        return db;
    }

    private static string GetLine(string db, string name)
    {
        var (stdout, _, code) = Fixture.Run("get", name, "--db", db);
        Assert.Equal(0, code);
        return stdout.Split('\n').Single(l => l.Contains("dropped at export time", StringComparison.Ordinal));
    }

    // ---- 入库 ----

    /// <summary>四个成因随 def 落到四列上,并且加起来就是那个总数。</summary>
    [Fact]
    public void 四类成因随def入库()
    {
        var db = Build("in", new MiniDef("Mixed", 6, (3, 2, 1, 0)), new MiniDef("Clean", 0, (0, 0, 0, 0)));
        using var snap = SnapshotDb.Open(db);

        var mixed = snap.GetDefsNamed("Mixed").Single();
        var causes = snap.TruncationCausesFor(mixed.Id);
        Assert.NotNull(causes);
        Assert.Equal((3, 2, 1, 0), (causes!.Cap, causes.Length, causes.Depth, causes.Items));
        Assert.Equal(mixed.FieldsTruncated, causes.Total);

        Assert.Equal(0, snap.TruncationCausesFor(snap.GetDefsNamed("Clean").Single().Id)!.Total);
    }

    /// <summary>
    /// 这份库根本没有那四列(0.13.0 之前建的)时,问出来的是 null 而不是四个零 ——
    /// 「没量过」与「量过了、四类都没发生」是两件事,后者是句假话。
    /// </summary>
    [Fact]
    public void 旧库没有那四列时答不知道而不是答零()
    {
        var db = Build("old", new MiniDef("Mixed", 6, (3, 2, 1, 0)));
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            raw.Open();
            foreach (var col in new[] { "cap", "length", "depth", "items" })
            {
                using var cmd = raw.CreateCommand();
                cmd.CommandText = $"ALTER TABLE defs DROP COLUMN truncated_by_{col}";
                cmd.ExecuteNonQuery();
            }
        }

        using var snap = SnapshotDb.Open(db);
        Assert.False(snap.DefsHaveTruncationBreakdown);
        Assert.Null(snap.TruncationCausesFor(snap.GetDefsNamed("Mixed").Single().Id));
    }

    // ---- 那一句 ----

    /// <summary>成因只有一类时不列举,直说「全是这一种」。</summary>
    [Fact]
    public void 单一成因说全是这一种()
    {
        var db = Build("one", new MiniDef("OneCause", 4, (0, 0, 4, 0)));
        var said = GetLine(db, "OneCause");
        Assert.Contains("at least 4 fields were dropped at export time, all of them nested past the depth cap", said);
        Assert.DoesNotContain(Vague, said);
    }

    /// <summary>多类并存时逐类给数,按条数从多到少 —— 读者要的是「先放开哪一个」。</summary>
    [Fact]
    public void 多成因逐类给数且大的在前()
    {
        var db = Build("many", new MiniDef("Many", 6, (2, 1, 3, 0)));
        var said = GetLine(db, "Many");
        Assert.Contains(
            "at least 6 fields were dropped at export time: 3 nested past the depth cap, "
            + "2 past this def's field cap, 1 with the value cut to the length cap", said);
        Assert.DoesNotContain(Vague, said);
        // 没发生的那一类一个字都不占 —— 印成零会让人以为集合上限也在参与。
        Assert.DoesNotContain("list item cap", said);
    }

    /// <summary>
    /// 只丢了一条时谓语跟着变,也不写「all of them」—— 一条东西没有「全都是」可言。
    ///
    /// 这一档在语料上永远碰不到:那里被截的 def 丢的是 3 条,于是 were 一直是对的。
    /// 真快照上恰恰反过来 —— baseline 的 27 个被截 def 里 24 个只丢了 1 条。
    /// </summary>
    [Fact]
    public void 只丢一条时谓语用单数()
    {
        var db = Build("single", new MiniDef("One", 1, (0, 0, 0, 1)));
        var said = GetLine(db, "One");
        Assert.Contains("at least 1 field was dropped at export time, past the list item cap", said);
        Assert.DoesNotContain("all of them", said);
        Assert.DoesNotContain("field were", said);
    }

    /// <summary>
    /// 0.13.0 之前导出的行(没有成因键)入了新库,四列拿的是 DEFAULT 0。
    /// 这时要退回那句含糊的,而不是把四个零当成量出来的结果印出去。
    /// </summary>
    [Fact]
    public void 旧导出没带成因键时退回含糊那句()
    {
        var db = Build("legacy", new MiniDef("Legacy", 3, null), new MiniDef("LegacyOne", 1, null));
        var said = GetLine(db, "Legacy");
        Assert.Contains("at least 3 fields were " + Vague, said);
        Assert.DoesNotContain("all of them", said);

        // 含糊那句的谓语也得跟着数走 —— 两句共用同一个开头,漏改一处就只有一半是对的。
        Assert.Contains("at least 1 field was " + Vague, GetLine(db, "LegacyOne"));
    }
}
