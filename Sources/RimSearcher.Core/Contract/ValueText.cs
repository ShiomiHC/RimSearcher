using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimSearcher.Contract
{
    public static class ValueText
    {
        public static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        public static string Range(float min, float max) => Float(min) + "~" + Float(max);

        public static string Tuple(IReadOnlyList<float> parts)
        {
            var sb = new StringBuilder("(");
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Float(parts[i]));
            }
            return sb.Append(')').ToString();
        }

        public static string Bool(bool value) => value ? "true" : "false";

        public static string Canonical(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var s = text.Trim();
            if (s.Equals("true", System.StringComparison.OrdinalIgnoreCase)) return "true";
            if (s.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return "false";

            if (s.Length >= 2 && s[0] == '(' && s[s.Length - 1] == ')')
            {
                var parts = s.Substring(1, s.Length - 2).Split(',');
                var sb = new StringBuilder("(");
                for (var i = 0; i < parts.Length; i++)
                {
                    string n;
                    if (!TryNumber(parts[i], out n)) return text;
                    if (i > 0) sb.Append(", ");
                    sb.Append(n);
                }
                return sb.Append(')').ToString();
            }

            var tilde = s.IndexOf('~');
            if (tilde > 0 && tilde == s.LastIndexOf('~'))
            {
                string a, b;
                return TryNumber(s.Substring(0, tilde), out a) && TryNumber(s.Substring(tilde + 1), out b)
                    ? a + "~" + b : text;
            }

            string single;
            return s.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 && TryNumber(s, out single) ? single : text;
        }

        private static bool TryNumber(string token, out string text)
        {
            text = "";
            var t = token.Trim();
            if (t.Length == 0) return false;
            if (t.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
            {
                long l;
                if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return false;
                text = l.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            double d;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                || double.IsNaN(d) || double.IsInfinity(d)) return false;
            text = d.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        private static readonly string[] Directions =
            { "North", "East", "South", "West", "NorthEast", "SouthEast", "SouthWest", "NorthWest" };

        public static string Rotation(int index, int count)
            => index >= 0 && index < count && count <= Directions.Length
                ? Directions[index]
                : index.ToString(CultureInfo.InvariantCulture);
    }
}
