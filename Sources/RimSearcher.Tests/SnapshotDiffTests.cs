using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RimSearcher.Cli;
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
        Assert.Contains("dropped at export time for depth or size", stdout, StringComparison.Ordinal);
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

    private static string PairDir()
    {
        lock (Gate)
        {
            if (_pairDir is not null) return _pairDir;
            var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "snapshot-diff");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            Import(dir, "prior", SameList, PriorDefs);
            Import(dir, "newer", SameList, NewerDefs);
            Import(dir, "fieldsonly", SameList, FieldOnlyNewerDefs);
            Import(dir, "otherlist", OtherList, PriorDefs);

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
        new("ThingDef", "GunA", "ludeon.rimworld", 0,
            ("thingClass", "RimWorld.Bullet"), ("damage", "10"), ("foo", "a"), ("bar", "b")),
        new("ThingDef", "GunB", "ludeon.rimworld", 0, ("thingClass", "RimWorld.Bullet")),
        new("ThingDef", "SharedTrunc", "ludeon.rimworld", 3, ("foo", "1")),
    ];

    private static readonly MiniDef[] FieldOnlyNewerDefs =
    [
        new("ThingDef", "GunA", "ludeon.rimworld", 0,
            ("thingClass", "RimWorld.Bullet"), ("damage", "20"), ("foo", "a"), ("bar", "b")),
        new("ThingDef", "GunB", "ludeon.rimworld", 0, ("thingClass", "RimWorld.Bullet")),
        new("ThingDef", "SharedTrunc", "ludeon.rimworld", 3, ("foo", "1")),
    ];

    private static readonly MiniDef[] NewerDefs =
    [
        new("ThingDef", "GunA", "ludeon.rimworld", 0,
            ("thingClass", "RimWorld.Bullet"), ("damage", "20"), ("foo", "a"), ("bar", "c"), ("baz", "1")),
        new("ThingDef", "GunC", "ludeon.rimworld", 0, ("thingClass", "RimWorld.Bullet")),
        new("ThingDef", "SharedTrunc", "ludeon.rimworld", 3, ("foo", "1")),
    ];

    private readonly record struct MiniDef(string Type, string Name, string Mod, int Truncated,
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
            w.WriteLine(new JsonLine()
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
                .Int(IntermediateFormat.KeyFieldsTruncated, d.Truncated)
                .ToString());
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
