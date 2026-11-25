using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimSearcher.Output;

public static class JsonTree
{
    public static string Write(object? value, bool indented)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = indented,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
            Value(w, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Value(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case char c: w.WriteStringValue(c.ToString()); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case short i: w.WriteNumberValue(i); break;
            case byte i: w.WriteNumberValue(i); break;
            case sbyte i: w.WriteNumberValue(i); break;
            case ushort i: w.WriteNumberValue(i); break;
            case uint i: w.WriteNumberValue(i); break;
            case ulong i: w.WriteNumberValue(i); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            case decimal m: w.WriteNumberValue(m); break;
            case Enum e: w.WriteNumberValue(Convert.ToInt64(e, CultureInfo.InvariantCulture)); break;
            case IReadOnlyDictionary<string, object?> map:
                w.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    w.WritePropertyName(k);
                    Value(w, v);
                }
                w.WriteEndObject();
                break;
            case IDictionary map:
                w.WriteStartObject();
                foreach (DictionaryEntry kv in map)
                {
                    w.WritePropertyName(Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!);
                    Value(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case IEnumerable items:
                w.WriteStartArray();
                foreach (var item in items) Value(w, item);
                w.WriteEndArray();
                break;
            default:
                throw new NotSupportedException(
                    $"JSON 输出无法写出 {value.GetType().FullName} 类型的取值；" +
                    "须在交给渲染器之前转成字典、列表或标量。");
        }
    }
}
