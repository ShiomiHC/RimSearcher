using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 一个工具的读取点认哪些键——从**产品源码自己**刮出来。参数层好几道闸都要问这个问题
// （「schema 声明的属性有没有读取点」「读得进来的键会不会被报成被忽略」「声明成认得的键
// 是不是真读得到」），故名单只在这里刮一次。判断仍各做各的（判据六）。
internal static class ToolSourceKeys
{
    // 传给 ToolArgs 读取函数的键。两种形态都要认：
    //
    //   1. **字面量** `ToolArgs.GetInt(args, 0, "startLine", "start")`。带空格的那些不是键
    //      （GetOptionalName 的 whatItNames 槽收的是 "a member name" 这类说明文字）。
    //   2. **名单常量** `ToolArgs.GetInt(args, 0, StartLineKeys)`。一族别名被收进一个 static
    //      数组之后，字面量就不在调用点上了——只刮字面量的话这个键族整片扫不到，而**扫不到的
    //      表现是键集变小，即判据变松，绿**。
    //
    // 认不出的名单标识符**判红**而不是跳过，理由同上：跳过就是静默变松。
    public static ISet<string> ReadBy(ITool tool, [CallerFilePath] string here = "")
    {
        var source = SourceOf(tool, here);

        // 工具 → 源文件按类名猜。猜不中时必须当场说清是怎么回事：直接把路径交给
        // File.ReadAllText 的话，注册表里多一个工具就抛一条 FileNotFoundException，
        // 读者看到的是一个路径而不是「这个工具的取参代码闸扫不到」。取参代码分在两个文件里
        // 那种情形这条判据仍然照不到（它只验第一个文件在不在），那是已知的钝角。
        Assert.True(File.Exists(source),
            $"{tool.Name} 的取参代码没在 {tool.GetType().Name}.cs 里找到，本闸扫不到它的读取点。"
            + "工具类与文件同名是这条判据的前提，改名或拆文件时要一并改这里的映射。");

        var text = File.ReadAllText(source);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match call in Regex.Matches(
                     text, @"ToolArgs\.(?:Get\w+|TryGetElement)\((?:[^()]|\([^()]*\))*\)"))
        {
            foreach (Match literal in Regex.Matches(call.Value, "\"(?<key>[^\"]*)\""))
            {
                var key = literal.Groups["key"].Value;
                if (key.Length > 0 && !key.Contains(' ')) keys.Add(key);
            }

            foreach (Match list in Regex.Matches(
                         call.Value, @"\b(?:(?<owner>\w+)\.)?(?<field>\w+Keys)\b"))
                keys.UnionWith(ResolveKeyList(list.Groups["owner"].Value, list.Groups["field"].Value, tool.GetType()));
        }

        // scope / limit 两族的读取点住在 ScopeAndLimitArgs 里，故按名单取——不在闸这边重列一遍。
        // 上面那个正则只看本工具的源文件，够不到别的类里的调用点。
        if (text.Contains("ScopeAndLimitArgs.Resolve", StringComparison.Ordinal))
            keys.UnionWith(ScopeAndLimitArgs.ScopeKeys);
        if (text.Contains("ScopeAndLimitArgs.GetDisplayLimit", StringComparison.Ordinal))
            keys.UnionWith(ScopeAndLimitArgs.LimitKeys);

        return keys;
    }

    // 必填参数**及其别名**，从 `GetRequired*` 的调用点刮。这一族与 ReadBy 不同：ReadBy 把全体
    // 键揉成一个集合，认得出「这个键读得到」，认不出「它是谁的别名」。而缺参提示里那句
    // `Aliases accepted: …` 说的恰恰是归属，故要一个分得开的名单。
    //
    // 形状是固定的：`ToolArgs.GetRequiredFuzzyString(args, ArgSpec, "symbol", "symbolName", …)`
    // ——函数名里带 Required 就说明这个参数必填，第一个字符串是正名，其余是别名。
    public static Dictionary<string, string[]> RequiredBy(ITool tool, [CallerFilePath] string here = "")
    {
        var source = SourceOf(tool, here);
        Assert.True(File.Exists(source),
            $"{tool.Name} 的取参代码没在 {tool.GetType().Name}.cs 里找到，本闸扫不到它的必填参数。");

        var required = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (Match call in Regex.Matches(
                     File.ReadAllText(source),
                     @"ToolArgs\.GetRequired\w*\((?:[^()]|\([^()]*\))*\)"))
        {
            var names = Regex.Matches(call.Value, "\"(?<key>[^\"]*)\"")
                .Select(m => m.Groups["key"].Value)
                .Where(key => key.Length > 0 && !key.Contains(' '))
                .ToArray();

            if (names.Length == 0) continue;
            required[names[0]] = [.. names.Skip(1)];
        }

        return required;
    }

    private static string SourceOf(ITool tool, string here)
        => Path.Combine(
            Directory.GetParent(Path.GetDirectoryName(here)!)!.FullName,
            "RimSearcher.Server", "Tools", $"{tool.GetType().Name}.cs");

    // 名单常量 → 它的值。owner 为空时是工具类自己的字段（read_code 的 StartLineKeys /
    // LineCountKeys），否则按类名在 Server 程序集里找（ScopeAndLimitArgs.LimitKeys）。
    private static string[] ResolveKeyList(string owner, string field, Type toolType)
    {
        var declaring = owner.Length == 0
            ? toolType
            : toolType.Assembly.GetTypes().FirstOrDefault(t => t.Name == owner);

        var value = declaring
            ?.GetField(field, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?.GetValue(null);

        Assert.True(value is string[],
            $"读取点用了名单 '{(owner.Length == 0 ? field : $"{owner}.{field}")}'，但闸反射不到它的值。"
            + "扫不到的表现是键集变小、判据变松、照绿，故这里判红——要么它不是 public static string[]，"
            + "要么它住在别的程序集里。");

        return (string[])value!;
    }
}
