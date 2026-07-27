using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimSearcher.Server;

// 一个逻辑源的完整声明。旧格式把同一个源拆在 CsharpSourcePaths / XmlSourcePaths 两个列表里、
// 靠 name 相同来隐式关联；这里把它收拢成一行，并允许每类路径有多个——
// DLC 的 Core/Royalty/Ideology 各有 Defs 目录，mod 也常是 1.6/Defs + Common/Defs。
[JsonConverter(typeof(SourceDefinitionConverter))]
public sealed class SourceDefinition
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Csharp { get; init; } = [];
    public IReadOnlyList<string> Xml { get; init; } = [];
    public IReadOnlyList<string> Assemblies { get; init; } = [];

    // 反编译产物写到第一个 csharp 路径；其余视为附加只读源码目录（手工副本、官方 Source 等）
    public string? DecompileTarget => Csharp.Count > 0 ? Csharp[0] : null;

    public bool CanFollow => Assemblies.Count > 0 && DecompileTarget != null;
}

// 新旧配置格式合并后的结果，下游只认这个
public sealed record ResolvedSources(List<SourcePathEntry> Csharp, List<SourcePathEntry> Xml)
{
    public bool HasAny => Csharp.Count > 0 || Xml.Count > 0;

    public IEnumerable<string> AllPaths => Csharp.Concat(Xml).Select(entry => entry.Path);

    public IEnumerable<(string Name, string Path)> AllSources =>
        Csharp.Concat(Xml).Select(entry => (entry.Name, entry.Path));

    public List<SourcePathEntry> Followable => Csharp.Where(entry => entry.CanFollow).ToList();
}

public sealed class SourceDefinitionConverter : JsonConverter<SourceDefinition>
{
    public override SourceDefinition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? name = null;
        List<string> csharp = [], xml = [], assemblies = [];

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var property = reader.GetString() ?? string.Empty;
            if (!reader.Read()) break;

            switch (Normalize(property))
            {
                case "name":
                    name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case "csharp" or "cs" or "csharppath" or "csharppaths" or "source" or "sources":
                    csharp = ReadPaths(ref reader);
                    break;
                case "xml" or "xmlpath" or "xmlpaths" or "defs":
                    xml = ReadPaths(ref reader);
                    break;
                case "assemblies" or "assembly" or "assemblypath" or "assemblypaths" or "dll" or "dlls":
                    assemblies = ReadPaths(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (csharp.Count == 0 && xml.Count == 0 && assemblies.Count == 0) return null;

        return new SourceDefinition
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? SourcePathEntryConverter.InferNameFrom(csharp.FirstOrDefault() ?? xml.FirstOrDefault() ?? assemblies[0])
                : name!.Trim(),
            Csharp = csharp,
            Xml = xml,
            Assemblies = assemblies
        };
    }

    // 单路径写成裸字符串是常见手写形态，不该逼用户为一个值套数组
    private static List<string> ReadPaths(ref Utf8JsonReader reader)
    {
        var results = new List<string>();

        if (reader.TokenType == JsonTokenType.String)
        {
            var single = reader.GetString();
            if (!string.IsNullOrWhiteSpace(single)) results.Add(single.Trim());
            return results;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;
                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) results.Add(value.Trim());
                }
                else reader.Skip();
            }
            return results;
        }

        reader.Skip();
        return results;
    }

    private static string Normalize(string key)
        => key.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    public override void Write(Utf8JsonWriter writer, SourceDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        WriteArray(writer, "csharp", value.Csharp);
        WriteArray(writer, "xml", value.Xml);
        WriteArray(writer, "assemblies", value.Assemblies);
        writer.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
