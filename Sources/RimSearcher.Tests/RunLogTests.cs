using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimSearcher.Cli;
using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// run-log 旁路。正常路径一个字节都不许进 stdout / stderr;这些闸同时钉形态与「没设就不写」。
/// </summary>
public class RunLogTests
{
    private static string UniqueLog()
    {
        var dir = Path.Combine(TestTemp.Root, "run-log");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("n") + ".jsonl");
    }

    private static string Exe
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "rimsearcher.exe" : "rimsearcher";
            var p = Path.Combine(AppContext.BaseDirectory, name);
            Assert.True(File.Exists(p), $"The CLI executable is not next to the tests at '{p}'.");
            return p;
        }
    }

    private static (string Stdout, string Stderr, int Code) RunChild(string? logPath, params string[] argv)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--db");
        psi.ArgumentList.Add(Fixture.Db);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(Fixture.NoConfigPath);
        if (logPath is not null) psi.Environment[RunLog.EnvVar] = logPath;
        else psi.Environment.Remove(RunLog.EnvVar);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (stdout.Replace("\r\n", "\n"), stderr.Replace("\r\n", "\n"), proc.ExitCode);
    }

    private static JsonElement OnlyLine(string path)
    {
        Assert.True(File.Exists(path), $"run-log was not written at '{path}'.");
        var lines = File.ReadAllText(path).Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length == 1, $"expected one jsonl line, got {lines.Length}.");
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    [Fact]
    public void 未设环境变量时不产生文件且输出不变()
    {
        var path = UniqueLog();
        var off = RunChild(logPath: null, "get", "Apparel_ShieldBelt");
        Assert.False(File.Exists(path));
        Assert.Equal(0, off.Code);

        var on = RunChild(path, "get", "Apparel_ShieldBelt");
        Assert.Equal(off.Stdout, on.Stdout);
        Assert.Equal(off.Stderr, on.Stderr);
        Assert.Equal(off.Code, on.Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 成功查询写出一行且exit为零()
    {
        var path = UniqueLog();
        var (stdout, stderr, code) = RunChild(path, "get", "Apparel_ShieldBelt");
        Assert.Equal(0, code);
        Assert.Equal("", stderr);

        var row = OnlyLine(path);
        Assert.Equal(RunLog.SchemaId, row.GetProperty("schema").GetString());
        Assert.Equal(0, row.GetProperty("exit").GetInt32());
        Assert.Equal("Apparel_ShieldBelt", row.GetProperty("argv")[1].GetString());
        Assert.True(row.TryGetProperty("ts", out _));
        Assert.True(row.GetProperty("pid").GetInt32() > 0);
        Assert.Equal("fixture", row.GetProperty("snapshot").GetString());

        foreach (var n in row.GetProperty("notices").EnumerateArray())
            Assert.Contains(n.GetProperty("text").GetString()!, stdout);
    }

    [Fact]
    public void 零结果写出一行且exit为一()
    {
        var path = UniqueLog();
        var (stdout, _, code) = RunChild(path, "get", "NoSuchDefAtAll");
        Assert.Equal(Runner.ExitNoResults, code);
        var row = OnlyLine(path);
        Assert.Equal(1, row.GetProperty("exit").GetInt32());
        Assert.True(row.GetProperty("notices").GetArrayLength() > 0);
        foreach (var n in row.GetProperty("notices").EnumerateArray())
            Assert.Contains(n.GetProperty("text").GetString()!, stdout);
    }

    [Fact]
    public void 用法错误写出一行且exit为二()
    {
        var path = UniqueLog();
        var (_, stderr, code) = RunChild(path, "search", "shield", "--nonsense");
        Assert.Equal(Runner.ExitUsage, code);
        var row = OnlyLine(path);
        Assert.Equal(2, row.GetProperty("exit").GetInt32());
        Assert.Contains("--nonsense", stderr);
        Assert.Equal(JsonValueKind.Array, row.GetProperty("notices").ValueKind);
        // 用法错的那句不经 Report,notices 于是是空的 —— 消息只在这个键里,
        // 没有它,run-log 上「选项打错了」就只剩一个退出码
        Assert.Contains("--nonsense", row.GetProperty("usageMessage").GetString()!);
    }

    [Fact]
    public void 非用法错时usageMessage为null()
    {
        var path = UniqueLog();
        var (_, _, code) = RunChild(path, "get", "Apparel_ShieldBelt");
        Assert.Equal(0, code);
        // 别的退出码下 stderr 上是进度行,那不是声明,不该被当成话记进来
        Assert.Equal(JsonValueKind.Null, OnlyLine(path).GetProperty("usageMessage").ValueKind);
    }

    [Fact]
    public void notices条数与json的notes一致()
    {
        var path = UniqueLog();
        var (stdout, _, code) = RunChild(path, "get", "Apparel_ShieldBelt", "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var notes = doc.RootElement.GetProperty("notes").GetArrayLength();
        var row = OnlyLine(path);
        Assert.Equal(notes, row.GetProperty("notices").GetArrayLength());
        for (var i = 0; i < notes; i++)
        {
            var a = doc.RootElement.GetProperty("notes")[i];
            var b = row.GetProperty("notices")[i];
            Assert.Equal(a.GetProperty("kind").GetString(), b.GetProperty("kind").GetString());
            Assert.Equal(a.GetProperty("text").GetString(), b.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void 设了之后stdout与未设逐字节相同()
    {
        var path = UniqueLog();
        var off = RunChild(logPath: null, "where", "compClass", "RimWorld.CompShield");
        var on = RunChild(path, "where", "compClass", "RimWorld.CompShield");
        Assert.Equal(off.Stdout, on.Stdout);
        Assert.Equal(off.Stderr, on.Stderr);
        Assert.Equal(off.Code, on.Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 夹在两块之间的notice两侧块标识都在()
    {
        var report = new Report()
            .Notice(NoticeKind.Count, "2 defs.")
            .Table("defs", ["def_name"], [new Dictionary<string, object?> { ["def_name"] = "A" }])
            .Notice(NoticeKind.Boundary, "Between the table and the fields.")
            .Table("fields", ["path"], [new Dictionary<string, object?> { ["path"] = "x" }]);

        using var doc = JsonDocument.Parse(RunLog.Format(["get", "A"], 0, report, "fixture"));
        Assert.False(doc.RootElement.GetProperty("quiet").GetBoolean());
        var mid = doc.RootElement.GetProperty("notices")[1];
        Assert.Equal("Between the table and the fields.", mid.GetProperty("text").GetString());
        Assert.Equal(2, mid.GetProperty("seq").GetInt32());
        Assert.Equal("defs", mid.GetProperty("prevBlock").GetProperty("name").GetString());
        Assert.Equal("fields", mid.GetProperty("nextBlock").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, mid.GetProperty("prevBlock").GetProperty("collection").ValueKind);
        Assert.Equal(JsonValueKind.Null, mid.GetProperty("count").ValueKind);
        Assert.Equal(JsonValueKind.Null, mid.GetProperty("data").ValueKind);
    }

    [Fact]
    public void count的total为null时照写第三态()
    {
        var report = new Report()
            .Notice(NoticeKind.Truncation, "at least 12 matches.", count: Tally.AtLeast(12));
        using var doc = JsonDocument.Parse(RunLog.Format(["search", "x"], 0, report, null));
        var n = doc.RootElement.GetProperty("notices")[0];
        Assert.Equal(12, n.GetProperty("count").GetProperty("shown").GetInt32());
        Assert.Equal(JsonValueKind.Null, n.GetProperty("count").GetProperty("total").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("snapshot").ValueKind);
    }

    [Fact]
    public void 落盘失败时stderr说清路径与原因且退出码不变()
    {
        var asDir = Path.Combine(TestTemp.Root, "run-log", "not-a-file-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(asDir);
        var (stdout, stderr, code) = RunChild(asDir, "get", "Apparel_ShieldBelt");
        Assert.Equal(0, code);
        Assert.Contains(asDir, stderr, StringComparison.Ordinal);
        Assert.Contains("Could not write the run log", stderr, StringComparison.Ordinal);
        Assert.Contains("Apparel_ShieldBelt", stdout);
    }

    [Fact]
    public void quiet键在两种情形下取值正确且声明没有变少()
    {
        var offPath = UniqueLog();
        var onPath = UniqueLog();
        var off = RunChild(offPath, "get", "Apparel_ShieldBelt");
        var on = RunChild(onPath, "get", "Apparel_ShieldBelt", "--quiet");
        Assert.Equal(0, off.Code);
        Assert.Equal(0, on.Code);
        Assert.NotEqual(off.Stdout, on.Stdout);

        var offRow = OnlyLine(offPath);
        var onRow = OnlyLine(onPath);
        Assert.False(offRow.GetProperty("quiet").GetBoolean());
        Assert.True(onRow.GetProperty("quiet").GetBoolean());
        Assert.Equal(offRow.GetProperty("notices").GetArrayLength(),
                     onRow.GetProperty("notices").GetArrayLength());
        Assert.True(onRow.GetProperty("notices").GetArrayLength() > 0);
        for (var i = 0; i < offRow.GetProperty("notices").GetArrayLength(); i++)
            Assert.Equal(offRow.GetProperty("notices")[i].GetProperty("text").GetString(),
                         onRow.GetProperty("notices")[i].GetProperty("text").GetString());
    }

    [Fact]
    public void Format的quiet为true时键为真且notices仍在()
    {
        var report = new Report()
            .Notice(NoticeKind.Count, "2 defs.")
            .Table("defs", ["def_name"], [new Dictionary<string, object?> { ["def_name"] = "A" }]);
        using var doc = JsonDocument.Parse(RunLog.Format(["get", "A"], 0, report, "fixture", quiet: true));
        Assert.True(doc.RootElement.GetProperty("quiet").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("notices").GetArrayLength());
        Assert.Equal("2 defs.", doc.RootElement.GetProperty("notices")[0].GetProperty("text").GetString());
    }

}
