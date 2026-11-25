using System.Text.RegularExpressions;
using RimTooling.Cli;

namespace RimSearcher.Cli;

public sealed record TypeLeadCandidate(string Lead, CliOption? MovedTo, string? Note, string? Conflict);

internal static class TypeLead
{
    private static readonly Regex DefTypeShape = new(@"^[A-Za-z_][A-Za-z0-9_]*Def$", RegexOptions.Compiled);

    public sealed record Outcome(
        TokenizedCommand Tokenized,
        TypeLeadCandidate? Deferred,
        (string Base, string Hint)? Unexpected);

    public static Outcome Apply(TokenizedCommand tokenized, string exeName)
    {
        var op = tokenized.Operation;
        var declared = op.Positionals;
        var positionals = tokenized.Positionals.ToList();
        var values = new Dictionary<CliOption, object>(tokenized.Values);
        var notes = tokenized.Notes.ToList();
        var variadic = declared.Count > 0 && declared[^1].Variadic;
        var typeOpt = op.Options.FirstOrDefault(o => string.Equals(o.Name, "type", StringComparison.Ordinal));

        var spelled = declared
            .Select(d => d.Option is { } o && values.TryGetValue(o, out var raw) && CommandArgs.AsList(raw) is { Count: > 0 } l
                ? l
                : null)
            .ToArray();
        var spelledCount = spelled.Count(s => s is not null);
        var open = declared.Count - spelledCount;
        var typeLead = declared.Count > 0 && positionals.Count >= 1 &&
                       (spelledCount > 0
                           ? positionals.Count <= open + 1
                           : positionals.Count >= declared.Count + 1 && (variadic || positionals.Count == declared.Count + 1)) &&
                       !string.Equals(declared[0].Name, "defType", StringComparison.Ordinal) &&
                       DefTypeShape.IsMatch(positionals[0]);
        var typeExplicit = typeOpt is not null && values.ContainsKey(typeOpt);
        TypeLeadCandidate? deferred = null;
        if (typeLead && !typeExplicit && typeOpt is not null)
        {
            var lead = positionals[0];
            positionals.RemoveAt(0);
            values[typeOpt] = lead;
            var spelledOut = declared
                .Select((d, i) => (d, i))
                .Where(t => spelled[t.i] is not null)
                .Select(t => $"--{t.d.Option!.Name} {string.Join($" --{t.d.Option.Name} ", spelled[t.i]!)}");
            var written = string.Join(" ", positionals.Concat(spelledOut).Concat(OtherOptions(op, values, typeOpt)));
            var note = $"'{lead}' 按 --type {lead} 读，不作 <{declared[0].Name}>。完整写出时，本次调用为 " +
                       $"'{exeName} {op.Name} " + written + $" --type {lead}'。";
            if (variadic && spelledCount == 0)
                deferred = new TypeLeadCandidate(lead, typeOpt, note, null);
            else
                notes.Add(note);
        }

        if (variadic && typeLead && typeExplicit && spelledCount == 0 &&
            CommandArgs.AsList(values[typeOpt!]) is { Count: > 0 } typed)
        {
            var lead = positionals[0];
            var rest = string.Join(" ", positionals.Skip(1).Concat(OtherOptions(op, values, typeOpt)));
            deferred = new TypeLeadCandidate(lead, null, null,
                $"def 类型重复指定：参数 '{lead}' 与选项 --type {typed[^1]}。保留其一；保留 --type {typed[^1]} 时整条查询为 " +
                $"'{exeName} {op.Name} {rest} --type {typed[^1]}'。");
        }

        return new Outcome(
            tokenized.With(positionals: positionals, values: values, notes: notes),
            deferred,
            spelledCount == 0 ? UnexpectedHint(tokenized, op, positionals, values, typeOpt, exeName) : null);
    }

    private static IEnumerable<string> OtherOptions(CliOperation op, Dictionary<CliOption, object> values, CliOption? typeOpt)
    {
        var slots = op.Positionals.Select(d => d.Option).OfType<CliOption>().ToHashSet();
        return op.Options.Append(Commands.GlobalOptions.Snapshot)
            .Where(o => !ReferenceEquals(o, typeOpt) && !slots.Contains(o) &&
                        values.TryGetValue(o, out var got) && CommandArgs.AsList(got).Count > 0)
            .Select(o => o.Kind == OptionKind.Flag
                ? o.FlagName
                : string.Join(" ", CommandArgs.AsList(values[o]).Select(v => $"--{o.Name} {ArgText.Pasteable(v)}")));
    }

    private static (string Base, string Hint)? UnexpectedHint(
        TokenizedCommand tokenized, CliOperation op, List<string> positionals,
        Dictionary<CliOption, object> values, CliOption? typeOpt, string exeName)
    {
        var declared = op.Positionals;
        var max = op.EffectiveMax;
        if (positionals.Count <= max) return null;
        var extra = positionals.Skip(max).ToList();
        if (tokenized.SawDoubleDash && extra.Any(p => p.StartsWith('-'))) return null;
        if (declared.Count == 0 || string.Equals(declared[0].Name, "defType", StringComparison.Ordinal) ||
            !DefTypeShape.IsMatch(positionals[0]))
            return null;

        var lead = positionals[0];
        var rest = string.Join(" ", positionals.Skip(1).Concat(OtherOptions(op, values, typeOpt)));
        var given = typeOpt is not null && values.TryGetValue(typeOpt, out var raw) &&
                    CommandArgs.AsList(raw) is { Count: > 0 } typeValues
            ? typeValues[^1]
            : null;
        var hint = given is not null
            ? $"def 类型重复指定：参数 '{lead}' 与选项 --type {given}。保留其一；保留 --type {given} 时整条查询为 " +
              $"'{exeName} {op.Name} {rest} --type {given}'。"
            : $"'{lead}' 形似 def 类型，而 '{op.Name}' 没有可接收 def 类型的筛选：整条查询为 " +
              $"'{exeName} {op.Name} {rest}'。";
        var baseMessage = tokenized.Registry.Messages.UnexpectedPositionals(op.Name, extra, declared, max);
        return (baseMessage, hint);
    }
}
