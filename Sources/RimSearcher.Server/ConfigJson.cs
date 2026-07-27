using System.Text.Json;

namespace RimSearcher.Server;

// config 的两个转换器与工具参数解析各自抄过一份「宽松地认 key」和「字符串或数组都收」，
// 三份规则曾经不一致：SourcePathEntry 那份只去下划线不去连字符，于是 assembly-paths
// 在 sources 里认得、在 csharpSourcePaths 里就被静默忽略。收拢到这里，规则只有一处。
internal static class ConfigJson
{
    // 手写 config 里 camelCase / snake_case / kebab-case 都会出现，全部折成同一个键
    public static string NormalizeKey(string key)
        => key.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    // 单值写成裸字符串是常见手写形态，不该逼用户为一个值套数组。
    // 调用时 reader 须已停在值上（PropertyName 之后的那次 Read 已经做过）。
    public static void ReadStringOrArray(ref Utf8JsonReader reader, List<string> target)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var single = reader.GetString();
            if (!string.IsNullOrWhiteSpace(single)) target.Add(single.Trim());
            return;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;
                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) target.Add(value.Trim());
                }
                else reader.Skip();
            }
            return;
        }

        // 数字、对象、null 等：跳过整棵子树，不让格式错误蔓延到后续 key 的读取
        reader.Skip();
    }

    public static List<string> ReadStringOrArray(ref Utf8JsonReader reader)
    {
        var results = new List<string>();
        ReadStringOrArray(ref reader, results);
        return results;
    }
}
