using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace RimSearcher.Core;

// 一个 def 的译文。Description 只在 inspect 里显示，且默认不收（见 LocalizationOptions）。
public sealed record LocalizedDef(string? Label, string? Description)
{
    public bool IsEmpty => string.IsNullOrEmpty(Label) && string.IsNullOrEmpty(Description);
}

// 要扫的一份语言包，连同它在覆盖顺序里的位置。
//
// RimWorld 的真规则是「后加载的 mod 覆盖先加载的」，而加载顺序在 ModsConfig.xml 里、不在我们
// 的配置里。故这里定义为：SourceRank = 该源在 config 里的书写序，FolderRank = 该源内 mod 布局
// 给出的目录优先级序（越小越优先，与 ModLayout.Folders 同向）。两者合起来是一个全序。
public sealed record LocalizationSource(LanguagePack Pack, int SourceRank, int FolderRank);

public sealed class LocalizationIndex
{
    // key："<DefType>/<defName>"。没有「只按 defName」的回退表——那张表会撞车：实测本体一份
    // 中文包里，3141 个顶层 label 中有 205 个 defName 跨 DefType 重名，其中 49 个译文并不相同
    // （Animals 在 SkillDef 下是「驯兽」，在 MainButtonDef 下是「动物」）。对不上类型就不显示，
    // 比抛硬币强。
    private readonly ConcurrentDictionary<string, Ranked> _entries = new(StringComparer.OrdinalIgnoreCase);

    private FrozenDictionary<string, LocalizedDef>? _frozen;

    // 权重随值一起存：并行扫描下谁先写完是不确定的，靠「后写覆盖先写」就成了竞态。
    // AddOrUpdate 里比较这个纯值，结果与完成顺序无关。
    private readonly record struct Ranked(LocalizedDef Value, int SourceRank, int FolderRank)
    {
        // 数越小越优先
        public bool Outranks(in Ranked other)
            => SourceRank != other.SourceRank
                ? SourceRank > other.SourceRank      // 后写的源赢：config 里靠后 = 加载靠后
                : FolderRank < other.FolderRank;     // 同源内按布局优先级，靠前的赢
    }

    public bool HasAny => _frozen != null ? _frozen.Count > 0 : !_entries.IsEmpty;

    public int Count => _frozen?.Count ?? _entries.Count;

    public static string KeyOf(string defType, string defName) => $"{defType}/{defName}";

    public LocalizedDef? Lookup(string? defType, string? defName)
    {
        if (string.IsNullOrEmpty(defType) || string.IsNullOrEmpty(defName)) return null;

        var key = KeyOf(defType, defName);

        if (_frozen != null)
            return _frozen.TryGetValue(key, out var frozen) ? frozen : null;

        return _entries.TryGetValue(key, out var ranked) ? ranked.Value : null;
    }

    // sources 里各份语言包并行读。tar 是顺序流拆不开，故并行的粒度是「一份语言包一个任务」——
    // 本体那边正好是每个 DLC 一个 tar，mod 那边一个源一份目录。
    public void Scan(IReadOnlyList<LocalizationSource> sources, bool includeDescription)
    {
        if (sources.Count == 0) return;

        Parallel.ForEach(sources, source =>
        {
            foreach (var entry in LanguageReader.Read(source.Pack))
            {
                var value = new LocalizedDef(entry.Label, includeDescription ? entry.Description : null);
                if (value.IsEmpty) continue;

                Add(entry.DefType, entry.DefName, value, source.SourceRank, source.FolderRank);
            }
        });
    }

    public void Add(string defType, string defName, LocalizedDef value, int sourceRank, int folderRank)
    {
        if (string.IsNullOrEmpty(defType) || string.IsNullOrEmpty(defName)) return;

        var candidate = new Ranked(value, sourceRank, folderRank);

        _entries.AddOrUpdate(
            KeyOf(defType, defName),
            candidate,
            (_, existing) => candidate.Outranks(existing) ? candidate : existing);
    }

    public void FreezeIndex()
        => _frozen = _entries.ToFrozenDictionary(
            pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        _entries.Clear();
        _frozen = null;
    }

    // 快照丢掉权重：那是建索引期间用来定胜负的，存进快照的每条已经是胜出者。
    // 导入时统一按最低优先级放回——缓存命中的路径下不会再有 Scan，真有的话也该让新扫到的赢。
    public LocalizationSnapshot ExportSnapshot()
    {
        var entries = _frozen != null
            ? _frozen.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            : _entries.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase);

        return new LocalizationSnapshot { Entries = entries };
    }

    public void ImportSnapshot(LocalizationSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        Clear();

        foreach (var (key, value) in snapshot.Entries)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null || value.IsEmpty) continue;
            _entries[key] = new Ranked(value, int.MinValue, 0);
        }
    }
}
