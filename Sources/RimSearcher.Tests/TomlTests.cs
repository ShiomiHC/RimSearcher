using RimSearcher.Config;

namespace RimSearcher.Tests;

/// <summary>
/// 配置解析。这是唯一一处**用户手写**的输入,所以它的报错质量直接决定「工具坏了」
/// 和「我第 8 行写错了」在用户眼里分不分得开。
/// </summary>
public class TomlTests
{
    private static Toml.Table Parse(string text) => Toml.Parse(text, "config.toml");

    [Fact]
    public void 基本键值()
    {
        var t = Parse("game_dir = \"D:/Games/RimWorld\"\nsnapshot_dir = \"~/.rimsearcher\"\n");
        Assert.Equal("D:/Games/RimWorld", t.String("game_dir"));
        Assert.Equal("~/.rimsearcher", t.String("snapshot_dir"));
    }

    /// <summary>
    /// Windows 路径里的反斜杠必须原样保留。TOML 严格说应该报「未知转义 \S」,但对一份
    /// 配置文件来说,**静默吃掉**那个反斜杠是最坏的结果 —— 路径变成 D:SteamLibrary,
    /// 后面每一条「找不到目录」都指着一个用户根本没写过的路径。宁可留着。
    /// </summary>
    [Fact]
    public void windows路径里的反斜杠不被吃掉()
    {
        var t = Parse(@"mod_roots = [""D:\SteamLibrary\steamapps\common\RimWorld\Mods""]");
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\RimWorld\Mods", t.Strings("mod_roots")[0]);
    }

    [Fact]
    public void 已知转义照常生效()
    {
        var t = Parse("a = \"line\\nbreak\"\nb = \"say \\\"hi\\\"\"\n");
        Assert.Equal("line\nbreak", t.String("a"));
        Assert.Equal("say \"hi\"", t.String("b"));
    }

    /// <summary>
    /// 路径列表天然要换行写。曾经的实现要求数组写在一行,报错措辞精确却规则本身是错的 ——
    /// 「消息说得清楚」不能替代「规则该不该存在」。
    /// </summary>
    [Fact]
    public void 数组可以跨行写()
    {
        var t = Parse("mod_roots = [\n  \"D:/a\",\n  \"D:/b\",\n  \"D:/c\",\n]\n");
        Assert.Equal(["D:/a", "D:/b", "D:/c"], t.Strings("mod_roots"));
    }

    [Fact]
    public void 单行数组与空数组()
    {
        Assert.Equal(["x", "y"], Parse("k = [\"x\", \"y\"]").Strings("k"));
        Assert.Empty(Parse("k = []").Strings("k"));
    }

    [Fact]
    public void 注释与空行被忽略()
    {
        var t = Parse("# 头部注释\n\ngame_dir = \"x\"   # 行尾注释\n\n# 尾部\n");
        Assert.Single(t.Values);
        Assert.Equal("x", t.String("game_dir"));
    }

    [Fact]
    public void 表头下的键落进子表()
    {
        var t = Parse("[scope_groups]\nfaction = [\"a\", \"b\"]\n");
        Assert.Equal(["a", "b"], t.Sub("scope_groups").Strings("faction"));
        // 同一个键不许同时出现在根上 —— 两处都有会让「哪个是权威」变成运气问题。
        Assert.Empty(t.Strings("faction"));
    }

    /// <summary>问一个不存在的键给空,不抛 —— 配置项本来就大多是可选的。</summary>
    [Fact]
    public void 缺失的键给空值而不抛()
    {
        var t = Parse("a = \"x\"\n");
        Assert.Null(t.String("nope"));
        Assert.Empty(t.Strings("nope"));
        Assert.Empty(t.Sub("nope").Values);
    }

    // ---- 报错 ----

    /// <summary>
    /// 每条报错都要带行号。一份几十行的配置里报「语法错误」而不说第几行,
    /// 用户只能整份重读 —— 那正是错误消息本该替他做的事。
    /// </summary>
    [Theory]
    [InlineData("a = \"unterminated\ngame_dir = \"x\"\n")]
    [InlineData("no_equals_sign\n")]
    [InlineData("[unclosed\n")]
    public void 语法错误带行号(string text)
    {
        var ex = Assert.Throws<TomlError>(() => Parse(text));
        Assert.Matches(@":\d+:", ex.Message);
    }

    [Fact]
    public void 行号指向真正出错的那一行()
    {
        var ex = Assert.Throws<TomlError>(() => Parse("a = \"ok\"\nb = \"ok\"\nc = broken \"quote\n"));
        Assert.Contains(":3:", ex.Message);
    }

    /// <summary>报错里要带上文件名,同时配了好几份 config 时才知道该改哪一份。</summary>
    [Fact]
    public void 报错带上出处文件名()
    {
        var ex = Assert.Throws<TomlError>(() => Toml.Parse("[unclosed\n", "somewhere/other.toml"));
        Assert.Contains("other.toml", ex.Message);
    }

    // ---- 与 RimConfig 的接缝 ----

    /// <summary>不存在的配置文件不是错误 —— 全默认值就该能跑。</summary>
    [Fact]
    public void 配置文件不存在时用默认值()
    {
        var cfg = RimConfig.Load(Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "definitely-not-here.toml"));
        Assert.NotNull(cfg);
        Assert.Empty(cfg.ModRoots);
    }
}
