using System.Xml.Linq;

namespace RimSearcher.Core;

// 一次 ParentName 链合并到底走完没有，以及没走完的话卡在哪。
//
// 存在的意义：合并失败是**静默**的——父 def 查不到时循环直接结束，而 CleanupMetadata 又把
// ParentName 属性删掉，于是「本来就没有父」「父已合进来」「父找不到所以少了一半」三种情形
// 渲染得逐字同形。而工具描述向调用方承诺的是「the complete effective definition」，
// 它会把半成品当完整定义：据此断定某个 hediff 没有 hediffClass、不关联任何 C# 类，
// 然后去补一个「缺失」的字段。
public sealed record DefInheritanceTrace(
    IReadOnlyList<string> Chain,
    string? UnresolvedParent,
    bool StoppedAtDepthLimit,
    bool StoppedByCycle)
{
    // 只有这三种中断会让合并结果少字段；链自然走到头（没有 ParentName）是正常完成
    public bool IsComplete => UnresolvedParent == null && !StoppedAtDepthLimit && !StoppedByCycle;

    public static readonly DefInheritanceTrace Empty = new([], null, false, false);
}

public static class XmlInheritanceHelper
{
    // vanilla 里最长的继承链也远在此之下；这个数只是防索引成环时空转
    private const int MaxChainDepth = 15;

    private static readonly HashSet<string> ListContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "comps", "stages", "modExtensions", "lifeStages", "hediffGivers",
        "parts", "verbs", "tools", "abilities", "hediffFilters", "disallowedTraits",
        "tags", "weaponTags", "apparelTags", "tradeTags", "thoughtContexts",
        "recipeUsers", "thingCategories", "researchPrerequisites", "skillRequirements",
        "descriptionHyperlinks", "forcedTraits", "disallowedTraitsWithDegree",
        "nullifyingTraitDegrees", "agreeableTraits", "disagreeableTraits",
        "disallowedThingDefs", "apparelRequired", "techHediffsRequired", "fixedInventory",
        "requirementSet", "fixedIngredientFilter", "defaultIngredientFilter",
        "requirementTags", "exclusionTags", "blacklistedGenders", "whiteListedGenders",
        "hediffClassList", "requiredHediffs", "requiredGeneDefs", "disallowedGenes",
        "startingResearchProjects", "addDesignators", "addDesignatorGroups"
    };

    /// <summary>
    /// Resolves XML inheritance and returns the merged XElement directly.
    /// Returns null if the def is not found or loading fails.
    /// </summary>
    public static Task<XElement?> ResolveDefXmlElementAsync(string defName, DefIndexer indexer, ScopeSelection scope)
        => ResolveDefXmlElementAsync(indexer.Lookup(defName, scope).Location, indexer, scope);

    /// <summary>
    /// 同上，但起点由调用方给定。挑好了 def 的调用方必须走这条：按名字重查一次会落回
    /// 默认胜者，defType 消歧挑中的那条就被丢掉了，返回里表头与正文来自两条不同的 def。
    /// </summary>
    public static async Task<XElement?> ResolveDefXmlElementAsync(
        DefLocation? target, DefIndexer indexer, ScopeSelection scope)
        => (await ResolveDefXmlWithTraceAsync(target, indexer, scope)).Element;

    // 一并带回链的完整性。调用方**必须**据此决定要不要在输出里加警示：
    // 少了字段的合并结果和完整的合并结果长得一模一样。
    public static async Task<(XElement? Element, DefInheritanceTrace Trace)> ResolveDefXmlWithTraceAsync(
        DefLocation? target, DefIndexer indexer, ScopeSelection scope)
    {
        var targetLoc = target;
        if (targetLoc == null) return (null, DefInheritanceTrace.Empty);

        var hierarchy = new Stack<XElement>();
        var currentLoc = targetLoc;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var chain = new List<string>();
        string? unresolvedParent = null;
        var stoppedAtDepthLimit = false;
        var stoppedByCycle = false;

        while (currentLoc != null)
        {
            if (!visited.Add(currentLoc.DefName + currentLoc.FilePath)) { stoppedByCycle = true; break; }
            if (visited.Count > MaxChainDepth) { stoppedAtDepthLimit = true; break; }

            chain.Add(currentLoc.DefName);
            var thisLink = currentLoc;

            try
            {
                var doc = indexer.GetOrLoadDocument(currentLoc.FilePath);
                XElement? node = null;
                var nodes = doc.Root?.Elements() ?? Enumerable.Empty<XElement>();

                // 同名不同型必须靠 DefType 分开。原先只比 defName，于是同一个文件里三个都叫
                // `Wolfein_PrototypeShieldBelt` 的 def（HediffDef / ThingDef / JobDef）永远命中
                // 文件里排在最前的那一个——而表头印的是 Lookup 选中的那一个：
                // `inspect(name, defType:'JobDef')` 回的是 `Type: JobDef` 加一句
                // 「showing the JobDef one」，正文却是 `<ThingDef>`。调用方已经明确说了要哪一个，
                // 这是返回里三处互相打架、且两处在骗人。
                // 退回分支保证不劣于原行为：找不到同型节点时结果与此前逐字相同（父链上的
                // 抽象节点用 Name 属性挂接，元素名未必等于子 def 的 DefType）。
                XElement? byNameOnly = null;
                foreach (var n in nodes)
                {
                    if (n.Element("defName")?.Value != currentLoc.DefName &&
                        n.Attribute("Name")?.Value != currentLoc.DefName)
                        continue;

                    byNameOnly ??= n;
                    if (n.Name.LocalName == currentLoc.DefType)
                    {
                        node = n;
                        break;
                    }
                }

                node ??= byNameOnly;

                if (node != null)
                {
                    hierarchy.Push(new XElement(node));
                    var parentName = currentLoc.ParentName;
                    if (string.IsNullOrEmpty(parentName))
                    {
                        // 链自然走到头，这才是「合并完整」
                        currentLoc = null;
                    }
                    else
                    {
                        // 父优先在子所在的源里找：撞名时 Milira 的 def 该接 Milira 自己的抽象基
                        var parent = indexer.Lookup(
                            parentName, scope, preferSameSourceAs: currentLoc.FilePath).Location;

                        // 父声明了却查不到——多半是那个源没配进 config，或 scope 把它挡在外面。
                        // 这里静默 break 掉，结果就少了整条上游链的字段，而输出看不出来。
                        if (parent == null) unresolvedParent = parentName;
                        currentLoc = parent;
                    }
                }
                else
                {
                    // 索引说这个 def 在这个文件里，实际读不到——索引落后于磁盘
                    unresolvedParent ??= thisLink.DefName;
                    break;
                }
            }
            catch
            {
                unresolvedParent ??= thisLink.DefName;
                break;
            }
        }

        var trace = new DefInheritanceTrace(chain, unresolvedParent, stoppedAtDepthLimit, stoppedByCycle);

        if (hierarchy.Count == 0) return (null, trace);

        XElement result = new XElement(hierarchy.Peek().Name);
        while (hierarchy.Count > 0) MergeXml(result, hierarchy.Pop());

        var defNameEl = result.Element("defName");
        var labelEl = result.Element("label");
        var descEl = result.Element("description");

        defNameEl?.Remove();
        labelEl?.Remove();
        descEl?.Remove();

        if (descEl != null) result.AddFirst(descEl);
        if (labelEl != null) result.AddFirst(labelEl);
        if (defNameEl != null) result.AddFirst(defNameEl);

        CleanupMetadata(result);
        return (result, trace);
    }

    private static void CleanupMetadata(XElement element)
    {
        if (element.Element("defName") != null)
        {
            element.Attribute("Name")?.Remove();
        }

        element.Attribute("ParentName")?.Remove();
        element.Attribute("Abstract")?.Remove();
        element.Attribute("Inherit")?.Remove();

        foreach (var sub in element.Elements())
        {
            CleanupMetadata(sub);
        }
    }

    private static void MergeXml(XElement parent, XElement child)
    {
        bool inherit = child.Attribute("Inherit")?.Value.ToLower() != "false";
        if (!inherit)
        {
            parent.RemoveAttributes();
            parent.RemoveNodes();
            foreach (var attr in child.Attributes().Where(a => a.Name.LocalName != "Inherit"))
                parent.SetAttributeValue(attr.Name, attr.Value);
            foreach (var node in child.Nodes())
                parent.Add(node);
            return;
        }

        parent.RemoveAttributes();
        foreach (var attr in child.Attributes().Where(a => a.Name.LocalName != "Inherit"))
            parent.SetAttributeValue(attr.Name, attr.Value);

        if (!child.Elements().Any() && !string.IsNullOrEmpty(child.Value))
        {
            parent.RemoveNodes();
            parent.Value = child.Value;
            return;
        }

        bool isListContainer = ListContainerNames.Contains(parent.Name.LocalName);

        foreach (var childNode in child.Elements())
        {

            if (childNode.Name.LocalName == "li" || isListContainer)
            {
                parent.Add(new XElement(childNode));
                continue;
            }

            var existingNode = parent.Element(childNode.Name);
            if (existingNode != null) MergeXml(existingNode, childNode);
            else parent.Add(new XElement(childNode));
        }
    }
}
