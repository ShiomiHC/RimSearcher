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

    // ---- 洞-5：缺参提示里那份参数名单，与 schema 的属性是同一份吗 ----
    //
    // §2 丁：`ToolArgSpec.allParameters` 是 schema `properties` 的散文版，六份**当前全对得上**，
    // 但没有任何一处保证它继续对得上，而它坏起来是静默的——加一个 schema 属性、忘了加进散文，
    // 缺参提示就少列一个参数，一条测试都不红。
    //
    // 事实侧不反射那个私有字段，而是**真调一次工具、拿它自己抛出来的那份用法说明**：要验的
    // 就是调用方缺参时看到的那段字，不是产品内部某个字段的值。
    //
    // 两个方向都判：漏列把调用方引到「这个工具没有这个参数」，错列把它引到一个传了会被
    // 报成未知的名字上。
    public static IEnumerable<ParamViolation> AllParametersMatchesTheSchema(Facts facts, string allParameters)
    {
        var listed = new HashSet<string>(ListedNames(allParameters), StringComparer.Ordinal);

        foreach (var property in facts.SchemaProperties)
        {
            if (listed.Contains(property)) continue;
            yield return new ParamViolation(
                "洞-5 用法说明漏列了一个参数",
                $"{facts.Tool.Name} 的 schema 有 '{property}'，而缺参提示的 All parameters 没列它——"
                + $"调用方照这句改会以为这个工具没有这个参数。原句：{allParameters.Trim()}");
        }

        var mine = new HashSet<string>(facts.SchemaProperties, StringComparer.Ordinal);
        foreach (var name in listed)
        {
            if (mine.Contains(name)) continue;
            yield return new ParamViolation(
                "洞-5 用法说明列了一个不存在的参数",
                $"{facts.Tool.Name} 的缺参提示列了 '{name}'，而 schema 里没有这个属性——"
                + $"照这句传进来会吃到一句未知参数提示。原句：{allParameters.Trim()}");
        }
    }

    // `path (required), limit (default 100), offset (page past the server cap).`
    // 参数名是每个顶层逗号段的头一个标识符。括号里也有逗号（`fileFilter (aliases: ext, extension)`），
    // 故先把括号连内容一起剥掉——括号里是说明不是名单，剥掉之后剩下的才是可切的。
    private static IEnumerable<string> ListedNames(string allParameters)
        => Regex.Replace(allParameters, @"\([^()]*\)", string.Empty)
            .Split(',')
            .Select(segment => Regex.Match(segment, @"[A-Za-z][A-Za-z0-9_]*"))
            .Where(match => match.Success)
            .Select(match => match.Value);

    // ---- §2 丁 后半：`requiredSummary` 里那串别名，与读取点收的是同一批吗 ----
    //
    // 这一条与洞-5 是同型的两侧对比，但事实侧换成源码的 `GetRequired*` 调用点——那里第一个
    // 字符串是正名、其余是别名，归属分得开，故漏列与错列两个方向都判得动。
    //
    // 散文的两种写法都认：单必填参数写 `Aliases accepted: a, b`，多必填参数写
    // `Aliases accepted for symbol: a, b`。多必填却不带 `for` 时判红而不是猜——猜错的方向
    // 是把另一个参数的别名算作已列出，那是变松。
    private static readonly Regex AliasClause = new(
        @"Aliases accepted(?: for (?<owner>\w+))?:(?<list>[^.]*)", RegexOptions.Compiled);

    public static IEnumerable<ParamViolation> RequiredAliasesAreComplete(
        Facts facts, string requiredSummary, IReadOnlyDictionary<string, string[]> required)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (Match clause in AliasClause.Matches(requiredSummary))
        {
            var owner = clause.Groups["owner"].Value;
            if (owner.Length == 0)
            {
                if (required.Count != 1)
                {
                    yield return new ParamViolation(
                        "洞-5′ 别名归属不明",
                        $"{facts.Tool.Name} 有 {required.Count} 个必填参数，而 `Aliases accepted:` 没说是谁的。"
                        + "写成 `Aliases accepted for <参数名>:`——不写的话读者只能猜，闸也只能猜。");
                    continue;
                }
                owner = required.Keys.Single();
            }

            declared[owner] = [.. Regex.Matches(clause.Groups["list"].Value, @"[A-Za-z][A-Za-z0-9_]*")
                .Select(m => m.Value)];
        }

        foreach (var (canonical, aliases) in required)
        {
            var listed = declared.TryGetValue(canonical, out var set) ? set : [];

            foreach (var alias in aliases.Where(alias => !listed.Contains(alias)))
                yield return new ParamViolation(
                    "洞-5′ 别名漏列",
                    $"{facts.Tool.Name} 的读取点收 '{alias}' 作 '{canonical}' 的别名，而缺参提示没列它。"
                    + $"原句：{requiredSummary.Trim()}");

            var real = new HashSet<string>(aliases, StringComparer.Ordinal) { canonical };
            foreach (var alias in listed.Where(alias => !real.Contains(alias)))
                yield return new ParamViolation(
                    "洞-5′ 别名错列",
                    $"{facts.Tool.Name} 的缺参提示把 '{alias}' 列成 '{canonical}' 的别名，而读取点不认它——"
                    + $"照这句传进来这个必填参数照样算缺。原句：{requiredSummary.Trim()}");
        }
    }

    // ---- 洞-6：schema 的 `required` 与 `GetRequired*` 的调用点 ----
    //
    // 顺带落地：上面那条为了分清别名归属已经把「哪些参数必填」从源码刮出来了，与 schema 的
    // `required` 对一下就是一句话。两边不一致时，schema 多一个会让校验型客户端提前拦下本来
    // 能跑的调用，少一个会让缺参走到服务端才报。
    public static IEnumerable<ParamViolation> RequiredListMatchesTheReadingPoints(
        Facts facts, IReadOnlyList<string> schemaRequired, IReadOnlyDictionary<string, string[]> required)
    {
        foreach (var name in schemaRequired.Where(name => !required.ContainsKey(name)))
            yield return new ParamViolation(
                "洞-6 schema 说必填，代码没当必填",
                $"{facts.Tool.Name} 的 schema 把 '{name}' 列进 required，而源码里没有一处 GetRequired* 读它。");

        foreach (var name in required.Keys.Where(name => !schemaRequired.Contains(name)))
            yield return new ParamViolation(
                "洞-6 代码当必填，schema 没说",
                $"{facts.Tool.Name} 的 '{name}' 走 GetRequired* 读取（缺了就抛），而 schema 的 required 没有它。");
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

    // schema 的 `required`。缺这个键与空数组同义（都是「没有必填参数」）。
    public static List<string> RequiredOf(ITool tool)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tool.JsonSchema));
        if (!document.RootElement.TryGetProperty("required", out var list)
            || list.ValueKind != JsonValueKind.Array) return [];

        return [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)];
    }

    // 缺参提示里那两行。事实侧要的是**调用方看到的字**，故由调用方真跑一次工具拿到 message
    // 再切开，不从产品的私有字段反射。
    public static (string RequiredSummary, string AllParameters) UsageIn(string missingMessage)
    {
        var required = Regex.Match(missingMessage, @"^Required: (?<text>.*)$", RegexOptions.Multiline);
        var all = Regex.Match(missingMessage, @"^All parameters: (?<text>.*)$", RegexOptions.Multiline);

        Assert.True(required.Success && all.Success,
            $"缺参提示里没找到 `Required:` / `All parameters:` 两行，本闸无从比对。原文：\n{missingMessage}");

        return (required.Groups["text"].Value.TrimEnd(), all.Groups["text"].Value.TrimEnd());
    }

    public static string Describe(string where, IReadOnlyList<ParamViolation> violations)
        => $"{where}：{violations.Count} 处\n  " + string.Join("\n  ", violations.Select(v => v.ToString()));
}
