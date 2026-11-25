using System.Globalization;
using System.Text.RegularExpressions;
using RimTooling.Cli;

namespace RimSearcher.Cli;

public static class ArgText
{
    public static string Normalize(string s) => CliParser.Normalize(s);

    internal static string Pasteable(string value) =>
        Regex.IsMatch(value, @"^[\w./-]+$")
            ? value
            : "\"" + value.Replace("\"", "\\\"") + "\"";
}

public sealed class CommandArgs(ParsedCommand parsed)
{
    public ParsedCommand Parsed { get; internal set; } = parsed;

    public CliOperation Operation => Parsed.Operation;

    public IReadOnlyList<string> Positionals => Parsed.Positionals;

    public IReadOnlyList<string> Notes => Parsed.Notes;

    public CliOption? Own(string name) =>
        Operation.Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

    private object? Raw(CliOption? option) =>
        option is not null && Parsed.Values.TryGetValue(option, out var raw) ? raw : null;

    internal static IReadOnlyList<string> AsList(object? raw) => raw switch
    {
        null => [],
        string s => [s],
        bool => ["true"],
        int n => [n.ToString(CultureInfo.InvariantCulture)],
        IReadOnlyList<string> l => l,
        _ => [],
    };

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;

    public string? Value(CliOption? option) => AsList(Raw(option)) is { Count: > 0 } l ? l[^1] : null;

    public IReadOnlyList<string> Values(CliOption? option) => AsList(Raw(option));

    public bool Flag(CliOption? option) => Raw(option) is not null;

    public bool Has(CliOption? option) => Raw(option) is not null;

    public string Spelled(CliOption option) =>
        Parsed.Values.ContainsKey(option) ? Parsed.Spelled(option) : option.FlagName;

    public string Narrowing()
    {
        var parts = new List<string>();
        foreach (var o in Operation.Options.Where(o => o.Narrows))
        {
            var given = Values(o);
            if (given.Count == 0) continue;
            if (o.Kind == OptionKind.Flag) { parts.Add(o.FlagName); continue; }
            parts.AddRange(given.Select(v => $"{o.FlagName} {v}"));
        }
        return string.Join(" ", parts);
    }

    public LimitValue Limit(CliOption option)
    {
        var raw = Value(option);
        if (raw is null) return LimitValue.All;
        if (int.TryParse(raw, out var n) && n > 0) return LimitValue.Of(n);
        throw new CliUsageException(
            $"{option.FlagName} 取值须为正整数（收到 '{raw}'）。");
    }

    public int Offset(CliOption option)
    {
        var raw = Value(option);
        if (raw is null) return 0;
        if (int.TryParse(raw, out var n) && n >= 0) return n;
        throw new CliUsageException(
            $"{option.FlagName} 取值须为不小于 0 的整数，即要跳过的行数（收到 '{raw}'）。");
    }

    public (IReadOnlyList<string> Paths, bool Exact) PathFilters(CliOption pathContains, CliOption exactPath)
    {
        var loose = Values(pathContains);
        var exact = Values(exactPath);
        if (loose.Count > 0 && exact.Count > 0)
            throw new CliUsageException(
                "--path-contains 与 --exact-path 不能同时传入：--path-contains 匹配路径中的片段，--exact-path 匹配整条路径。");
        return exact.Count > 0 ? (exact, true) : (loose, false);
    }

    public int Int(CliOption option, int fallback)
    {
        var raw = Value(option);
        if (raw is null) return fallback;
        if (int.TryParse(raw, out var n)) return n;
        throw new CliUsageException($"{option.FlagName} 取值须为整数（收到 '{raw}'）。");
    }
}

public readonly record struct LimitValue(int? Count)
{
    public static LimitValue All => new(null);
    public static LimitValue Of(int n) => new(n);
    public bool IsAll => Count is null;
    public int Effective => Count ?? int.MaxValue;
}

public sealed class CliUsageException(string message) : Exception(message);

public sealed class GameRunException(string message) : Exception(message);
