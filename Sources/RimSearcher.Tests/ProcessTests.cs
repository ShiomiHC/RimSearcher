using System.Diagnostics;
using System.Text;
using RimSearcher.Cli;

namespace RimSearcher.Tests;

/// <summary>
/// 事实侧。别的测试都在进程内调 <c>Runner.Run</c>,这里真起一个进程、真读它的 stdout。
///
/// 01 的教训是「两侧立闸,另一侧不许是另一份声明」。声明侧已经把名单、措辞、文法都判过了;
/// 唯独有一批东西只有真进程才暴露:控制台编码、退出码怎么传给 shell、程序集名跟文档里
/// 写的可执行文件名对不对得上。这几件里任何一件错了,调用方照文档敲的第一条命令就失败,
/// 而进程内测试会全绿。
/// </summary>
public class ProcessTests
{
    /// <summary>
    /// 可执行文件跟测试程序集躺在同一个输出目录里 —— 测试工程 ProjectReference 了 CLI,
    /// 所以只要测试编得出来,它就在。因此这里**不做「找不到就跳过」**:
    /// 会静默跳过的闸不是闸,而 xunit 2.x 也没有真正的跳过,拿 Assert.True 冒充
    /// 只会把「没跑」记成「跑过且通过」。找不到就是真出了问题,该红。
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
    /// 文档、skill、报错消息里写的都是 <c>rimsearcher</c>。程序集名跟它对不上,
    /// 调用方照着敲的第一条命令就找不到文件 —— 而这件事进程内一次也测不出来。
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
    /// 中文标签必须能原样出到 stdout。Windows 控制台默认不是 UTF-8,
    /// 少设一行 <c>Console.OutputEncoding</c> 就会变成一串问号 —— 而那时
    /// 「查不到」和「查到了但显示不出来」在调用方眼里长得一模一样。
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
        foreach (var a in new[] { "types", "--db", Fixture.Db, "--config", Fixture.NoConfigPath })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var raw = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);

        Assert.DoesNotContain('\r', raw);
        Assert.EndsWith("\n", raw);
        Assert.False(raw.EndsWith("\n\n", StringComparison.Ordinal), "Output ends with a blank line.");
    }

    /// <summary>--json 出来的必须是能解析的 JSON,而不是「看起来像」。</summary>
    [Fact]
    public void json模式输出可解析()
    {
        var (stdout, _, _) = Run("get", "Apparel_ShieldBelt", "--json");
        var doc = System.Text.Json.JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("def", out _));
    }
}
