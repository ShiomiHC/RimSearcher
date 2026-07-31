using System.Diagnostics;
using System.Text;
using RimSearcher.Cli;

namespace RimSearcher.Tests;

/// <summary>
/// 事实侧。别的测试都在进程内调 <c>Runner.Run</c>,这里真起一个进程、真读它的 stdout ——
/// 控制台编码、退出码怎么传给 shell、程序集名与文档里的可执行文件名是否一致,
/// 这三件只有真进程才暴露。
/// </summary>
public class ProcessTests
{
    /// <summary>
    /// 测试工程 ProjectReference 了 CLI,可执行文件必与测试程序集同目录,
    /// 所以找不到就该红,**不做「找不到就跳过」**。
    /// </summary>
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

    private static (string Stdout, string Stderr, int Code) Run(params string[] argv)
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

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (stdout.Replace("\r\n", "\n"), stderr.Replace("\r\n", "\n"), proc.ExitCode);
    }

    /// <summary>
    /// 文档、skill、报错消息里写的都是 <c>rimsearcher</c>,程序集名必须与之一致。
    /// </summary>
    [Fact]
    public void 可执行文件就叫rimsearcher()
    {
        Assert.Equal(CommandRegistry.ExeName, Path.GetFileNameWithoutExtension(Exe));
    }

    [Fact]
    public void 真进程能查出结果且退出码为零()
    {
        var (stdout, stderr, code) = Run("find", "compClass", "RimWorld.CompShield");
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains("Apparel_ShieldBelt", stdout);
    }

    /// <summary>
    /// 中文标签必须能原样出到 stdout。Windows 控制台默认不是 UTF-8,少设一行
    /// <c>Console.OutputEncoding</c> 就会变成一串问号,与「查不到」在调用方眼里同形。
    /// </summary>
    [Fact]
    public void 中文原样出到标准输出()
    {
        var (stdout, _, _) = Run("get", "Apparel_ShieldBelt");
        Assert.Contains("护盾腰带", stdout);
        Assert.DoesNotContain("???", stdout);
    }

    /// <summary>三个退出码要真的传给 shell,脚本才分得清「用错了」「没结果」「成功」。</summary>
    [Theory]
    [InlineData(new[] { "find", "compClass", "RimWorld.CompShield" }, 0)]
    [InlineData(new[] { "get", "NoSuchDefAtAll" }, Runner.ExitNoResults)]
    [InlineData(new[] { "search", "shield", "--nonsense" }, Runner.ExitUsage)]
    [InlineData(new[] { "no-such-command" }, Runner.ExitUsage)]
    public void 退出码如实传给shell(string[] argv, int expected)
    {
        Assert.Equal(expected, Run(argv).Code);
    }

    /// <summary>结果走 stdout、报错走 stderr。混在一起,管道就没法只取结果。</summary>
    [Fact]
    public void 报错走stderr而结果走stdout()
    {
        var (stdout, stderr, _) = Run("search", "shield", "--nonsense");
        Assert.Equal("", stdout);
        Assert.Contains("--nonsense", stderr);
    }

    /// <summary>输出恰好一个结尾换行,且不带 CR —— 输出契约的最后一寸。</summary>
    [Fact]
    public void 输出以单个LF结尾且不含CR()
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true };
        foreach (var a in new[] { "list", "--db", Fixture.Db, "--config", Fixture.NoConfigPath })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var raw = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        Assert.DoesNotContain('\r', raw);
        Assert.EndsWith("\n", raw);
        Assert.False(raw.EndsWith("\n\n", StringComparison.Ordinal), "Output ends with a blank line.");
    }

    /// <summary>
    /// --json 出来的必须是能解析的 JSON,且形状**恒定**:单个 def 也是长度 1 的 defs 数组。
    /// </summary>
    [Fact]
    public void json模式输出可解析()
    {
        var (stdout, _, _) = Run("get", "Apparel_ShieldBelt", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("defs", out var defs));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, defs.ValueKind);
        Assert.Equal(1, defs.GetArrayLength());
        Assert.True(defs[0].TryGetProperty("def", out _));
        Assert.True(defs[0].TryGetProperty("fields", out _));
    }

    /// <summary>
    /// 同名跨 def 类型时每个 def 都保住自己的槽位 —— 上面那条形状约定要挡的就是这种撞键覆盖。
    /// </summary>
    [Fact]
    public void 同名def在json里各占一个槽位()
    {
        var (stdout, _, _) = Run("get", "Firefoam", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        var defs = doc.RootElement.GetProperty("defs");
        Assert.Equal(2, defs.GetArrayLength());

        var types = defs.EnumerateArray()
                        .Select(d => d.GetProperty("def").GetProperty("def_type").GetString())
                        .ToList();
        Assert.Equal(["StatDef", "ThingDef"], types.Order().ToList());

        // 有字段的那一份不许被没字段的那一份盖掉。
        Assert.Contains(defs.EnumerateArray(), d => d.GetProperty("fields").GetArrayLength() > 0);
    }
}
