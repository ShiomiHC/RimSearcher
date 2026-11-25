using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimSearcher.Contract
{
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

        public JsonLine Num(string key, float? value)
        {
            Key(key);
            if (value.HasValue) _sb.Append(value.Value.ToString("F2", CultureInfo.InvariantCulture));
            else _sb.Append("null");
            return this;
        }

        public JsonLine Strs(string key, IEnumerable<string> values)
        {
            Key(key);
            _sb.Append('[');
            var first = true;
            foreach (var v in values)
            {
                if (!first) _sb.Append(',');
                first = false;
                AppendQuoted(_sb, v ?? "");
            }
            _sb.Append(']');
            return this;
        }

        public JsonLine Bools(string key, IEnumerable<bool> values)
        {
            Key(key);
            _sb.Append('[');
            var first = true;
            foreach (var v in values)
            {
                if (!first) _sb.Append(',');
                first = false;
                _sb.Append(v ? "true" : "false");
            }
            _sb.Append(']');
            return this;
        }

        public JsonLine Raw(string key, string json)
        {
            Key(key);
            _sb.Append(json ?? "null");
            return this;
        }

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
