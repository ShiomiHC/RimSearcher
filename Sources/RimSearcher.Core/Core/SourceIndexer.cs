using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace RimSearcher.Core;

// 一次正则扫描里「命中集为什么可能不完整」的全部成因。展示层据此决定尾注怎么写：
// 本工具对调用方的契约是「没有尾注即完整」，所以任何一项非零都必须说出来。
public readonly record struct RegexScanDiagnostics(
    int CandidateFiles,
    int TimedOutFiles,
    int UnreadableFiles,
    int LineCappedFiles,
    int LineCap)
{
    public bool AnyFileIncomplete => TimedOutFiles > 0 || UnreadableFiles > 0 || LineCappedFiles > 0;
}

public class SourceIndexer
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _typeMap = new(StringComparer.OrdinalIgnoreCase);
    // 类型 → 主基类，一对一。只用来向上走链路（GetInheritanceChain），故只存一条边。
    // 「主基类」是按命名猜的启发式，判定规则与代价见 RoslynHelper.GetPrimaryBaseType。
    private readonly ConcurrentDictionary<string, string> _inheritanceMap = new(StringComparer.OrdinalIgnoreCase);

    // 超类型 → 直接派生/实现它的类型，一对多，收基类型列表的全集（接口也在内）。
    // GetInheritors 查的是这一份：按接口找实现是本工具的主要用途之一，只记第一项会漏。
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _inheritorsMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _shortTypeMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentBag<(string TypeName, string MemberName, string MemberType, string FilePath)>> _memberIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _ngramIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _processedFiles = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _cachedAllTypeNames = new();
    
    private FrozenDictionary<string, string[]>? _frozenIndex;
    private FrozenDictionary<string, string[]>? _frozenTypeMap;
    private FrozenDictionary<string, string[]>? _frozenInheritorsMap;
    private FrozenDictionary<string, string[]>? _frozenShortTypeMap;
    private FrozenDictionary<string, string[]>? _frozenNgramIndex;
    private FrozenDictionary<string, (string TypeName, string MemberName, string MemberType, string FilePath)[]>? _frozenMemberIndex;
    
    // 索引里实际收进来了多少个文件。启动后用来判定「索引是空的」——空索引下每一条
    // 「没找到」都是不可信的，调用方必须被告知，否则会把它读成「这东西不存在」。
    public int IndexedFileCount => _processedFiles.Count;

    public void FreezeIndex()
    {
        _frozenIndex = _index.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenTypeMap = _typeMap.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenInheritorsMap = _inheritorsMap.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenShortTypeMap = _shortTypeMap.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenNgramIndex = _ngramIndex.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        _frozenMemberIndex = _memberIndex.ToFrozenDictionary(
            kv => kv.Key, 
            kv => kv.Value.Distinct().ToArray(), 
            StringComparer.OrdinalIgnoreCase);
        
        _cachedAllTypeNames = _frozenTypeMap.Keys.Concat(_frozenShortTypeMap.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SourceIndexerSnapshot ExportSnapshot()
    {
        var fileIndex = _frozenIndex != null
            ? _frozenIndex.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _index.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        var typeMap = _frozenTypeMap != null
            ? _frozenTypeMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _typeMap.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        var inheritorsMap = _frozenInheritorsMap != null
            ? _frozenInheritorsMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _inheritorsMap.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        var shortTypeMap = _frozenShortTypeMap != null
            ? _frozenShortTypeMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _shortTypeMap.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        var ngramIndex = _frozenNgramIndex != null
            ? _frozenNgramIndex.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : _ngramIndex.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, SourceMemberSnapshot[]> memberIndex;
        if (_frozenMemberIndex != null)
        {
            memberIndex = _frozenMemberIndex.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(member => new SourceMemberSnapshot
                {
                    TypeName = member.TypeName,
                    MemberName = member.MemberName,
                    MemberType = member.MemberType,
                    FilePath = member.FilePath
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            memberIndex = _memberIndex.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Distinct().Select(member => new SourceMemberSnapshot
                {
                    TypeName = member.TypeName,
                    MemberName = member.MemberName,
                    MemberType = member.MemberType,
                    FilePath = member.FilePath
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        var processedFiles = _processedFiles.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new SourceIndexerSnapshot
        {
            FileIndex = fileIndex,
            TypeMap = typeMap,
            InheritanceMap = new Dictionary<string, string>(_inheritanceMap, StringComparer.OrdinalIgnoreCase),
            InheritorsMap = inheritorsMap,
            ShortTypeMap = shortTypeMap,
            MemberIndex = memberIndex,
            NgramIndex = ngramIndex,
            ProcessedFiles = processedFiles
        };
    }

    // 就地清空以便重扫。索引对象本身不换，故持有它的 tool 无需感知重建。
    public void Clear()
    {
        _index.Clear();
        _typeMap.Clear();
        _inheritanceMap.Clear();
        _inheritorsMap.Clear();
        _shortTypeMap.Clear();
        _memberIndex.Clear();
        _ngramIndex.Clear();
        _processedFiles.Clear();
        _cachedAllTypeNames = new List<string>();
        ResetFrozenState();
    }

    public void ImportSnapshot(SourceIndexerSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        Clear();

        foreach (var (key, values) in snapshot.FileIndex)
        {
            _index[key] = ToStringBag(values);
        }

        foreach (var (key, values) in snapshot.TypeMap)
        {
            _typeMap[key] = ToStringBag(values);
        }

        foreach (var (key, value) in snapshot.InheritanceMap)
        {
            _inheritanceMap[key] = value;
        }

        foreach (var (key, values) in snapshot.InheritorsMap)
        {
            _inheritorsMap[key] = ToStringBag(values);
        }

        foreach (var (key, values) in snapshot.ShortTypeMap)
        {
            _shortTypeMap[key] = ToStringBag(values);
        }

        foreach (var (key, values) in snapshot.MemberIndex)
        {
            var entries = values
                .Select(member => (member.TypeName, member.MemberName, member.MemberType, member.FilePath))
                .Distinct()
                .ToArray();
            _memberIndex[key] = new ConcurrentBag<(string TypeName, string MemberName, string MemberType, string FilePath)>(entries);
        }

        foreach (var (key, values) in snapshot.NgramIndex)
        {
            _ngramIndex[key] = ToStringBag(values);
        }

        foreach (var file in snapshot.ProcessedFiles.Where(file => !string.IsNullOrWhiteSpace(file)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _processedFiles[file] = 0;
        }

        _cachedAllTypeNames = _typeMap.Keys.Concat(_shortTypeMap.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // excludedFiles：mod 多版本布局里被顶掉的文件，见 ModLayoutResolver
    public void Scan(string rootPath, IReadOnlySet<string>? excludedFiles = null)
    {
        if (!Directory.Exists(rootPath)) return;
        var blacklistedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".git", ".vs", ".idea", ".build", "temp" };

        var allFiles = CollectFilesIterative(rootPath, blacklistedDirs);
        var newFiles = ScanFilter.SelectNew(allFiles, excludedFiles, full => _processedFiles.TryAdd(full, 0));

        Parallel.ForEach(newFiles, file =>
        {
            var internedFile = string.Intern(file);
            var fileName = Path.GetFileNameWithoutExtension(internedFile);
            _index.GetOrAdd(fileName, _ => new ConcurrentBag<string>()).Add(internedFile);

            if (internedFile.EndsWith(".cs"))
            {
                var (types, members) = RoslynHelper.GetClassInfoCombined(internedFile);

                foreach (var type in types)
                {
                    _typeMap.GetOrAdd(type.FullName, _ => new ConcurrentBag<string>()).Add(internedFile);
                    var shortName = type.FullName.Contains('.') ? type.FullName.Split('.').Last() : type.FullName;
                    _shortTypeMap.GetOrAdd(shortName, _ => new ConcurrentBag<string>()).Add(type.FullName);

                    // 两份继承数据分工不同，见 TypeInheritance：
                    // 主基类链每个类型只有一条出边（GetInheritanceChain 靠它一路向上走），
                    if (!string.IsNullOrEmpty(type.PrimaryBase))
                        _inheritanceMap[type.FullName] = type.PrimaryBase;

                    // 而 inheritors 是反向的一对多，且必须收全部直接超类型（含接口）——
                    // 原先只收基类型列表第一项，按 IDisposable 查实现者恒为空。
                    foreach (var superType in type.DirectSuperTypes)
                        _inheritorsMap.GetOrAdd(superType, _ => new ConcurrentBag<string>()).Add(type.FullName);

                    IndexNgrams(type.FullName);
                    IndexNgrams(shortName);
                }
                
                IndexMembersFromList(members, internedFile);
            }
        });

        _cachedAllTypeNames = _typeMap.Keys.Concat(_shortTypeMap.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> CollectFilesIterative(string rootPath, HashSet<string> blacklistedDirs)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var currentPath = stack.Pop();
            try
            {
                foreach (var file in Directory.GetFiles(currentPath))
                {
                    if (file.EndsWith(".cs") || file.EndsWith(".xml")) result.Add(file);
                }
                foreach (var dir in Directory.GetDirectories(currentPath))
                {
                    if (!blacklistedDirs.Contains(Path.GetFileName(dir))) stack.Push(dir);
                }
            }
            catch { }
        }
        return result;
    }

    private bool TryGetInheritors(string key, out IReadOnlyList<string> values)
    {
        if (_frozenInheritorsMap != null && _frozenInheritorsMap.TryGetValue(key, out var frozen))
        { values = frozen; return true; }
        if (_inheritorsMap.TryGetValue(key, out var bag))
        { values = bag.ToArray(); return true; }
        values = Array.Empty<string>(); return false;
    }
    
    private bool TryGetShortType(string key, out IReadOnlyList<string> values)
    {
        if (_frozenShortTypeMap != null && _frozenShortTypeMap.TryGetValue(key, out var frozen))
        { values = frozen; return true; }
        if (_shortTypeMap.TryGetValue(key, out var bag))
        { values = bag.ToArray(); return true; }
        values = Array.Empty<string>(); return false;
    }
    
    private bool ContainsType(string key) =>
        (_frozenTypeMap?.ContainsKey(key) ?? false) || _typeMap.ContainsKey(key);

    // 「索引里到底有没有这个类型」——零结果时用来把「不存在」和「存在但没有结果」分开。
    // 全名与短名两条路都要试，否则传短名的调用方会被判成「不存在」。
    public bool IsKnownType(string typeName)
        => ContainsType(typeName) || TryGetShortType(typeName, out _);
    
    private IReadOnlyList<string> GetTypeFiles(string key)
    {
        if (_frozenTypeMap != null && _frozenTypeMap.TryGetValue(key, out var frozen)) return frozen;
        if (_typeMap.TryGetValue(key, out var bag)) return bag.ToArray();
        return Array.Empty<string>();
    }

    // 一个类型名的直接子类/实现者，把「全名 / 短名 / 短名候选」三条查法并起来。
    private HashSet<string> DirectInheritorsOf(string typeName)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryGetInheritors(typeName, out var directInheritors))
        {
            foreach (var item in directInheritors) results.Add(item);
        }

        if (TryGetShortType(typeName, out var fullNames))
        {
            foreach (var fullName in fullNames)
            {
                if (TryGetInheritors(fullName, out var inheritors))
                {
                    foreach (var item in inheritors) results.Add(item);
                }
            }
        }

        var shortNameCandidate = typeName.Contains('.') ? typeName.Split('.').Last() : typeName;
        if (shortNameCandidate != typeName && TryGetInheritors(shortNameCandidate, out var shortInheritors))
        {
            foreach (var item in shortInheritors) results.Add(item);
        }

        return results;
    }

    // _inheritorsMap 的值是类型名而非路径，故归属判定要先经 _typeMap 反查定义文件；
    // 反查不到路径的类型（引用了未索引的基类）按未知源处理，只有全域 scope 才收。
    //
    // 逐层 BFS 走到底，不是只取直接子类。RimWorld 的类型层级普遍三四层深
    // （ThingComp → CompShield → …），只回直接子类而对外称「子类树」时，
    // 「X 是不是 ThingComp 的子类」这个最常见的问题会被答成「不是」——
    // 而那正是本 mode 存在的理由。Depth 一并回传，供展示层说清每一条在第几层。
    public ScopedResult<string> GetInheritors(string baseTypeName, ScopeSelection scope, int limit = 0)
        => GetInheritors(baseTypeName, scope, limit, out _);

    public ScopedResult<string> GetInheritors(
        string baseTypeName, ScopeSelection scope, int limit, out IReadOnlyDictionary<string, int> depths)
    {
        var depthOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var frontier = new List<string> { baseTypeName };

        // 环保护：反编译产物里同名类型跨命名空间互指是可能的，短名归并后就会成环
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseTypeName };

        for (int depth = 1; frontier.Count > 0 && depth <= MaxInheritorDepth; depth++)
        {
            var next = new List<string>();
            foreach (var parent in frontier)
            {
                foreach (var child in DirectInheritorsOf(parent))
                {
                    if (!visited.Add(child)) continue;
                    depthOf[child] = depth;
                    next.Add(child);
                }
            }
            frontier = next;
        }

        depths = depthOf;

        // 浅的排前面：截断时留下的该是直接子类，而不是字母序恰好靠前的某个曾孙
        var candidates = depthOf
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ScoredCandidate<string>(kv.Key, 100.0, FirstPathOfType(kv.Key) ?? string.Empty));

        // 子类树没有分数梯度（全是精确的继承关系），断层收口在此无意义
        return ScopeFilter.Apply(candidates, scope, limit, scoreGap: null);
    }

    // 继承深度的护栏。RimWorld 里最深的链也就个位数，这个数只是防索引成环时空转。
    private const int MaxInheritorDepth = 24;

    private string? FirstPathOfType(string typeName)
    {
        var files = GetTypeFiles(typeName);
        if (files.Count > 0) return files[0];
        if (TryGetShortType(typeName, out var fullNames))
        {
            foreach (var fullName in fullNames)
            {
                var byFullName = GetTypeFiles(fullName);
                if (byFullName.Count > 0) return byFullName[0];
            }
        }
        return null;
    }

    // 短名传进来时可能对应多个全名（不同命名空间的同名类，跨源时尤其常见）。原先只试
    // fullNames 的第一个，而那份数组的次序由索引期的并发写入决定：碰上一个没有基类的同名类，
    // 整条链就成了空——inspect 于是在 Outline 明明列着 `X : Y` 的同一次返回里不画继承图。
    // 逐个试到第一条走得通的为止。
    public List<(string Child, string Parent)> GetInheritanceChain(string typeName)
    {
        if (ContainsType(typeName))
        {
            var direct = WalkInheritanceChain(typeName);
            if (direct.Count > 0) return direct;
        }

        if (TryGetShortType(typeName, out var fullNames))
        {
            foreach (var fullName in fullNames)
            {
                var chain = WalkInheritanceChain(fullName);
                if (chain.Count > 0) return chain;
            }
        }

        return new List<(string Child, string Parent)>();
    }

    private List<(string Child, string Parent)> WalkInheritanceChain(string startType)
    {
        var chain = new List<(string Child, string Parent)>();

        string? current = startType;

        while (_inheritanceMap.TryGetValue(current, out var parent))
        {
            if (chain.Any(x => x.Child == current)) break;
            chain.Add((current, parent));
            
            current = ContainsType(parent) ? parent : null;
            if (current == null && TryGetShortType(parent, out var parentFullNames))
            {
                current = parentFullNames.FirstOrDefault();
            }
            
            if (current == null || chain.Count > 20) break;
        }
        return chain;
    }

    public List<string> GetPathsByType(string typeName)
    {
        var files = GetTypeFiles(typeName);
        if (files.Count > 0) return files.ToList();
        if (TryGetShortType(typeName, out var fullNames))
        {
            return fullNames.Distinct()
                .SelectMany(fn => GetTypeFiles(fn)).ToList();
        }
        return new List<string>();
    }

    // scope 内的定义文件；调用方据 OutOfScope 判断「换个 scope 才看得到」
    public ScopedResult<string> GetPathsByType(string typeName, ScopeSelection scope, int limit = 0)
    {
        var candidates = GetPathsByType(typeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ScoredCandidate<string>(path, 100.0, path));

        return ScopeFilter.Apply(candidates, scope, limit, scoreGap: null);
    }

    public ScopedResult<string> Search(string query, ScopeSelection scope, int limit = 30)
    {
        var source = _frozenIndex != null
            ? (IEnumerable<KeyValuePair<string, string[]>>)_frozenIndex
            : _index.Select(kv => new KeyValuePair<string, string[]>(kv.Key, kv.Value.Distinct().ToArray()));

        var candidates = source
            .Select(kv => new { kv.Key, kv.Value, Score = FuzzyMatcher.CalculateFuzzyScore(kv.Key, query) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Key.Length)
            .SelectMany(x => x.Value.Select(path => new ScoredCandidate<string>(path, x.Score, path)))
            .DistinctBy(x => x.Item, StringComparer.OrdinalIgnoreCase);

        return ScopeFilter.Apply(candidates, scope, limit);
    }

    private void IndexMembersFromList(List<(string TypeName, string MemberName, string MemberType)> members, string filePath)
    {
        foreach (var (typeName, memberName, memberType) in members)
        {
            var words = FuzzyMatcher.SplitIntoWords(memberName);
            foreach (var word in words)
            {
                if (word.Length >= 2)
                {
                    _memberIndex.GetOrAdd(word.ToLowerInvariant(), _ => new ConcurrentBag<(string, string, string, string)>())
                        .Add((typeName, memberName, memberType, filePath));
                }
            }
            _memberIndex.GetOrAdd(memberName.ToLowerInvariant(), _ => new ConcurrentBag<(string, string, string, string)>())
                .Add((typeName, memberName, memberType, filePath));
        }
    }

    private void IndexNgrams(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var ngrams = FuzzyMatcher.GenerateNgrams(name, 2).Distinct();
        foreach (var ngram in ngrams)
        {
            _ngramIndex.GetOrAdd(ngram, _ => new ConcurrentBag<string>()).Add(name);
        }
    }

    public ScopedResult<string> FuzzySearchTypes(string query, ScopeSelection scope, int limit = 20)
    {
        HashSet<string> searchSet;

        if (query.Length <= 4)
        {
            searchSet = new HashSet<string>(_cachedAllTypeNames, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var queryNgrams = FuzzyMatcher.GenerateNgrams(query, 2).Distinct().ToList();
            var candidateScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var ngram in queryNgrams)
            {
                IEnumerable<string>? names = null;
                if (_frozenNgramIndex != null && _frozenNgramIndex.TryGetValue(ngram, out var frozenNames))
                    names = frozenNames;
                else if (_ngramIndex.TryGetValue(ngram, out var namesBag))
                    names = namesBag.Distinct();
                    
                if (names != null)
                {
                    foreach (var name in names)
                        candidateScores[name] = candidateScores.GetValueOrDefault(name) + 1;
                }
            }

            if (candidateScores.Count < 50)
            {
                searchSet = new HashSet<string>(_cachedAllTypeNames, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                searchSet = new HashSet<string>(
                    candidateScores.OrderByDescending(kv => kv.Value).Take(500).Select(kv => kv.Key),
                    StringComparer.OrdinalIgnoreCase
                );
            }
        }

        // 打分本来就是全量的，Take 只截断输出——故在过滤之前拿到的总数是真实命中数，零额外开销
        var scored = searchSet
            .Select(name => new { Name = name, Score = FuzzyMatcher.CalculateFuzzyScore(name, query) })
            .Where(x => x.Score > 0)
            .ToList();

        // 第三级按名字排：前两级并列的条目之间，次序否则由 searchSet 的枚举顺序决定，
        // 而它跟着索引期的并发写入走——同一个查询换个进程重跑，前十条就能换一批。
        var candidates = CollapseNameAliases(scored.Select(x => (x.Name, x.Score)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ScoredCandidate<string>(x.Name, x.Score, FirstPathOfType(x.Name) ?? string.Empty));

        return ScopeFilter.Apply(candidates, scope, limit);
    }

    // 一个类型在索引里有短名与全名两条记录（见 _cachedAllTypeNames 的拼装），同一个查询会把
    // 两条都命中。折叠必须发生在这里、而不是调用方那边：截断在 ScopeFilter 里就已经做了，
    // 留到之后再去重，被 limit 砍掉的那一半重复就再也数不出来——「+N more」于是承诺一个
    // limit:'all' 兑现不了的数字（实测 locate 'shield' 报 +83，展开后总共只有 51 条）。
    //
    // 保留短名而非全名：两种形态喂给 inspect / read_code 都认，短名更短，而且 inspect 靠
    // 「模糊搜索能回一条与输入同形态的结果」来纠正调用方的错拼，折成全名会让那条路径失效。
    private List<(string Name, double Score)> CollapseNameAliases(IEnumerable<(string Name, double Score)> scored)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(string Name, double Score)>();

        foreach (var entry in scored)
        {
            present.Add(entry.Name);
            items.Add(entry);
        }

        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var (name, score) in items)
        {
            var canonical = CanonicalAlias(name, present);
            if (!best.TryGetValue(canonical, out var existing))
            {
                order.Add(canonical);
                best[canonical] = score;
            }
            else if (score > existing)
            {
                best[canonical] = score;
            }
        }

        return order.Select(name => (name, best[name])).ToList();
    }

    private string CanonicalAlias(string name, HashSet<string> present)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0) return name;

        // 短名指向多个类型时不折叠：那时短名代表的是「这批同名类型」，与其中任何一个都不等价，
        // 合并会让「有两个不同的 Gizmo」这件事从结果里消失。
        var shortName = name[(dot + 1)..];
        return present.Contains(shortName) && TryGetShortType(shortName, out var owners) && owners.Count == 1
            ? shortName
            : name;
    }

    public ScopedResult<(string TypeName, string MemberName, string MemberType, string FilePath)> SearchMembersByKeywords(
        string[] keywords,
        ScopeSelection scope,
        int limit = 30)
    {
        if (keywords == null || keywords.Length == 0)
            return ScopedResult<(string, string, string, string)>.Empty;
        var matchedMembers = new Dictionary<(string, string, string, string), int>();
        
        var memberKeys = _frozenMemberIndex != null 
            ? (IEnumerable<string>)_frozenMemberIndex.Keys 
            : _memberIndex.Keys;

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2) continue;
            var keyLower = keyword.ToLowerInvariant();

            IEnumerable<(string TypeName, string MemberName, string MemberType, string FilePath)>? members = null;
            if (_frozenMemberIndex != null && _frozenMemberIndex.TryGetValue(keyLower, out var frozenMembers))
                members = frozenMembers;
            else if (_memberIndex.TryGetValue(keyLower, out var bagMembers))
                members = bagMembers;
                
            if (members != null)
            {
                foreach (var member in members)
                {
                    var key = (member.TypeName, member.MemberName, member.MemberType, member.FilePath);
                    matchedMembers[key] = matchedMembers.GetValueOrDefault(key) + 1;
                }
            }

            IEnumerable<string> fuzzyCandidates;
            if (keyLower.Length >= 3)
            {
                var ngrams = FuzzyMatcher.GenerateNgrams(keyLower, 2).Distinct().ToList();
                var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ngram in ngrams)
                {
                    foreach (var mk in memberKeys)
                    {
                        if (mk.Contains(ngram, StringComparison.OrdinalIgnoreCase))
                            candidateSet.Add(mk);
                        if (candidateSet.Count >= 200) break;
                    }
                    if (candidateSet.Count >= 200) break;
                }
                fuzzyCandidates = candidateSet;
            }
            else
            {
                fuzzyCandidates = memberKeys.Where(k => k.StartsWith(keyLower, StringComparison.OrdinalIgnoreCase)).Take(50);
            }
            
            var fuzzyMatches = fuzzyCandidates
                .Select(k => (Key: k, Score: FuzzyMatcher.CalculateFuzzyScore(k, keyLower)))
                .Where(x => x.Score >= 60.0)
                .OrderByDescending(x => x.Score)
                .Take(10)
                .Select(x => x.Key);
                
            foreach (var fuzzyKey in fuzzyMatches)
            {
                IEnumerable<(string TypeName, string MemberName, string MemberType, string FilePath)>? fuzzyMemberList = null;
                if (_frozenMemberIndex != null && _frozenMemberIndex.TryGetValue(fuzzyKey, out var frozenFuzzy))
                    fuzzyMemberList = frozenFuzzy;
                else if (_memberIndex.TryGetValue(fuzzyKey, out var bagFuzzy))
                    fuzzyMemberList = bagFuzzy;
                    
                if (fuzzyMemberList != null)
                {
                    foreach (var member in fuzzyMemberList)
                    {
                        var key = (member.TypeName, member.MemberName, member.MemberType, member.FilePath);
                        matchedMembers[key] = matchedMembers.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        var candidates = matchedMembers
            .Select(kv =>
            {
                var (typeName, memberName, memberType, filePath) = kv.Key;
                var matchCount = kv.Value;
                var baseScore = FuzzyMatcher.CalculateFuzzyScore(memberName, string.Join("", keywords));
                var keywordBonus = Math.Min(matchCount - 1, 5) * 10.0;
                var score = Math.Min(baseScore + keywordBonus, 100.0);
                return (typeName, memberName, memberType, filePath, score);
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.memberName.Length)
            .Select(x => new ScoredCandidate<(string, string, string, string)>(
                (x.typeName, x.memberName, x.memberType, x.filePath), x.score, x.filePath));

        return ScopeFilter.Apply(candidates, scope, limit);
    }

    // scope 与扩展名过滤都必须在扫描之前生效：这两个条件若留到结果产出后再筛，
    // 命中上限会被过滤掉的文件吃光，筛完就成了假空（fileFilter 原先正是这个写法）。
    // 单文件最多留几条预览。只限「留」，不限「数」：原先收满就 break 掉整个文件，
    // 于是该文件剩下的命中既不进总数、也不进上层的「+N more in this file」，两个数一起少报，
    // 调用方据此以为这个文件里就那么几处。
    //
    // 这个数必须与展示层每文件显示的条数一致（SearchRegexTool 的 Take(3)）。取 5 时多出来的
    // 两条照样占掉 maxResults 配额却永远不会被显示，等于把默认档能覆盖的文件数从 33 压到 20——
    // 而展示层的文件上限是 50，密集命中下那个上限根本摸不到，扫描先停了。
    private const int MaxPreviewsPerFile = 3;

    // 数完整个文件是为了把每文件命中数报准，但不能让一个病态大文件吃光整轮扫描的时间。
    private const int MaxLinesScannedPerFile = 20000;

    // 预览行长度上限，与 trace usages 用的是同一个数。ScopeArgs.HardLimit 那笔体积账
    // （一条一行、每行按 100 字符算，200 行 ≈ 20KB）正是以此为前提，而本方法此前整条
    // 链路一次都没截：XML 里一行写完的 <li> 列表、反编译产物里的长泛型签名都能把单行
    // 拉到几百字符，150 行预览就此涨到那笔账的三倍，且随 pattern 与 scope 不可预测地浮动。
    private const int MaxPreviewLength = 100;

    // 在收集处截而不是展示处：matchesByFile 数的是命中数、不碰预览文本，因此不受影响，
    // 而截短的行也不必再在内存里多留一份完整副本。与 TraceTool 的 usages 做法对称。
    private static string TruncatePreview(string line)
    {
        var preview = line.Trim();
        return preview.Length > MaxPreviewLength
            ? preview[..(MaxPreviewLength - 3)] + "..."
            : preview;
    }

    // MatchesByFile 是每个文件的真实命中数，与 Results 里的预览条数不是一回事。
    //
    // Candidates / Failed / LineCapped 三个诊断量必须回传给展示层：本工具的契约是
    //「没有尾注就是完整命中集」，而扫描里有三处会静默减少命中——文件读不开、正则在单个文件上
    // 超时（1s，灾难性回溯）、单文件扫到 MaxLinesScannedPerFile 行就停。这三处原先都被
    // `catch { }` 和一句 break 吞掉，输出照旧宣称完整。
    public async Task<(List<(string Path, int LineNumber, string Preview)> Results,
                       bool Truncated,
                       IReadOnlyDictionary<string, int> MatchesByFile,
                       RegexScanDiagnostics Diagnostics)> SearchRegexAsync(
        string pattern,
        ScopeSelection scope,
        string? fileFilter = null,
        bool ignoreCase = true,
        int maxResults = 100,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        var results = new ConcurrentBag<(string Path, int LineNumber, string Preview)>();
        var matchesByFile = new ConcurrentDictionary<string, int>();
        var regex = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        var allFiles = (_frozenIndex != null
                ? _frozenIndex.Values.SelectMany(x => x)
                : _index.Values.SelectMany(x => x).Distinct())
            .Where(path => scope.Contains(path))
            .Where(path => string.IsNullOrEmpty(fileFilter) || path.EndsWith(fileFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (maxResults <= 0) maxResults = 100;

        int globalCount = 0;
        int processedCount = 0;
        int totalFiles = allFiles.Count;
        int truncatedFlag = 0;
        int timedOutFiles = 0;
        int unreadableFiles = 0;
        int lineCappedFiles = 0;

        await Parallel.ForEachAsync(allFiles, ct, async (filePath, internalCt) =>
        {
            if (Interlocked.CompareExchange(ref globalCount, 0, 0) >= maxResults)
            {
                Interlocked.Exchange(ref truncatedFlag, 1);
                return;
            }

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? line;
                int lineNum = 0;
                int matchesInThisFile = 0;

                while ((line = await reader.ReadLineAsync(internalCt)) != null)
                {
                    lineNum++;
                    if (regex.IsMatch(line))
                    {
                        matchesInThisFile++;

                        // 已经打开的文件一律读到底：句柄和缓冲的钱都付过了，读完才换得
                        // 一个准确的每文件命中数，而不是一个恰好等于上限的假数。
                        if (matchesInThisFile <= MaxPreviewsPerFile)
                        {
                            var currentCount = Interlocked.Increment(ref globalCount);
                            if (currentCount <= maxResults)
                                results.Add((filePath, lineNum, TruncatePreview(line)));
                            else
                                Interlocked.Exchange(ref truncatedFlag, 1);
                        }
                    }
                    if (lineNum >= MaxLinesScannedPerFile)
                    {
                        Interlocked.Increment(ref lineCappedFiles);
                        break;
                    }
                }

                if (matchesInThisFile > 0) matchesByFile[filePath] = matchesInThisFile;
            }
            // 超时的文件被弃在半路：预览可能已经进了 results，而 matchesByFile 那一行在 try 尾部、
            // 走不到，于是上层的「+N more in this file」凭空消失。必须计数并让展示层说出来。
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref timedOutFiles);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref unreadableFiles);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Interlocked.Increment(ref unreadableFiles);
            }
            finally
            {
                var current = Interlocked.Increment(ref processedCount);
                if (current % 10 == 0 || current == totalFiles) progress?.Report((double)current / totalFiles);
            }
        });

        // 命中上限后剩下的文件从委托头部直接 return，不经过 finally 的计数，进度于是停在
        // 半路。扫描到此已经结束，补一次满格，别让调用方的进度条挂着。
        progress?.Report(1.0);

        var finalCount = Interlocked.CompareExchange(ref globalCount, 0, 0);
        var wasTruncated = Interlocked.CompareExchange(ref truncatedFlag, 0, 0) == 1;
        var diagnostics = new RegexScanDiagnostics(
            CandidateFiles: totalFiles,
            TimedOutFiles: Interlocked.CompareExchange(ref timedOutFiles, 0, 0),
            UnreadableFiles: Interlocked.CompareExchange(ref unreadableFiles, 0, 0),
            LineCappedFiles: Interlocked.CompareExchange(ref lineCappedFiles, 0, 0),
            LineCap: MaxLinesScannedPerFile);

        return (results.Take(maxResults).ToList(),
                wasTruncated || finalCount >= maxResults,
                matchesByFile,
                diagnostics);
    }

    // 同名文件在多个源里都存在时（Ratkin 与 vanilla 同名 .cs 之类），优先给 scope 内的；
    // scope 内没有才回退到全域，并由 outOfScopeFallback 告知调用方它读的是别处的文件。
    public string? GetPath(string name, ScopeSelection scope, out bool outOfScopeFallback)
    {
        outOfScopeFallback = false;

        IReadOnlyList<string> paths;
        if (_frozenIndex != null && _frozenIndex.TryGetValue(name, out var frozen)) paths = frozen;
        else if (_index.TryGetValue(name, out var bag)) paths = bag.Distinct().ToArray();
        else return null;

        var best = paths
            .Select(path => (Path: path, Rank: scope.RankOf(path)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .Select(x => x.Path)
            .FirstOrDefault();

        if (best != null) return best;

        var fallback = paths.FirstOrDefault();
        if (fallback != null) outOfScopeFallback = true;
        return fallback;
    }

    public IEnumerable<string> GetAllFiles(ScopeSelection scope)
    {
        var all = _frozenIndex != null
            ? _frozenIndex.Values.SelectMany(x => x)
            : _index.Values.SelectMany(x => x).Distinct();

        return all.Where(path => scope.Contains(path));
    }

    private void ResetFrozenState()
    {
        _frozenIndex = null;
        _frozenTypeMap = null;
        _frozenInheritorsMap = null;
        _frozenShortTypeMap = null;
        _frozenNgramIndex = null;
        _frozenMemberIndex = null;
    }

    private static ConcurrentBag<string> ToStringBag(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ConcurrentBag<string>(list);
    }
}
