using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Contract;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// <c>snapshot diff</c> 的行为闸。语料是两份迷你 export,不走共享 fixture ——
/// 那边 core / other 的名单不同,一比就被拒。
/// </summary>
public class SnapshotDiffTests
{
    private static readonly object Gate = new();
    private static string? _pairDir;

    /// <summary>给 GateTests 的列名探针:三张表都得有至少一行。</summary>
    internal static string DiffJsonFor(string _)
    {
        var dir = PairDir();
        return Run(dir, "snapshot", "diff", "prior", "newer", "--json").Stdout;
    }

    [Fact]
    public void 同名单一字段变了加了一个删了一个()
    {
        var dir = PairDir();
        var (stdout, stderr, code) = Run(dir, "snapshot", "diff", "prior", "newer");

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.DoesNotContain("--scope", stdout, StringComparison.Ordinal);
        Assert.Contains("GunA", stdout, StringComparison.Ordinal);
        Assert.Contains("damage", stdout, StringComparison.Ordinal);
        Assert.Contains("10", stdout, StringComparison.Ordinal);
        Assert.Contains("20", stdout, StringComparison.Ordinal);
        Assert.Contains("GunC", stdout, StringComparison.Ordinal);
        Assert.Contains("GunB", stdout, StringComparison.Ordinal);
        // 两侧都没分过类:截断账是一格总数。
        Assert.Contains(ExportCap.DefsWithFieldsDropped + "  1", stdout, StringComparison.Ordinal);
        Assert.Contains("1 def added.", stdout, StringComparison.Ordinal);
        Assert.Contains("1 def removed.", stdout, StringComparison.Ordinal);

        var json = Run(dir, "snapshot", "diff", "prior", "newer", "--json").Stdout;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("GunC", doc.RootElement.GetProperty("defs_added")[0].GetProperty("def_name").GetString());
        Assert.Equal("GunB", doc.RootElement.GetProperty("defs_removed")[0].GetProperty("def_name").GetString());
        var fields = doc.RootElement.GetProperty("fields");
        Assert.True(fields.GetArrayLength() >= 1);
        Assert.Contains("damage", fields.EnumerateArray().Select(r => r.GetProperty("path").GetString()));
    }

    [Fact]
    public void json三键恒在且零差异是空数组()
    {
        var dir = PairDir();
        var (stdout, _, code) = Run(dir, "snapshot", "diff", "prior", "prior", "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(stdout);
        foreach (var key in new[] { "defs_added", "defs_removed", "fields" })
        {
            Assert.True(doc.RootElement.TryGetProperty(key, out var arr), $"missing {key}");
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(0, arr.GetArrayLength());
        }
        Assert.Contains("No difference", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void 文本零的一侧仍报零而不是整张表消失()
    {
        var dir = PairDir();
        var (stdout, _, code) = Run(dir, "snapshot", "diff", "prior", "fieldsonly");
        Assert.Equal(0, code);
        Assert.Contains("0 defs added.", stdout, StringComparison.Ordinal);
        Assert.Contains("0 defs removed.", stdout, StringComparison.Ordinal);
        Assert.Contains("1 field.", stdout, StringComparison.Ordinal);
        Assert.Contains("damage", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("No difference", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("GunC", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("GunB", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void 文本全零仍是一句NoDifference()
    {
        var dir = PairDir();
        var (stdout, _, code) = Run(dir, "snapshot", "diff", "prior", "prior");
        Assert.Equal(0, code);
        Assert.Contains("No difference between those snapshots in defs or field values.",
                        stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("defs added", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("defs removed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void 名单差拒比并指路mod_list()
    {
        var dir = PairDir();
        var (stdout, stderr, code) = Run(dir, "snapshot", "diff", "prior", "otherlist");

        Assert.Equal(Runner.ExitUsage, code);
        Assert.Equal("", stdout);
        Assert.Contains("mod_list", stderr, StringComparison.Ordinal);
        Assert.Contains("different mod lists", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("defs_added", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("GunA", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void 声明与help都不提供scope()
    {
        var spec = new CommandRegistry().Specs.Single(s => s.Name == "snapshot diff");
        Assert.DoesNotContain(spec.Options, o => o.Name == "scope");
        Assert.Contains("no mod filter", spec.Remarks, StringComparison.Ordinal);
        Assert.Contains("declared the def", spec.Remarks, StringComparison.Ordinal);

        var help = Run(PairDir(), "snapshot", "diff", "--help").Stdout;
        Assert.DoesNotContain("--scope", help, StringComparison.Ordinal);
        Assert.Contains("declaring packageId", help, StringComparison.Ordinal);
        Assert.Contains("including zero", help, StringComparison.Ordinal);
    }

    [Fact]
    public void 两份库时不碰live快照()
    {
        // 目录里不止一份、又没有 --db / --snapshot。若误走 ctx.Db,Resolve 会以
        // 「没有安全默认」退出 2,而这条命令问的是两个名字,应当成功。
        var dir = PairDir();
        var (_, stderr, code) = Run(dir, "snapshot", "diff", "prior", "newer");
        Assert.Equal(0, code);
        Assert.DoesNotContain("no safe default", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("changed on disk", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void 截断复用where的计数文法()
    {
        var dir = PairDir();
        var (json, _, code) = Run(dir, "snapshot", "diff", "prior", "newer", "--limit", "1", "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("fields");
        Assert.Equal(1, fields.GetArrayLength());

        var trunc = doc.RootElement.GetProperty("notes").EnumerateArray()
            .Where(n => n.GetProperty("kind").GetString() == "truncation")
            .ToList();
        Assert.NotEmpty(trunc);
        var note = trunc.First(n => n.GetProperty("text").GetString()!.Contains("field", StringComparison.Ordinal));
        Assert.Equal(1, note.GetProperty("shown").GetInt32());
        Assert.True(note.GetProperty("total").GetInt32() > 1);
    }

    /// <summary>
    /// 两侧都分过类时,截断账是两格各数各的(truncation 块);读法住 help:少了行的那拨看不见
    /// 的是行,只切了值的那拨行两边都在,而两侧切在同一处,切口之后的差异会印成「没变」。
    /// 此前是一句两拨分开说的散文(Docs/25 丁1)。
    /// </summary>
    [Fact]
    public void 两侧都分过类时截断账两格各数各的()
    {
        var dir = PairDir();
        var (stdout, _, code) = Run(dir, "snapshot", "diff", "classA", "classB", "--json");
        Assert.Equal(0, code);
        var block = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("truncation");
        Assert.Equal(1, block.GetProperty(ExportCap.DefsWithPathsDropped).GetInt32());
        Assert.Equal(1, block.GetProperty(ExportCap.DefsWithValuesCut).GetInt32());
        Assert.False(block.TryGetProperty(ExportCap.DefsWithFieldsDropped, out _));
        var (help, _, _) = Run(dir, "snapshot", "diff", "--help");
        Assert.Contains("may have been one of the ones they lost", help, StringComparison.Ordinal);
        Assert.Contains("may still differ past the cut", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一侧没分过类就只有一格总数。**这是常态不是意外**:`--keep` 留下的 `.prev`
    /// 全是旧代,而 diff 的正主就是拿旧代比新库。把没分过类的那侧算成「只切了值」,
    /// 等于把它丢掉的行说成没丢 —— 所以那两格不许印成零,而是整个不在。
    /// </summary>
    [Fact]
    public void 一侧没分过类时只有一格总数()
    {
        var dir = PairDir();
        var (stdout, _, code) = Run(dir, "snapshot", "diff", "classLegacy", "classA", "--json");
        Assert.Equal(0, code);
        var block = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("truncation");
        Assert.True(block.GetProperty(ExportCap.DefsWithFieldsDropped).GetInt32() > 0);
        Assert.False(block.TryGetProperty(ExportCap.DefsWithPathsDropped, out _));
        Assert.False(block.TryGetProperty(ExportCap.DefsWithValuesCut, out _));
    }

    private static string PairDir()
    {
        lock (Gate)
        {
            if (_pairDir is not null) return _pairDir;
            var dir = Path.Combine(TestTemp.Root, "snapshot-diff");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            Import(dir, "prior", SameList, PriorDefs);
            Import(dir, "newer", SameList, NewerDefs);
            Import(dir, "fieldsonly", SameList, FieldOnlyNewerDefs);
            Import(dir, "otherlist", OtherList, PriorDefs);
            Import(dir, "classA", SameList, ClassifiedDefs);
            Import(dir, "classB", SameList, ClassifiedDefs);
            Import(dir, "classLegacy", SameList, LegacyOfClassified);

            File.WriteAllText(Path.Combine(dir, "config.toml"),
                $"snapshot_dir = '{dir}'\n", new UTF8Encoding(false));
            return _pairDir = dir;
        }
    }

    private static (string Stdout, string Stderr, int Code) Run(string dir, params string[] argv)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        List<string> all = [.. argv, "--config", Path.Combine(dir, "config.toml")];
        var code = Runner.Run(all, stdout, stderr);
        return (stdout.ToString(), stderr.ToString(), code);
    }

    private static readonly (string Id, string Name)[] SameList =
        [("ludeon.rimworld", "Core")];

    private static readonly (string Id, string Name)[] OtherList =
        [("ludeon.rimworld", "Core"), ("extra.mod", "Extra")];

    private static readonly MiniDef[] PriorDefs =
    [
        new("ThingDef", "GunA", "ludeon.rimworld", 0, null,
            ("thingClass", "RimWorld.Bullet"), ("damage", "10"), ("foo", "a"), ("bar", "b")),
        new("ThingDef", "GunB", "ludeon.rimworld", 0, null, ("thingClass", "RimWorld.Bullet")),
        new("ThingDef", "SharedTrunc", "ludeon.rimworld", 3, null, ("foo", "1")),
    ];

    private static readonly MiniDef[] FieldOnlyNewerDefs =
    [
        new("ThingDef", "GunA", "ludeon.rimworld", 0, null,
            ("thingClass", "RimWorld.Bullet"), ("damage", "20"), ("foo", "a"), ("bar", "b")),
        new("ThingDef", "GunB", "ludeon.rimworld", 0, null, ("thingClass", "RimWorld.Bullet")),
        new("ThingDef", "SharedTrunc", "ludeon.rimworld", 3, null, ("foo", "1")),
    ];

    private static readonly MiniDef[] NewerDefs =
    [
        new("ThingDef", "GunA", "ludeon.rimworld", 0, null,
            ("thingClass", "RimWorld.Bullet"), ("damage", "20"), ("foo", "a"), ("bar", "c"), ("baz", "1")),
        new("ThingDef", "GunC", "ludeon.rimworld", 0, null, ("thingClass", "RimWorld.Bullet")),
        new("ThingDef", "SharedTrunc", "ludeon.rimworld", 3, null, ("foo", "1")),
    ];

    /// <summary>两侧都分过类的那一对:一个真丢了行,一个只是值被切。</summary>
    private static readonly MiniDef[] ClassifiedDefs =
    [
        new("ThingDef", "LostRows", "ludeon.rimworld", 2, (1, 0, 1, 0), ("foo", "1")),
        new("ThingDef", "OnlyCut", "ludeon.rimworld", 3, (0, 3, 0, 0), ("foo", "1")),
    ];

    /// <summary>同一批 def 的旧代形态：同名同总数，但不带成因键。</summary>
    private static readonly MiniDef[] LegacyOfClassified =
    [
        new("ThingDef", "LostRows", "ludeon.rimworld", 2, null, ("foo", "1")),
        new("ThingDef", "OnlyCut", "ludeon.rimworld", 3, null, ("foo", "1")),
    ];

    /// <param name="Causes">null = 不带成因键,即 0.13.0 之前导出的形态。</param>
    private readonly record struct MiniDef(string Type, string Name, string Mod, int Truncated,
                                           (int Cap, int Len, int Dep, int Items)? Causes,
                                           params (string Path, string Value)[] Fields);

    private static void Import(string dir, string name, (string Id, string Name)[] mods, MiniDef[] defs)
    {
        var export = Path.Combine(dir, name + IntermediateFormat.FileExtension);
        WriteMini(export, mods, defs);
        new SnapshotImporter().Import(export, Path.Combine(dir, name + ".db"));
    }

    private static void WriteMini(string path, (string Id, string Name)[] mods, MiniDef[] defs)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };

        long records = 0;
        var modsJson = "[" + string.Join(",", mods.Select(m =>
            new JsonLine().Str("package_id", m.Id).Str("name", m.Name).Str("version", "1.6").ToString())) + "]";

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
            .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
            .Str(IntermediateFormat.KeyExporterVersion, "0.4.0")
            .Str(IntermediateFormat.KeyExportedAtUtc, "2026-01-01T00:00:00.0000000Z")
            .Str(IntermediateFormat.KeyGameVersion, Fixture.GameVersion)
            .Str(IntermediateFormat.KeyLanguage, Fixture.Language)
            .Raw(IntermediateFormat.KeyMods, modsJson)
            .Raw(IntermediateFormat.KeyLimits, new JsonLine().Int("max_field_depth", 6).ToString())
            .Str(IntermediateFormat.KeyModSettingsHash, "")
            .ToString());
        records++;

        foreach (var d in defs)
        {
            var pairs = d.Fields.Select(f => new ExportedField(f.Path, f.Value, DefaultState.Differs)).ToList();
            var line = new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDef)
                .Str(IntermediateFormat.KeyDefType, d.Type)
                .Str(IntermediateFormat.KeyDefName, d.Name)
                .Str(IntermediateFormat.KeyLabel, d.Name)
                .Str(IntermediateFormat.KeyDescription, "")
                .Str(IntermediateFormat.KeySourceMod, d.Mod)
                .Str(IntermediateFormat.KeySourceFile, d.Name + ".xml")
                .Bool(IntermediateFormat.KeyGenerated, false)
                .Str(IntermediateFormat.KeyClass, "Verse." + d.Type)
                .Fields(IntermediateFormat.KeyFields, pairs)
                .Int(IntermediateFormat.KeyFieldsTruncated, d.Truncated);
            if (d.Causes is { } c)
                line.Int(IntermediateFormat.KeyTruncatedByCap, c.Cap)
                    .Int(IntermediateFormat.KeyTruncatedByLength, c.Len)
                    .Int(IntermediateFormat.KeyTruncatedByDepth, c.Dep)
                    .Int(IntermediateFormat.KeyTruncatedByItems, c.Items);
            w.WriteLine(line.ToString());
            records++;
        }

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
            .Int(IntermediateFormat.KeyRecords, records + 1)
            .Int(IntermediateFormat.KeyDefs, defs.Length)
            .Int(IntermediateFormat.KeyInjections, 0)
            .Int(IntermediateFormat.KeyXmlNodes, 0)
            .ToString());
        w.Flush();
    }
}
