using System.Text;
using System.Text.RegularExpressions;
using RimSearcher.Core;

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
    // 受单复数、名词槽两组规则覆盖的名词。**这就是本闸的覆盖面**：新加一个计数名词却不登记，
    // 那个槽位就没人守。
    //
    // 名单不在这里，在产品侧的 `CountedNoun`——这里只取它。此前这是一张手抄的字面量表，
    // 「与产品同步」只是表头一句注释，于是它从落地那天起就漂着：`changed sources` 与
    // `name keys` 在产品侧一处对应的字面量都没有，而产品真正在数的 `checked sources` 从没进过
    // 表。现在名词是个类型，赋值处就是登记处，抄错这件事在编译期就不成立了。
    //
    // 照 §3 判据六：**只取名单，不取判断**。下面每一条规则的判据仍是这里自己写的——闸问的是
    // 「文本里出现了名单外的计数名词吗」，不是「产品那边怎么算的」。
    private static readonly string[] CountedNouns =
        CountedNoun.All.Select(n => n.Plural).ToArray();

    // 登记名词的两种合法写法：复数式本身，与它的单数式。两个名词槽在 N==1 时印的正是后者
    // （`... +1 more C# type`），只认复数式的话那一侧整片都是假阳性。
    //
    // 单数式由产品那边算（`CountedNoun.Singular`）：闸与产品共用同一个构词结果，否则
    // 「这里说该写 match」与「那里写出 matche」会各自成立。这仍是取名单，不是取判断——
    // 单数式是名词自身的一个属性，与「什么时候该用它」无关。
    private static readonly HashSet<string> CountedNounForms = new(
        CountedNoun.All.SelectMany(n => new[] { n.Plural, n.Singular }),
        StringComparer.Ordinal);

    // 名词槽里那个词是不是登记在案。
    //
    // 两个槽此前都只验形状（「非空且首词不在 NotNouns 里」/「下一个字符是字母」），于是
    // `... +7 more nothing (…)` 与 `5 of 12 in scope 'all'` 都能过闸——而单复数那条规则又只对
    // 表里的词生效。三条合起来：没登记的名词，覆盖是零。这个函数是那个零的补丁。
    private static bool IsCountedNoun(string noun)
        => CountedNounForms.Contains(noun);

    // tail 是不是**以**一个登记名词开头。`N of M` 那个槽后面还跟着句子的其余部分
    // （`10 of 21 C# types (1 at 100%)`），故只能判词头。
    //
    // 词边界对齐：`type` 不许把 `types` 的前四个字母认下来，否则单复数那一侧就被这条悄悄豁免了。
    private static bool LeadsWithCountedNoun(string tail)
    {
        var t = tail.TrimStart();
        return CountedNounForms.Any(n =>
            t.StartsWith(n, StringComparison.Ordinal)
            && (t.Length == n.Length || !char.IsLetter(t[n.Length])));
    }

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

    // 折叠行的正式文法：`<缩进>... +N more [of M ]<名词> [(<下一步>)]`
    private static readonly Regex FoldLine = new(
        @"^(?<indent>[ \t]*)\.\.\. \+(?<n>\d+) more (?:of (?<m>\d+) )?(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex FoldTail = new(
        @"^(?<noun>[^(]*?)\s*\((?<hint>.+)\)$", RegexOptions.Compiled);

    // `N of M`。`the` / `at least` 是两个已知的插入语：前者出现在 sync_sources 的
    // `2 of the 5 changed files`，后者是下界改口。
    //
    // `(?<![-\d])` 把**区间**那一形排除在外：read_code 的 `(lines 2-30 of 30)` 里 `30 of 30`
    // 不是本条管的那个计数记号，而是「第 2 到第 30 行，全文共 30 行」——名词按英文语序落在
    // 区间**前面**，且这个 of 说的是「取自」而不是「被截了」。两形只是长得像。
    // 这条豁免就是本规则的边界：它管 `N of M` 这一个计数惯用法，不管所有含 of 的句子。
    private static readonly Regex NofM = new(
        @"(?<![-\d])\b(?<n>\d+) of (?:the )?(?<floor>at least )?(?<m>\d+)(?<tail>.{0,40})",
        RegexOptions.Compiled);

    // 每文件折叠行的名词与它后面那个作用域限定。分成两个常量是因为规则一要拿前者去查词表，
    // 而这一形的名词槽本来就带着 `in this file`（见 Fold.PerFile）——那半句说的是「在哪儿」，
    // 不是名词的一部分，连着查词表要么恒红、要么逼词表收一个带地点状语的假名词。
    private const string PerFileFoldNoun = "matching lines";
    private const string PerFileFoldTail = $" {PerFileFoldNoun} in this file";

    // 每文件折叠行。README 明写它是**唯一**不带括号的一形：它的下一步整份返回里只说一次
    // （`... previews are capped at N lines per file …`），不逐文件重复。规则一与规则九
    // 共用这一个判据——两处各写一份的话，改了一处另一处就成了新的假阳性来源。
    private static bool IsPerFileFold(string trimmed)
        => trimmed.EndsWith(PerFileFoldTail, StringComparison.Ordinal)
           && trimmed.StartsWith("... +", StringComparison.Ordinal);

    private static readonly Regex OnePlus = new(
        @"\b1 (?<phrase>[A-Za-z#][A-Za-z#]*(?: [A-Za-z#][A-Za-z#]*){0,2})", RegexOptions.Compiled);

    private static readonly Regex RowLabel = new(
        @"^[ \t]*-\s.*\s\[(?<tag>[^\]]+)\]$", RegexOptions.Compiled);

    private static readonly Regex LazyPlural = new(@"\d+ [A-Za-z]+\(e?s\)", RegexOptions.Compiled);

    private static readonly Regex ServerCapReached = new(@"server cap \d+ reached", RegexOptions.Compiled);

    // `... ` 开头但**故意**不带计数的那三句共用行。它们各自的存在理由写在 README「低 Token
    // 消耗」一节里，都不是折叠行：前两句数不出还剩多少（后面的文件根本没打开过），第三句
    // 说的是一个没有参数放得宽的常数上限。
    private static readonly string[] NonFoldNotices =
    [
        "... more matches exist (",
        "... some files were not scanned in full (",
        "... previews are capped at ",
    ];

    // `at least` 的成因。出现下界记号却一条成因都不给时，调用方无从判断「narrow the query」
    // 到底要窄到什么程度——F26/F30 两条都是这个形状。
    private static readonly string[] FloorCauses =
    [
        "were not scanned in full",
        "expansion cap",
    ];

    // 「看起来像成因、其实不是」的上限说明。它们与下界记号同屏时，读者会就近用它们解释那个下界。
    private static readonly string[] FloorDecoys =
    [
        "previews are capped at",
        "files are listed",
        "scan stopped at",
        "server cap",
    ];

    // 下界记号自带的、指向真成因的引用（ScanReport.LowerBoundReason / locate 的 floorNotice）
    private static readonly string[] FloorPointers =
    [
        "'at least' because",
        "so the member total above is a floor",
    ];

    public static IReadOnlyList<GrammarViolation> Check(string text)
    {
        var found = new List<GrammarViolation>();
        if (string.IsNullOrEmpty(text)) return found;

        var lines = text.Split('\n');

        FoldLineShape(lines, found);
        Agreement(lines, found);
        OfMeansTruncated(lines, found);
        AtLeastHasACause(text, found);
        CountsCarryANoun(lines, found);
        SourceLabelsAreHoisted(lines, found);
        NoTrailingBlankLine(text, found);
        HeaderAndFoldAgree(text, found);
        TruncationGivesANextStep(text, lines, found);

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
    // `... +` 开头就是被截断了，这一形全服一套：`... +N more [of M ]<名词> (<下一步>)`。
    // 唯一豁免是每文件折叠行——它的下一步整份返回里只说一次，不逐文件重复（README 明写）。
    private static void FoldLineShape(string[] lines, List<GrammarViolation> found)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("... ", StringComparison.Ordinal)) continue;
            if (NonFoldNotices.Any(n => trimmed.StartsWith(n, StringComparison.Ordinal))) continue;

            var counting = trimmed.Contains(" more ", StringComparison.Ordinal)
                           || trimmed.StartsWith("... +", StringComparison.Ordinal);
            if (!counting) continue;

            var m = FoldLine.Match(line);
            if (!m.Success)
            {
                // sync_sources 两处折叠行历史上就落在这里：`... 12 more of 30 — next page: …`
                // 与 `... 12 more members — pass file=… `——一处丢了 `+` 与名词槽，一处把下一步
                // 写在破折号后面。调用方按共用文法认 `... +`，这两行就整个认不出来。
                found.Add(new GrammarViolation("折叠行文法", line,
                    "以 `... ` 开头且在数东西，却不合 `... +N more [of M ]<名词> (<下一步>)`"));
                continue;
            }

            var rest = m.Groups["rest"].Value;
            var perFileFold = IsPerFileFold(trimmed);

            var tail = FoldTail.Match(rest);
            var noun = (tail.Success ? tail.Groups["noun"].Value : rest).Trim();

            if (!tail.Success && !perFileFold)
                found.Add(new GrammarViolation("折叠行文法", line,
                    "折叠行不以 `(<下一步>)` 收尾，而唯一豁免是每文件折叠行"));

            // 每文件折叠行的名词后面还带一个作用域限定（`… matching lines in this file`）：
            // 名词是 `matching lines`，`in this file` 说的是「在哪儿」。判词表前先摘掉它，
            // 否则这一形要么恒红，要么逼着词表收一个带地点状语的假名词。
            var slot = perFileFold ? PerFileFoldNoun : noun;

            var head = slot.Split(' ').FirstOrDefault() ?? string.Empty;
            if (slot.Length == 0 || NotNouns.Contains(head))
                found.Add(new GrammarViolation("名词槽不空", line,
                    $"折叠行数的是什么没写出来（名词槽 = '{slot}'）"));

            // 非空还不够：`... +7 more nothing (…)` 也满足上面那条。名词必须是登记在案的那批，
            // 否则它落在单复数规则的辖域之外——即「加一个词就能把这个槽的单复数覆盖清零」。
            else if (!IsCountedNoun(slot))
                found.Add(new GrammarViolation("名词槽登记在案", line,
                    $"折叠行的名词 '{slot}' 不在 CountedNouns 里，它的单复数没人守"));
        }
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
    // 闸这边执行的从来就是窄的那条（下面的 NofM 只认 `\d+ of \d+`，还专门豁免了区间形），
    // 与这句话本来就不是一回事；此处只是把注释改回它实际在做的事。
    //
    // 在这条窄读法之下，`N of M` 里 N < M 是最低要求——N == M 时那个 of 在说一件没发生的事，
    // N > M 则连算术都不成立。
    private static void OfMeansTruncated(string[] lines, List<GrammarViolation> found)
    {
        foreach (var line in lines)
        {
            foreach (Match m in NofM.Matches(line))
            {
                var n = int.Parse(m.Groups["n"].Value);
                var total = int.Parse(m.Groups["m"].Value);
                if (n < total) continue;
                found.Add(new GrammarViolation("of 的读法", line,
                    $"`{n} of {total}`：of 是截断记号，而这一段没被截"));
            }
        }
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
    //     反面用例见 GrammarRulesTests.FloorMarkNextToADecoyCause_NeedsAPointer：`at least` 与一个
    //     **并非其成因**的上限说明同时可见时，不带引用就判违规。
    // 乙：反向。有文件没扫全时总数只是下界，表头必须跟着改口，否则一句说「7 found」、一句说
    //     「有文件没扫全」，调用方无从判断该信哪个。
    //     例外是表头已经换了量纲的那一支：扫描停在预览上限时表头数的是**印出来的**预览行
    //     （`first N preview lines`），那个数是确定的，不该也不能改口。
    private static void AtLeastHasACause(string text, List<GrammarViolation> found)
    {
        var hasFloor = text.Contains("at least ", StringComparison.Ordinal);
        var hasCause = FloorCauses.Any(c => text.Contains(c, StringComparison.Ordinal));

        if (hasFloor && !hasCause)
            found.Add(new GrammarViolation("at least 的读法", string.Empty,
                "表头改口成 `at least` 却没有任何一句说清成因"));

        // 成因在场还不够：返回里同时可见另一个上限说明时，读者能就近拿它解释这个下界。
        // 此时记号必须自带指向真成因的引用。
        if (hasFloor && hasCause && FloorDecoys.Any(d => text.Contains(d, StringComparison.Ordinal))
            && !FloorPointers.Any(p => text.Contains(p, StringComparison.Ordinal)))
            found.Add(new GrammarViolation("at least 的读法", string.Empty,
                "`at least` 与一个并非其成因的上限说明同时可见，而记号自己没有指向真成因的引用"));

        var switchedUnit = text.Contains("preview lines in scope", StringComparison.Ordinal);
        if (text.Contains("were not scanned in full", StringComparison.Ordinal) && !hasFloor && !switchedUnit)
            found.Add(new GrammarViolation("at least 的读法", string.Empty,
                "有文件没扫全（总数只是下界）而表头仍写成确定值"));
    }

    // ---- 五、名词槽不空（管「怎么说」；R19/R21） ----
    //
    // 折叠行那一侧在规则一里判了，这里补计数那一侧：`N of M` 后面必须紧跟一个**登记在案的**名词。
    // 历史上 sync_sources 的 `... 12 more of 30 — next page` 就是一个裸着的 30。
    //
    // 此前这里只验「下一个字符是字母」，于是 `5 of 12 in scope 'all'` 里的 `in` 就当了那个名词
    // ——一个数了什么全没写的计数记号照样过闸。查词表之后这一形才有判据。
    private static void CountsCarryANoun(string[] lines, List<GrammarViolation> found)
    {
        foreach (var line in lines)
        {
            foreach (Match m in NofM.Matches(line))
            {
                var tail = m.Groups["tail"].Value;
                if (LeadsWithCountedNoun(tail)) continue;

                var naked = tail.Length < 2 || tail[0] != ' ' || !char.IsLetter(tail[1]);
                found.Add(new GrammarViolation("名词槽不空", line,
                    naked
                        ? $"`{m.Groups["n"].Value} of {m.Groups["m"].Value}` 后面没有名词，数的是什么全靠猜"
                        : $"`{m.Groups["n"].Value} of {m.Groups["m"].Value}` 后面跟的"
                          + $"`{tail.Trim()}` 不是登记在案的计数名词"));
            }
        }
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
    // 名词落在词表之外时这条跳过——见 CountedNouns 的说明。
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
    // 乙：`limit` 已经顶到硬上限时不许再劝 `limit:'all'`——照做是原地重试。同一份返回里
    //     `server cap N reached` 与 `pass limit:'all'` 互斥（见 Fold.Line 的三分支）。
    private static void TruncationGivesANextStep(string text, string[] lines, List<GrammarViolation> found)
    {
        string[] actionable =
        [
            "pass ", "use ", "raise ", "narrow", "broaden", "reword", "offset=", "shorter",
            "limit", "scope", "next page", "read_code", "search_regex", "locate", "trace",
        ];

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("... ", StringComparison.Ordinal)) continue;
            // `... [Truncated N lines: a-b] ...` 是 inspect 的窗口标记，不是截断提示：
            // 它两侧的行都印着，下一步（翻页参数）写在紧邻的那句里。
            if (trimmed.StartsWith("... [Truncated ", StringComparison.Ordinal)) continue;
            // 每文件折叠行的下一步整份返回里只说一次，故这一形本来就不带下一步——与规则一同一条豁免
            if (IsPerFileFold(trimmed)) continue;
            if (actionable.Any(a => trimmed.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;

            found.Add(new GrammarViolation("下一步", line, "截断提示没给出可执行的下一步"));
        }

        if (ServerCapReached.IsMatch(text) && text.Contains("pass limit:'all'", StringComparison.Ordinal))
            found.Add(new GrammarViolation("下一步", string.Empty,
                "已经报了服务端上限，同一份返回里却还在劝 `limit:'all'`——照做是原地重试"));
    }

    private static string Quote(string s) => "\"" + s.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}
