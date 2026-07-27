using System.Xml.Linq;

namespace RimSearcher.Core;

public static class XmlInheritanceHelper
{
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
    public static async Task<XElement?> ResolveDefXmlElementAsync(DefLocation? target, DefIndexer indexer, ScopeSelection scope)
    {
        var targetLoc = target;
        if (targetLoc == null) return null;

        var hierarchy = new Stack<XElement>();
        var currentLoc = targetLoc;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (currentLoc != null)
        {
            if (!visited.Add(currentLoc.DefName + currentLoc.FilePath)) break;
            if (visited.Count > 15) break;

            try
            {
                var doc = indexer.GetOrLoadDocument(currentLoc.FilePath);
                XElement? node = null;
                var nodes = doc.Root?.Elements() ?? Enumerable.Empty<XElement>();
                foreach (var n in nodes)
                {
                    if (n.Element("defName")?.Value == currentLoc.DefName ||
                        n.Attribute("Name")?.Value == currentLoc.DefName)
                    {
                        node = n;
                        break;
                    }
                }

                if (node != null)
                {
                    hierarchy.Push(new XElement(node));
                    var parentName = currentLoc.ParentName;
                    // 父优先在子所在的源里找：撞名时 Milira 的 def 该接 Milira 自己的抽象基
                    currentLoc = !string.IsNullOrEmpty(parentName)
                        ? indexer.Lookup(parentName, scope, preferSameSourceAs: currentLoc.FilePath).Location
                        : null;
                }
                else break;
            }
            catch
            {
                break;
            }
        }

        if (hierarchy.Count == 0) return null;

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
        return result;
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
