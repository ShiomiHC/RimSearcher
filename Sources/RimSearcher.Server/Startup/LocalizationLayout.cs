using RimSearcher.Core;

namespace RimSearcher.Server;

// 配置里的语言目录 → 本轮真正要读的那些语言包。一个 Languages 目录下常有十几种语言，
// 这一步把它收敛成「选中语言的那一份」（目录或 tar），选不出就没有。
public static class LocalizationLayout
{
    public static IReadOnlyList<LocalizationSource> Resolve(ResolvedSources sources, string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || sources.Languages.Count == 0) return [];

        var resolved = new List<LocalizationSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sources.Languages)
        {
            var pack = LanguageReader.Find(entry.Path, language);
            if (pack == null) continue;

            // 同一个语言包被两条源指到（配置里把 mod 根和它的子目录都写了）时只读一遍：
            // 重复读不会改变结果，但会白花一份解析时间
            if (!seen.Add(pack.Path)) continue;

            resolved.Add(new LocalizationSource(pack, entry.SourceRank, entry.FolderRank));
        }

        return resolved;
    }
}
