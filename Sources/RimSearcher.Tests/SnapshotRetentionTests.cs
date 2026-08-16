using System.IO.Compression;
using System.Text;
using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 同名快照的一代对照物:旋转、<c>.prev</c> 仍不同时拒绝、解析结果相同则不动。
/// </summary>
public class SnapshotRetentionTests
{
    [Fact]
    public void 第一次写入不造prev()
    {
        var dir = FreshDir("first");
        Import(dir, "current", 10);
        Assert.True(File.Exists(Path.Combine(dir, "current.db")));
        Assert.False(File.Exists(Path.Combine(dir, "current.prev.db")));
    }

    [Fact]
    public void 第二次不同内容旋转出prev()
    {
        var dir = FreshDir("rotate");
        Import(dir, "current", 10);
        var (stdout, _, code) = Import(dir, "current", 20);
        Assert.Equal(0, code);
        Assert.Contains("current.prev", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "current.prev.db")));

        var diff = Run(dir, "snapshot", "diff", "current.prev", "current", "--json").Stdout;
        Assert.Contains("\"old\": \"10\"", diff, StringComparison.Ordinal);
        Assert.Contains("\"new\": \"20\"", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void 第三次同名拒并指路改名()
    {
        var dir = FreshDir("refuse");
        Import(dir, "current", 10);
        Import(dir, "current", 20);
        var prevWrite = File.GetLastWriteTimeUtc(Path.Combine(dir, "current.prev.db"));
        var destWrite = File.GetLastWriteTimeUtc(Path.Combine(dir, "current.db"));

        var (stdout, stderr, code) = Import(dir, "current", 30);
        Assert.Equal(Runner.ExitUsage, code);
        Assert.Equal("", stdout);
        Assert.Contains("would discard 'current.prev'", stderr, StringComparison.Ordinal);
        Assert.Contains("snapshot diff current.prev current", stderr, StringComparison.Ordinal);
        Assert.Contains("--name current-0817", stderr, StringComparison.Ordinal);
        Assert.Contains("--replace-prev", stderr, StringComparison.Ordinal);
        Assert.Equal(prevWrite, File.GetLastWriteTimeUtc(Path.Combine(dir, "current.prev.db")));
        Assert.Equal(destWrite, File.GetLastWriteTimeUtc(Path.Combine(dir, "current.db")));
    }

    [Fact]
    public void 改名另起则旧库都在()
    {
        var dir = FreshDir("rename");
        Import(dir, "current", 10);
        Import(dir, "current", 20);
        var (stdout, _, code) = Import(dir, "current-0817", 30);
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(dir, "current.db")));
        Assert.True(File.Exists(Path.Combine(dir, "current.prev.db")));
        Assert.True(File.Exists(Path.Combine(dir, "current-0817.db")));
        Assert.DoesNotContain("would discard", stdout, StringComparison.Ordinal);

        var left = Run(dir, "snapshot", "diff", "current.prev", "current", "--json").Stdout;
        Assert.Contains("\"old\": \"10\"", left, StringComparison.Ordinal);
        var right = Run(dir, "snapshot", "diff", "current", "current-0817", "--json").Stdout;
        Assert.Contains("\"old\": \"20\"", right, StringComparison.Ordinal);
        Assert.Contains("\"new\": \"30\"", right, StringComparison.Ordinal);
    }

    [Fact]
    public void 相同内容跳过且不挤掉prev()
    {
        var dir = FreshDir("same");
        Import(dir, "current", 10);
        Import(dir, "current", 20);
        var prevWrite = File.GetLastWriteTimeUtc(Path.Combine(dir, "current.prev.db"));

        var (stdout, _, code) = Import(dir, "current", 20);
        Assert.Equal(0, code);
        Assert.Contains("left in place", stdout, StringComparison.Ordinal);
        Assert.Equal(prevWrite, File.GetLastWriteTimeUtc(Path.Combine(dir, "current.prev.db")));

        var diff = Run(dir, "snapshot", "diff", "current.prev", "current", "--json").Stdout;
        Assert.Contains("\"old\": \"10\"", diff, StringComparison.Ordinal);
        Assert.Contains("\"new\": \"20\"", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void replaceprev才丢掉仍不同的上一代()
    {
        var dir = FreshDir("force");
        Import(dir, "current", 10);
        Import(dir, "current", 20);
        var (_, stderr, code) = Import(dir, "current", 30, "--replace-prev");
        Assert.Equal(0, code);
        Assert.Equal("", stderr);

        var diff = Run(dir, "snapshot", "diff", "current.prev", "current", "--json").Stdout;
        Assert.Contains("\"old\": \"20\"", diff, StringComparison.Ordinal);
        Assert.Contains("\"new\": \"30\"", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\": \"10\"", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void 两条造库口都声明replaceprev且拒绝句指路改名()
    {
        var specs = new CommandRegistry().Specs.ToDictionary(s => s.Name);
        foreach (var name in new[] { "export", "snapshot import" })
            Assert.Contains(specs[name].Options, o => o.Name == "replace-prev" && ReferenceEquals(o, SnapshotRetention.ReplacePrev));

        var refuse = SnapshotRetention.WouldDiscard("current");
        Assert.Contains("--name current-0817", refuse, StringComparison.Ordinal);
        Assert.Contains("snapshot diff current.prev current", refuse, StringComparison.Ordinal);
    }

    private static string FreshDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "snapshot-retention", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"),
            $"snapshot_dir = '{dir}'\n", new UTF8Encoding(false));
        return dir;
    }

    private static (string Stdout, string Stderr, int Code) Import(string dir, string name, int damage,
                                                                   params string[] extra)
    {
        var export = Path.Combine(dir, name + "-" + damage + IntermediateFormat.FileExtension);
        WriteMini(export, damage);
        return Run(dir, ["snapshot", "import", export, "--name", name, "--no-harvest-translations", .. extra]);
    }

    private static (string Stdout, string Stderr, int Code) Run(string dir, params string[] argv)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = Runner.Run([.. argv, "--config", Path.Combine(dir, "config.toml")], stdout, stderr);
        return (stdout.ToString(), stderr.ToString(), code);
    }

    private static void WriteMini(string path, int damage)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };

        var mods = "[" + new JsonLine().Str("package_id", "ludeon.rimworld")
            .Str("name", "Core").Str("version", "1.6") + "]";
        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
            .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
            .Str(IntermediateFormat.KeyExporterVersion, "0.4.0")
            .Str(IntermediateFormat.KeyExportedAtUtc, "2026-01-01T00:00:00.0000000Z")
            .Str(IntermediateFormat.KeyGameVersion, Fixture.GameVersion)
            .Str(IntermediateFormat.KeyLanguage, Fixture.Language)
            .Raw(IntermediateFormat.KeyMods, mods)
            .Raw(IntermediateFormat.KeyLimits, new JsonLine().Int("max_field_depth", 6).ToString())
            .Str(IntermediateFormat.KeyModSettingsHash, "")
            .ToString());

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDef)
            .Str(IntermediateFormat.KeyDefType, "ThingDef")
            .Str(IntermediateFormat.KeyDefName, "GunA")
            .Str(IntermediateFormat.KeyLabel, "gun")
            .Str(IntermediateFormat.KeyDescription, "")
            .Str(IntermediateFormat.KeySourceMod, "ludeon.rimworld")
            .Str(IntermediateFormat.KeySourceFile, "GunA.xml")
            .Bool(IntermediateFormat.KeyGenerated, false)
            .Str(IntermediateFormat.KeyClass, "Verse.ThingDef")
            .Fields(IntermediateFormat.KeyFields,
                [new ExportedField("damage", damage.ToString(), DefaultState.Differs)])
            .Int(IntermediateFormat.KeyFieldsTruncated, 0)
            .ToString());

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
            .Int(IntermediateFormat.KeyRecords, 3)
            .Int(IntermediateFormat.KeyDefs, 1)
            .Int(IntermediateFormat.KeyInjections, 0)
            .Int(IntermediateFormat.KeyXmlNodes, 0)
            .ToString());
        w.Flush();
    }
}
