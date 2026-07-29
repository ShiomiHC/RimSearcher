using System.Text.Json;
using System.Text.RegularExpressions;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 参数层常驻闸的**与工具无关**那一层，与输出层的 GrammarRules 同一个分工：规则只吃一个工具的
// 「五元组」（schema、ExtraAcceptedKeys、Description、schema 里逐属性的 description、
// 源码里刮出来的读取键集），回答的是「这几份说法互相对得上吗」。
//
// 参数层指导 §3 列了九个洞，九个的失效签名都是**绿**——它们全都不进返回文本，故 73 份
// tools/call 基线一个字都照不到，而 tools/list 那 7 份只回答「动了哪些字」、不回答「说得对不对」。
//
// 这一版只做两条，理由在下面各自的注释里。两条都严守 §4 甲：**schema 一侧允许反射读取
// （那是名单），另一侧必须独立取得（那是被比对的事实）**——拿 schema 去断言 schema 自己，
// 两边同时错时一片绿。
public readonly record struct ParamViolation(string Rule, string Detail)
{
    public override string ToString() => $"[{Rule}] {Detail}";
}

public static class ParamRules
{
    // 一个工具的五份说法。凑齐了才谈得上比对——单看任何一份都是自洽的。
    public sealed record Facts(
        ITool Tool,
        IReadOnlyList<string> SchemaProperties,
        IReadOnlyDictionary<string, string> SchemaDescriptions,
        IReadOnlyList<string> KeysActuallyRead);

    // ---- 洞-1：schema 声明了的属性，真的有读取点吗 ----
    //
    // 现有的两条闸（EveryKeyAToolReads / EveryKeyAToolDeclares）把 schema 属性名**排除在外**，
    // 注释写着「那是工具的正式参数，认得它们是定义而不是额外声明」。于是这个方向整个没人守：
    // 把 fileFilter 的读取点改个名而 schema 不动，这个参数会被 UnknownKeyNotice 认作合法
    // （它正是从 schema 反射来的）**然后静默吞掉**——调用方传进来，既不生效，也不提示。
    //
    // 判据六：名单取 schema（反射），事实取源码里的读取点（另一侧独立取得），不共用判断。
    public static IEnumerable<ParamViolation> DeclaredPropertiesAreRead(Facts facts)
    {
        var read = new HashSet<string>(facts.KeysActuallyRead, StringComparer.OrdinalIgnoreCase);

        foreach (var property in facts.SchemaProperties)
        {
            if (read.Contains(property)) continue;

            yield return new ParamViolation(
                "洞-1 声明了却没有读取点",
                $"{facts.Tool.Name} 的 schema 声明了 '{property}'，但源码里没有任何读取点认这个名字。"
                + "UnknownKeyNotice 是从 schema 反射出来的，故它会把这个键认作合法然后静默吞掉"
                + "——调用方传进来既不生效也不提示。");
        }
    }

    // ---- 洞-9：散文里带引号的参数名，指的是谁的参数 ----
    //
    // §2 己-6 那一条的形状：list_directory 的 Description 说「the N configured sources listed
    // under 'scope'」，而这个工具的 schema 里只有 path / limit / offset——真传 `scope:` 会吃到
    // 一句未知参数提示。说的是**别的工具**那个 scope，但没说这件事。
    //
    // 判得动的理由（§6 第 2 条）：散文里出现 'xxx' 这种带引号的记号，如果它恰好是**别的工具**
    // 的参数名而不是本工具的，那么要么句子里点出了是哪个工具，要么它就在骗人。两个条件都是
    // 机器能查的，不必懂这句话在说什么。
    //
    // 射程要划死：**只管记号指认得上指认不上，不管这句话说得好不好。**故只在「该记号是本服务器
    // 某个工具的参数名」时才发问——'all'、'usages'、'inheritors' 这些是**值**不是参数名，
    // 一律不碰。越过这条线就回到了非目标里那条禁令（Description 只动被证伪的陈述，不重写）。
    //
    // 己-1 / 己-5 那种「量词与代码的行为相斥」判**不动**：`one member` 对「全部返回」、
    // `at most 40` 对「40 是默认」，两者都要懂那句话在断言什么才判得出来。它们只能靠人读，
    // 而 P0 的基线保证下次改它们时 diff 看得见。这里不假装能判。
    private static readonly Regex QuotedToken = new(@"'(?<token>[A-Za-z][A-Za-z0-9_]*)'", RegexOptions.Compiled);

    public static IEnumerable<ParamViolation> QuotedParameterNamesResolve(
        Facts facts, IReadOnlyDictionary<string, IReadOnlyList<string>> parametersByTool)
    {
        var mine = new HashSet<string>(facts.SchemaProperties, StringComparer.OrdinalIgnoreCase);
        foreach (var key in facts.Tool.ExtraAcceptedKeys) mine.Add(key);

        // 别人有、我没有的参数名。只有这一批才可能把调用方引到「本工具也收它」上。
        var theirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tool, parameters) in parametersByTool)
        {
            if (tool == facts.Tool.Name) continue;
            foreach (var parameter in parameters)
                if (!mine.Contains(parameter)) theirs.Add(parameter);
        }

        // 工具短名（`rimworld-searcher__` 之后那一截）。句子里出现任意一个，就算点了名。
        //
        // 钝角要说清：`trace` / `locate` 这两个短名也是常见英文动词，一句里恰好用到它们
        // 就会白白免责一次。放宽的方向是安全的（漏报而不是误报），而收紧要么得懂句法、
        // 要么得给工具名加引号——后者会把已经写对的那些句子一并判违规。
        var toolNames = parametersByTool.Keys
            .Select(name => name.Contains("__") ? name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : name)
            .ToArray();

        foreach (var (where, prose) in ProseOf(facts))
        foreach (var sentence in Sentences(prose))
        {
            foreach (Match match in QuotedToken.Matches(sentence))
            {
                var token = match.Groups["token"].Value;
                if (!theirs.Contains(token)) continue;
                if (toolNames.Any(name => sentence.Contains(name, StringComparison.Ordinal))) continue;

                yield return new ParamViolation(
                    "洞-9 记号指认不上",
                    $"{facts.Tool.Name} 的 {where} 里出现 '{token}'，那是别的工具的参数名而不是这个工具的。"
                    + "同一句里也没点出是哪个工具，故读者最自然的读法是「本工具也收它」——"
                    + $"而真传会吃到一句未知参数提示。原句：{sentence.Trim()}");
            }
        }
    }

    private static IEnumerable<(string Where, string Prose)> ProseOf(Facts facts)
    {
        yield return ("Description", facts.Tool.Description);
        foreach (var (property, description) in facts.SchemaDescriptions)
            yield return ($"schema '{property}' 的说明", description);
    }

    // 逐句切。判据是「同一句里有没有点名」——整段一起看的话，一处点名会替整段免责，
    // 而己-6 那句恰恰紧挨着一堆点了别的名字的句子。
    private static IEnumerable<string> Sentences(string prose)
        => Regex.Split(prose, @"(?<=[.;])\s+").Where(s => s.Length > 0);

    // ---- 五元组的采集 ----

    // schema 的属性名与逐属性说明。反射读 JsonSchema 那个匿名对象——它是**名单**那一侧，
    // 允许这么取；被比对的事实（读取点）由调用方另行取得。
    public static (List<string> Properties, Dictionary<string, string> Descriptions) SchemaOf(ITool tool)
    {
        var properties = new List<string>();
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tool.JsonSchema));
        if (!document.RootElement.TryGetProperty("properties", out var bag)) return (properties, descriptions);

        foreach (var property in bag.EnumerateObject())
        {
            properties.Add(property.Name);
            if (property.Value.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String)
                descriptions[property.Name] = text.GetString()!;
        }

        return (properties, descriptions);
    }

    public static string Describe(string where, IReadOnlyList<ParamViolation> violations)
        => $"{where}：{violations.Count} 处\n  " + string.Join("\n  ", violations.Select(v => v.ToString()));
}
