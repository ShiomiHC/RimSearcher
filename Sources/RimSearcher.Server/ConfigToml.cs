using Tomlyn.Model;

namespace RimSearcher.Server;

// config 的每个读取点都要「宽松地认 key」和「字符串或数组都收」，这套规则曾经被抄成三份
// 且并不一致：SourcePathEntry 那份只去下划线不去连字符，于是 assembly-paths 在 sources 里
// 认得、在 csharpSourcePaths 里就被静默忽略。收拢到这里，规则只有一处。
internal static class ConfigToml
{
    // 手写 config 里 camelCase / snake_case / kebab-case / PascalCase 都会出现，全部折成同一个键。
    // TOML 的 key 本身大小写敏感，宽松性全靠这一步。
    public static string NormalizeKey(string key)
        => key.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    // 别名两侧都归一化，故调用点可以照可读的写法传（"assemblyPaths"），不必自己折成小写。
    // config 一个进程只解析一次，这点线性扫描的开销无关紧要。
    public static object? Find(TomlTable table, params string[] aliases)
    {
        foreach (var pair in table)
        {
            var key = NormalizeKey(pair.Key);
            foreach (var alias in aliases)
            {
                if (key == NormalizeKey(alias)) return pair.Value;
            }
        }

        return null;
    }

    // 单值写裸字符串是常见手写形态，不该逼用户为一个值套数组。
    // 数字、表、null 一律忽略：格式错误止步于这一个字段，不蔓延到同一张表的其余 key。
    public static List<string> StringList(object? value)
    {
        var results = new List<string>();

        switch (value)
        {
            case string single when !string.IsNullOrWhiteSpace(single):
                results.Add(single.Trim());
                break;
            case TomlArray array:
                foreach (var item in array)
                {
                    if (item is string text && !string.IsNullOrWhiteSpace(text)) results.Add(text.Trim());
                }
                break;
        }

        return results;
    }

    public static string? String(object? value)
        => value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public static bool Bool(object? value, bool fallback)
        => value as bool? ?? fallback;

    // TOML 的整数一律是 long
    public static int Int(object? value, int fallback)
        => value is long number && number is >= int.MinValue and <= int.MaxValue ? (int)number : fallback;

    // [[sources]] 解析成 TomlTableArray，而 sources = [{ ... }] 这种内联写法解析成装着
    // TomlTable 的 TomlArray。两种都是合法 TOML，也都会被手写出来，故两种都要认。
    public static IEnumerable<object?> Items(object? value)
        => value switch
        {
            TomlTableArray tables => tables,
            TomlArray array => array,
            _ => []
        };
}
