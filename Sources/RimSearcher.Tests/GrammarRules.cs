using System.Text;
using System.Text.RegularExpressions;
using RimSearcher.Core;
using RimSearcher.Server.Tools;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Tests;

// 输出文法常驻闸的**与工具无关**那一层。
//
// 与隔壁 OutputReadabilityTests 那七十例的区别在判据的量词：那边钉的是「这个工具的这句话该长
// 什么样」，这里钉的是「**任何**工具的**任何**一段返回都不许违反这几条」。两者互补不替代——
// 那边钉语义，这边钉文法一致性。
//
// 值得单起一层的理由：台账里已落地的可读性缺陷有一半是同一个形状——某条共用文法在某个工具上
// 漏实现了（R30 单复数写成 `1 more C# types`、R47 折叠行只给增量不给总数、R19/R21 名词槽留空、
// F26 表头没随尾注改口 `at least`、F30 新记号只在 locate 一处实现）。这一形状**全是可判定的
// 形式性质**，此前却每一条都靠人在某一轮里肉眼读出来。四轮抓了八条，说明漏网的概率不低。
//
// 所以这里吃的是**文本**，不是某个工具的返回类型：新工具、新分支只要进了遍历器就自动受约束，
// 不必记得去补断言。
//
// 每条规则都标两件事，缺一不可：
//   - 管的是「说不说」还是「怎么说」。README 明写共享的是「什么时候印」而不是「怎么排版」
//     （locate 用 `- 名字`、trace 用括号），把排版差异判成违规就是给未来的合理改动挖坑。
//   - 对应哪一条历史缺陷。指不出历史缺陷的规则不写——那种规则要么常绿要么常红，两头都没用。
public readonly record struct GrammarViolation(string Rule, string Line, string Detail)
{
    public override string ToString()
        => $"[{Rule}] {Detail}" + (Line.Length == 0 ? "" : $"\n            原文: {Line.Trim()}");
}

public static class GrammarRules
{
    // 计数名词的名单在产品侧的 `CountedNoun`，本文件一处也不抄它。**它就是本闸的覆盖面**：
    // 新加一个计数名词却不登记，那个槽位就没人守。
    //
    // 此前这里另有一张手抄的字面量表，「与产品同步」只是表头一句注释，于是它从落地那天起就漂着：
    // `changed sources` 与 `name keys` 在产品侧一处对应的字面量都没有，而产品真正在数的
    // `checked sources` 从没进过表。名词改成类型之后赋值处就是登记处，抄错在编译期就不成立了；
    // M2 又把「名词槽合不合法」从查表改成了渲染产地（渲染得出来的行，名词必然在名单里），
    // 于是那张表连同它的两个查表函数一起没了用处，一并删掉。
    //
    // 名单的两处消费点：`NounsMentionedIn` 给产地渲染当候选实参，规则二乙拿 `CountedNoun.All`
    // 逐词验单复数。照 §3 判据六：**只取名单，不取判断**。
    //
    // 数词后面**合法地**跟着一个以 s 结尾的非名词的那几个词。规则二甲是纯结构判定（不查词表），
    // 少了这张表会把 `1 file was abandoned mid-scan` 里的 was 判成复数名词。
    // `ms` 是单位符号，单复数同形（1 ms / 5 ms），不是漏写的单数式。它此前不在表里，于是
    // sync_sources 的 `Source check (N ms, …)` 只在这次耗时恰好取整到 1 时才红——一道
    // 跟着机器快慢闪的闸比没有闸更糟：它红的时候没人相信是真的。
    private static readonly HashSet<string> NotNouns = new(StringComparer.Ordinal)
    {
        "is", "was", "has", "this", "its", "as", "less", "plus", "versus", "does", "goes",
        "needs", "gives", "says", "exists", "starts", "shares", "of", "more", "and", "or",
        "to", "in", "for", "at", "from", "on", "the", "ms",
    };

    // 结果行末尾方括号里**不是**来源标签的那几种。来源标签的判据是「行尾的 [x]」（见
    // SourceLabeling），而 def 行的 [Abstract]、trace 的 [depth N] 与 F34 的
    // [conditional: X] 在没有后续字段时也会落到行尾。
    //
    // conditional 那一个尤其要豁免：单源 scope 下来源标签整段不印，于是几条同一个条件目录
    // 的结果行会各挂一个逐字相同的 [conditional: 1.6/CE]——而它按设计就该逐行挂（哪一行
    // 受影响是逐行不同的事实），把它当成「该上提到段头的噪音」正好反了。
    private static readonly Regex NonSourceTag = new(
        @"^(Abstract|depth \d+|conditional: .+)$", RegexOptions.Compiled);

    private static readonly Regex OnePlus = new(
        @"\b1 (?<phrase>[A-Za-z#][A-Za-z#]*(?: [A-Za-z#][A-Za-z#]*){0,2})", RegexOptions.Compiled);

    private static readonly Regex RowLabel = new(
        @"^[ \t]*-\s.*\s\[(?<tag>[^\]]+)\]$", RegexOptions.Compiled);

    private static readonly Regex LazyPlural = new(@"\d+ [A-Za-z]+\(e?s\)", RegexOptions.Compiled);

    // 规则九乙要认的两句提示语。它们的产地是 Fold.Line 里的一个局部变量，从外面够不着，
    // 故连着折叠行整句渲染出来，再按 OutputText.FoldLine 的框架把外壳减掉——剩下的就是提示语。
    //
    // 此前这两句在闸这边是一条 `server cap \d+ reached` 的正则加一个 `pass limit:'all'` 的字面量，
    // 与规则一那两条正则同一个毛病：产品改一个词，这条判据静默失效，而它失效的样子是「继续绿」。
    private static string HintIn(string? foldLine, int hidden, CountedNoun noun)
    {
        var frame = OutputText.FoldLine(hidden, noun, Slot, null, string.Empty)!;
        var cut = frame.IndexOf(Slot, StringComparison.Ordinal);
        return foldLine![cut..^(frame.Length - cut - Slot.Length)];
    }

    private static string? CapBranch(int hidden, int shown, ResultLimit limit, string? capAction = null)
        => Fold.Line(hidden, shown, null, true, CountedNoun.Files, string.Empty, limit, capAction);

    // 「顶到服务端上限了」那一支。capAction 由各工具自己填，故喂两个不同的取值求公共前缀。
    private static readonly string ServerCapReached = CommonPrefix(
        HintIn(CapBranch(1, ScopeAndLimitArgs.HardLimit,
            new ResultLimit(ScopeAndLimitArgs.HardLimit, true), "a"), 1, CountedNoun.Files),
        HintIn(CapBranch(1, ScopeAndLimitArgs.HardLimit,
            new ResultLimit(ScopeAndLimitArgs.HardLimit, true), "b"), 1, CountedNoun.Files));

    // 「'all' 真的能展开」那两支（藏起来的比硬上限少 / 比硬上限还多，措辞不同、建议同向）。
    private static readonly string AdvisesLimitAll = CommonPrefix(
        HintIn(CapBranch(1, 1, new ResultLimit(5, false)), 1, CountedNoun.Files),
        HintIn(CapBranch(ScopeAndLimitArgs.HardLimit, 1, new ResultLimit(5, false)),
            ScopeAndLimitArgs.HardLimit, CountedNoun.Files));

    // ---- 产地断言的两条通用手法（规则一 / 三 / 五 / 九 共用） ----
    //
    // 闸吃的是**文本**（见类头），故「往回验产地」不可能是「拿返回对象来断言」——那会把这一层
    // 降级成隔壁 OutputReadabilityTests。能做的是把产地**当函数调一遍**，看它渲染出来的东西
    // 在不在文本里。结构类的几条规则全部改用下面两条手法，本文件里描述文法的正则一条不剩。
    //
    //   甲、**固定框架**：同一个产地喂两组不同实参，渲染结果的公共前缀就是它恒定不变的那截。
    //       产品那句话改一个字，框架自动跟着变——闸这边一个描述文法的字面量都不必写。
    //   乙、**驱动实参**：产地是函数，不给实参渲染不出来，而候选实参只能从被检文本里来：
    //       行内出现的整数，加上 CountedNoun 的名单。这仍是「取名单不取判断」（§3 判据六）——
    //       闸问的还是它自己的问题（这一行合不合文法），只是把「文法长什么样」从正则改成了求值。
    //
    // 收益不是少写几行正则，是**豁免消失**。规则三此前挂着一条 `(?<![-\d])` 的区间豁免，认的是
    // 「N 前面有没有连字符」这个纯文本特征——read_code 哪天把 `lines 2-30` 写成 `L2–L30`，
    // 豁免默默失效，闸会把一条正常的行判红。改成问产地之后那一形自己有了产地（Tally.Window），
    // 豁免整条不必存在。规则九那两条手写的 StartsWith 豁免同理。

    // 「这一段由调用方填」的哨兵。取一个输出里绝不可能出现的控制字符，渲染完按它切开，
    // 就得到产地在这一形上恒定的前后两截。
    private const string Slot = "\u0001";

    private static string CommonPrefix(params string?[] renderings)
    {
        var texts = renderings.OfType<string>().ToArray();
        var head = texts[0];
        foreach (var other in texts.Skip(1))
        {
            var i = 0;
            while (i < head.Length && i < other.Length && head[i] == other[i]) i++;
            head = head[..i];
        }
        return head;
    }

    private static string CommonSuffix(params string?[] renderings)
    {
        var texts = renderings.OfType<string>().ToArray();
        var tail = texts[0];
        foreach (var other in texts.Skip(1))
        {
            var i = 0;
            while (i < tail.Length && i < other.Length && tail[^(i + 1)] == other[^(i + 1)]) i++;
            tail = tail[^i..];
        }
        return tail;
    }

    // 行内出现的整数，去重。产地要实参才渲染得出来，而实参只能从被检文本里猜——这是唯一来源。
    // 封顶 12 个是组合数的护栏：这几条规则都要拿它做二到三重循环，而真正带计数记号的行
    // （表头、折叠行、位置行）整数从没超过 6 个。超顶时闸只会**少认**一种渲染，即判红，不会漏放。
    private static int[] IntegersIn(string text)
        => Regex.Matches(text, @"\d+")
            .Select(m => int.TryParse(m.Value, out var v) ? v : -1)
            .Where(v => v >= 0).Distinct().Take(12).ToArray();

    private static IEnumerable<int?> WithAndWithoutTotal(int[] numbers)
        => numbers.Select(n => (int?)n).Prepend(null);

    // 这一行**可能**用到哪几个名词。纯粹是上面那些循环的剪枝：任何一条产地渲染都含它的名词
    // 形式之一，故不含某个名词的行，那个名词的渲染一条也不可能是它的子串。只会剪掉不可能匹配的，
    // 不改变判据。
    private static IEnumerable<CountedNoun> NounsMentionedIn(string line)
        => CountedNoun.All.Where(n =>
            line.Contains(n.Plural, StringComparison.Ordinal)
            || line.Contains(n.Singular, StringComparison.Ordinal));

    // ---- `... ` 开头那一族行的产地 ----
    //
    // 全服只有这六个。前两个是折叠行（有「还剩多少没印」这件事要说），后四个不是：数不出还剩
    // 多少（扫描在上限处停了，后面的文件根本没打开过）、两侧都印着（中段省略）、或者说的是一个
    // 没有参数放得宽的常数上限。各自的存在理由见 README「低 Token 消耗」一节。
    private enum DotLine
    {
        Unknown,           // 没有任何产地渲染得出它
        Fold,              // OutputText.FoldLine —— Fold.Line / Fold.Explicit 都转发到它
        PerFileFold,       // Fold.PerFile —— 下一步整份返回里只说一次，故这一形不带括号
        PreviewCapNotice,  // Fold.PerFilePreviewCap —— 上面那句「只说一次」的下一步本身
        Elision,           // Fold.Elision —— 中段省略，两侧都印着
        ScanStopped,       // ScanReport.ScanStopped
        NotScannedInFull,  // ScanReport.NotScannedInFull
    }

    // 非折叠那四句的固定框架，全部由产地喂两组实参求公共前缀得来（手法甲）。
    // 此前这里是一张手抄的三句名单——第四句（中段省略）从来没进过它，只在规则九那边另有一条
    // StartsWith 豁免，于是同一件事在闸里有两份互不知情的判据。
    private static readonly (DotLine Kind, string Frame)[] NonFoldFrames =
    [
        (DotLine.PreviewCapNotice, CommonPrefix(Fold.PerFilePreviewCap(1), Fold.PerFilePreviewCap(2))),
        (DotLine.Elision, CommonPrefix(Fold.Elision(1, 1, 1), Fold.Elision(2, 2, 2))),
        (DotLine.NotScannedInFull,
            CommonPrefix(ScanReport.NotScannedInFull(["a"]), ScanReport.NotScannedInFull(["b"]))),
        (DotLine.ScanStopped, CommonPrefix(
            ScanReport.ScanStopped(1, new ResultLimit(1, false)),
            ScanReport.ScanStopped(2, new ResultLimit(2, false)),
            ScanReport.ScanStopped(1, new ResultLimit(1, true)))),
    ];

    // 这一行是哪个产地渲染出来的。null = 它压根不以 `... ` 开头，不归规则一与规则九管。
    private static DotLine? ClassifyDotLine(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("... ", StringComparison.Ordinal)) return null;

        var indent = line[..(line.Length - trimmed.Length)];
        var numbers = IntegersIn(line);
        var nouns = NounsMentionedIn(line).ToArray();

        // 折叠行的 `(<下一步>)` 那一段由调用方填，故拿哨兵渲染一次再切成前后两截逐字比对。
        foreach (var hidden in numbers)
        foreach (var total in WithAndWithoutTotal(numbers))
        foreach (var noun in nouns)
        {
            if (OutputText.FoldLine(hidden, noun, Slot, total, indent) is not { } rendered) continue;
            var cut = rendered.IndexOf(Slot, StringComparison.Ordinal);
            var head = rendered[..cut];
            var tail = rendered[(cut + Slot.Length)..];
            if (line.Length >= head.Length + tail.Length
                && line.StartsWith(head, StringComparison.Ordinal)
                && line.EndsWith(tail, StringComparison.Ordinal))
                return DotLine.Fold;
        }

        // 每文件折叠行没有调用方填的槽，整行逐字相等。
        foreach (var hidden in numbers)
        foreach (var totalInFile in numbers)
            if (line == Fold.PerFile(hidden, totalInFile, indent)) return DotLine.PerFileFold;

        foreach (var (kind, frame) in NonFoldFrames)
            if (trimmed.StartsWith(frame, StringComparison.Ordinal)) return kind;

        return DotLine.Unknown;
    }

    public static IReadOnlyList<GrammarViolation> Check(string text)
    {
        var found = new List<GrammarViolation>();
        if (string.IsNullOrEmpty(text)) return found;

        var lines = text.Split('\n');

        // `... ` 开头的行归哪个产地只判一次：规则一问「有没有产地」，规则九问「是不是那两形
        // 本来就不带下一步的」。此前两处各写一份 IsPerFileFold / StartsWith 判据，改了一处
        // 另一处就成了新的假阳性来源。
        var dotLines = lines.Select(ClassifyDotLine).ToArray();

        FoldLineShape(lines, dotLines, found);
        Agreement(lines, found);
        CountIdiomComesFromAnOrigin(lines, found);
        AtLeastHasACause(text, found);
        SourceLabelsAreHoisted(lines, found);
        NoTrailingBlankLine(text, found);
        HeaderAndFoldAgree(text, found);
        TruncationGivesANextStep(text, lines, dotLines, found);

        return found;
    }

    public static string Describe(string where, IReadOnlyList<GrammarViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(where).Append(" 违反输出文法 ").Append(violations.Count).Append(" 处：");
        foreach (var v in violations) sb.Append("\n  · ").Append(v);
        return sb.ToString();
    }

    // ---- 一、折叠行文法（管「怎么说」；R19/R21 名词槽留空、R47 只给增量） ----
    //
    // `... ` 开头的行**必须逐字等于某个产地渲染得出来的样子**（判定见 ClassifyDotLine）。
    //
    // 此前这一条是两条正则加一张手抄的「不是折叠行的三句」名单。正则把文法重新声明了一遍——
    // 于是产品与闸各存一份，可以各改各的；名单把例外重新声明了一遍——于是第四句例外
    // （`... [Truncated …]`）从来没进过它，只在规则九那边另有一条 StartsWith 豁免。改成问产地
    // 之后两者都不必写：例外就是「另一个产地」，新增一形只要在 NonFoldFrames 里点名它的产地。
    //
    // 名词槽的两条判据一并由此得出，不再单独判：产地的名词参数取自 CountedNoun，故渲染得出来的
    // 行，名词必然登记在案。渲染不出来时下面再问一次「是不是只有名词槽不对」——那只影响措辞。
    private static void FoldLineShape(string[] lines, DotLine?[] kinds, List<GrammarViolation> found)
    {
        for (var i = 0; i < lines.Length; i++)
            if (kinds[i] == DotLine.Unknown)
                found.Add(Diagnose(lines[i]));
    }

    // 配不上任何产地的 `... ` 行，坏在哪儿。
    //
    // 同一个产地喂两个不同的名词，公共前缀就是名词槽**之前**那截固定框架，公共后缀里
    // 哨兵之前那截就是名词槽**之后**的开头（手法甲）。前缀对得上，说明形状是对的、坏的是这个槽。
    private static GrammarViolation Diagnose(string line)
    {
        var trimmed = line.TrimStart();
        var indent = line[..(line.Length - trimmed.Length)];
        var numbers = IntegersIn(line);

        foreach (var hidden in numbers)
        foreach (var total in WithAndWithoutTotal(numbers))
        {
            var a = OutputText.FoldLine(hidden, CountedNoun.Files, Slot, total, indent);
            var b = OutputText.FoldLine(hidden, CountedNoun.Members, Slot, total, indent);
            if (a is null || b is null) continue;

            var frame = CommonPrefix(a, b);
            if (frame.Length == 0 || !line.StartsWith(frame, StringComparison.Ordinal)) continue;

            var afterSlot = CommonSuffix(a, b);
            var opener = afterSlot[..afterSlot.IndexOf(Slot, StringComparison.Ordinal)];
            var end = line.IndexOf(opener, frame.Length, StringComparison.Ordinal);
            var slot = end < 0 ? line[frame.Length..] : line[frame.Length..end];

            // `... +7 more nothing (…)` 满足旧判据的「非空且首词不是介词」。名词必须是登记在案的
            // 那批，否则它落在单复数规则的辖域之外——加一个词就能把这个槽的覆盖清零。
            return new GrammarViolation("名词槽登记在案", line,
                $"折叠行的形状对得上，名词槽里的 '{slot}' 却不是登记在案的计数名词，它的单复数没人守");
        }

        // sync_sources 两处折叠行历史上就落在这里：`... 12 more of 30 — next page: …`
        // 与 `... 12 more members — pass file=… `——一处丢了 `+` 与名词槽，一处把下一步
        // 写在破折号后面。调用方按共用文法认 `... +`，这两行就整个认不出来。
        return new GrammarViolation("折叠行文法", line,
            "以 `... ` 开头，却不是任何一条产地渲染得出来的样子"
            + "（折叠行走 OutputText.FoldLine 或 Fold.PerFile，非折叠的四句各有产地）");
    }

    // ---- 二、单复数（管「怎么说」；R30 `... +1 more C# types`） ----
    //
    // 甲是纯结构判定、不查词表，故对没登记的名词也成立——历史缺陷全部落在 N==1 这一侧
    // （复数式是各处写死的常量，写错只会写成「1 个复数」）。
    // 乙查词表，补上 N>1 那一侧。
    // 丙禁 `N thing(s)`：那是把判断推给读者，README 明写全服不写这一形。
    private static void Agreement(string[] lines, List<GrammarViolation> found)
    {
        foreach (var line in lines)
        {
            foreach (Match m in OnePlus.Matches(line))
            {
                foreach (var word in m.Groups["phrase"].Value.Split(' '))
                {
                    if (NotNouns.Contains(word)) continue;
                    if (!word.EndsWith('s')) continue;
                    if (word.EndsWith("ss", StringComparison.Ordinal)
                        || word.EndsWith("us", StringComparison.Ordinal)
                        || word.EndsWith("is", StringComparison.Ordinal)) continue;
                    found.Add(new GrammarViolation("单复数", line, $"`1 …{word}` 用了复数式"));
                }
            }

            foreach (var noun in CountedNoun.All)
            {
                var plural = noun.Plural;
                var singular = noun.Singular;
                foreach (Match m in Regex.Matches(line, $@"\b(?<n>\d+) {Regex.Escape(singular)}\b"))
                    if (int.Parse(m.Groups["n"].Value) != 1)
                        found.Add(new GrammarViolation("单复数", line,
                            $"`{m.Groups["n"].Value} {singular}` 该用复数式 '{plural}'"));
            }

            foreach (Match m in LazyPlural.Matches(line))
                found.Add(new GrammarViolation("单复数", line, $"`{m.Value}` 把单复数推给了读者"));
        }
    }

    // ---- 三、`of` 的读法（管「说不说」；R33） ----
    //
    // 这条读法的辖域是**一个计数惯用法**，不是 `of` 这个词：`<数> of <数>` 里的 of 表示没给全。
    //
    // 说清辖域不是咬文嚼字。基线语料里的 `of` 有三种用法并存：截断记号、改不掉的普通英文介词
    // （`lines of a N-line file`、`tokens of that length`、`the total number of matching files`）、
    // 以及 read_code 的区间形（`lines 2-30 of 30`）。故「看到 of 就是被截了」当作一条关于这个词
    // 的规则来读时是**假陈述**——照它读，`2001 lines of a 2003-line file` 会被读成截断记号。
    //
    // 处数会随输出改动而变（N5 把 inheritors 的 `Subclasses of 'X'` 那 7 处普通介词整批换掉了），
    // 故这里不再钉具体计数——要钉的是**三类并存**这件事，那才是辖域必须写窄的理由。
    //
    // ---- 五、名词槽不空（管「怎么说」；R19/R21 的 `N of M` 侧） ----
    //
    // 三与五合用同一次产地遍历，因为它们问的是同一件事的两面：**这个计数惯用法必须出自产地**。
    // 出自产地的，算术与名词槽按构造都对——Tally.Cell 只在 total > shown 时才写 of（故 N < M
    // 不必再验），名词参数取自 CountedNoun（故名词槽不会空、也不会是名单外的词）。配不上产地的，
    // 就是有人手拼了一个长得像的东西，两条历史缺陷（`12 of 12`、`5 of 12 in scope 'all'`）
    // 都是这一形。
    //
    // 候选只认「数字紧跟 of」：普通介词前面不是数字（`lines of a N-line file`、
    // `the total number of matching files`），一条都进不来。而区间形（`lines 2-30 of 30`，
    // 那个 of 说的是「取自」）现在有产地 Tally.Window，故此前那条 `(?<![-\d])` 的豁免**整条删掉**
    // ——它认的是「N 前面有没有连字符」这个纯文本特征，产品那边换个写法它就默默失效。
    // 这条豁免删得掉，正是「真的换成了产地断言」的度量（见「单一产地重构指导」§4 M2）。
    private static readonly Regex CountThenOf = new(@"\d+ of ", RegexOptions.Compiled);

    private static void CountIdiomComesFromAnOrigin(string[] lines, List<GrammarViolation> found)
    {
        foreach (var line in lines)
        {
            foreach (Match m in CountThenOf.Matches(line))
            {
                if (ComesFromACountIdiomOrigin(line, m.Index)) continue;

                // 报哪一条只影响措辞，判红与否上面已经定了：行里连一个登记在案的计数名词都没有时，
                // 缺的是名词槽（`5 of 12 in scope 'all'` 里 `in` 当了那个名词）；有名词却配不上
                // 产地，那就是这个惯用法被手拼了一遍（`12 of 12` 里的 of 在说一件没发生的事）。
                var mentionsANoun = NounsMentionedIn(line).Any();
                found.Add(mentionsANoun
                    ? new GrammarViolation("of 的读法", line,
                        $"`{m.Value.Trim()} …` 不出自任何计数记号的产地——Tally.Cell 的 of 表示没给全"
                        + "（故它只在 N < M 时才写），Tally.Window 是区间形。这一处是手拼的")
                    : new GrammarViolation("名词槽不空", line,
                        $"`{m.Value.Trim()} …` 后面没有登记在案的计数名词，数的是什么全靠猜"));
            }
        }
    }

    // 行内 at 这个位置，落在某条计数记号产地渲染出来的片段里没有。
    //
    // 实参从行内整数与名词名单里来（手法乙）；名词先按「这一行提没提到它」剪一道，故绝大多数行
    // 只需要试一两个名词。找到就返回，不必把片段全枚举出来。
    private static bool ComesFromACountIdiomOrigin(string line, int at)
    {
        var numbers = IntegersIn(line);
        foreach (var noun in NounsMentionedIn(line))
        foreach (var total in numbers)
        foreach (var shown in numbers)
        {
            if (Covers(line, at, Tally.Cell(shown, total, noun))) return true;
            if (Covers(line, at, Tally.Cell(shown, total, noun, true))) return true;
            foreach (var from in numbers)
                if (Covers(line, at, Tally.Window(noun, from, shown, total))) return true;
        }
        return false;
    }

    private static bool Covers(string line, int at, string rendering)
    {
        for (var i = line.IndexOf(rendering, StringComparison.Ordinal);
             i >= 0;
             i = line.IndexOf(rendering, i + 1, StringComparison.Ordinal))
            if (i <= at && at < i + rendering.Length) return true;
        return false;
    }

    // ---- 四、`at least` 的读法（管「说不说」；F26 表头没随尾注改口、F30 新记号只在一处实现） ----
    //
    // 甲：出现下界记号就必须给成因，**且成因必须是可指认的**。两个扫描类工具的 `at least` 恒与
    //     「有文件没扫全」同现，调用方从那里学到的读法就是「看到 at least 去找成因」；locate 加
    //     这个记号时若只改表头，同一个记号在两个工具上就要各学一遍。
    //
    //     第九轮把这条从「同现」升格成「可指认」：三条互不相干的任务链在**成因确实同现**的返回上
    //     各自独立误读了同一个 `at least 105`——它们就近拿 `limit` 的 default 100 去解释那个下界
    //     （只差 5，算术上太顺），而真正的成因隔在整份结果之后、中间还夹着别的尾注。同现不够，
    //     记号自己必须带一条指向成因的引用（LowerBoundReason）。
    //     反面用例见 GrammarRulesTests.FloorMarkWithoutAPointer_IsCaught。
    // 乙：引用指向的那条尾注必须真的在场。ScanReport 那条引用明说「见结尾那句 not scanned in
    //     full」，尾注却不在，等于一个悬空的指路牌。
    // 丙：反向。有文件没扫全时总数只是下界，表头必须跟着改口，否则一句说「7 found」、一句说
    //     「有文件没扫全」，调用方无从判断该信哪个。
    //     例外是表头已经换了量纲的那一支：扫描停在预览上限时表头数的是**印出来的**预览行
    //     （ScanReport.PreviewLineCount），那个数是确定的，不该也不能改口。
    //
    // 四张手抄字符串表（成因 / 诱饵 / 引用 / 记号）全部由产地渲染取代：
    //   - 记号取 ScanReport.FloorMark（Tally.Cell 也取它，两侧不会各写一个词）；
    //   - 引用取 ScanReport.LowerBoundReason 与 LocateTool.MemberFloorNotice——全服 `at least`
    //     只有这两处出处（扫描没扫全 / 候选池装不下），各自带自己那条；
    //   - 成因取 ScanReport.NotScannedInFull 的固定框架；
    //   - **诱饵表整张删掉**：甲从「同屏有诱饵时才要引用」升格成「一律要引用」。诱饵表原本要
    //     枚举「读者可能拿来就近解释这个下界的别的上限说明」，那是个开放集合——语料里能想到的
    //     四条之外，任何新加的上限句都会静默漏进来。而现存三份带 `at least` 的基线本来就条条
    //     带引用，升格不改任何一处输出，只是把判据从「数得清的诱饵」换成了「记号自己说清楚」。
    //     顺带删掉的还有表里那条 `'at least' because`——产品一处都不产出它，它只在闸自己的
    //     反面用例里出现过，是一条从落地起就没有产地的死项。
    private static void AtLeastHasACause(string text, List<GrammarViolation> found)
    {
        var scanPointer = ScanReport.LowerBoundReason(true);
        var poolPointer = CommonPrefix(LocateTool.MemberFloorNotice(1), LocateTool.MemberFloorNotice(2));
        var scanCause = CommonPrefix(
            ScanReport.NotScannedInFull(["a"]), ScanReport.NotScannedInFull(["b"]));

        var hasFloor = text.Contains(ScanReport.FloorMark, StringComparison.Ordinal);
        var pointsAtItsCause = text.Contains(scanPointer, StringComparison.Ordinal)
                               || text.Contains(poolPointer, StringComparison.Ordinal);

        if (hasFloor && !pointsAtItsCause)
            found.Add(new GrammarViolation("at least 的读法", string.Empty,
                $"表头改口成 `{ScanReport.FloorMark.Trim()}`，记号旁边却没有一句说清成因在哪"));

        if (text.Contains(scanPointer, StringComparison.Ordinal)
            && !text.Contains(scanCause, StringComparison.Ordinal))
            found.Add(new GrammarViolation("at least 的读法", string.Empty,
                "下界记号指向结尾那条「有文件没扫全」的尾注，而那条尾注不在这份返回里"));

        // 换了量纲的那一支：拿行内整数驱动产地渲染一遍，看表头是不是它（手法乙）。
        // 此前这里是一句手抄的 "preview lines in scope"——那个片段横跨表头与它后面的 scope 标注，
        // 两者本来就出自不同的地方，产品把 scope 标注挪个位置，这条豁免就默默失效。
        var switchedUnit = IntegersIn(text)
            .Any(n => text.Contains(ScanReport.PreviewLineCount(n), StringComparison.Ordinal));

        if (text.Contains(scanCause, StringComparison.Ordinal) && !hasFloor && !switchedUnit)
            found.Add(new GrammarViolation("at least 的读法", string.Empty,
                "有文件没扫全（总数只是下界）而表头仍写成确定值"));
    }

    // ---- 六、来源标签（管「说不说」；同源标签上提那一条） ----
    //
    // 判据来自 SourceLabeling：一段结果全部同源时标签提到段头印一次，逐行不再重复；
    // 真的混源才逐行印。于是**逐行标签存在 ⟹ 整份返回里至少有两种不同的标签**——某一段之所以
    // 逐行印，正是因为那一段内部就有两个不同的源。全篇逐行标签只有一种取值，就说明有一段把
    // 纯噪音逐行印了出来（实测一次 200 条的返回里 412 个标签约占正文 14%）。
    //
    // 只看**行尾**的 `[x]`：这条判的是「印在哪儿」，不是「怎么排版」，故段头上的同一个记号不算。
    private static void SourceLabelsAreHoisted(string[] lines, List<GrammarViolation> found)
    {
        var tags = new List<(string Tag, string Line)>();
        foreach (var line in lines)
        {
            var m = RowLabel.Match(line);
            if (!m.Success) continue;
            var tag = m.Groups["tag"].Value;
            if (NonSourceTag.IsMatch(tag)) continue;
            tags.Add((tag, line));
        }

        if (tags.Count == 0) return;
        if (tags.Select(t => t.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) return;

        found.Add(new GrammarViolation("来源标签", tags[0].Line,
            $"{tags.Count} 行都挂着同一个 `[{tags[0].Tag}]`，该提到段头印一次"));
    }

    // ---- 七、收尾（管「怎么说」；F21） ----
    //
    // 结尾空行会被读成「后面还有、被截断了」——这是本服务里最便宜也最容易漏的一条。
    private static void NoTrailingBlankLine(string text, List<GrammarViolation> found)
    {
        if (text.Length != text.TrimEnd().Length)
            found.Add(new GrammarViolation("收尾", string.Empty,
                $"返回以空白收尾（末 20 字符 = {Quote(text[Math.Max(0, text.Length - 20)..])}）"));
    }

    // ---- 八、自洽（管「怎么说」；R47） ----
    //
    // 表头说 `N of M <名词>`、同一份返回里又有 `... +K more <同名词>` 时，N + K 必须等于 M。
    // R47 之前折叠行只数「取回的这批里还剩几条」，而取回本身已被 limit.Scale(3) 砍过：
    // method:CompTick 报 +25 而实际有 186 条——表头与折叠行各说各的，两个数没有一处对得上。
    //
    // 按名词配对而不是按位置：位置是排版，名词是语义。这也是这条断言唯一不与排版耦合的写法
    // （design 里点名的风险：「自洽」那条解析正文行就会随排版一变就红）。
    // 名词落在名单之外时这条跳过——见文件顶上那段关于 CountedNoun 的说明。
    private static void HeaderAndFoldAgree(string text, List<GrammarViolation> found)
    {
        foreach (var noun in CountedNoun.All)
        {
            var either = $"(?:{Regex.Escape(noun.Plural)}|{Regex.Escape(noun.Singular)})";

            var header = Regex.Match(text, $@"\b(?<n>\d+) of (?:the )?(?:at least )?(?<m>\d+) {either}\b");
            if (!header.Success) continue;

            var fold = Regex.Match(text, $@"\.\.\. \+(?<k>\d+) more {either}\b");
            if (!fold.Success) continue;

            var n = int.Parse(header.Groups["n"].Value);
            var total = int.Parse(header.Groups["m"].Value);
            var hidden = int.Parse(fold.Groups["k"].Value);
            if (n + hidden == total) continue;

            found.Add(new GrammarViolation("自洽", fold.Value,
                $"'{noun}' 段表头说 {n} of {total}，折叠行说 +{hidden}，{n} + {hidden} != {total}"));
        }
    }

    // ---- 九、下一步（管「说不说」；FoldLine 三分支那一条） ----
    //
    // 甲：每条 `... ` 开头的截断提示都要给出可执行的下一步。只说「被截了」而不说怎么拿到，
    //     调用方只能把它读成服务端在敷衍。
    // 乙：`limit` 已经顶到硬上限时不许再劝 `limit:'all'`——照做是原地重试。Fold.Line 的三分支
    //     本就把这两句写成互斥的，故这里判的是「同一份返回里两支同时出现」，即有人绕开了那三分支
    //     另拼了一句。两句提示语都从 Fold.Line 渲染回来（见 ServerCapReached / AdvisesLimitAll），
    //     不在闸这边抄。
    private static void TruncationGivesANextStep(
        string text, string[] lines, DotLine?[] kinds, List<GrammarViolation> found)
    {
        string[] actionable =
        [
            "pass ", "use ", "raise ", "narrow", "broaden", "reword", "offset=", "shorter",
            "limit", "scope", "next page", "read_code", "search_regex", "locate", "trace",
        ];

        for (var i = 0; i < lines.Length; i++)
        {
            if (kinds[i] is not { } kind) continue;

            // 两形本来就不带下一步，各有各的理由：每文件折叠行的下一步整份返回里只说一次
            // （那一句自己就是 PreviewCapNotice），中段省略两侧都印着、翻页参数写在紧邻的续读
            // 提示里。此前这两条豁免是这里两句手写的 StartsWith / IsPerFileFold，与规则一那边
            // 各判各的；现在同取 ClassifyDotLine 的结论，一处改动两处同步。
            if (kind is DotLine.PerFileFold or DotLine.Elision) continue;

            var trimmed = lines[i].TrimStart();
            if (actionable.Any(a => trimmed.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;

            found.Add(new GrammarViolation("下一步", lines[i], "截断提示没给出可执行的下一步"));
        }

        if (text.Contains(ServerCapReached, StringComparison.Ordinal)
            && text.Contains(AdvisesLimitAll, StringComparison.Ordinal))
            found.Add(new GrammarViolation("下一步", string.Empty,
                "已经报了服务端上限，同一份返回里却还在劝 `limit:'all'`——照做是原地重试"));
    }

    private static string Quote(string s) => "\"" + s.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}
