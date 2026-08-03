using System.Text;
using RimSearcher.Output;

namespace RimSearcher.Cli;

/// <summary>
/// 严格解析器。未知 flag 绝不静默吞掉 —— 那会让调用方以为过滤生效、实际拿到未过滤结果。
///
///   1. 归一化吃掉纯拼写差异(大小写、<c>-</c>/<c>_</c>/无分隔),这些**有意接受**;
///   2. 声明过的同义词按别名接受;
///   3. 其余一律报错,且必须给近似候选。
/// </summary>
public static class ArgParser
{
    /// <summary>归一化:小写 + 去掉所有非字母数字。fileFilter / file_filter / File-Filter 同归一。</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static ParseResult Parse(CommandSpec spec, IReadOnlyList<OptionSpec> globals, IReadOnlyList<string> argv,
                                    IReadOnlyList<CommandSpec>? siblings = null)
    {
        var options = spec.UsesGlobals ? [.. spec.Options, .. globals] : spec.Options.ToArray();

        // 名字 → 声明。同一声明会挂多个键(规范名、短名、别名),全部走归一化。
        var byKey = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
        foreach (var o in options)
        {
            byKey[Normalize(o.Name)] = o;
            foreach (var a in o.Aliases) byKey[Normalize(a)] = o;
        }
        var byShort = new Dictionary<char, OptionSpec>();
        foreach (var o in options)
            if (o.Short is { } c) byShort[c] = o;

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positionals = new List<string>();
        var errors = new List<string>();
        var wantsHelp = false;

        void Record(OptionSpec o, string v)
        {
            if (!values.TryGetValue(o.Name, out var list))
                values[o.Name] = list = [];
            if (o.Arity == Arity.Single) list.Clear();
            list.Add(v);
        }

        var noMoreOptions = false;
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];

            if (noMoreOptions) { positionals.Add(arg); continue; }
            if (arg == "--") { noMoreOptions = true; continue; }

            if (arg is "-h" or "--help" or "-?" or "/?" or "help")
            {
                wantsHelp = true;
                continue;
            }

            if (arg.Length >= 2 && arg[0] == '-' && arg != "-")
            {
                var isLong = arg.StartsWith("--", StringComparison.Ordinal);
                var body = isLong ? arg[2..] : arg[1..];
                string? inlineValue = null;
                var eq = body.IndexOf('=');
                if (eq >= 0) { inlineValue = body[(eq + 1)..]; body = body[..eq]; }

                OptionSpec? opt = null;
                if (!isLong && body.Length == 1 && byShort.TryGetValue(body[0], out var s)) opt = s;
                if (opt is null) byKey.TryGetValue(Normalize(body), out opt);

                if (opt is null)
                {
                    // 那个词后面挂着的取值。判据与下面跳过它的那条一致 —— 报错要把它填进
                    // 正确的写法里,而不是让人对着 <占位符> 自己再拼一遍。
                    var attached = inlineValue ??
                        (i + 1 < argv.Count && !argv[i + 1].StartsWith('-') ? argv[i + 1] : null);
                    errors.Add(UnknownOptionMessage(arg, body, options, spec, siblings, attached));
                    // 未知 flag 后面若跟着一个非 flag 的词,大概率是它的取值,一并跳过,
                    // 免得那个词又被当成位置参数引出第二条无关报错。
                    if (inlineValue is null && i + 1 < argv.Count && !argv[i + 1].StartsWith('-')) i++;
                    continue;
                }

                if (opt.Arity == Arity.Flag)
                {
                    if (inlineValue is not null)
                        errors.Add($"--{opt.Name} is a switch and takes no value (got '{inlineValue}').");
                    Record(opt, "true");
                    continue;
                }

                var value = inlineValue;
                if (value is null)
                {
                    if (i + 1 >= argv.Count)
                    {
                        errors.Add($"--{opt.Name} needs a value: {opt.Help}");
                        continue;
                    }
                    value = argv[++i];
                }

                if (opt.Choices.Length > 0 &&
                    !opt.Choices.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"--{opt.Name} does not accept '{value}'. Valid values: {string.Join(", ", opt.Choices)}.");
                    continue;
                }

                Record(opt, value);
                continue;
            }

            positionals.Add(arg);
        }

        // 位置参数:数量与必填
        var declared = spec.Positionals;
        var variadic = declared.Length > 0 && declared[^1].Variadic;
        if (!variadic && positionals.Count > declared.Length)
        {
            var extra = string.Join(", ", positionals.Skip(declared.Length).Select(p => $"'{p}'"));
            var shape = declared.Length == 0
                ? $"{spec.Name} takes no positional arguments"
                : $"{spec.Name} takes {declared.Length} positional argument(s): {string.Join(" ", declared.Select(d => $"<{d.Name}>"))}";
            errors.Add($"Unexpected argument(s) {extra}. {shape}.");
        }

        if (!wantsHelp)
        {
            for (var i = 0; i < declared.Length; i++)
                if (declared[i].Required && i >= positionals.Count)
                    errors.Add($"Missing required argument <{declared[i].Name}>: {declared[i].Help}");

            foreach (var o in options)
                if (o.Required && !values.ContainsKey(o.Name))
                    errors.Add($"Missing required option --{o.Name}: {o.Help}");
        }

        return new ParseResult(spec, positionals, values, errors, wantsHelp);
    }

    private static string UnknownOptionMessage(string raw, string body, IReadOnlyList<OptionSpec> options,
                                               CommandSpec spec, IReadOnlyList<CommandSpec>? siblings,
                                               string? attachedValue = null)
    {
        var scored = Scored(body, options);
        var candidates = Ranked(scored);

        // 同一个词在这条命令上是**位置参数**。这一档排在「别的命令有」之前:那句只说得出
        // 「这里不行」,而这里说得出「这里该怎么写」,还能把值填进去 —— 而且落空的多半正是
        // 跨命令搬过来的写法(--field 是 get / inherit / read 认的,where 把它放在位置上)。
        //
        // 与选项比分,严格更近才赢,两边同分让给选项:`--values` 在位置参数 <value> 与
        // 选项 --value 上都是前缀命中,而它想要的显然是拼错了的那个选项。反过来 `--field`
        // 是 <fieldPath> 的前缀(0 分),在选项那边只是别名 any-field 的子串(1 分) ——
        // 不比分只看「选项那边有没有候选」的话,这一条会被那个别名挡掉。
        var positional = MatchPositional(body, spec.Positionals);
        var bestOption = scored.Count == 0 ? int.MaxValue : scored.Min(t => t.Score);
        if (positional is { } hit && hit.Score < bestOption)
        {
            var at = Array.IndexOf(spec.Positionals, hit.Spec);
            var shape = string.Join(" ", spec.Positionals.Select(
                (p, idx) => idx == at && attachedValue is not null ? attachedValue : $"<{p.Name}>"));
            return $"Unknown option '{raw}'. On '{spec.Name}' it is an argument rather than an option: " +
                   $"'{CommandRegistry.ExeName} {spec.Name} {shape}'.";
        }

        var n = Normalize(body);
        var elsewhere = (siblings ?? [])
            .Where(s => !string.Equals(s.Name, spec.Name, StringComparison.Ordinal))
            .Where(s => s.Options.Any(o => Normalize(o.Name) == n || o.Aliases.Any(a => Normalize(a) == n)))
            .Select(s => $"'{s.Name}'")
            .ToList();

        // 本命令自己的近似候选排在跨命令那句之前。反过来放会把改名后的旧名指向错方向:
        // `get --path-contains` 改名之后 `--path` 在 docs 上仍是 --out 的别名,于是
        // 「It is accepted by 'docs'」抢先返回,而 get 自己叫它什么一个字都没说。
        // 两句都留:跨命令那条信息没有因为让位而丢掉。
        var msg = $"Unknown option '{raw}'.";
        if (candidates.Count > 0)
        {
            msg += $" Did you mean {string.Join(" or ", candidates.Select(c => "--" + c))}?";
            if (elsewhere.Count > 0)
                msg += " The name as typed is accepted by " +
                       NameList.Render(elsewhere, Limits.MaxSuggestions) + $", but not by '{spec.Name}'.";
        }
        else if (elsewhere.Count > 0)
            // 截断走 NameList 而非自己 Take:它会带出被省掉的数量,
            // 否则「只列前三条」与「一共就这三条认」逐字同形。
            msg += " It is accepted by " +
                   NameList.Render(elsewhere, Limits.MaxSuggestions) + $", but not by '{spec.Name}'.";
        else
            // 没有近似候选时直接列出接受的名字,省掉一次 --help 往返。
            msg += $" This command accepts: {string.Join(", ", options.Select(o => "--" + o.Name).OrderBy(s => s, StringComparer.Ordinal))}.";
        return msg;
    }

    /// <summary>
    /// 打进来的名字指的是不是这条命令的某个位置参数,以及有多近。
    ///
    /// 分数与 <see cref="Scored"/> 同尺:0 是前缀关系、1 是包含关系。不吃编辑距离 ——
    /// 位置参数一共就一两个,距离一放宽,随便一个拼错的选项都会撞上其中一个。
    /// </summary>
    private static (PositionalSpec Spec, int Score)? MatchPositional(
        string typed, IReadOnlyList<PositionalSpec> positionals)
    {
        var n = Normalize(typed);
        if (n.Length == 0) return null;
        (PositionalSpec Spec, int Score)? best = null;
        foreach (var p in positionals)
        {
            var k = Normalize(p.Name);
            int score;
            if (k.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(k, StringComparison.Ordinal))
                score = 0;
            else if (k.Contains(n, StringComparison.Ordinal) || n.Contains(k, StringComparison.Ordinal))
                score = 1;
            else
                continue;
            if (best is null || score < best.Value.Score) best = (p, score);
        }
        return best;
    }

    /// <summary>按归一化后的编辑距离给候选。前缀/包含关系优先,拼错次之。</summary>
    public static List<string> Suggest(string typed, IReadOnlyList<OptionSpec> options)
        => Ranked(Scored(typed, options));

    /// <summary>
    /// 只留最好的那一档。跨档并列会把一条准的稀释掉:`--path` 在 `--path-contains` 上是
    /// 前缀(0 分),在 `--db` 上只是别名 snapshot-path 的子串(1 分),而
    /// 「Did you mean --path-contains or --db?」读起来两个一样可信。
    /// 同档并列照旧全列 —— 那时确实分不出谁更近。
    /// </summary>
    private static List<string> Ranked(List<(int Score, string Name)> scored)
    {
        if (scored.Count == 0) return [];
        var best = scored.Min(t => t.Score);
        return scored.Where(t => t.Score == best).OrderBy(t => t.Name, StringComparer.Ordinal)
                     .Take(Limits.MaxSuggestions).Select(t => t.Name).ToList();
    }

    /// <summary>
    /// 候选与它们的分数。分数要出得来,是因为「这个词其实是位置参数」那一档得跟选项比远近,
    /// 而只看「选项那边有没有候选」会被一条别名的子串关系挡掉。
    /// </summary>
    private static List<(int Score, string Name)> Scored(string typed, IReadOnlyList<OptionSpec> options)
    {
        var n = Normalize(typed);
        if (n.Length == 0) return [];

        var scored = new List<(int Score, string Name)>();
        foreach (var o in options)
        {
            // 别名只认前缀/包含关系,不参与编辑距离打分:距离是给「打错规范名」用的,
            // 别名吃距离会把毫不相干的参数拉成近似候选(`--type` 经别名 `top` 命中 `--limit`)。
            var best = int.MaxValue;
            foreach (var (key, isAlias) in new[] { (o.Name, false) }.Concat(o.Aliases.Select(a => (a, true))))
            {
                var k = Normalize(key);
                int score;
                if (k.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(k, StringComparison.Ordinal))
                    score = 0;
                else if (k.Contains(n, StringComparison.Ordinal) || n.Contains(k, StringComparison.Ordinal))
                    score = 1;
                else if (isAlias)
                    continue;
                else
                    score = Distance(n, k);
                best = Math.Min(best, score);
            }
            // 阈值随长度放宽,但不至于把毫无关系的名字也拉进来
            if (best <= Math.Max(2, n.Length / 3))
                scored.Add((best, o.Name));
        }

        return scored;
    }

    /// <summary>Levenshtein 距离。</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>解析产物。命令实现只通过这里取参,不碰 argv。</summary>
public sealed class ParseResult(
    CommandSpec spec,
    List<string> positionals,
    Dictionary<string, List<string>> values,
    List<string> errors,
    bool wantsHelp)
{
    public CommandSpec Spec { get; } = spec;
    public IReadOnlyList<string> Positionals { get; } = positionals;
    public IReadOnlyList<string> Errors => errors;
    public bool WantsHelp { get; } = wantsHelp;
    public bool HasErrors => errors.Count > 0;

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;

    public string? Value(string name) => values.TryGetValue(name, out var l) && l.Count > 0 ? l[^1] : null;

    public IReadOnlyList<string> Values(string name) => values.TryGetValue(name, out var l) ? l : [];

    public bool Flag(string name) => values.ContainsKey(name);

    public bool Has(string name) => values.ContainsKey(name);

    /// <summary>
    /// 这一次真给了的**收窄参数**,按声明层的 <see cref="OptionSpec.Narrows"/> 认,
    /// 渲染成可以原样贴回命令行的一串(<c>--type MentalStateDef --exact</c>)。
    /// </summary>
    public string Narrowing()
    {
        var parts = new List<string>();
        foreach (var o in Spec.Options.Where(o => o.Narrows))
        {
            if (!values.TryGetValue(o.Name, out var given) || given.Count == 0) continue;
            if (o.Arity == Arity.Flag) { parts.Add($"--{o.Name}"); continue; }
            parts.AddRange(given.Select(v => $"--{o.Name} {v}"));
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// --limit 的取值。<c>all</c> 是正式取值,不是错误;
    /// 超过 <see cref="Limits.MaxLimit"/> 的数被夹紧,夹紧事实由调用方在输出里声明。
    /// </summary>
    public LimitValue Limit(string name = "limit", int? fallback = null)
    {
        var raw = Value(name);
        if (raw is null) return LimitValue.Of(fallback ?? Limits.DefaultLimit);
        if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase) ||
            raw == "0" || raw == "-1")
            return LimitValue.All;
        if (int.TryParse(raw, out var n) && n > 0)
            return LimitValue.Of(Math.Min(n, Limits.MaxLimit), clamped: n > Limits.MaxLimit);
        throw new CliUsageException(
            $"--{name} expects a positive whole number or 'all' (got '{raw}').");
    }

    /// <summary>
    /// --offset 的取值。负数在 SQL 里等同于 0,会让 <c>--offset -5</c> 静默出第一页,故拒收。
    /// </summary>
    public int Offset(string name = "offset")
    {
        var raw = Value(name);
        if (raw is null) return 0;
        if (int.TryParse(raw, out var n) && n >= 0) return n;
        throw new CliUsageException(
            $"--{name} expects a whole number of rows to skip, zero or more (got '{raw}').");
    }

    public int Int(string name, int fallback)
    {
        var raw = Value(name);
        if (raw is null) return fallback;
        if (int.TryParse(raw, out var n)) return n;
        throw new CliUsageException($"--{name} expects a whole number (got '{raw}').");
    }
}

/// <summary>limit 的三态取值:具体数 / 全部 / 被夹紧的具体数。</summary>
public readonly record struct LimitValue(int? Count, bool Clamped)
{
    public static LimitValue All => new(null, false);
    public static LimitValue Of(int n, bool clamped = false) => new(n, clamped);
    public bool IsAll => Count is null;
    /// <summary>拿去做 SQL LIMIT 用的数;全部时返回 int.MaxValue。</summary>
    public int Effective => Count ?? int.MaxValue;
}

/// <summary>用法错误。CLI 无 schema 兜底,错误消息是一等公民。</summary>
public sealed class CliUsageException(string message) : Exception(message);
