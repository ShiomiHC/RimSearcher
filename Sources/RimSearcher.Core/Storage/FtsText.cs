using System.Text;

namespace RimSearcher.Storage;

/// <summary>
/// FTS 文本处理。
///
/// CJK bigram 展开:unicode61 分词器把一整串汉字当一个 token,「热量」搜不到「营养热量上限」。
/// 把 CJK 连续段展开成相邻二元组,中文检索才有召回。
///
/// 前缀问题:unicode61 下 <c>Apparel_ShieldBelt</c> 搜 <c>shield</c> 不中。对策是查询侧
/// 自动补前缀通配加下划线切分,调用方不需要知道 <c>*</c> 的存在。
/// </summary>
public static class FtsText
{
    public static bool IsCjk(char c) =>
        (c >= '㐀' && c <= '䶿') ||   // 扩展 A
        (c >= '一' && c <= '鿿') ||   // 基本区
        (c >= '豈' && c <= '﫿') ||   // 兼容表意
        (c >= '぀' && c <= 'ヿ');     // 假名

    /// <summary>把文本里的 CJK 连续段展开成 bigram,拼在原文之后一并入索引。</summary>
    public static string ExpandCjkBigrams(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var extra = new StringBuilder();
        for (var i = 0; i + 1 < text.Length; i++)
            if (IsCjk(text[i]) && IsCjk(text[i + 1]))
                extra.Append(' ').Append(text[i]).Append(text[i + 1]);
        return extra.Length == 0 ? text : text + extra.ToString();
    }

    /// <summary>下划线/驼峰切分,让 <c>Apparel_ShieldBelt</c> 的 <c>shield</c> 段独立成 token。</summary>
    public static string SplitIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length * 2);
        sb.Append(name);
        var part = new StringBuilder();
        void Flush()
        {
            if (part.Length > 1) sb.Append(' ').Append(part);
            part.Clear();
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-' or '.' or ' ') { Flush(); continue; }
            if (char.IsUpper(c) && part.Length > 0 && !char.IsUpper(name[i - 1])) Flush();
            part.Append(c);
        }
        Flush();
        return sb.ToString();
    }

    /// <summary>入索引前的规范化:标识符切分 + CJK bigram。</summary>
    public static string ForIndex(string? text, bool identifier = false)
    {
        var s = identifier ? SplitIdentifier(text) : text ?? string.Empty;
        return ExpandCjkBigrams(s);
    }

    private static readonly char[] FtsSpecials = ['"', '*', ':', '(', ')', '^', '-', '+', ',', '\''];

    /// <summary>
    /// 把用户输入变成 MATCH 表达式。每个词都自动补 <c>*</c> 前缀通配,
    /// CJK 段展开成 bigram,与索引侧同一口径。
    /// </summary>
    public static string BuildMatchQuery(string userQuery, bool prefix = true)
    {
        var terms = Tokenize(userQuery);
        if (terms.Count == 0) return "\"\"";
        var parts = new List<string>(terms.Count);
        foreach (var t in terms)
        {
            var clean = new string(t.Where(c => !FtsSpecials.Contains(c)).ToArray());
            if (clean.Length == 0) continue;
            if (clean.Length >= 2 && clean.All(IsCjk))
            {
                // CJK 词:拆成 bigram 序列,与索引里的展开对上
                for (var i = 0; i + 1 < clean.Length; i++)
                    parts.Add($"\"{clean[i]}{clean[i + 1]}\"");
                if (clean.Length == 1) parts.Add($"\"{clean}\"");
            }
            else parts.Add(prefix ? $"\"{clean}\"*" : $"\"{clean}\"");
        }
        return parts.Count == 0 ? "\"\"" : string.Join(" ", parts);
    }

    /// <summary>
    /// 这个查询词**真正参与匹配**的是哪几段。说破用的 —— 匹配本身仍走
    /// <see cref="BuildMatchQuery"/>,这里是它那两道剥离的可读形态。
    ///
    /// 两道叠在一起,而两道都不留痕:先是 <see cref="FtsSpecials"/>(FTS5 的语法字符,
    /// 留着会让 MATCH 解析失败),它把 <c>Command*Settle</c> **拼**成 <c>CommandSettle</c>;
    /// 再是分词器把其余非字母数字当分隔符,于是 <c>CE_</c> 实际按 <c>CE</c> 匹配。
    /// 第一道让 <c>*</c> 看着像通配符,第二道让前缀计数悄悄变宽。
    /// </summary>
    public static IReadOnlyList<string> EffectiveTerms(string userQuery)
    {
        var result = new List<string>();
        foreach (var raw in Tokenize(userQuery))
        {
            var clean = new string(raw.Where(c => !FtsSpecials.Contains(c)).ToArray());
            var sb = new StringBuilder();
            foreach (var c in clean)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) result.Add(sb.ToString());
        }
        return result;
    }

    private static List<string> Tokenize(string q)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in q)
        {
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
