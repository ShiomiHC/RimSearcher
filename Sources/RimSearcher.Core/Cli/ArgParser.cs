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
    /// <summary>
    /// def 类型名的形状。判据是**纯静态**的(以 Def 结尾的标识符)—— 解析层没有库,
    /// 问不出「这个词是不是这份快照里的一个 def 类型」。够用的理由是它只在
    /// 今天必然硬失败的入参上被问到:认错了的代价是一句诚实的「这份快照里没有这个类型」,
    /// 而不认的代价是一条 usage 错误。实测语料里 261 次全部以 Def 结尾。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DefTypeShape =
        new(@"^[A-Za-z_][A-Za-z0-9_]*Def$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 负数取值的形状。选项名一律不以数字打头(短名是字母,长名以 <c>--</c> 打头),于是
    /// <c>-74</c> / <c>-0.5</c> 只可能是取值 —— 不放行的话它落进「未知选项」,而报错里
    /// 给的近似候选(实测 <c>Did you mean --db?</c>)与真正的问题毫无关系。
    ///
    /// 这一条同时是 <c>--</c> 被误用的成因:负值不需要 <c>--</c> 挡,而拿 <c>--</c> 挡了的话,
    /// 它之后的每个词都成了位置参数,连 <c>--exact</c> 一起。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NegativeNumber =
        new(@"^-(\d|\.\d)", System.Text.RegularExpressions.RegexOptions.Compiled);

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
        var sawDoubleDash = false;
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];

            if (noMoreOptions) { positionals.Add(arg); continue; }
            if (arg == "--") { noMoreOptions = sawDoubleDash = true; continue; }

            if (arg is "-h" or "--help" or "-?" or "/?" or "help")
            {
                wantsHelp = true;
                continue;
            }

            if (arg.Length >= 2 && arg[0] == '-' && arg != "-" && !NegativeNumber.IsMatch(arg))
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
                    errors.Add(UnknownOptionMessage(arg, body, options, spec, siblings, attached, positionals));
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
        var notes = new List<string>();

        // 「类型打头」:`<命令> <SomethingDef> <真正的参数>`。list 与 fields 把 def 类型放在
        // 位置上,get / values / search / where 把它放在 --type 上,而调用方把前者外推到后者
        // 是稳定行为 —— 2026-08-31 在 9282 次真实调用里量到 get 122 / values 57 / where 82 次。
        //
        // **只改今天必然硬失败的入参**。四条判据一起成立:恰好多出一个位置参数、首位形如
        // <名字>Def、这条命令确实有 --type、--type 没被显式给过。四条同时成立时,原样跑下去
        // 的唯一结果是下面那条 usage 错误 —— 也就是说这里改写不掉任何一条今天跑得通的命令,
        // 于是不存在「抢走一个合法查询」这种代价。
        //
        // where 的两个位置参数被填满时**不进这里**(那时 positionals.Count == declared.Length),
        // 而那正是不许重解释的那一档:`thingDef` 是真字段名,路径匹配 NOCASE,
        // `where ThingDef X` 与 `where thingDef X` 从入参上分不开。那一档只出声,
        // 由命令自己判 —— 它手上有库,判得出「这个词同时是个 def 类型」。
        //
        // 可变位置参数(get)上「恰好多出一个」不再是硬失败 —— `get ThingDef X` 会读成两个
        // 名字,而 'ThingDef' 那一块落空。判据放宽成「至少两个」:抢走的只有「几个名字里
        // 第一个恰好以 Def 收尾」这一种写法,而那句说明原样印出来,读的人当场看得见。
        // 显式给过 --type 时同样让位(下面那条 usage 错误),两者不一致正是最该出声的时候。
        //
        // 某一格用选项拼法给了(PositionalSpec.Option,`where ThingDef --field X`)时按**空着的格**数:
        // 那时裸词只可能是没用选项给的那几格加一个类型。`--field` 在 where / values 上被拒了六周、
        // 每周仍出现(Docs/26 §4),60 条历史样本里 38 条是这个类型打头的形状,--value 就在旁边 ——
        // 于是选项拼法在场时,像类型的裸首词读作类型,想给一个以 Def 收尾的值就写 --value。
        var spelled = declared.Select(d => d.Option is { } on && values.TryGetValue(on, out var sv) && sv.Count > 0 ? sv : null).ToArray();
        var spelledCount = spelled.Count(s => s is not null);
        var open = declared.Length - spelledCount;
        var typeLead = declared.Length > 0 && positionals.Count >= 1 &&
                       (spelledCount > 0
                           ? positionals.Count <= open + 1
                           : positionals.Count >= declared.Length + 1 && (variadic || positionals.Count == declared.Length + 1)) &&
                       !string.Equals(declared[0].Name, "defType", StringComparison.Ordinal) &&
                       DefTypeShape.IsMatch(positionals[0]);
        var typeExplicit = byKey.TryGetValue("type", out var typeOpt0) && values.ContainsKey(typeOpt0.Name);
        if (typeLead && !typeExplicit && byKey.TryGetValue("type", out var typeOpt))
        {
            var lead = positionals[0];
            positionals.RemoveAt(0);
            Record(typeOpt, lead);
            // 静默接受,但不静默 —— 不说的话下次还是这么写,而「within --type X」那句
            // 只说得出「筛过了」,说不出「你写的那个词是从哪一格挪过去的」。
            // 选项拼法在场时裸词列表装不下这一格(`--field X` 不在里面),把它按选项形态补回去。
            // 不照 argv 整条抄:--db / --config 这类管道选项会跟进来,而那句是给人粘的。
            var spelledOut = declared.Where((_, i) => spelled[i] is not null)
                                     .Select((d, i) => $"--{d.Option} {string.Join($" --{d.Option} ", spelled[Array.IndexOf(declared, d)]!)}");
            var written = string.Join(" ", positionals.Concat(spelledOut));
            notes.Add($"Read '{lead}' as --type {lead}, not as <{declared[0].Name}>: on '{spec.Name}' the def " +
                      $"type is an option. Written out, this call is '{CommandRegistry.ExeName} {spec.Name} " +
                      written + $" --type {lead}'.");
        }

        // 选项拼法落格:按声明顺序,每一格取选项给的,没给的依次拿裸词。前面有格空着时后面的
        // 选项拼法不落格(表里不留空洞,命令侧照旧从选项读);格都填完还剩裸词,就是同一格
        // 给了两遍 —— 说清哪一格,不让它落进下面「多给了 N 个」那句。
        if (spelledCount > 0)
        {
            var filled = new List<string>();
            var raw = new Queue<string>(positionals);
            for (var i = 0; i < declared.Length; i++)
            {
                if (spelled[i] is { } sv)
                {
                    if (declared[i].Variadic) filled.AddRange(sv); else filled.Add(sv[^1]);
                }
                else if (raw.Count > 0) filled.Add(raw.Dequeue());
                else break;
            }
            if (raw.Count > 0 && variadic)
                filled.AddRange(raw);
            else if (raw.Count > 0)
            {
                // 点名的裸词取**原来站在那一格位置上**的那个:`where compClass X --field thingClass`
                // 里多出来的是 X,但与 --field 撞的是 compClass。
                var twice = declared.Select((d, i) => (d, i)).First(t => spelled[t.i] is not null);
                var atSlot = twice.i < positionals.Count ? positionals[twice.i] : raw.Peek();
                errors.Add($"<{twice.d.Name}> is given twice: '{atSlot}' as an argument and --{twice.d.Option} " +
                           $"{spelled[twice.i]![^1]} as an option. They are the same argument; keep one.");
            }
            positionals = filled;
        }

        // 可变位置参数上,首位像类型而 --type 又明写了:两个类型不一致,不许悄悄把首位
        // 当成一个 def 名字吞下去 —— 那样得到的是一块「No def is named 'ThingDef'」,
        // 而真正的问题(两处类型对不上)一个字都不会出现。
        if (variadic && typeLead && typeExplicit && byKey.TryGetValue("type", out var explicitType) &&
            values.TryGetValue(explicitType.Name, out var typed) && typed.Count > 0)
        {
            var lead = positionals[0];
            var rest = string.Join(" ", positionals.Skip(1));
            errors.Add($"The def type is given twice: '{lead}' as an argument and --type {typed[^1]} as an " +
                       $"option. Keep one — with --type {typed[^1]} the whole query is " +
                       $"'{CommandRegistry.ExeName} {spec.Name} {rest} --type {typed[^1]}'.");
        }

        if (!variadic && positionals.Count > declared.Length)
        {
            var extra = string.Join(", ", positionals.Skip(declared.Length).Select(p => $"'{p}'"));
            var shape = declared.Length == 0
                ? $"{spec.Name} takes no positional arguments"
                : $"{spec.Name} takes {declared.Length} positional argument(s): {string.Join(" ", declared.Select(d => $"<{d.Name}>"))}";
            // 上面那条没接住、而首位仍长得像 def 类型。光说「多了一个参数」的话,读的人下一步
            // 多半是去掉**后面**那个,而后面那个才是他要查的东西 —— 所以把去掉类型之后的
            // 那条命令原样给出来。
            //
            // 但没接住有两种成因,给的话完全不同,合成一句必然有一种是假话:
            // 这条命令根本没有类型面(inherit),与它有、只是已经明写过一个(那时按位置
            // 写的那个不许悄悄盖掉明写的那个 —— 两者不一致正是最该出声的时候)。
            // list / fields 不进来:它们的第一个位置参数本来就是类型,那个词没放错格。
            var lead = declared.Length > 0 && !string.Equals(declared[0].Name, "defType", StringComparison.Ordinal) &&
                       positionals.Count > 0 && DefTypeShape.IsMatch(positionals[0])
                ? positionals[0] : null;
            // `--` 吞掉的那一档。多出来的位置参数里有以 `-` 打头的词,而 `--` 又确实给过 ——
            // 那时「多给了 N 个位置参数」这句话是真的,但它说不出这 N 个是从哪儿来的:
            // 调用方写下的 `--exact` 在他眼里是个选项。这一档比下面的类型打头更具体,先判。
            // 给的出路是**去掉 `--` 之后的整条命令**,不是「把 `--` 挪个位置」。
            var dashSwallowed = sawDoubleDash &&
                                positionals.Skip(declared.Length).Any(p => p.StartsWith('-'));
            var rest = string.Join(" ", positionals.Skip(1));
            var given = lead is not null && byKey.TryGetValue("type", out var declaredType) &&
                        values.TryGetValue(declaredType.Name, out var typeValues) && typeValues.Count > 0
                ? typeValues[^1] : null;
            errors.Add($"Unexpected argument(s) {extra}. {shape}." +
                       (dashSwallowed
                           ? $" '--' turns every following word into a positional argument, so " +
                             $"{string.Join(", ", positionals.Skip(declared.Length).Where(p => p.StartsWith('-')).Select(p => $"'{p}'"))} " +
                             $"stopped being options. A value that starts with a minus sign does not need it: " +
                             $"'{CommandRegistry.ExeName} {spec.Name} {string.Join(" ", argv.Where(a => a != "--"))}'."
                       : lead is null
                           ? ""
                           : given is not null
                               ? $" The def type is given twice: '{lead}' as an argument and --type {given} as an " +
                                 $"option. Keep one — with --type {given} the whole query is " +
                                 $"'{CommandRegistry.ExeName} {spec.Name} {rest} --type {given}'."
                               : $" '{lead}' looks like a def type, and '{spec.Name}' has no def-type filter to " +
                                 $"put it in: '{CommandRegistry.ExeName} {spec.Name} {rest}' is the whole query."));
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

        return new ParseResult(spec, positionals, values, errors, wantsHelp, notes);
    }

    private static string UnknownOptionMessage(string raw, string body, IReadOnlyList<OptionSpec> options,
                                               CommandSpec spec, IReadOnlyList<CommandSpec>? siblings,
                                               string? attachedValue = null, IReadOnlyList<string>? positionals = null)
    {
        // 有意不收的拼法排在最前:那句话说的是边界(这件事住在哪条命令里),近似候选与
        // 「别的命令认它」在这一档都是往错处指 —— `grep` 恰好是 list --find 的别名,实测
        // 那句「It is accepted by 'get', 'list', 'inherit', and 2 more」把人指向了三条无关命令。
        // 读者已经写下的词填回那句话里:跟在后面的值进 ValuePlaceholder,第一个位置参数
        // (read 的 <file>)进 <file>,于是给出去的是整条能粘的命令,不是占位符。
        var n0 = Normalize(body);
        var refused = spec.Refused.FirstOrDefault(r => Normalize(r.Name) == n0 || r.Aliases.Any(a => Normalize(a) == n0));
        if (refused is not null)
        {
            var where = refused.Where;
            // 值带着 shell 会吃的字符(`smelt|Smelt`)就加引号 —— 这句是给人粘的。
            if (attachedValue is not null && refused.ValuePlaceholder is { } ph)
                where = where.Replace(ph, System.Text.RegularExpressions.Regex.IsMatch(attachedValue, @"^[\w./-]+$")
                    ? attachedValue
                    : "\"" + attachedValue.Replace("\"", "\\\"") + "\"");
            if (positionals is { Count: > 0 } && spec.Positionals.Length > 0)
                where = where.Replace($"<{spec.Positionals[0].Name}>", positionals[0]);
            return $"Unknown option '{raw}'. {where}";
        }

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
        else
            // 没有近似候选时列出这条命令自己收什么,省掉一次 --help 往返。
            //
            // 「别的命令收它」在这一支**不说** —— 它与「这里有什么」面对的是同一种局面
            // (本命令没有近似名),而那件事回答不了读者的问题:`export --check` 会被指去
            // 'docs',而那条判的是文档文件是否最新,与要导出的东西无关。
            // 名字在别处存在,不蕴含那边那个就是这里要的东西。
            //
            // 这里原先还举 `read --start` 会被指去 search / where / list(那边它是
            // --offset 的别名)。那个例子 2026-09-09 起不存在了:实测那三个别名逐字
            // 各 0 次,已从 --offset 摘掉,`--start` 转由 read 自己收作行号。**决定没变,
            // 变的是它剩几条腿** —— 上面 export 那条与它同形,单独也立得住。
            //
            // 选项表则是自足的:它同时答得出「这里有什么」和「这里没有什么」。
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
            // 别名只认前缀关系,不参与编辑距离打分也不认中段包含:距离是给「打错规范名」
            // 用的,别名吃距离会把毫不相干的参数拉成近似候选(`--type` 经别名 `top`
            // 命中 `--limit`)。
            //
            // 中段包含是同一个坑往下一格:`--from` 落在 `--offset` 的别名 `page-from`
            // 尾巴上,于是删掉 `--scope` 的 `from` 别名之后,旧写法被指去 `--offset` ——
            // 一个体面的、方向完全错的答案。别名本来就是「另一种叫法」,不是「另一个词根」,
            // 判前缀够了。规范名照旧认包含(它是这条选项的正身,子串关系有意义)。
            var best = int.MaxValue;
            foreach (var (key, isAlias) in new[] { (o.Name, false) }.Concat(o.Aliases.Select(a => (a, true))))
            {
                var k = Normalize(key);
                int score;
                if (k.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(k, StringComparison.Ordinal))
                    score = 0;
                else if (isAlias)
                    continue;
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
    bool wantsHelp,
    List<string>? notes = null)
{
    public CommandSpec Spec { get; } = spec;
    public IReadOnlyList<string> Positionals { get; } = positionals;
    public IReadOnlyList<string> Errors => errors;

    /// <summary>解析层**接受**下来、但改了写法的那些事。空表示逐字照收。</summary>
    public IReadOnlyList<string> Notes => notes ?? [];
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
    /// --limit 的取值:正整数,要多少给多少。**不给就是全部**,没有第二道闸压在它之上。
    ///
    /// 2026-09-05 一次改到位,三件旧设施同时退役,判据都是重放 Vethara 侧的真实调用:
    /// - 列表类默认 25 / get 默认 60:没写 --limit 的 547 次里全量后最大 15 KB,
    ///   没有一次撞到 harness 的 30000 字符截断;
    /// - 2000 夹板:330 次数字调用无一超过它,写过的最大数字是 80。留着只会让
    ///   `--limit 5000` 拿到比不填更少的行;
    /// - <c>all</c> / <c>none</c> / <c>0</c> / <c>-1</c> 这几个「解除上限」的取值:
    ///   不给已经就是全部,它们没有一个还能表达别的意思。
    /// </summary>
    public LimitValue Limit(string name = "limit")
    {
        var raw = Value(name);
        if (raw is null) return LimitValue.All;
        if (int.TryParse(raw, out var n) && n > 0) return LimitValue.Of(n);
        throw new CliUsageException(
            $"--{name} expects a positive whole number (got '{raw}').");
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

    /// <summary>
    /// 路径轴的两条拼法收成一份:<c>--path-contains</c>(子串)与 <c>--exact-path</c>(整条)。
    /// 回传的 bool 是「按整条比」。
    ///
    /// 两个一起给要报错而不是取并集:它们是同一条轴上的两档,一次调用只能站一档 ——
    /// 取并集的话那句「其中几条是整段命中」按哪一档数都说不清,而那句数正是这条轴上
    /// 唯一分得开两态的东西。同 <c>read</c> 的 --lines 与 --start/--end。
    /// </summary>
    public (IReadOnlyList<string> Paths, bool Exact) PathFilters()
    {
        var loose = Values("path-contains");
        var exact = Values("exact-path");
        if (loose.Count > 0 && exact.Count > 0)
            throw new CliUsageException(
                // 末句不解释「为什么不许混」—— 那是替限制找理由,删掉读者不少一个动作。
                // 「重复同一个旗会放宽」也不在这里说:触发这条报错的调用正在收窄
                // (--path-contains comps --exact-path comps[0].compClass),那句是反向指引。
                // 两个旗各带自己的占位符,因为一个收片段、一个收整条路径,而混用的人多半
                // 正把片段原样换旗重敲。
                // 不写 "as a whole":那个词在这一轮之前同时指整段名字、整条路径、整个值,
                // 而这句话正处在读者拿片段换旗重敲的当口。
                "--path-contains and --exact-path cannot be combined: one matches a fragment of the path, " +
                "the other matches it end to end. Pass --path-contains <text> for a fragment, or " +
                "--exact-path <path> for a complete field path.");
        return exact.Count > 0 ? (exact, true) : (loose, false);
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
public readonly record struct LimitValue(int? Count)
{
    public static LimitValue All => new(null);
    public static LimitValue Of(int n) => new(n);
    public bool IsAll => Count is null;
    /// <summary>拿去做 SQL LIMIT 用的数;全部时返回 int.MaxValue。</summary>
    public int Effective => Count ?? int.MaxValue;
}

/// <summary>用法错误。CLI 无 schema 兜底,错误消息是一等公民。</summary>
public sealed class CliUsageException(string message) : Exception(message);
