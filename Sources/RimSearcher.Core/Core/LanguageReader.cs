using System.Formats.Tar;
using System.Xml;
using System.Xml.Linq;

namespace RimSearcher.Core;

// DefInjected 里的一条可用译文。Key 是 "<DefType>/<defName>"——目录名给出类型，键的首段给出
// defName，两者缺一都对不上索引里的 def。
public readonly record struct LanguageEntry(string DefType, string DefName, string? Label, string? Description);

// 一个语言包在磁盘上的形态。本体的官方语言自 1.6 起打成未压缩 tar（Data\<DLC>\Languages\
// <语言> (原生名).tar），mod 自带的翻译则是明文目录。两种都要读，且 tar 内部没有语言层——
// 条目直接从 "DefInjected/" 打头。
public sealed record LanguagePack(string Path, bool IsArchive)
{
    public static LanguagePack ForDirectory(string path) => new(path, IsArchive: false);
    public static LanguagePack ForArchive(string path) => new(path, IsArchive: true);
}

// 把语言包读成一串 LanguageEntry。只认「顶层 def 字段」，即恰好两段、末段为 label /
// description 的键。
//
// 嵌套键（AlcoholHigh.stages.drunk.label、Beer.tools.bottle.label）一律丢弃：它们译的是 def
// 内部某个子对象，挂到 def 头上就是张冠李戴。实测本体中文包 4284 个 .label 里有 1143 个是这类。
public static class LanguageReader
{
    private const string DefInjectedDirName = "DefInjected";

    private static readonly XmlReaderSettings XmlReadSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreWhitespace = true
    };

    // 语言目录名形如 "ChineseSimplified (简体中文)"，tar 名形如 "ChineseSimplified (简体中文).tar"，
    // 而用户在 config 里通常只写 "ChineseSimplified"。故按「首个空格前的段」比对，同时允许
    // 用户把完整名字连括号一起抄进来。
    public static bool NameMatches(string candidate, string requested)
    {
        var normalizedCandidate = StripArchiveSuffix(candidate);

        if (string.Equals(normalizedCandidate, requested, StringComparison.OrdinalIgnoreCase)) return true;

        var head = normalizedCandidate.Split(' ', 2)[0];
        return string.Equals(head, StripArchiveSuffix(requested).Split(' ', 2)[0], StringComparison.OrdinalIgnoreCase);
    }

    private static string StripArchiveSuffix(string name)
        => name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // 一个 Languages 目录下，指定语言的那一份（目录或 tar）。都没有就返回 null——
    // 大多数 mod 只带英文，这是常态而非错误。
    public static LanguagePack? Find(string languagesDir, string language)
    {
        if (string.IsNullOrWhiteSpace(languagesDir) || !Directory.Exists(languagesDir)) return null;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(languagesDir))
            {
                if (NameMatches(Path.GetFileName(directory), language))
                    return LanguagePack.ForDirectory(directory);
            }

            foreach (var file in Directory.EnumerateFiles(languagesDir, "*.tar"))
            {
                if (NameMatches(Path.GetFileName(file), language))
                    return LanguagePack.ForArchive(file);
            }
        }
        catch
        {
            // 权限/占用问题当作「这里没有语言包」，别让一个目录拖垮整轮扫描
        }

        return null;
    }

    // 读出一个语言包里的全部可用译文。读不动的单个文件跳过，不影响其余——
    // 翻译文件里出现半个未转义的 & 是常事，为它丢掉整包翻译不值得。
    public static IEnumerable<LanguageEntry> Read(LanguagePack pack)
        => pack.IsArchive ? ReadArchive(pack.Path) : ReadDirectory(pack.Path);

    private static IEnumerable<LanguageEntry> ReadDirectory(string packDir)
    {
        var defInjected = Path.Combine(packDir, DefInjectedDirName);
        if (!Directory.Exists(defInjected)) yield break;

        string[] typeDirs;
        try
        {
            typeDirs = Directory.GetDirectories(defInjected);
        }
        catch
        {
            yield break;
        }

        foreach (var typeDir in typeDirs)
        {
            // 目录名即 def 的运行时类型名，也就是索引里的 DefType
            var defType = Path.GetFileName(typeDir);
            if (string.IsNullOrEmpty(defType)) continue;

            string[] files;
            try
            {
                // 类型目录下允许再分子目录（ThingDef/Weapons/Guns.xml），故递归
                files = Directory.GetFiles(typeDir, "*.xml", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                XDocument? document;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = XmlReader.Create(stream, XmlReadSettings);
                    document = XDocument.Load(reader);
                }
                catch
                {
                    continue;
                }

                foreach (var entry in ParseDocument(document, defType)) yield return entry;
            }
        }
    }

    // tar 是顺序流：只能从头读到尾，不能按条目寻址，故整包一遍过。条目路径形如
    // "DefInjected/<DefType>/<file>.xml"（前面没有语言目录那一层）。
    private static IEnumerable<LanguageEntry> ReadArchive(string archivePath)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            yield break;
        }

        using (stream)
        {
            TarReader reader;
            try
            {
                reader = new TarReader(stream);
            }
            catch
            {
                yield break;
            }

            using (reader)
            {
                while (true)
                {
                    TarEntry? tarEntry;
                    try
                    {
                        tarEntry = reader.GetNextEntry();
                    }
                    catch
                    {
                        // 包本身坏了，后面的条目也读不出来
                        yield break;
                    }

                    if (tarEntry == null) break;
                    if (tarEntry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;

                    var defType = DefTypeFromArchivePath(tarEntry.Name);
                    if (defType == null) continue;

                    XDocument? document;
                    try
                    {
                        var dataStream = tarEntry.DataStream;
                        if (dataStream == null) continue;

                        using var xmlReader = XmlReader.Create(dataStream, XmlReadSettings);
                        document = XDocument.Load(xmlReader);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var entry in ParseDocument(document, defType)) yield return entry;
                }
            }
        }
    }

    // "DefInjected/ThingDef/Drugs.xml" → "ThingDef"。Keyed / Strings 与非 xml 一律返回 null。
    // 类型目录下的子目录同样收（取紧跟 DefInjected 的那一段）。
    private static string? DefTypeFromArchivePath(string entryName)
    {
        if (!entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) return null;
        if (!segments[0].Equals(DefInjectedDirName, StringComparison.OrdinalIgnoreCase)) return null;

        return segments[1];
    }

    // <LanguageData> 下每个元素名是一个键路径。收 "<defName>.label" 与 "<defName>.description"，
    // 同一个 defName 的两者合成一条。
    private static IEnumerable<LanguageEntry> ParseDocument(XDocument document, string defType)
    {
        var root = document.Root;
        if (root == null) yield break;

        // 同一份文件里 label 与 description 分行出现，先并到一起再吐出去，
        // 免得下游为「同一个 def 的两条记录」再做一次合并
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in root.Elements())
        {
            var key = element.Name.LocalName;

            // 恰好两段。defName 本身不含点（RimWorld 的 defName 只允许字母数字下划线），
            // 故首段即 defName，多出来的段说明这是嵌套字段的译文。
            var separator = key.IndexOf('.');
            if (separator <= 0 || separator == key.Length - 1) continue;

            var defName = key[..separator];
            var field = key[(separator + 1)..];
            if (field.IndexOf('.') >= 0) continue;

            var value = element.Value.Trim();
            if (value.Length == 0) continue;

            if (field.Equals("label", StringComparison.Ordinal)) labels[defName] = value;
            else if (field.Equals("description", StringComparison.Ordinal)) descriptions[defName] = value;
        }

        foreach (var (defName, label) in labels)
        {
            yield return new LanguageEntry(defType, defName, label, descriptions.GetValueOrDefault(defName));
        }

        // 只译了 description 没译 label 的（少见但存在）也要收，否则 inspect 会缺一块
        foreach (var (defName, description) in descriptions)
        {
            if (!labels.ContainsKey(defName))
                yield return new LanguageEntry(defType, defName, null, description);
        }
    }
}
