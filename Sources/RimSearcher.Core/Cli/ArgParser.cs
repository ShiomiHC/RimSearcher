using System.Text;

namespace RimSearcher.Cli;

/// <summary>
/// 严格解析器。CLI 形态的第一新雷区是「未知 flag 被静默吞掉」——调用方以为过滤生效了,
/// 实际拿到的是未过滤结果(master ExtraAcceptedKeys 教训的同构体,01)。这里的立场:
///
///   1. 归一化吃掉纯拼写差异(大小写、<c>-</c>/<c>_</c>/无分隔),这些**有意接受**;
///   2. 声明过的同义词按别名接受;
///   3. 其余一律报错,且必须给近似候选 —— 07-② 实证参数名发明是常态,
///      光说「不认识」等于让调用方再猜一轮。
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

    public static ParseResult Parse(CommandSpec spec, IReadOnlyList<OptionSpec> globals, IReadOnlyList<string> argv)
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
                    errors.Add(UnknownOptionMessage(arg, body, options));
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

    private static string UnknownOptionMessage(string raw, string body, IReadOnlyList<OptionSpec> options)
    {
        var candidates = Suggest(body, options);
        var msg = $"Unknown option '{raw}'.";
        if (candidates.Count > 0)
            msg += $" Did you mean {string.Join(" or ", candidates.Select(c => "--" + c))}?";
        else
            // 没有近似候选时把接受的名字直接列出来。07-② 实证参数名发明是常态,让调用方
            // 为了看一眼选项再跑一次 --help,是白白多花一个来回。
            msg += $" This command accepts: {string.Join(", ", options.Select(o => "--" + o.Name).OrderBy(s => s, StringComparer.Ordinal))}.";
        return msg;
    }

    /// <summary>按归一化后的编辑距离给候选。前缀/包含关系优先,拼错次之。</summary>
    public static List<string> Suggest(string typed, IReadOnlyList<OptionSpec> options)
    {
        var n = Normalize(typed);
        if (n.Length == 0) return [];

        var scored = new List<(int score, string name)>();
        foreach (var o in options)
        {
            var best = int.MaxValue;
            foreach (var key in new[] { o.Name }.Concat(o.Aliases))
            {
                var k = Normalize(key);
                int score;
                if (k.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(k, StringComparison.Ordinal))
                    score = 0;
                else if (k.Contains(n, StringComparison.Ordinal) || n.Contains(k, StringComparison.Ordinal))
                    score = 1;
                else
                    score = Distance(n, k);
                best = Math.Min(best, score);
            }
            // 阈值随长度放宽,但不至于把毫无关系的名字也拉进来
            if (best <= Math.Max(2, n.Length / 3))
                scored.Add((best, o.Name));
        }

        return scored.OrderBy(t => t.score).ThenBy(t => t.name, StringComparer.Ordinal)
                     .Take(Limits.MaxSuggestions).Select(t => t.name).ToList();
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
    /// --limit 的取值。<c>all</c> 是正式取值(07-② 实证:真实调用方高频使用),不是错误;
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

/// <summary>用法错误。CLI 无 schema 兜底,错误消息是一等公民(06 输出契约)。</summary>
public sealed class CliUsageException(string message) : Exception(message);
