using System.Reflection;
using System.Reflection.Metadata;

namespace RimSearcher.Metadata;

public sealed record TypeRelation(TypeHit Type, string BaseName, IReadOnlyList<string> Interfaces);

public sealed class HierarchyByReach(MetadataLookup lookup)
{
    private readonly Dictionary<(bool LastResort, bool WithHidden), Hierarchy> _built = [];

    public Hierarchy For(TypeHit type, bool withHidden = false)
    {
        lookup.ReadLike(type);
        var key = (lookup.LastResort, withHidden);
        return _built.TryGetValue(key, out var h) ? h : _built[key] = new Hierarchy(lookup, withHidden);
    }
}

public sealed class Hierarchy
{
    private readonly Dictionary<string, List<TypeHit>> _derived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRelation> _byName = new(StringComparer.Ordinal);

    public Hierarchy(MetadataLookup lookup, bool withHidden = false)
    {
        foreach (var hit in lookup.AllTypes(withHidden))
        {
            var md = lookup.FileOf(hit.Assembly)!.Metadata;
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

    public TypeRelation? Relation(string fullName)
        => _byName.TryGetValue(fullName, out var r) ? r : null;

    public IReadOnlyList<TypeHit> DirectDerived(string fullName)
        => _derived.TryGetValue(fullName, out var l) ? l : [];

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
                    var n = md.GetString(t.Name);
                    if (t.IsNested) return NameOf(md, t.GetDeclaringType()) + "+" + n;
                    var ns = md.GetString(t.Namespace);
                    return ns.Length > 0 ? ns + "." + n : n;
                }
                case HandleKind.TypeReference:
                {
                    var t = md.GetTypeReference((TypeReferenceHandle)handle);
                    var n = md.GetString(t.Name);
                    if (t.ResolutionScope.Kind == HandleKind.TypeReference) return NameOf(md, t.ResolutionScope) + "+" + n;
                    var ns = md.GetString(t.Namespace);
                    return ns.Length > 0 ? ns + "." + n : n;
                }
                case HandleKind.TypeSpecification:
                {
                    var sig = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    if (sig.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "";
                    sig.ReadSignatureTypeCode();
                    return NameOf(md, sig.ReadTypeHandle());
                }
                default:
                    return "";
            }
        }
        catch { return ""; }
    }
}
