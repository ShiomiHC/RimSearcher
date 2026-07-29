using System.Text;

namespace RimSearcher.Storage;

/// <summary>
/// FTS 文本处理。
///
/// CJK bigram 展开(02-8:与去噪清单是同一个提交进来的,改 FTS 结构时别顺手丢了):
/// unicode61 分词器把一整串汉字当一个 token,「热量」搜不到「营养热量上限」。把 CJK 连续段
/// 展开成相邻二元组,中文检索才有召回。
///
/// 前缀问题(02-7):unicode61 下 <c>Apparel_ShieldBelt</c> 搜 <c>shield</c> 不中,上游 SKILL.md
/// 专门教用户手加 <c>*</c> —— 教用户绕自家缺陷是反模式。这里的对策是查询侧自动补前缀
/// 加下划线切分,调用方不需要知道 <c>*</c> 的存在。
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
    /// 把用户输入变成 MATCH 表达式。每个词都自动补 <c>*</c> 前缀通配 —— 调用方不该需要
    /// 知道这件事(02-7)。CJK 段展开成 bigram,与索引侧同一口径。
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
