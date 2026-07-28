using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RimSearcher.Core;

public record DefLocation(string FilePath, string DefType, string DefName, string? ParentName, string? Label, bool IsAbstract = false);

// 一次按名查 def 的结果。同名 def 可能散在多个源里，故除了选中项还要带上
// 「scope 内还有几条同名」与「scope 外哪些源也有同名」，供调用方提示换 scope。
public sealed class DefLookup
{
    public static readonly DefLookup NotFound = new(null, 0, Array.Empty<string>());

    public DefLookup(
        DefLocation? location,
        int inScopeCount,
        IReadOnlyList<string> otherSources,
        IReadOnlyList<string>? inScopeDefTypes = null,
        bool requestedDefTypeUnavailable = false,
        int sameDefTypeCount = 0)
    {
        Location = location;
        InScopeCount = inScopeCount;
        OtherSources = otherSources;
        InScopeDefTypes = inScopeDefTypes ?? Array.Empty<string>();
        RequestedDefTypeUnavailable = requestedDefTypeUnavailable;
        SameDefTypeCount = sameDefTypeCount;
    }

    public DefLocation? Location { get; }
    public int InScopeCount { get; }
    public IReadOnlyList<string> OtherSources { get; }

    // scope 内全部同名 def 的类型（含选中的那个）。「Human 有三条」这句话本身没有可操作性，
    // 而「ThingDef / BodyDef / HediffGiverSetDef 各一条，现在给的是后者」才让调用方看得出
    // 自己要的是不是这一条。
    public IReadOnlyList<string> InScopeDefTypes { get; }

    // 调用方点名了一种 defType，而 scope 内这个名字下没有那一种。返回的是别的类型那条，
    // 不说一句的话它会被当成「就是我要的那种」。
    public bool RequestedDefTypeUnavailable { get; }

    // 与选中那条同 DefType 的 scope 内条数（含它自己）。>1 就意味着 defType 分不开这几条，
    // 此时「pass defType to pick another」是条死路指令：照做拿回来的是逐字相同的结果。
    public int SameDefTypeCount { get; }

    public bool Found => Location != null;

    // scope 内就有重名：选了一个，另几个只能靠更窄的 scope 才看得到
    public bool AmbiguousInScope => InScopeCount > 1;

    // 只在别的源里存在——这正是「按 scope 查报找不到但东西其实在」的场景
    public bool ExistsOnlyElsewhere => Location == null && OtherSources.Count > 0;
}

public class DefIndexer
{
    private static readonly Regex WordSplitRegex = new(@"\W+", RegexOptions.Compiled);

    // 字段内容索引的建键下限。低于它的词从不进索引，故用它查内容命中恒为空——而「没查」与
    // 「没有」在返回里此前逐字同形（第十二轮盲测：`Plants_Wild.xml` 里实打实有六处
    // `<li>20</li>`，`locate('20')` 却连 Content Matches 这个段头都不出现）。展示层要据此
    // 自报盲区，故这个数必须是公开常量而不是散落三处的字面 3。
    public const int MinContentTokenLength = 3;

    // 单值字典会让同名 def 后写覆盖先写（实测参考 mod 与 vanilla 有 40+ 处 defName 重名，
    // 多为 TraitDef）。覆盖之后按 scope 查就会「vanilla 里明明有却报找不到」，且父链解析
    // 会顺着别的 mod 的同名 def 往上走。故一名多值，由 scope 决定取哪个。
    private readonly ConcurrentDictionary<string, ConcurrentBag<DefLocation>> _defNameIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentBag<DefLocation>> _parentNameIndex = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentBag<DefLocation>> _labelIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentBag<(DefLocation Location, string FieldPath)>> _fieldContentIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _processedFiles = new(StringComparer.OrdinalIgnoreCase);
    // IgnoreWhitespace 不是性能开关，是 inspect def 模式的排版前提。留着纯空白文本节点，
    // XElement.ToString() 就不再重排缩进，于是**同一份**合并 XML 里两种坏形态并存：
    // 从文件里搬来的分支带着原文缩进与空行（vanilla 的 BodyDef Human 合并后 609 行里近半是
    // 空行），而合并时新插入的节点没有空白节点、被整排挤进一行（ThingDef Human 有一行 968
    // 字符）。要害不在体积——空行只值一个换行符——而在 inspect 的截断与 xmlStartLine 续读
    // **以行为单位**：一行可能是一个字段也可能是三十个，「首屏 200 行」到底给了多少内容
    // 随源文件的排版浮动，调用方无从判断。丢掉纯空白节点后整棵树由 XLinq 统一缩进，
    // 行重新成为一个稳定的量。
    //
    // 只有 whitespace-only 的文本节点会被丢弃，<description> 那种真文本一个字不动。
    private static readonly XmlReaderSettings XmlReadSettings = new()
    {
        DtdProcessing = DtdProcessing.Parse,
        IgnoreWhitespace = true
    };
    
    private FrozenDictionary<string, DefLocation[]>? _frozenDefNameIndex;
    private FrozenDictionary<string, DefLocation[]>? _frozenParentNameIndex;
    private FrozenDictionary<string, DefLocation[]>? _frozenLabelIndex;
    private FrozenDictionary<string, (DefLocation Location, string FieldPath)[]>? _frozenFieldContentIndex;

    // 同 SourceIndexer.IndexedFileCount：判定索引是否为空，据此决定要不要在工具输出里
    // 声明「这次的『没找到』不可信」
    public int IndexedFileCount => _processedFiles.Count;

    public void FreezeIndex()
    {
        _frozenDefNameIndex = _defNameIndex.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenParentNameIndex = _parentNameIndex.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenLabelIndex = _labelIndex.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenFieldContentIndex = _fieldContentIndex.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public DefIndexerSnapshot ExportSnapshot()
    {
        var labelIndex = _frozenLabelIndex != null
            ? _frozenLabelIndex.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _labelIndex.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, DefFieldContentSnapshot[]> fieldContentIndex;
        if (_frozenFieldContentIndex != null)
        {
            fieldContentIndex = _frozenFieldContentIndex.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(entry => new DefFieldContentSnapshot
                {
                    Location = entry.Location,
                    FieldPath = entry.FieldPath
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            fieldContentIndex = _fieldContentIndex.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Distinct().Select(entry => new DefFieldContentSnapshot
                {
                    Location = entry.Location,
                    FieldPath = entry.FieldPath
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        var defNameIndex = _frozenDefNameIndex != null
            ? _frozenDefNameIndex.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _defNameIndex.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        var parentNameIndex = _frozenParentNameIndex != null
            ? _frozenParentNameIndex.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _parentNameIndex.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        return new DefIndexerSnapshot
        {
            DefNameIndex = defNameIndex,
            ParentNameIndex = parentNameIndex,
            LabelIndex = labelIndex,
            FieldContentIndex = fieldContentIndex,
            ProcessedFiles = _processedFiles.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    // 就地清空以便重扫，见 SourceIndexer.Clear
    public void Clear()
    {
        _defNameIndex.Clear();
        _parentNameIndex.Clear();
        _labelIndex.Clear();
        _fieldContentIndex.Clear();
        _processedFiles.Clear();
        ResetFrozenState();
    }

    public void ImportSnapshot(DefIndexerSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        Clear();

        foreach (var (key, values) in snapshot.DefNameIndex)
        {
            _defNameIndex[key] = new ConcurrentBag<DefLocation>(values.Distinct());
        }

        foreach (var (key, values) in snapshot.ParentNameIndex)
        {
            _parentNameIndex[key] = new ConcurrentBag<DefLocation>(values.Distinct());
        }

        foreach (var (key, values) in snapshot.LabelIndex)
        {
            var deduped = values.Distinct().ToArray();
            _labelIndex[key] = new ConcurrentBag<DefLocation>(deduped);
        }

        foreach (var (key, values) in snapshot.FieldContentIndex)
        {
            var deduped = values
                .Select(entry => (entry.Location, entry.FieldPath))
                .Distinct()
                .ToArray();
            _fieldContentIndex[key] = new ConcurrentBag<(DefLocation Location, string FieldPath)>(deduped);
        }

        foreach (var file in snapshot.ProcessedFiles.Where(file => !string.IsNullOrWhiteSpace(file)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _processedFiles[file] = 0;
        }
    }

    // excludedFiles：被 mod 的高优先级同名文件顶掉、游戏根本不解析的那些 xml（绝对路径）。
    // 见 ModLayoutResolver——收了它们等于把运行时不生效的旧定义摆进搜索结果。
    public void Scan(string rootPath, IReadOnlySet<string>? excludedFiles = null)
    {
        if (!Directory.Exists(rootPath)) return;
        var blacklistedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".git", ".vs", ".idea", ".build", "temp" };

        var allFiles = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var currentPath = stack.Pop();
            try
            {
                foreach (var file in Directory.GetFiles(currentPath, "*.xml")) allFiles.Add(file);
                foreach (var dir in Directory.GetDirectories(currentPath))
                {
                    if (!blacklistedDirs.Contains(Path.GetFileName(dir))) stack.Push(dir);
                }
            }
            catch { }
        }

        var newFiles = ScanFilter.SelectNew(allFiles, excludedFiles, full => _processedFiles.TryAdd(full, 0));
        int totalParsed = 0;

        Parallel.ForEach(newFiles, file =>
        {
            var internedFile = string.Intern(file);
            
            try
            {
                var doc = GetOrLoadDocument(internedFile);
                if (doc.Root == null || doc.Root.Name.LocalName != "Defs") return;

                int nodeIdx = 0;
                foreach (var defElement in doc.Root.Elements())
                {
                    nodeIdx++;
                    string defType = defElement.Name.LocalName;
                    string? nameAttr = defElement.Attribute("Name")?.Value;
                    string? parentNameAttr = defElement.Attribute("ParentName")?.Value;
                    string? abstractAttr = defElement.Attribute("Abstract")?.Value;
                    bool isAbstract = string.Equals(abstractAttr, "true", StringComparison.OrdinalIgnoreCase);

                    string? defName = defElement.Element("defName")?.Value;
                    string? label = defElement.Element("label")?.Value;

                    string identifier = defName ?? nameAttr ?? $"[Unnamed_{defType}_{nodeIdx}]";
                    var loc = new DefLocation(internedFile, defType, identifier, parentNameAttr, label, isAbstract);

                    if (!string.IsNullOrEmpty(defName))
                        _defNameIndex.GetOrAdd(defName, _ => new ConcurrentBag<DefLocation>()).Add(loc);
                    if (!string.IsNullOrEmpty(nameAttr))
                        _parentNameIndex.GetOrAdd(nameAttr, _ => new ConcurrentBag<DefLocation>()).Add(loc);
                    if (!string.IsNullOrEmpty(label))
                    {
                        _labelIndex.GetOrAdd(label, _ => new ConcurrentBag<DefLocation>()).Add(loc);
                    }

                    IndexElementRecursive(defElement, loc, "", 0);

                    Interlocked.Increment(ref totalParsed);
                }
            }
            catch { }
        });

    }

    public XDocument GetOrLoadDocument(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = XmlReader.Create(stream, XmlReadSettings);
        return XDocument.Load(reader);
    }

    private void IndexElementRecursive(XElement element, DefLocation location, string pathPrefix, int depth = 0)
    {
        if (depth >= 3) return;
        
        var currentPath = string.IsNullOrEmpty(pathPrefix)
            ? element.Name.LocalName
            : $"{pathPrefix}.{element.Name.LocalName}";

        var elementName = element.Name.LocalName;
        if (elementName.Length >= MinContentTokenLength)
        {
            _fieldContentIndex.GetOrAdd(elementName.ToLowerInvariant(), _ => new ConcurrentBag<(DefLocation, string)>())
                .Add((location, currentPath));
        }

        if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
        {
            var value = element.Value.Trim();
            var words = WordSplitRegex.Split(value)
                .Where(w => w.Length >= MinContentTokenLength)
                .Select(w => w.ToLowerInvariant())
                .Distinct();

            foreach (var word in words)
            {
                _fieldContentIndex.GetOrAdd(word, _ => new ConcurrentBag<(DefLocation, string)>())
                    .Add((location, currentPath));
            }
        }

        foreach (var child in element.Elements())
        {
            IndexElementRecursive(child, location, currentPath, depth + 1);
        }
    }

    // 三档来源的权重原先是 1.2 / 1.0 / 0.8。CalculateFuzzyScore 的值域是 0~100（精确命中 100），
    // 乘 1.2 之后精确命中的 def 算出 120，被 LocateTool 的 ({Score:F0}%) 渲染成 "120%" ——
    // 一个越界的百分比，读的人会以为分数体系另有量纲。
    // 这里按原比例整体除以 1.2 归一化，而不是单删 defName 那档的 1.2：单删会让 defName 与
    // ParentName 同权，丢掉「同样的模糊分下 defName 命中优先」这条本来有效的组内排序规则。
    // 归一化保持三者比值不变，故组内相对次序与改前完全一致，只是分数落回 0~100。
    private const double DefNameWeight = 1.0;
    private const double ParentNameWeight = 1.0 / 1.2;
    private const double LabelWeight = 0.8 / 1.2;

    // 抽象 def 是模板不是可用条目，压一半让具体 def 排在前面；这个因子是有排序意义的，
    // 与上面被归一化掉的均匀乘子不同，不能一并去掉。
    private static double AbstractPenalty(DefLocation location) => location.IsAbstract ? 0.5 : 1.0;

    public ScopedResult<DefLocation> FuzzySearch(string query, ScopeSelection scope, int limit = 50)
    {
        var defNameSource = ExpandLocations(_frozenDefNameIndex, _defNameIndex);
        var parentNameSource = ExpandLocations(_frozenParentNameIndex, _parentNameIndex);
        var labelSource = _frozenLabelIndex != null
            ? _frozenLabelIndex.SelectMany(kv => kv.Value.Select(loc => (Key: kv.Key, Location: loc)))
            : _labelIndex.SelectMany(kv => kv.Value.Select(loc => (Key: kv.Key, Location: loc)));

        var candidates = defNameSource
            .Select(entry => new
            {
                Loc = entry.Location,
                Score = FuzzyMatcher.CalculateFuzzyScore(entry.Key, query) * DefNameWeight * AbstractPenalty(entry.Location)
            })
            .Concat(parentNameSource.Select(entry => new
            {
                Loc = entry.Location,
                Score = FuzzyMatcher.CalculateFuzzyScore(entry.Key, query) * ParentNameWeight * AbstractPenalty(entry.Location)
            }))
            .Concat(labelSource.Select(entry => new
            {
                Loc = entry.Location,
                Score = FuzzyMatcher.CalculateFuzzyScore(entry.Key, query) * LabelWeight * AbstractPenalty(entry.Location)
            }))
            .Where(x => x.Score > 0)
            // 同名 def 现在可能来自多个源，分组键要带文件路径，否则跨源的同名条目会被合成一条
            .GroupBy(x => $"{x.Loc.DefType}/{x.Loc.DefName}@{x.Loc.FilePath}")
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Loc.DefName.Length)
            // 末级定序，判据同 SourceIndexer 的成员搜索：`Vethara_Head_0` 与 `Vethara_Head_3`
            // 同分同长，谁进前十全看 def 索引的写入顺序
            .ThenBy(x => x.Loc.DefName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Loc.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ScoredCandidate<DefLocation>(x.Loc, x.Score, x.Loc.FilePath));

        return ScopeFilter.Apply(candidates, scope, limit);
    }

    private static IEnumerable<(string Key, DefLocation Location)> ExpandLocations(
        FrozenDictionary<string, DefLocation[]>? frozen,
        ConcurrentDictionary<string, ConcurrentBag<DefLocation>> live)
    {
        if (frozen != null)
            return frozen.SelectMany(kv => kv.Value.Select(loc => (kv.Key, loc)));

        return live.SelectMany(kv => kv.Value.Distinct().Select(loc => (kv.Key, loc)));
    }

    public ScopedResult<(DefLocation Location, List<string> MatchedFields)> SearchByContent(
        string[] keywords,
        ScopeSelection scope,
        int limit = 30)
    {
        if (keywords == null || keywords.Length == 0)
            return ScopedResult<(DefLocation, List<string>)>.Empty;

        var matchedDefs = new Dictionary<string, (DefLocation Location, HashSet<string> FieldPaths, int MatchCount)>();

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < MinContentTokenLength)
                continue;

            var keyLower = keyword.ToLowerInvariant();

            IEnumerable<(DefLocation Location, string FieldPath)>? matches = null;
            if (_frozenFieldContentIndex != null && _frozenFieldContentIndex.TryGetValue(keyLower, out var frozenMatches))
                matches = frozenMatches;
            else if (_fieldContentIndex.TryGetValue(keyLower, out var bagMatches))
                matches = bagMatches;

            if (matches != null)
            {
                foreach (var (location, fieldPath) in matches)
                {
                    var defKey = $"{location.DefType}/{location.DefName}@{location.FilePath}";

                    if (!matchedDefs.TryGetValue(defKey, out var existing))
                    {
                        existing = (location, new HashSet<string>(), 0);
                        matchedDefs[defKey] = existing;
                    }

                    existing.FieldPaths.Add(fieldPath);
                    existing.MatchCount++;
                    matchedDefs[defKey] = existing;
                }
            }
        }

        var candidates = matchedDefs.Values
            .OrderByDescending(x => x.MatchCount)
            .ThenBy(x => x.Location.DefName.Length)
            .ThenBy(x => x.Location.DefName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Location.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ScoredCandidate<(DefLocation, List<string>)>(
                (x.Location, x.FieldPaths.ToList()), x.MatchCount, x.Location.FilePath));

        // 排序键是关键词命中计数而非 0~100 的相似度，断层阈值在这个量纲上没有意义
        return ScopeFilter.Apply(candidates, scope, limit, scoreGap: null);
    }

    // preferSameSourceAs：解析父链时传子 def 的文件路径，让 Milira 的 def 优先接上 Milira 自己的
    // 抽象基，而不是撞名的 vanilla 同名 def。
    // defType：调用方指定要哪一种同名 def（'ThingDef'）。指定了却没有那一种时不报空——
    // 仍给出选中的那条，由 RequestedDefTypeUnavailable 让上层说清「你要的那种没有」。
    public DefLookup Lookup(
        string name, ScopeSelection scope, string? preferSameSourceAs = null, string? defType = null)
    {
        var byDefName = GetLocations(_frozenDefNameIndex, _defNameIndex, name);
        var byParentName = GetLocations(_frozenParentNameIndex, _parentNameIndex, name);

        var all = byDefName.Concat(byParentName).Distinct().ToList();
        if (all.Count == 0) return DefLookup.NotFound;

        var inScope = all
            .Select(loc => (Loc: loc, Rank: scope.RankOf(loc.FilePath)))
            .Where(x => x.Rank >= 0)
            .ToList();

        if (inScope.Count == 0)
        {
            var otherSources = all
                .Select(loc => scope.OutOfScopeLabel(loc.FilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DefLookup(null, 0, otherSources);
        }

        var preferredSource = preferSameSourceAs != null ? scope.SourceNameOf(preferSameSourceAs) : null;

        // 指定了 defType 就只在那一种里挑；一种都没有时保持原样，交给上层解释。
        // 收窄只影响「挑哪一条」，计数与类型清单仍按 scope 内的全部同名 def 算——
        // 那两个数回答的是「这个名字在 scope 内有几条、都是些什么」，与本次挑法无关。
        var candidates = inScope;
        var requestedDefTypeUnavailable = false;
        if (!string.IsNullOrWhiteSpace(defType))
        {
            var narrowed = inScope
                .Where(x => string.Equals(x.Loc.DefType, defType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (narrowed.Count > 0) candidates = narrowed;
            else requestedDefTypeUnavailable = true;
        }

        // 同一个源里的同名 def（Human 就是 ThingDef / BodyDef / HediffGiverSetDef 各一条）
        // 排到这里 Rank 完全相同，而 OrderBy 是稳定排序——胜负于是落在 `all` 的顺序上，
        // 那份顺序来自并发扫描写入的 ConcurrentBag（或快照里的数组）。结果是重建一次索引
        // inspect('Human') 就换一条 def 返回，同一个问题在不同时刻给不同答案。
        // 补一组与索引构建过程无关的确定性键：路径、类型、名字。
        var best = candidates
            .OrderByDescending(x => preferredSource != null
                && string.Equals(scope.SourceNameOf(x.Loc.FilePath), preferredSource, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Rank)
            .ThenBy(x => x.Loc.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Loc.DefType, StringComparer.Ordinal)
            .ThenBy(x => x.Loc.DefName, StringComparer.Ordinal)
            .First().Loc;

        var outOfScopeSources = all
            .Where(loc => scope.RankOf(loc.FilePath) < 0)
            .Select(loc => scope.OutOfScopeLabel(loc.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inScopeDefTypes = inScope
            .Select(x => x.Loc.DefType)
            .Where(type => !string.IsNullOrEmpty(type))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        var sameDefTypeCount = inScope.Count(x =>
            string.Equals(x.Loc.DefType, best.DefType, StringComparison.OrdinalIgnoreCase));

        return new DefLookup(
            best, inScope.Count, outOfScopeSources, inScopeDefTypes, requestedDefTypeUnavailable, sameDefTypeCount);
    }

    private static IReadOnlyList<DefLocation> GetLocations(
        FrozenDictionary<string, DefLocation[]>? frozen,
        ConcurrentDictionary<string, ConcurrentBag<DefLocation>> live,
        string name)
    {
        if (frozen != null)
            return frozen.TryGetValue(name, out var frozenValues) ? frozenValues : Array.Empty<DefLocation>();

        return live.TryGetValue(name, out var bag) ? bag.Distinct().ToArray() : Array.Empty<DefLocation>();
    }

    private void ResetFrozenState()
    {
        _frozenDefNameIndex = null;
        _frozenParentNameIndex = null;
        _frozenLabelIndex = null;
        _frozenFieldContentIndex = null;
    }
}
