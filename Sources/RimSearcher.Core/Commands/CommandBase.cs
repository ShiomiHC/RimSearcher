using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 全局参数 —— 与命令声明同源,由 <see cref="ArgParser"/> 合并进每条命令的参数表。
/// </summary>
public static class GlobalOptions
{
    public static readonly OptionSpec Snapshot = new()
    {
        Name = "snapshot",
        // 'profile' 归 export 的 --modlist:mod 列表才是 RimWorld 语境里的 profile。
        Aliases = ["snap", "env"],
        Placeholder = "<name>",
        Help = "Query this named snapshot instead of the one that would be picked automatically. " +
               "An explicit choice always wins over auto-detection.",
    };

    public static readonly OptionSpec Db = new()
    {
        Name = "db",
        Aliases = ["database", "snapshot-path"],
        Placeholder = "<path>",
        Help = "Query the snapshot database at this path directly, bypassing the registry.",
    };

    public static readonly OptionSpec Json = new()
    {
        Name = "json",
        Arity = Arity.Flag,
        // 这两个键的存在必须写在自述里,否则下游根本不知道有,照旧去正则抠句子 ——
        // 而句子是会改的。写清「缺席 = 这条不是计数」,别让缺席被读成「没有截断」。
        Help = "Emit machine-readable JSON. Anything the text output would have said in prose " +
               "moves into a 'notes' array. The command's own table key is " +
               "always present: an empty array when nothing matched. " +
               "A note that reports a count also carries 'shown' and 'total' as numbers, so the " +
               "figures never have to be parsed back out of its text; 'total' is null when only a " +
               "lower bound is known, and both keys are absent on notes that are not counts.",
    };

    public static readonly OptionSpec Quiet = new()
    {
        Name = "quiet",
        Aliases = ["data-only"],
        Arity = Arity.Flag,
        // 事实:stdout 只剩数据块;脚注与快照标签一并去掉。机制:声明仍整份进 run-log。
        // 出路:不加这个旗,声明照旧印在 stdout。零结果那条路本来只有声明,于是 stdout
        // 变成空的 —— 那时退出码自己说是哪种零:1 量了是空;3 / 4 带着 absent / found_as 表,
        // 表是数据块,照印。
        Help = "Stdout prints only data blocks. Notices, including footnotes and the snapshot tag, " +
               "are omitted from stdout; they are still written in full to the run log. " +
               "A query that finds nothing then prints no stdout at all and exits 1; the absent and " +
               "found_as tables are data blocks, so an exit 3 or 4 still prints its table. " +
               "Leave this flag off to print the notices on stdout as well.",
    };

    public static readonly OptionSpec Config = new()
    {
        Name = "config",
        Placeholder = "<path>",
        Help = "Use this config file instead of the default one.",
    };

    public static readonly IReadOnlyList<OptionSpec> All = [Snapshot, Db, Json, Quiet, Config];
}

/// <summary>常用的按命令参数。声明放在这里是为了让别名与措辞只有一个产地。</summary>
public static class CommonOptions
{
    /// <summary>
    /// 真实调用方把「最多要几条」拼成 maxResults / max_results / limit 三种 —— 同义词进别名。
    /// </summary>
    /// <remarks>
    /// <c>all</c> 不在取值里,是用法错误(d155104)。它曾占过全部调用的 65.6%
    /// (16298 / 24862),撤掉四天后残留 0.92%(32 / 3492),没有一例落进
    /// 「改写成一个巨大的数字」—— 而那个数字与不给逐字节相同,所以那条坑本身也不伤人。
    /// 于是报错只说收什么、不再补一句「不给就是全部」,与隔壁 <c>Offset</c> 一致。
    /// 产地 tools/scan-all-token.py(分代)与 tools/scan-all-rewrite.py(重写形态)。
    /// </remarks>
    public static OptionSpec Limit(string what) => new()
    {
        Name = "limit",
        Short = 'n',
        Aliases = ["max-results", "count", "top", "rows", "num", "head"],
        Placeholder = "<n>",
        Help = $"How many {what} to return, at most. Left out, every one is returned.",
        Default = "every one",
    };

    /// <summary>翻页。措辞产地在 <see cref="Report.PageNotice"/>;每条列表命令都认它。</summary>
    /// <remarks>
    /// 一个别名都不留。原先挂着 skip / start / page-from,而这三个词在 28377 次真实调用里
    /// **逐字各 0 次**(主名 --offset 自己 5 次),产地 tools/scan-alias-spelling.py。
    ///
    /// 摘掉不是为了短:`start` 同时是 read 上写行区间的第一直觉(--start 与 --end 各
    /// 64 次 / 49 份会话,完全成对),而它被这里占着,同一个词在 CLI 里就有两个意思。
    /// 让位之后 read 才收得下它,见 ReadCommand 的 --start。
    /// </remarks>
    public static OptionSpec Offset(string what) => new()
    {
        Name = "offset",
        Placeholder = "<n>",
        Help = $"Skip this many {what} before listing. The total is always reported, so you can tell when " +
               "you have reached the end.",
        Default = "0",
    };

    public static readonly OptionSpec Scope = new()
    {
        Name = "scope",
        // **"source" 不许在这里。** code-search / read 上有一个真的 --source,它收的是
        // 反编译源码树的名字,与这里的「快照里的哪些 mod」是两回事。两边互收对方的名字时,
        // `code-search X --scope vanilla` 不报错 —— 恰好有一棵叫 vanilla 的树,于是它体面地
        // 回答了另一个问题。
        //
        // 删哪一半按用量定:Vethara 的 603 个会话、8040 次真调用里,`--source` 落在这一族上
        // **零次**,落在 code-search / read 上 256 次;`--scope` 落在这一族上 104 次,
        // 落在 code-search 上 2 次(其中一次写的是组名 races,那边没有同名树,报错了)。
        // 于是两边各删掉自己不被用的那个别名,一次真实用法都没牺牲。
        // "from" 同理:modlist save 有一个真的 --from(从哪份名单读 id)。两个别名都是零用量。
        Aliases = ["mod", "mods"],
        Placeholder = "<expr>",
        // 一词两义:这里的 'vanilla' = Ludeon 出的每一个模块(Core 加全部已装 DLC),
        // 而一份**叫** vanilla 的快照可能只有 Core。
        Help = "Restrict results to some of the mods in the snapshot. Comma-separated; a leading '-' excludes. " +
               "'all', 'vanilla', a packageId, or a group name from the config file. Writing 'all,-vanilla' " +
               "means everything except vanilla. 'vanilla' (also 'core', 'base', 'official') means every module " +
               "Ludeon ships — Core and each DLC in the snapshot — which is not the same thing as a snapshot " +
               "that happens to be named vanilla; the output spells out what it resolved to.",
        Default = ScopeFilter.DefaultScope,
        Narrows = true,
    };

    /// <summary>
    /// 后缀匹配的对侧开关。缺省那条后缀按整段对齐,但不限它前面还有几段:
    /// <c>compClass</c> 命中每一条以这一段结尾的路径,而这个旗把它钉成整条。
    /// 结果里那句「横跨几种路径形状」说的就是缺省态还剩几种,粘一条回来就收窄到一种。
    ///
    /// 这一条是**旗**,因为 <c>where</c> / <c>values</c> 的路径走位置参数,它只改匹配方式。
    /// <c>get</c> / <c>inherit</c> / <c>fields</c> 的路径走选项,那边同名的是
    /// <see cref="ExactPathFilter"/>,收值。
    /// </summary>
    public static OptionSpec ExactPath() => new()
    {
        Name = "exact-path",
        Aliases = ["whole-path", "path-exact"],
        Arity = Arity.Flag,
        // `[]` 不在这句里说 —— 它是路径文法的一部分,每个收路径的入口都认(判据在
        // SnapshotDb.PathLike)。写在这条旗底下会让人以为得开这个旗才用得上 `[]`,
        // 而那正是它此前只在一半入口生效时留下的读法。
        // 后面不跟 `graphicData.shaderType` 那个例子:`where` 的 Remarks 用的就是这一对,
        // 两句同屏。`values` 的 Remarks 确实没讲「点号不切开」这件事,但那个缺口在这条旗
        // 收窄之前就在,补它是另一件事 —— 不靠一句在 where 上重复的话去顺带盖住。
        // 正面说自己做什么,不说自己不是什么。原句「as a whole instead of as a suffix」
        // 与 get 那族的「as a whole rather than as a substring」同词不同义 —— 两边否定的
        // 是各自的默认,而读者是从一族学到另一族的,于是 whole 读成了两件事。
        // 缺省态不必在这里重复:位置参数自己的说明就写着「一条路径或它的最后一段」。
        Help = "Match the field path given as an argument end to end.",
        Narrows = true,
    };

    /// <summary>
    /// <c>--path-contains</c> 的严格那一档,收值。<paramref name="noun"/> 是本命令列的东西。
    ///
    /// **它收值而不是当旗,是实测定的。** 读者是在 <c>where</c> 上学会这个词的,而那边
    /// 路径走位置参数、这个词是旗。带到 <c>get</c> 上的 7 次调用**无一例外**写成
    /// <c>--exact-path &lt;路径&gt;</c> —— 迁移过来的是这个词,不是那个形状。写成旗的话
    /// 那 7 次仍然跑不对:路径会被当成 def 名,而输出还照印一整块。
    ///
    /// 于是同一个词在两族上 arity 不同,而这跟着**路径由谁携带**走:哪边携带路径,
    /// 这个词就长在哪边。两族的语义逐字相同 —— 整条匹配。
    /// </summary>
    /// <summary>
    /// <c>where</c> / <c>values</c> 上的 <c>--path-contains</c>。谓词与 <c>get</c> 那族
    /// **逐字同一个**(路径含这段文本),区别只在这边它与位置参数按 AND 合。
    ///
    /// 补它的理由是路径轴在这两条命令上只有一个槽,而实测 11 次伸手要的是两个正交条件:
    /// 位置参数说「结尾是什么」,这个说「上面某处有什么」。八个真实案例里六个的那段祖先
    /// **不紧邻叶子**,而且命中形状不止一种(<c>thingDefs</c> ∧ <c>filter</c> 在 baseline
    /// 上 13 种,中间几段各不相同),多段后缀要求把中间每一段都写对 —— 那恰是问的人
    /// 不知道的部分。给的那段文本还常常不是完整的一段(<c>killedLeaving</c> 要命中
    /// <c>killedLeavings[]</c> 与 <c>killedLeavingsRanges[]</c>)。
    ///
    /// **不收 "path" 这个别名**,而 get / inherit / fields 三条都收:那边没有位置参数,
    /// 这边有,且唯一一次实测的 <c>values --path</c> 要的正是位置参数(<c>values ThingDef
    /// --path projectile.speed</c>)。同一个词在这两条命令上会指向另一个槽。
    /// </summary>
    public static OptionSpec PathContainsBeside() => new()
    {
        Name = "path-contains",
        Arity = Arity.Multi,
        Aliases = ["filter", "grep"],
        Placeholder = "<text>",
        // 前半句与 get 那族逐字同源(「含这段文本的路径」),新增的只有与位置参数按 AND 合
        // 那一句 —— 那是这一族独有的、推不出来的事。
        //
        // 不写「位置参数说结尾是什么,这个说上面有什么」。上面那段实测讲的是**人怎么用它**
        // (八个案例里六个给的是祖先段),谓词本身不区分位置:`where compClass
        // --path-contains compClass` 照命中 8391 条,`--path-contains filter` 也命中
        // `fixedIngredientFilter.thingDefs[0]` —— 那段文本坐在叶子自己身上。
        // 把用法观察印成语义,读者按它去推就会推错。
        //
        // 也不写「不给位置参数时它收窄搜索面」:`values` 的路径是必填位置参数,
        // 那半句在两条命令里有一条根本到不了。同理去掉了原先那个名词参数 ——
        // 它存在的唯一理由就是给这半句填空,而 where 填 fields、values 填 paths
        // 说的还是同一样东西(字段路径)。
        Help = "Only match field paths containing this text; when a path argument is given, it has to " +
               "match as well. Repeat it to widen the selection. " + AnyIndexNote,
        Narrows = true,
    };

    public static OptionSpec ExactPathFilter() => new()
    {
        Name = "exact-path",
        Aliases = ["whole-path", "path-exact"],
        Arity = Arity.Multi,
        Placeholder = "<path>",
        // 措辞不带本命令的名词。三条命令拿这个筛选去做的事各不相同(列字段 / 列路径 /
        // 数见证者),而这条旗改的只有一件:比法。挂上名词就得三份措辞,而它们说的是同一件事。
        // 不写「与 --path-contains 同一个筛选」。它在 inherit 上是假的 —— 那边这个筛选不挑
        // 要显示的行,是拿路径去数见证者。而且那句话请人把手上的片段原样换个旗再敲一遍,
        // 那必然回 0 行:这条收的是整条路径,不是片段。**输入形状才是那 7 次失败调用缺的
        // 东西**,所以它进正文,而不是留给读者从「whole」二字里推。
        // 同上,正面陈述。输入形状那半句留着 —— 那 7 次失败调用缺的正是它,
        // 而它推不出来(「end to end」不告诉人这里该填整条还是片段)。
        Help = "Match this one field path end to end, so it takes a complete field path rather than a " +
               "fragment of one. " + AnyIndexNote,
        Narrows = true,
    };

    /// <summary>
    /// <c>[]</c> 那半句的唯一产地。每个收路径的入口都得说一遍 —— <c>--help</c> 是逐命令的,
    /// 读 <c>get --help</c> 的人看不到 <c>where</c> 的位置参数说明 —— 但只能有一种措辞。
    /// 五处各写各的话,同一件事会长出五个版本,而下一次改措辞只会改到其中几个。
    ///
    /// <c>inherit</c> 不在这五处里:它的帮助已经说「与 <c>get --path-contains</c> 是同一种匹配」,
    /// 那是一句完整的等同,再补一条「也包括 []」等于把一个全称说成部分。
    /// </summary>
    /// <remarks>
    /// 后半句不写「把本工具印给你的形状原样贴回来」—— 那是个**在四屏里有三屏不成立的处境**:
    /// 印 <c>[]</c> 形状的只有 <c>where</c>(FindPathShapes 的调用点全在它里面),
    /// <c>get</c> 印 <c>statBases[0].stat</c>、<c>fields</c> 印 <c>comps[0].props.energyMax</c>、
    /// <c>values</c> 的 matched_paths 印 <c>equippedStatOffsets[0].value</c>,全是真下标。
    /// 改成直接说这个写法匹配什么 —— 那句话对四屏都成立,而且不需要读者先做过别的调用。
    /// </remarks>
    public const string AnyIndexNote =
        "'[]' stands for any index: 'comps[].props.energyMax' matches every comps[N].props.energyMax.";

    public static readonly OptionSpec Type = new()
    {
        Name = "type",
        // "category" 不在这里:economy 有一个真的 --category(经济分类),两件事不许互收名字。
        Aliases = ["def-type", "kind"],
        Placeholder = "<DefType>",
        Help = "Restrict results to one def type, for example ThingDef or HediffDef.",
        Narrows = true,
    };
}

/// <summary>一次命令执行的上下文。命令只通过它取参、开库、写报告。</summary>
public sealed class CommandContext(RimConfig config, ParseResult args)
{
    private SnapshotDb? _db;
    private bool _snapshotNoticed;
    private bool _scopeNoticed;

    public RimConfig Config { get; } = config;
    public ParseResult Args { get; } = args;
    public Report Report { get; } = new() { Narrowing = args.Narrowing() };
    public bool Json => Args.Flag("json");
    public bool Quiet => Args.Flag("quiet");

    /// <summary>
    /// 这一次的命令行,拿掉 <paramref name="options"/> 那几个筛子之后的样子 —— 原样可贴。
    /// 位置参数照给,这条命令自己的选项凡是给了的都照给、按声明顺序排(不只 Narrows 那几个:
    /// <c>--derived</c> / <c>--member</c> 这类定问题形状的拿掉就不是同一条查询),--snapshot 跟着走;
    /// 不带的只有分页(--limit / --offset)与调用方自己选的寻址(--db,与别处的回显同一口径)。
    /// </summary>
    public string Without(params string[] options)
    {
        var parts = new List<string> { CommandRegistry.ExeName, Args.Spec.Name };
        parts.AddRange(Args.Positionals.Select(QuoteArg));
        foreach (var o in Args.Spec.Options)
        {
            if (options.Contains(o.Name, StringComparer.Ordinal) || o.Name is "limit" or "offset") continue;
            if (Args.Values(o.Name).Count > 0) parts.Add(FilterAsGiven(o.Name));
        }
        // --snapshot 是全局选项,不在 Spec.Options 里,拿掉它得单独看:「在别的快照里」那档
        // 自己接 --snapshot <别名>,原来那份跟着走就成了两个。
        if (!options.Contains("snapshot", StringComparer.Ordinal) &&
            Args.Value("snapshot") is { Length: > 0 } snap)
            parts.Add($"--snapshot {QuoteArg(snap)}");
        return string.Join(" ", parts);
    }

    /// <summary>筛子那一格的写法:<c>--type ThingDef</c> / <c>--exact-path</c>,照命令行的拼法。</summary>
    public string FilterAsGiven(string option)
    {
        // 无值开关内部存的是 "true",那不是命令行上写的样子。
        if (Args.Spec.Options.FirstOrDefault(o => string.Equals(o.Name, option, StringComparison.Ordinal))?.Arity == Arity.Flag)
            return $"--{option}";
        var given = Args.Values(option);
        return given.Count == 0 ? $"--{option}" : string.Join(" ", given.Select(v => $"--{option} {QuoteArg(v)}"));
    }

    internal static string QuoteArg(string v) => v.Length == 0 || v.Any(char.IsWhiteSpace) ? $"\"{v}\"" : v;

    /// <summary>
    /// 这次真正开库之后的快照名(别名,或路径去扩展名)。没开过库就是 null ——
    /// run-log 那时改从 <c>--snapshot</c> / <c>--db</c> 取。
    /// </summary>
    public string? SnapshotName { get; private set; }

    /// <summary>
    /// 跑很久的命令用来**当场**说一句话的地方 —— <see cref="Report"/> 攒到命令结束才渲染。
    /// 走 stderr:它不是结果,不该混进 stdout 那份有字节级闸的输出。默认丢弃。
    /// </summary>
    public TextWriter Progress { get; init; } = TextWriter.Null;

    public SnapshotDb Db
    {
        get
        {
            if (_db is not null) return _db;
            var selection = SnapshotCatalog.Resolve(Config, Args.Value("db"), Args.Value("snapshot"));
            _db = SnapshotDb.Open(selection.Path);
            AnnounceSnapshot(selection, _db);
            return _db;
        }
    }

    /// <summary><c>--limit</c> 的取值。不给就是全部,给了就照给的数来,中间没有夹板。</summary>
    public LimitValue Limit(string name = "limit") => Args.Limit(name);

    public ScopeFilter Scope()
    {
        var filter = ScopeFilter.Parse(Args.Value("scope"), Db.PackageIds(), Config);
        if (filter.UnknownTokens.Count > 0)
            throw new CliUsageException(
                $"--scope does not know {string.Join(", ", filter.UnknownTokens.Select(t => $"'{t}'"))}. " +
                // 举例子也要说清没举出来的有多少,否则「举了 8 个」与「一共就这 8 个」同形
                // (产地在 NameList)。
                $"This snapshot contains: {NameList.Render(Db.PackageIds(), 8)}" +
                ". 'rimsearcher mods' lists them all.");
        AnnounceScope(filter);
        return filter;
    }

    /// <summary>
    /// 一个 scope 词展开成了什么,在**有结果时**也要说 —— 展开口径直接决定答案怎么读
    /// (<c>vanilla</c> 展开成六个 Ludeon 模块,含 DLC)。
    ///
    /// 判据取「展开与你输入的字面不同」而不是「多于一个 mod」:后者对
    /// <c>--scope ludeon.rimworld</c> 这种写死 packageId 的调用也要发声,而你写的就是你得到的。
    /// </summary>
    private void AnnounceScope(ScopeFilter filter)
    {
        if (_scopeNoticed || filter.IsAll) return;
        var described = filter.Describe();
        if (string.Equals(described, filter.Expression, StringComparison.Ordinal)) return;
        _scopeNoticed = true;
        Report.Notice(NoticeKind.Filter, $"--scope {described}.");
    }

    /// <summary>
    /// 不带任何 mod 过滤的 scope。零结果分流要用它 —— 「这个 scope 里没有」和
    /// 「整个快照里没有」是两件事,而分不清时报后者就是把缺席说成事实。
    /// </summary>
    public ScopeFilter Unscoped() => ScopeFilter.Parse("all", Db.PackageIds(), Config);

    /// <summary>
    /// <c>--scope all,-X</c> 排除掉的那一半里,有多少也满足这次查询 —— 非零就说破。
    ///
    /// **这里的沉默推得出错结论**,与本项目别处的沉默不同:排除式的心智模型是
    /// 「我只是不想要 X」,而 X 里可能正是答案。实测
    /// <c>where compClass --value Vethara --scope all,-vanilla</c> 返回 92 个 def,
    /// 表干净、完整、看不出任何问题 —— 而问的「这个 mod 挂到哪些宿主上」那 7 个宿主
    /// 全在被排除的 vanilla 里,一个都没进表,零提示。**一张静默的错表比零结果贵得多**,
    /// 零结果至少当场知道自己没拿到东西。
    ///
    /// 判据当场算得出来:补集在 <see cref="ScopeFilter"/> 里就是 universe 减 included,
    /// 不用重新解析表达式;数一遍多跑一次查询,那是同一条 SQL 换个谓词。
    ///
    /// 计数放句尾,免得动词跟着单复数变 —— NounRegistry 管名词,不管动词。
    /// </summary>
    /// <param name="qualifier">
    /// 紧跟名词的限定语(<c>" of 'compClass'"</c>),口径与 <see cref="Tally.RenderTotalFirst"/>
    /// 的同名参数逐字相同。一次调用收几个参数的命令必须给它 —— 不给,几句挨着的
    /// 「it finds 3 values」分不出说的是哪个参数。
    /// </param>
    public void AnnounceExcluded(ScopeFilter scope, Func<ScopeFilter, int> count, string noun,
                                 string qualifier = "")
    {
        var rest = scope.Complement();
        if (rest is null) return;
        var n = count(rest);
        if (n == 0) return;
        Report.Notice(NoticeKind.Boundary,
            $"What --scope {scope.Expression} left out is not empty for this query: with " +
            $"--scope {rest.Describe()} instead, it finds {Tally.Complete(n).Render(noun, qualifier)}.");
    }

    /// <summary>
    /// 快照寻址与过期自证是同一次比对的两个产出。**正常态一个字都不说** —— 一致时发声
    /// 等于每次查询都交一次上下文税。
    /// </summary>
    private void AnnounceSnapshot(SnapshotSelection selection, SnapshotDb db)
    {
        if (_snapshotNoticed) return;
        _snapshotNoticed = true;

        var report = SnapshotCatalog.Compare(db, Config);
        var name = selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path);
        SnapshotName = name;

        // 「这次用了哪个快照」与「这个快照过没过期」是两件事。快照选错就是答案错,
        // 所以自动选择要说出选了哪个;只有一个快照时仍然零字节 —— 那时不存在选错。
        //
        // 走标签不走整句(<see cref="Report.SnapshotTag"/> 记着为什么),而 auto-detected
        // 的「这是猜的」一并进标签:`snapshot list` 的 active 列标得出**哪个**在用,标不出
        // 它是钉的还是猜的。
        //
        // 不报「还注册了哪几个」,也不指路 `snapshot list`:名单逐字重复,而那句指路
        // 在 SKILL.md 的 Snapshots 一节里已经是原文。这里只留这一次真正的选择结果。
        if (selection.Source is not (SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb))
        {
            if (SnapshotCatalog.Enumerate(Config).Count > 1)
                Report.SnapshotTag =
                    selection.Source == SelectionSource.Pinned ? name : $"{name} auto-detected";
        }

        // 一词两义,而两义在这一次调用里都活着:快照叫 vanilla,--scope vanilla 是另一回事。
        // 「显式指定就闭嘴」这条原则在这里不成立 —— 它的前提是调用方知道自己选的环境是什么,
        // 而这一格正是他以为自己知道其实不知道的。只在**撞名**时说。
        if (ScopeFilter.IsGroupName(name, Config))
        {
            var ids = Db.PackageIds();
            Report.Notice(NoticeKind.Boundary,
                $"'{name}' is both this snapshot's name and a --scope group name, and the two cover " +
                $"different things. This snapshot holds {Tally.Complete(ids.Count).Render("mod")}: " +
                $"{NameList.Render(ids, 6)}.");
        }

        // 「游戏现在多开/少开了哪些 mod」这一层**每次查询都不说**(成因见
        // EnvironmentReport.Added)。次序不是那一层:没人挑得出一个「次序变体」环境,
        // 而加载顺序决定同名 patch 谁赢 —— 于是快照里的值不是不全,是错的。
        // 这条与 VersionDrift / ContentDrift 同类,显式选择也不闭嘴。
        // 尾句不指 `snapshot status`:SKILL.md 的 Snapshots 一节把「它是那次完整比对」写死了,
        // 而这条每次查询都发,那个指路就是同一句话的第 N 份副本。留下的是后果 —— 它决定
        // 下面那个值怎么读,推不出来。
        if (report.Reordered)
            Report.Notice(NoticeKind.Staleness,
                $"The mods snapshot '{name}' describes are in a different load order in the game now. " +
                "Load order decides which patch wins, so a value below can differ from what the game resolves.");

        switch (report.Match)
        {
            case EnvironmentMatch.Same:
                return;   // 一致:除了上面那行「用的是哪个」,不再多说

            case EnvironmentMatch.VersionDrift:
                // 「下面的值出自旧那一版」是前半句的同义反复 —— 两个版本号已经把它说完了。
                Report.Notice(NoticeKind.Staleness,
                    $"Snapshot '{name}' was exported from game version {db.Meta.GameVersion}, " +
                    $"but the game is now on {report.GameVersion}. Re-export to refresh.");
                return;

            // 显式选择在这一格**不闭嘴**:`--snapshot modded` 声明的是「我要查那个环境」,
            // 不是「我知道那些 mod 的 XML 已经不是导出时那份了」。与 VersionDrift 同类。
            //
            // 但位置让结果去定(见 Report.DeferredNotice):这一条与版本漂移不同,它点着
            // 具体几个 mod,而绝大多数查询与那几个无关 —— 每次都占着表头第一行,读到第五遍
            // 就把表头整块训练成盲区,而表头恰恰是 scope 展开、精确/包含拆分这些真会改变
            // 答案的东西所在。无关时沉到表下,点到了(或证不出无关)照旧在最上面。
            case EnvironmentMatch.ContentDrift:
                // 2026-09-18 起是表不是句子:r19 量到弱档把句子里举例的 mod 名当成 def 去 get;
                // 表的 next 列是一条能粘的命令。与 snapshot status 的 xml 表同形。
                Report.DeferredTable(ContentDrift.Table, ContentDrift.Columns,
                    ContentDrift.Rows(name, report.Content!),
                    [.. report.Content!.Changed, .. report.Content.Missing]);
                return;

            case EnvironmentMatch.Unknown:
                // 读不到 ModsConfig.xml 是常态噪声,不在每次查询里发声,详情分流到 snapshot status。
                return;
        }
    }

    public void Dispose() => _db?.Dispose();
}

/// <summary>
/// 「参考侧 XML 变了」的**唯一产地**:一张 <c>xml</c> 表(package_id / state / next),
/// 日常查询与 <c>snapshot status</c> 印同一张;status 上面多一句总账(<see cref="Sentence"/>)。
/// 两处各写一遍的话,详略不同会被读成两件不同的事。
/// </summary>
public static class ContentDrift
{
    public const string Table = "xml";
    public static readonly string[] Columns = ["package_id", "state", "next"];

    /// <summary>state 列的封闭词表:文件变了 / mod 从磁盘上没了。两者的下一步不同。</summary>
    public const string Changed = "changed";
    public const string Missing = "missing";

    /// <summary>
    /// 每个 mod 一行。<c>changed</c> 的 next 是重导;<c>missing</c> 没有 CLI 能做的下一步
    /// (得先把 mod 装回来),那一格空着。
    ///
    /// 措辞刻意说 "changed",不说 "edited":判据是长度与 mtime,而 Steam 重下一份逐字节
    /// 相同的文件也会让它响(<see cref="Snapshot.ContentFingerprint"/>)。
    /// </summary>
    public static List<IReadOnlyDictionary<string, object?>> Rows(string snapshot, ContentComparison content)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var id in content.Changed)
            rows.Add(new Dictionary<string, object?>
            {
                ["package_id"] = id,
                ["state"] = Changed,
                ["next"] = Snapshot.DataLayers.ExportCommand(snapshot),
            });
        foreach (var id in content.Missing)
            rows.Add(new Dictionary<string, object?>
            {
                ["package_id"] = id,
                ["state"] = Missing,
                ["next"] = null,
            });
        return rows;
    }

    /// <summary>
    /// <c>snapshot status</c> 表上那句总账:只有数,名单在表里。「读到的是旧文件」「重导」
    /// 「没法比」这些后果与出路此前都在句子里,现在由 state / next 两列承担。
    /// </summary>
    public static string Sentence(ContentComparison content)
    {
        var parts = new List<string>();

        if (content.Changed.Count > 0)
            parts.Add($"{Tally.Complete(content.Changed.Count).Render("mod")} " +
                      $"{(content.Changed.Count == 1 ? "has" : "have")} Defs or Patches XML that changed on disk " +
                      "since the export.");

        if (content.Missing.Count > 0)
            parts.Add($"{Tally.Complete(content.Missing.Count).Render("mod")} the export read " +
                      "cannot be found on disk now.");

        return string.Join(" ", parts);
    }
}

/// <summary>
/// 「导出器半路停了,字段没发全」这半句的**唯一产地**。<c>get</c> / <c>snapshot status</c> /
/// <c>snapshot import</c> 三处都念它,此前各写各的,后两句已经逐字相同却仍是两份。
///
/// **只统一事实,不统一后果。** 三处的读者站的位置不一样:一个正看着这个 def 的字段表
/// (「下面这张表」),一个还没查任何 def(「以后 get 的时候」),一个刚导完(「工具将来会提醒你」)。
/// 把后果也压成一句,三处就都得说得含糊,而后果恰恰是这条声明存在的理由。
/// </summary>
public static class ExportCap
{
    /// <summary>
    /// 某一个 def 上丢了几个字段。数是**下界** —— 导出器是停下来了,不是数完了
    /// (见 <c>snapshot truncated</c> 那一侧的同一条注解)。
    /// </summary>
    public static string OnDef(int fields)
        => $"{Head(fields)} for depth or size";

    /// <summary>
    /// 「至少 N 个字段被丢了」这半句。谓语跟着数走 —— 语料里那个 def 丢了 3 个,
    /// 于是 were 一直是对的;真快照上 27 个被截的 def 里 24 个只丢了 1 个。
    /// </summary>
    private static string Head(int fields)
        => $"{Tally.AtLeast(fields).Render("field")} {(fields == 1 ? "was" : "were")} dropped at export time";

    /// <summary>
    /// 同上,但说得出**是哪一种**上限。<paramref name="by"/> 为 null = 这份库没分类过
    /// (0.13.0 之前导的),退回上面那句含糊的「depth or size」——四类都印成零会把
    /// 「没量过」说成「量过了、四类都没发生」,而后者是句假话。
    ///
    /// 分类不是装饰:四种截断的出路完全不同(深度要放开、条数上限要抬、值长度是展示
    /// 取舍、集合是宽度问题),而合在一个数里连本项目自己都把大头连猜错两次。
    ///
    /// **每一类各带自己的名词**,不共用「N fields were dropped」那个头 —— 四个数
    /// 数的不是同一样东西:值长度那一类一条路径都没丢(那一格就在表里,只是值不全),
    /// 深度与集合各是「一整棵没走的子树 / 一条没走完的列表」算一。共用一个名词时,
    /// 现在盘上七个库里最大的那一类(值长度)会被读成「丢了 29 个字段」,而正确的
    /// 读法是「29 个字段的值不全」。
    /// </summary>
    public static string OnDef(TruncationCauses? by, int fields)
    {
        if (by is null || by.Total == 0) return OnDef(fields);
        return string.Join("; ", by.Ranked().Select(p => Phrase(p.Cause, p.Count)));
    }

    private static string Phrase(TruncationCause cause, int n)
    {
        var was = n == 1 ? "was" : "were";
        return cause switch
        {
            // 这一类是真的「丢了几条」:上限一到,此后每碰一格加一。
            TruncationCause.Cap => $"{Tally.Complete(n).Render("field")} {was} dropped past this def's field cap",
            // 路径在表里,值不全 —— 这一类不许说成 dropped。
            TruncationCause.Length => $"{Tally.Complete(n).Render("value")} {was} cut to the length cap",
            // 一棵子树算一,底下有多少条没数过。
            TruncationCause.Depth =>
                $"{Tally.Complete(n).Render("nested object")} {was} left unwalked past the depth cap",
            TruncationCause.Items => $"{Tally.Complete(n).Render("list")} stopped at the item cap",
            _ => throw new ArgumentOutOfRangeException(nameof(cause)),
        };
    }

    /// <summary>
    /// 整份库(或一次比较的两侧)里被截过的 def 数,分成两格:真少了路径的,与一条路径没少、
    /// 只是值不全的。<paramref name="spread"/> 为 null = 这份库没分类过,那时只有一格总数。
    ///
    /// 分开是因为那个总数**盖着两件相反的事**:七个官方快照上「只切了值」占 27 个里的 22 个,
    /// 而按总数说出去的「这些 def 缺了路径」对那 22 个是假的 —— 读者据此去找的行本来就在表里。
    /// 两拨在每条命令上的读法不一样,读法住各命令的 help;这里只有数(Docs/25 丁1)。
    /// 此前是一句「N defs … lost field paths … Another M kept every field path …」,三处各拼。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> DroppedDefs(SnapshotDb.TruncationSpread? spread, int defs)
        => defs == 0 ? []   // 一个都没截过就一格也不出:分过类的库上 0 与「没分过类」同形,不许印成后者
         : spread is { } s
            ? [new(DefsWithPathsDropped, s.LostPaths), new(DefsWithValuesCut, s.ValuesOnly)]
            : [new(DefsWithFieldsDropped, defs)];

    public const string DefsWithPathsDropped = "defs_with_paths_dropped";
    public const string DefsWithValuesCut = "defs_with_values_cut";
    /// <summary>没分过类的库上那一格:两拨合在一个数里,分不开。</summary>
    public const string DefsWithFieldsDropped = "defs_with_fields_dropped";
}

public abstract class Command
{
    public abstract CommandSpec Spec { get; }
    public abstract int Run(CommandContext ctx);
}
