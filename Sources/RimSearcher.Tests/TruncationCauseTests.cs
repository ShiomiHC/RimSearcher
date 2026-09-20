using System.IO.Compression;
using System.Text;
using RimSearcher.Commands;
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
        var dir = Path.Combine(TestTemp.Root, "trunc-cause");
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

    /// <summary>那条截断提示句。语料里每个 mini def 只有一个字段(label),于是表上的路径数恒为 1。</summary>
    private static string GetLine(string db, string name)
    {
        var (stdout, _, code) = Fixture.Run("get", name, "--db", db);
        Assert.Equal(0, code);
        return stdout.Split('\n').Single(
            l => l.Contains("The exporter stopped short", StringComparison.Ordinal)
                 || l.Contains("this def has is indexed", StringComparison.Ordinal)
                 || l.Contains("this def has are indexed", StringComparison.Ordinal));
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

    /// <summary>
    /// 四类各带自己的名词,不共用「N fields were dropped」那个头。
    ///
    /// 四个数**数的不是同一样东西**:条数上限是每碰一格加一;值长度那一类一条路径都没丢;
    /// 深度与集合各是「一整棵没走的子树 / 一条没走完的列表」算一。共用一个名词时,
    /// 现在盘上七个库里最大的那一类(值长度)会被读成「丢了 29 个字段」。
    /// </summary>
    [Theory]
    [InlineData(4, 0, 0, 0, "4 fields were dropped past this def's field cap")]
    [InlineData(0, 4, 0, 0, "4 values were cut to the length cap")]
    [InlineData(0, 0, 4, 0, "4 nested objects were left unwalked past the depth cap")]
    [InlineData(0, 0, 0, 4, "4 lists stopped at the item cap")]
    [InlineData(1, 0, 0, 0, "1 field was dropped past this def's field cap")]
    [InlineData(0, 1, 0, 0, "1 value was cut to the length cap")]
    [InlineData(0, 0, 1, 0, "1 nested object was left unwalked past the depth cap")]
    [InlineData(0, 0, 0, 1, "1 list stopped at the item cap")]
    public void 每一类各带自己的名词(int cap, int len, int dep, int items, string expected)
    {
        var db = Build($"noun{cap}{len}{dep}{items}",
                       new MiniDef("N", cap + len + dep + items, (cap, len, dep, items)));
        var said = GetLine(db, "N");
        Assert.Contains(expected, said);
        Assert.DoesNotContain(Vague, said);
    }

    /// <summary>多类并存时逐类给数,按条数从多到少 —— 读者要的是「先放开哪一个」。</summary>
    [Fact]
    public void 多成因逐类给数且大的在前()
    {
        var db = Build("many", new MiniDef("Many", 6, (2, 1, 3, 0)));
        var said = GetLine(db, "Many");
        Assert.Contains(
            "3 nested objects were left unwalked past the depth cap; "
            + "2 fields were dropped past this def's field cap; "
            + "1 value was cut to the length cap", said);
        Assert.DoesNotContain(Vague, said);
        // 没发生的那一类一个字都不占 —— 印成零会让人以为集合上限也在参与。
        Assert.DoesNotContain("item cap", said);
    }

    /// <summary>
    /// 只有值被切时,**没有一条路径缺席** —— 那一格就在表里,缺的是它后半截的字。
    ///
    /// 此前这一支会印「a path missing from the list below is not evidence that the def
    /// lacks it」并把这几条加进路径总数:读者被派去找不存在的缺行,而那个总数把已经
    /// 数过的行又数了一遍。现在盘上七个库里这一类恰恰是最大的一类。
    /// </summary>
    [Fact]
    public void 只有值被切时不报缺行也不加进总数()
    {
        var db = Build("cut", new MiniDef("Cut", 3, (0, 3, 0, 0)));
        var said = GetLine(db, "Cut");
        // 「没缺行」与那个数在同一句里说完 —— 表上只有 label 一条路径,而它就是全部。
        Assert.Contains("The only field path this def has is indexed", said);
        Assert.Contains("what the exporter cut is text, not rows: 3 values were cut to the length cap", said);
        Assert.DoesNotContain("dropped", said);
    }

    // 「values / where 落空时指向 'snapshot truncated'」那段 2026-09-20 删(40 次实印、0 次照做),
    // 连同「只在真分过类的库上说 which cap it hit」那条闸;分类与否的差别现在只在 get 的截断行与
    // snapshot status 上体现,各自有闸。

    /// <summary>
    /// 混着来时,加进总数的只有**没进索引**的那几类。值长度不算 —— 它的路径就在表里。
    /// </summary>
    [Fact]
    public void 加进总数的只有没进索引的那几类()
    {
        var db = Build("mix", new MiniDef("Mix", 5, (1, 3, 0, 1)));
        var said = GetLine(db, "Mix");
        // 1(条数) + 1(集合) = 2 条没进索引,加上表上那 1 条 = 至少 3 条,而不是 1+5=6。
        Assert.Contains("Added to the 1 paths that did get indexed, that is at least 3 field paths", said);
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

    // ---- 一整批 def 那一句 ----

    /// <summary>
    /// 分过类的库上 <c>snapshot truncated</c> 的 JSON —— 给 GateTests 验四个成因列用:
    /// 共享夹具没分过类,那上面只有 fields_dropped 一列。
    /// </summary>
    internal static string TruncatedJsonFor()
    {
        var db = Build("gate-truncated", new MiniDef("Lost", 2, (1, 0, 1, 0)), new MiniDef("Cut", 3, (0, 3, 0, 0)));
        return Fixture.Run("snapshot", "truncated", "--db", db, "--json").Stdout;
    }

    /// <summary>
    /// 分过类的库上每个 def 的四种成因各一列;没分过类的库上只有那一个总数,四列不许印成零。
    /// 此前只有 fields_dropped 一列加一句「下界」散文(Docs/25 丁1)。
    /// </summary>
    [Fact]
    public void 每个def的成因四列各数各的()
    {
        var rows = System.Text.Json.JsonDocument.Parse(TruncatedJsonFor()).RootElement.GetProperty("truncated");
        var byName = rows.EnumerateArray().ToDictionary(r => r.GetProperty("def_name").GetString()!);
        Assert.Equal(1, byName["Lost"].GetProperty("past_field_cap").GetInt32());
        Assert.Equal(1, byName["Lost"].GetProperty("past_depth_cap").GetInt32());
        Assert.Equal(0, byName["Lost"].GetProperty("values_cut").GetInt32());
        Assert.Equal(3, byName["Cut"].GetProperty("values_cut").GetInt32());
        Assert.Equal(0, byName["Cut"].GetProperty("lists_cut").GetInt32());
        Assert.False(byName["Cut"].TryGetProperty("fields_dropped", out _));

        var legacy = Build("trunc-legacy", new MiniDef("Legacy", 3, null));
        var (stdout, _, code) = Fixture.Run("snapshot", "truncated", "--db", legacy, "--json");
        Assert.Equal(0, code);
        var row = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("truncated")[0];
        Assert.Equal(3, row.GetProperty("fields_dropped").GetInt32());
        Assert.False(row.TryGetProperty("past_field_cap", out _));

        // 「每个数都是下界」住 help,不在表下。
        var (help, _, _) = Fixture.Run("snapshot", "truncated", "--help");
        Assert.Contains("lower bound", help, StringComparison.Ordinal);
        Assert.DoesNotContain("lower bound", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 整份库的截断账:<c>snapshot status</c> 的 snapshot 块里那两格(没分过类的库一格)。
    /// <c>snapshot import</c> 与 <c>snapshot diff</c> 走同一组格(ExportCap.DroppedDefs)。
    /// 此前是表下一句两拨分开说的散文,读法现在住 help(Docs/25 丁1)。
    /// </summary>
    private static System.Text.Json.JsonElement StatusBlock(string db)
    {
        var (stdout, _, code) = Fixture.Run("snapshot", "status", "--db", db, "--json");
        Assert.Equal(0, code);
        return System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("snapshot").Clone();
    }

    /// <summary>
    /// 被截过的 def 里一条路径都没少时,「缺了路径」那一格是 0,数落在「只切了值」那一格。
    ///
    /// 这不是边角:七个官方快照上这一拨占 27 个里的 22 个,而此前那句按总数发,
    /// 对那 22 个整句是假的。
    /// </summary>
    [Fact]
    public void 整份库的账在只切了值时缺路径那格是零()
    {
        var block = StatusBlock(Build("agg-len", new MiniDef("Cut", 4, (0, 4, 0, 0))));
        Assert.Equal(0, block.GetProperty(ExportCap.DefsWithPathsDropped).GetInt32());
        Assert.Equal(1, block.GetProperty(ExportCap.DefsWithValuesCut).GetInt32());
        Assert.False(block.TryGetProperty(ExportCap.DefsWithFieldsDropped, out _));
    }

    /// <summary>两拨都有时两格各数各的,而且两个数加起来是那个总数;没被截的不进任何一拨。</summary>
    [Fact]
    public void 整份库的账两拨都有时两格各数各的()
    {
        var block = StatusBlock(Build("agg-both",
            new MiniDef("Lost", 2, (1, 0, 1, 0)),
            new MiniDef("LostToo", 1, (0, 0, 0, 1)),
            new MiniDef("Cut", 3, (0, 3, 0, 0)),
            new MiniDef("Clean", 0, (0, 0, 0, 0))));
        Assert.Equal(2, block.GetProperty(ExportCap.DefsWithPathsDropped).GetInt32());
        Assert.Equal(1, block.GetProperty(ExportCap.DefsWithValuesCut).GetInt32());
    }

    /// <summary>没分过类的库上只有一格总数 —— 两拨一个都不许猜,那两格不许印成零。</summary>
    [Fact]
    public void 整份库的账在没分过类的库上只有一格总数()
    {
        var block = StatusBlock(Build("agg-legacy", new MiniDef("Legacy", 3, null)));
        Assert.Equal(1, block.GetProperty(ExportCap.DefsWithFieldsDropped).GetInt32());
        Assert.False(block.TryGetProperty(ExportCap.DefsWithPathsDropped, out _));
        Assert.False(block.TryGetProperty(ExportCap.DefsWithValuesCut, out _));
        // 读法住 help:两拨的后果各是哪句,没分类那一格的后果是对两拨都真的那句。
        var (help, _, _) = Fixture.Run("snapshot", "status", "--help");
        Assert.Contains("on those, what 'get' prints is not the whole def", help, StringComparison.Ordinal);
        Assert.Contains("'get' lists fewer field paths than the def has", help, StringComparison.Ordinal);
    }
}
