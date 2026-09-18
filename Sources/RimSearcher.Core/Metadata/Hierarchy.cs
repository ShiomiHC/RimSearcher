using System.Reflection;
using System.Reflection.Metadata;

namespace RimSearcher.Metadata;

/// <summary>继承图里的一条关系。</summary>
public sealed record TypeRelation(TypeHit Type, string BaseName, IReadOnlyList<string> Interfaces);

/// <summary>
/// 类型之间的继承关系,按类型全名连边。
///
/// 用全名而不是元数据 token 当键,是因为这张图**必须跨程序集** —— mod 里的类几乎都派生自
/// 游戏本体的类,而 token 只在自己的程序集里有意义。
///
/// 全部现算,不落盘:实测把一个 16158 个类型的程序集建成反向边 15 毫秒,查一次传递闭包
/// (ThingComp 的 378 个派生类型)不到 1 毫秒。
/// </summary>
public sealed class Hierarchy
{
    private readonly Dictionary<string, List<TypeHit>> _derived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRelation> _byName = new(StringComparer.Ordinal);

    public Hierarchy(MetadataLookup lookup)
    {
        foreach (var asm in lookup.Assemblies)
        {
            var file = lookup.FileOf(asm);
            if (file is null) continue;
            var md = file.Metadata;

            foreach (var hit in lookup.AllTypes().Where(t => ReferenceEquals(t.Assembly, asm)))
            {
                var def = md.GetTypeDefinition(hit.Handle);
                var baseName = NameOf(md, def.BaseType);
                var ifaces = new List<string>();
                foreach (var ih in def.GetInterfaceImplementations())
                {
                    var n = NameOf(md, md.GetInterfaceImplementation(ih).Interface);
                    if (n.Length > 0) ifaces.Add(n);
                }

                _byName[hit.FullName] = new TypeRelation(hit, baseName, ifaces);

                if (baseName.Length > 0)
                {
                    if (!_derived.TryGetValue(baseName, out var list)) _derived[baseName] = list = [];
                    list.Add(hit);
                }

                foreach (var i in ifaces)
                {
                    if (!_derived.TryGetValue(i, out var list)) _derived[i] = list = [];
                    list.Add(hit);
                }
            }
        }
    }

    public TypeRelation? Relation(string fullName)
        => _byName.TryGetValue(fullName, out var r) ? r : null;

    /// <summary>直接派生自它的类型(实现该接口的也算)。</summary>
    public IReadOnlyList<TypeHit> DirectDerived(string fullName)
        => _derived.TryGetValue(fullName, out var l) ? l : [];

    /// <summary>传递闭包。返回顺序是广度优先 —— 直接子类排在孙类前面。</summary>
    public IReadOnlyList<TypeHit> AllDerived(string fullName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TypeHit>();
        var queue = new Queue<string>();
        queue.Enqueue(fullName);

        while (queue.Count > 0)
        {
            foreach (var child in DirectDerived(queue.Dequeue()))
            {
                if (!seen.Add(child.FullName)) continue;
                result.Add(child);
                queue.Enqueue(child.FullName);
            }
        }
        return result;
    }

    /// <summary>
    /// 基类链,从直接基类往上。
    ///
    /// 走到树外的类型就停 —— <c>System.Object</c> 这类不在任何一棵反编译树里,链条到那里
    /// 断掉不是查漏,是那些程序集本来就不在范围内。最后一项若不是 object,说明链是断的。
    /// </summary>
    public IReadOnlyList<string> BaseChain(string fullName)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { fullName };
        var cur = fullName;

        while (_byName.TryGetValue(cur, out var rel) && rel.BaseName.Length > 0)
        {
            chain.Add(rel.BaseName);
            if (!seen.Add(rel.BaseName)) break;
            cur = rel.BaseName;
        }
        return chain;
    }

    /// <summary>
    /// 这个类型里覆写了基类方法的那些成员。
    ///
    /// 判据是元数据上的 virtual 且非 newslot —— 名字相同不足以说明是覆写(同名遮蔽是另一回事)。
    /// </summary>
    public static IReadOnlyList<MethodHit> Overrides(MetadataLookup lookup, TypeHit type)
    {
        var file = lookup.FileOf(type.Assembly);
        if (file is null) return [];
        var md = file.Metadata;
        var def = md.GetTypeDefinition(type.Handle);
        var result = new List<MethodHit>();

        foreach (var mh in def.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var a = m.Attributes;
            if ((a & MethodAttributes.Virtual) == 0) continue;
            if ((a & MethodAttributes.NewSlot) != 0) continue;

            result.Add(new MethodHit
            {
                Type = type,
                Handle = mh,
                Name = md.GetString(m.Name),
                SourceName = MetadataLookup.SourceNameOf(md.GetString(m.Name)),
                Signature = SignatureText.Method(md, m, md.GetString(m.Name)),
                Bodyless = m.RelativeVirtualAddress == 0,
                BodyKind = MetadataLookup.BodyKindOf(m),
            });
        }
        return result;
    }

    private static string NameOf(MetadataReader md, EntityHandle handle)
    {
        if (handle.IsNil) return "";
        try
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                {
                    var t = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    var ns = md.GetString(t.Namespace);
                    var n = md.GetString(t.Name);
                    return ns.Length > 0 ? ns + "." + n : n;
                }
                case HandleKind.TypeReference:
                {
                    var t = md.GetTypeReference((TypeReferenceHandle)handle);
                    var ns = md.GetString(t.Namespace);
                    var n = md.GetString(t.Name);
                    return ns.Length > 0 ? ns + "." + n : n;
                }
                case HandleKind.TypeSpecification:
                    // 泛型实例化的基类(`Comp<T>` 这种)。签名解码要一整套 provider,而这里
                    // 只需要那个开放泛型的名字 —— 留空比给个解不开的串强,调用侧会说这一条读不出。
                    return "";
                default:
                    return "";
            }
        }
        catch { return ""; }
    }
}
