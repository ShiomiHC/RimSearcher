namespace RimSearcher.Core;

// 计数名词的单一产地：全服每一个「N 个什么」里的那个「什么」都是这里的一个成员。
//
// 为什么要一个类型，而不是一张字面量清单：**名词的产地是调用点的实参，而其中两处是变量。**
// 直接传字面量的调用约二十处，但 LocateRenderer 传的是 `section.Noun`（运行时取五种之一）、
// RoslynHelper 传的是 `kindPlural`（运行时取三种之一）。清单式的登记对这两个槽完全无效——
// 它们的取值在别的文件里赋进去，清单看不见。换成类型之后赋值处自动落进名单，
// **没登记的名词编译期就进不来**。
//
// 这条边界此前只有一句注释在守（`CountedNouns` 那张表写着「新加一个计数名词却不登记在这里，
// 那个槽位的单复数就没人守」），而它真的漂了：`changed sources` 从落地那天起就没有对应的产品
// 字面量，产品那边一直叫 `checked sources`，那个词又从没进过表（见「单一产地重构指导」§2 甲）。
//
// 住在 Core 而不是 Server 的 `Tools/Output/`：两个变量槽里的 `RoslynHelper.kindPlural` 就在
// Core 里，而项目引用是 Server → Core 单向的——入口放进 `Output/` 时 Core 那个槽根本够不到，
// 等于一开始就漏掉一半。判据与 `OutputText` 同一条：**不含成因判断才下得去**。计数名词是纯
// 名单加构词，不读 `ScopeArgs.HardLimit` / `ResultLimit`，下得去。
public sealed class CountedNoun
{
    // 名单在这里一处。加词只有这一种写法，且加完立刻处处可用；删词会让引用它的那一行编译不过。
    private static readonly List<CountedNoun> Registry = [];

    private CountedNoun(string plural)
    {
        Plural = plural;
        Singular = Singularize(plural);
    }

    private static CountedNoun Register(string plural)
    {
        var noun = new CountedNoun(plural);
        Registry.Add(noun);
        return noun;
    }

    // 闸取的是这份名单（`GrammarRules.CountedNouns`），判断仍归闸自己写——同「指导」§3 判据六：
    // 闸与产品只许共用「名单」，不许共用「判断」。
    public static IReadOnlyList<CountedNoun> All => Registry;

    public string Plural { get; }

    // 单数式在构造时算一次。它不入名单：产品侧传的一律是名词本身，单数是构词的产物而不是
    // 第二个词条，两者并列会让「这张表与产品字面量同步」这条判据分不清哪些是漏删的死项。
    public string Singular { get; }

    // R5 已经为 locate 表头定过这条规矩（不写 `1 C# types`），其余槽位一直漏着——全语料里
    // `... +1 more C# types` 这类出现在 locate / inspect / trace 三个工具上。
    public string For(int n) => n == 1 ? Singular : Plural;

    public string Quantity(int n) => $"{n} {For(n)}";

    public override string ToString() => Plural;

    // ---- 名单 ----
    //
    // 分组只为可读，没有语义。每一条都要在产品里真的被用到——空着的条目由
    // CountedNounRegistryTests 判红，那正是 `changed sources` / `name keys` 当年逃掉的那一类。

    // locate 的五段
    public static readonly CountedNoun CSharpTypes = Register("C# types");
    public static readonly CountedNoun Members = Register("members");
    public static readonly CountedNoun XmlDefs = Register("XML defs");
    public static readonly CountedNoun ContentMatches = Register("content matches");
    public static readonly CountedNoun Files = Register("files");

    // 扫盘两工具
    public static readonly CountedNoun MatchingFiles = Register("matching files");
    public static readonly CountedNoun MatchingLines = Register("matching lines");
    public static readonly CountedNoun PreviewLines = Register("preview lines");

    // trace inheritors
    public static readonly CountedNoun Subclasses = Register("subclasses");
    public static readonly CountedNoun Levels = Register("levels");

    // inspect 的成员大纲与关联类型
    public static readonly CountedNoun Methods = Register("methods");
    public static readonly CountedNoun Properties = Register("properties");
    public static readonly CountedNoun Fields = Register("fields");
    public static readonly CountedNoun Types = Register("types");

    // read_code / list_directory
    public static readonly CountedNoun Lines = Register("lines");
    public static readonly CountedNoun Entries = Register("entries");

    // sync_sources
    public static readonly CountedNoun ChangedFiles = Register("changed files");
    public static readonly CountedNoun CheckedSources = Register("checked sources");
    public static readonly CountedNoun Versions = Register("versions");
    public static readonly CountedNoun CSharpPaths = Register("C# paths");
    public static readonly CountedNoun XmlPaths = Register("XML paths");

    // 跨工具
    public static readonly CountedNoun Matches = Register("matches");
    public static readonly CountedNoun Items = Register("items");
    public static readonly CountedNoun Parameters = Register("parameters");
    public static readonly CountedNoun ConditionalFolders = Register("conditional folders");
    public static readonly CountedNoun Minutes = Register("minutes");

    // 裸去 's' 在 entries / content matches / properties 上都会写错，故按英文构词回推。
    // 覆盖的是本服务实际用到的那批名词，不是通用英文形态学——名单是封闭的，故这件事做得完，
    // 且每一条的单数式都由 CountedNounRegistryTests 逐词钉住。
    private static string Singularize(string plural)
    {
        if (plural.EndsWith("ies", StringComparison.Ordinal)) return plural[..^3] + "y";
        if (plural.EndsWith("es", StringComparison.Ordinal))
        {
            // sses / ches / shes / xes / zes 的 "es" 整个是词尾，去掉两个字母；
            // types / lines 这类只是词干末尾恰好有 e，去一个。
            var stem = plural[..^2];
            if (stem.EndsWith("s", StringComparison.Ordinal) || stem.EndsWith("x", StringComparison.Ordinal)
                || stem.EndsWith("z", StringComparison.Ordinal) || stem.EndsWith("ch", StringComparison.Ordinal)
                || stem.EndsWith("sh", StringComparison.Ordinal))
                return stem;
        }
        return plural.EndsWith("s", StringComparison.Ordinal) ? plural[..^1] : plural;
    }
}
