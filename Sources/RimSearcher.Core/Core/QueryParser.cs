namespace RimSearcher.Core;


public class ParsedQuery
{
    public List<string> Keywords { get; set; } = new();
    public string? TypeFilter { get; set; }
    public string? MethodFilter { get; set; }
    public string? FieldFilter { get; set; }
    public string? DefFilter { get; set; }

    // 'scope:milira' 写在 query 里时的落点；工具层优先用它，其次才用 scope 参数
    public string? ScopeFilter { get; set; }

    // 认不出的过滤前缀（'member:'、'bogus:'）。整个 token 仍按原样进 Keywords——那是有意的、
    // 也有用例钉着——但调用方必须被告知，否则 'member:CompTick' 会回一句 "No results"，
    // 而 'method:CompTick' 有 144 条：一个确实存在的符号被报成不存在。
    public List<string> UnknownPrefixes { get; } = new();

    // 写了前缀却没给值（'type:'，或分词后落单的 'type: X' 里那半个）。恒零命中，
    // 且此前会连带把该段的搜索词覆盖成空串，使整段静默消失。
    public bool HadEmptyFilterValue { get; set; }
}

public static class QueryParser
{
    public static ParsedQuery Parse(string rawQuery)
    {
        var result = new ParsedQuery();

        if (string.IsNullOrWhiteSpace(rawQuery))
            return result;
        
        var tokens = SplitQuery(rawQuery);

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (token.Contains(':'))
            {
                var parts = token.Split(':', 2);
                if (parts.Length == 2)
                {
                    var prefix = parts[0].ToLowerInvariant();
                    var value = parts[1];

                    // 'type: CompShield'（冒号后带空格）分词后是 ["type:", "CompShield"]，
                    // 而这是人写查询时极常见的写法。此前空值照样被当成「用户指定了过滤词」，
                    // 于是该段的搜索词被覆盖成空串、整段消失——C# Types 段直接不见，
                    // 读起来就是「这个类型不存在」。把值绑到下一个 token 即可与无空格写法等价。
                    if (value.Length == 0 && IsKnownPrefix(prefix))
                    {
                        if (i + 1 < tokens.Count && !string.IsNullOrWhiteSpace(tokens[i + 1])
                            && !tokens[i + 1].Contains(':'))
                        {
                            value = tokens[i + 1];
                            i++;
                        }
                        else
                        {
                            // 光杆前缀：不设过滤器（设了等于用空串去搜，恒零命中）
                            result.HadEmptyFilterValue = true;
                            continue;
                        }
                    }

                    // 这里认 16 个拼法而 locate 的 Description 只举 5 个（己-4）。**有意，不要
                    // 「修」它**：缩写与近义词（m: / c: / p: / in:）救的是没照说明写的调用方，
                    // 而照说明写的那些用的就是那 5 个规范前缀，一点不吃亏；把 16 个列进说明只是
                    // 把一段本来读得完的话变成一张表。判据与理由见 ToolArgs.cs 顶部那条政策。
                    //
                    // 与 ToolArgs.LocateFilterPrefixes 不必相等：那份名单干的是另一件事
                    // （把调用方带到只认裸名的工具上的前缀剥掉），故它宽到含 locate 自己不认的
                    // `member:` 是对的——意图明显，剥了正好。
                    switch (prefix)
                    {
                        case "method" or "m":
                            result.MethodFilter = value;
                            break;
                        case "type" or "t" or "class" or "c":
                            result.TypeFilter = value;
                            break;
                        case "field" or "f" or "property" or "p":
                            result.FieldFilter = value;
                            break;
                        case "def" or "d":
                            result.DefFilter = value;
                            break;
                        case "scope" or "s" or "source" or "in":
                            result.ScopeFilter = value;
                            break;
                        default:
                            // 整个 token 连前缀一起当关键词是既定行为（QueryParserTests 钉着），
                            // 这里只是把「我没认出这个前缀」这件事记下来交给工具层说出去
                            result.UnknownPrefixes.Add(parts[0]);
                            result.Keywords.Add(token);
                            break;
                    }
                    continue;
                }
            }

            result.Keywords.Add(token);
        }

        return result;
    }

    private static bool IsKnownPrefix(string prefix) => prefix switch
    {
        "method" or "m" or "type" or "t" or "class" or "c"
            or "field" or "f" or "property" or "p"
            or "def" or "d" or "scope" or "s" or "source" or "in" => true,
        _ => false
    };
    
    private static List<string> SplitQuery(string query)
    {
        var tokens = new List<string>();
        var currentToken = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString());
                    currentToken.Clear();
                }
            }
            else
            {
                currentToken.Append(c);
            }
        }

        if (currentToken.Length > 0)
        {
            tokens.Add(currentToken.ToString());
        }

        return tokens;
    }
    
    public static string GetCombinedSearchTerm(ParsedQuery query)
    {
        var terms = new List<string>();

        if (!string.IsNullOrEmpty(query.TypeFilter))
            terms.Add(query.TypeFilter);
        if (!string.IsNullOrEmpty(query.MethodFilter))
            terms.Add(query.MethodFilter);
        if (!string.IsNullOrEmpty(query.FieldFilter))
            terms.Add(query.FieldFilter);
        if (!string.IsNullOrEmpty(query.DefFilter))
            terms.Add(query.DefFilter);

        terms.AddRange(query.Keywords);

        return string.Join(" ", terms);
    }
}
