using System.Text;
using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Snapshot;

namespace RimSearcher.Tests;

/// <summary>
/// <c>snapshot rename</c> 的行为闸。成功路径会挪文件,所以不走共享 fixture,
/// 每条用例一份临时目录 —— 真数据在 <c>~/.rimsearcher</c>,这里碰不得。
/// </summary>
public class SnapshotRenameTests
{
    [Fact]
    public void 三处齐全时都挪并说清()
    {
        var dir = FreshDir("all-three");
        Place(dir, "vanilla", db: true, rml: true, export: true);

        var (stdout, stderr, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains("vanilla.db to baseline.db", stdout, StringComparison.Ordinal);
        Assert.Contains("vanilla.rml to baseline.rml", stdout, StringComparison.Ordinal);
        Assert.Contains("vanilla.rsx.jsonl.gz to baseline.rsx.jsonl.gz", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("not present", stdout, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(dir, "snapshots", "vanilla.db")));
        Assert.True(File.Exists(Path.Combine(dir, "snapshots", "baseline.db")));
        Assert.False(File.Exists(Path.Combine(dir, "modlists", "vanilla.rml")));
        Assert.True(File.Exists(Path.Combine(dir, "modlists", "baseline.rml")));
        Assert.False(File.Exists(Path.Combine(dir, "exports", "vanilla" + IntermediateFormat.FileExtension)));
        Assert.True(File.Exists(Path.Combine(dir, "exports", "baseline" + IntermediateFormat.FileExtension)));
    }

    [Fact]
    public void 只有库时跳过另外两处并说在哪找过()
    {
        var dir = FreshDir("db-only");
        Place(dir, "vanilla", db: true);

        var (stdout, stderr, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains("vanilla.db to baseline.db", stdout, StringComparison.Ordinal);
        Assert.Contains("looked for 'vanilla.rml' next to the config file", stdout, StringComparison.Ordinal);
        Assert.Contains("looked for 'vanilla.rsx.jsonl.gz' in the export directory", stdout, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(dir, "snapshots", "baseline.db")));
        Assert.False(File.Exists(Path.Combine(dir, "modlists", "baseline.rml")));
        Assert.False(File.Exists(Path.Combine(dir, "exports", "baseline" + IntermediateFormat.FileExtension)));
    }

    [Fact]
    public void 只有rml时跳过库和导出()
    {
        var dir = FreshDir("rml-only");
        Place(dir, "vanilla", rml: true);

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Contains("vanilla.rml to baseline.rml", stdout, StringComparison.Ordinal);
        Assert.Contains("looked for 'vanilla.db' in the snapshot directory", stdout, StringComparison.Ordinal);
        Assert.Contains("looked for 'vanilla.rsx.jsonl.gz' in the export directory", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "modlists", "baseline.rml")));
        Assert.False(Directory.Exists(Path.Combine(dir, "snapshots")) &&
                     File.Exists(Path.Combine(dir, "snapshots", "baseline.db")));
    }

    [Fact]
    public void 只有导出时跳过库和rml()
    {
        var dir = FreshDir("export-only");
        Place(dir, "vanilla", export: true);

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Contains("vanilla.rsx.jsonl.gz to baseline.rsx.jsonl.gz", stdout, StringComparison.Ordinal);
        Assert.Contains("looked for 'vanilla.db' in the snapshot directory", stdout, StringComparison.Ordinal);
        Assert.Contains("looked for 'vanilla.rml' next to the config file", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void pin指向旧名时跟着走并说出来()
    {
        var dir = FreshDir("pin-follows");
        Place(dir, "vanilla", db: true);
        File.WriteAllText(Path.Combine(dir, "state.toml"), "active_snapshot = 'vanilla'\n", new UTF8Encoding(false));

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Contains("pin       followed", stdout, StringComparison.Ordinal);
        Assert.Contains("The pinned snapshot now follows as 'baseline'", stdout, StringComparison.Ordinal);

        var state = File.ReadAllText(Path.Combine(dir, "state.toml"));
        Assert.Contains("active_snapshot = \"baseline\"", state, StringComparison.Ordinal);
        Assert.DoesNotContain("active_snapshot = \"vanilla\"", state, StringComparison.Ordinal);
    }

    [Fact]
    public void pin指向别处时不动并说unchanged()
    {
        var dir = FreshDir("pin-other");
        Place(dir, "vanilla", db: true);
        Place(dir, "other", db: true);
        File.WriteAllText(Path.Combine(dir, "state.toml"), "active_snapshot = 'other'\n", new UTF8Encoding(false));

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Contains("unchanged (still 'other')", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("now follows", stdout, StringComparison.Ordinal);
        Assert.Contains("active_snapshot = 'other'", File.ReadAllText(Path.Combine(dir, "state.toml")),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void 没有pin时说not_pinned而不是省略()
    {
        var dir = FreshDir("no-pin");
        Place(dir, "vanilla", db: true);

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Contains("not pinned", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧代跟着当前库一起挪()
    {
        var dir = FreshDir("prev");
        Place(dir, "vanilla", db: true, prev: true);

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(0, code);
        Assert.Contains("vanilla.prev.db to baseline.prev.db", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "snapshots", "baseline.prev.db")));
        Assert.False(File.Exists(Path.Combine(dir, "snapshots", "vanilla.prev.db")));
    }

    [Fact]
    public void 新名的旧代文件也算撞名且一口都不挪()
    {
        var dir = FreshDir("prev-clash");
        Place(dir, "vanilla", db: true, prev: true);
        Directory.CreateDirectory(Path.Combine(dir, "snapshots"));
        File.WriteAllText(Path.Combine(dir, "snapshots", "baseline.prev.db"), "taken");

        var (stdout, stderr, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(Runner.ExitUsage, code);
        Assert.Equal("", stdout);
        Assert.Contains("baseline.prev.db", stderr, StringComparison.Ordinal);
        Assert.Contains("Nothing was moved", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "snapshots", "vanilla.db")));
        Assert.True(File.Exists(Path.Combine(dir, "snapshots", "vanilla.prev.db")));
    }

    [Fact]
    public void 撞名时旧文件原封不动()
    {
        var dir = FreshDir("clash-untouched");
        Place(dir, "vanilla", db: true, rml: true, export: true);
        Place(dir, "baseline", db: true);

        var (_, stderr, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline");
        Assert.Equal(Runner.ExitUsage, code);
        Assert.Contains("baseline.db", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "snapshots", "vanilla.db")));
        Assert.True(File.Exists(Path.Combine(dir, "modlists", "vanilla.rml")));
        Assert.True(File.Exists(Path.Combine(dir, "exports", "vanilla" + IntermediateFormat.FileExtension)));
    }

    [Fact]
    public void json三处键恒在含缺席()
    {
        var dir = FreshDir("json-absent");
        Place(dir, "vanilla", db: true);

        var (stdout, _, code) = Run(dir, "snapshot", "rename", "vanilla", "baseline", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        var renamed = doc.RootElement.GetProperty("renamed");
        Assert.Equal("vanilla", renamed.GetProperty("from").GetString());
        Assert.Equal("baseline", renamed.GetProperty("to").GetString());
        Assert.Contains("vanilla.db to baseline.db", renamed.GetProperty("snapshot").GetString(), StringComparison.Ordinal);
        Assert.Contains("not present", renamed.GetProperty("modlist").GetString(), StringComparison.Ordinal);
        Assert.Contains("not present", renamed.GetProperty("export").GetString(), StringComparison.Ordinal);
        Assert.Equal("not pinned", renamed.GetProperty("pin").GetString());
    }

    [Fact]
    public void 改名后list认新名不认旧名()
    {
        var dir = FreshDir("list-follows");
        Place(dir, "vanilla", db: true);

        Run(dir, "snapshot", "rename", "vanilla", "baseline");
        var (listed, _, code) = Run(dir, "snapshot", "list", "--json");
        Assert.Equal(0, code);
        Assert.Contains("\"name\": \"baseline\"", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\": \"vanilla\"", listed, StringComparison.Ordinal);
    }

    private static string FreshDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "snapshot-rename", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), "", new UTF8Encoding(false));
        return dir;
    }

    private static void Place(string dir, string name, bool db = false, bool rml = false,
                              bool export = false, bool prev = false)
    {
        if (db || prev)
        {
            Directory.CreateDirectory(Path.Combine(dir, "snapshots"));
            if (db) File.WriteAllText(Path.Combine(dir, "snapshots", name + ".db"), "db");
            if (prev) File.WriteAllText(Path.Combine(dir, "snapshots", name + ".prev.db"), "prev");
        }
        if (rml)
        {
            Directory.CreateDirectory(Path.Combine(dir, "modlists"));
            File.WriteAllText(Path.Combine(dir, "modlists", name + ".rml"),
                "<savedModList><modList><ids><li>ludeon.rimworld</li></ids></modList></savedModList>\n");
        }
        if (export)
        {
            Directory.CreateDirectory(Path.Combine(dir, "exports"));
            File.WriteAllText(Path.Combine(dir, "exports", name + IntermediateFormat.FileExtension), "gz");
        }
    }

    private static (string Stdout, string Stderr, int Code) Run(string dir, params string[] argv)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = Runner.Run([.. argv, "--config", Path.Combine(dir, "config.toml")], stdout, stderr);
        return (stdout.ToString(), stderr.ToString(), code);
    }
}
