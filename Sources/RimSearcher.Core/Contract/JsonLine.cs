// 中间格式的写侧原语 —— 与 IntermediateFormat 同属共享契约文件,net472 可编译。
//
// 为什么手写而不用序列化库:游戏侧(net472,进程里跑在 RimWorld 内)不引入任何运行时依赖是
// B 案的核心收益(02-8 整条消失)。转义规则写在这里一份,读侧用 System.Text.Json,
// 两边对 JSON 标准的理解不会漂。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimSearcher.Contract
{
    /// <summary>拼一行 JSON 对象。用法:new JsonLine().Str(k,v).Int(k,v)...ToString()</summary>
    public sealed class JsonLine
    {
        private readonly StringBuilder _sb = new StringBuilder(256);
        private bool _any;

        public JsonLine() { _sb.Append('{'); }

        private void Key(string key)
        {
            if (_any) _sb.Append(',');
            _any = true;
            AppendQuoted(_sb, key);
            _sb.Append(':');
        }

        public JsonLine Str(string key, string value)
        {
            Key(key);
            if (value == null) _sb.Append("null");
            else AppendQuoted(_sb, value);
            return this;
        }

        public JsonLine Int(string key, long value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLine Bool(string key, bool value)
        {
            Key(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>原样嵌入一段已经是合法 JSON 的文本(数组/对象)。</summary>
        public JsonLine Raw(string key, string json)
        {
            Key(key);
            _sb.Append(json ?? "null");
            return this;
        }

        /// <summary>
        /// 字段表:<c>[["path","value",默认态],…]</c>。数组比对象省字节,且允许同路径重复。
        ///
        /// 默认态跟着自己那一行走,而不是另开一个「哪些路径是默认值」的并行数组 ——
        /// 并行数组一旦错位,产出的行与没错位的逐字同形,而这正是本轮反复在拆的那个形状。
        /// </summary>
        public JsonLine Fields(string key, IEnumerable<ExportedField> fields)
        {
            Key(key);
            _sb.Append('[');
            var first = true;
            foreach (var f in fields)
            {
                if (!first) _sb.Append(',');
                first = false;
                _sb.Append('[');
                AppendQuoted(_sb, f.Path);
                _sb.Append(',');
                AppendQuoted(_sb, f.Value ?? string.Empty);
                _sb.Append(',');
                _sb.Append(f.Default.ToString(CultureInfo.InvariantCulture));
                _sb.Append(']');
            }
            _sb.Append(']');
            return this;
        }

        public override string ToString() => _sb.ToString() + "}";

        /// <summary>JSON 字符串转义。控制字符一律 \\uXXXX,保证输出是 7-bit 安全的单行。</summary>
        public static void AppendQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            AppendQuoted(sb, s ?? string.Empty);
            return sb.ToString();
        }
    }
}
