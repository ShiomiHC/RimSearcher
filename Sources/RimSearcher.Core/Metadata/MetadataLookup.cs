using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using RimSearcher.Sources;

namespace RimSearcher.Metadata;

public sealed record TypeHit
{
    public required ResolvedAssembly Assembly { get; init; }
    public required TypeDefinitionHandle Handle { get; init; }

    public required string FullName { get; init; }

    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public bool CompilerGenerated => Name.StartsWith('<');
}

public sealed record MethodHit
{
    public required TypeHit Type { get; init; }
    public required MethodDefinitionHandle Handle { get; init; }

    public required string Name { get; init; }

    public required string SourceName { get; init; }

    public required string Signature { get; init; }

    public IReadOnlyList<string> ParameterTypes { get; init; } = [];

    public bool Overloaded { get; init; }

    public string DisplayName => Overloaded ? $"{Name}({string.Join(", ", ParameterTypes)})" : Name;

    public required bool Bodyless { get; init; }

    public string? BodyKind { get; init; }

    public MethodHit? StateMachine { get; init; }

    public string? StateMachineKind { get; init; }
}

public sealed record MemberRow
{
    public required TypeHit Type { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }
    public required string Signature { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsAbstract { get; init; }

    public required bool IsOverride { get; init; }

    public required string Accessibility { get; init; }

    public required MethodDefinitionHandle Handle { get; init; }
}

public enum CodeTag { Normal, Hidden, Marked }

public sealed record CodeNote(ResolvedAssembly Assembly, string Noun, bool Hidden);

public sealed class MetadataLookup : IDisposable
{
    private readonly List<(ResolvedAssembly Asm, PEFile File)> _open = [];

    private readonly Func<string, int> _tier;

    private IReadOnlyList<ResolvedAssembly>? _lastResort;

    private const int LastTier = SourcePlanner.NameTiers - 1;

    private readonly Func<ResolvedAssembly, CodeTag> _tag;
    private readonly Dictionary<ResolvedAssembly, CodeTag> _tags = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);

    public MetadataLookup(IEnumerable<ResolvedAssembly> assemblies, Func<string, int>? tier = null,
                          Func<ResolvedAssembly, CodeTag>? tag = null, IReadOnlyList<ResolvedAssembly>? lastResort = null)
    {
        _tier = tier ?? (_ => 0);
        _tag = tag ?? (_ => CodeTag.Normal);
        _lastResort = lastResort;
        Open(assemblies);
    }

    private void Open(IEnumerable<ResolvedAssembly> assemblies)
    {
        foreach (var a in assemblies)
        {
            try { _open.Add((a, new PEFile(a.Path))); }
            catch
            {
                Unreadable.Add(a);
            }
        }
    }

    public List<ResolvedAssembly> Unreadable { get; } = [];

    public IReadOnlyList<ResolvedAssembly> Assemblies => Visible.Select(o => o.Asm).ToList();

    public CodeTag TagOf(ResolvedAssembly asm)
        => _tags.TryGetValue(asm, out var t) ? t : _tags[asm] = _tag(asm);

    public List<CodeNote> Notes { get; } = [];

    private int _quiet;

    public IDisposable Unnoted()
    {
        _quiet++;
        return new Release(() => _quiet--);
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    public void Note(ResolvedAssembly asm, string noun, string key)
    {
        var tag = TagOf(asm);
        if (tag == CodeTag.Normal || _quiet > 0) return;
        if (_noted.Add($"{noun}\n{asm.Tree}\n{asm.Name}\n{key}")) Notes.Add(new CodeNote(asm, noun, tag == CodeTag.Hidden));
    }

    public bool HasHidden => _open.Any(o => TagOf(o.Asm) == CodeTag.Hidden);

    public bool LastResort { get; private set; }

    public Action<IReadOnlyList<ResolvedAssembly>>? LastResortOpened { get; set; }

    public bool IsLastResort(TypeHit type) => _tier(type.Assembly.Tree) == LastTier;

    public void Ask(IReadOnlyList<string> names, Func<bool>? orElse = null)
    {
        LastResort = false;
        using var quiet = Unnoted();
        if (_lastResort is null && !_open.Any(o => _tier(o.Asm.Tree) == LastTier)) return;
        var hits = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => (Name: n, Found: FindTypes(n)))
                        .FirstOrDefault(h => h.Found.Count > 0);
        if (hits.Found is { Count: > 0 } && (!hits.Name.Contains('.') || hits.Found.Any(t => IsNamed(t, hits.Name)))) return;
        if (hits.Found is not { Count: > 0 } && orElse?.Invoke() == true) return;

        if (_lastResort is { } last)
        {
            _lastResort = null;
            Open(last);
            LastResortOpened?.Invoke(last);
        }
        LastResort = hits.Found is not { Count: > 0 }
                     || TypesIn(_open.Where(o => _tier(o.Asm.Tree) == LastTier)).Any(t => IsNamed(t, hits.Name));
    }

    public void ReadLike(TypeHit type) => LastResort = IsLastResort(type);

    private static bool IsNamed(TypeHit t, string name)
        => string.Equals(t.FullName, name.Replace('/', '+'), StringComparison.OrdinalIgnoreCase);

    private IEnumerable<(ResolvedAssembly Asm, PEFile File)> InReach
        => LastResort ? _open : _open.Where(o => _tier(o.Asm.Tree) < LastTier);

    private IEnumerable<(ResolvedAssembly Asm, PEFile File)> Visible => InReach.Where(o => TagOf(o.Asm) != CodeTag.Hidden);

    public HashSet<string> TreesInAnswer { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> TreesSearched { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AnsweredFromEveryTree() => TreesSearched.UnionWith(Visible.Select(o => o.Asm.Tree));

    public int AssemblyCount => Visible.Count();

    public PEFile? FileOf(ResolvedAssembly asm)
        => _open.FirstOrDefault(o => ReferenceEquals(o.Asm, asm)).File;

    public IEnumerable<TypeHit> AllTypes(bool withHidden = false) => TypesIn(withHidden ? InReach : Visible);

    private IEnumerable<(ResolvedAssembly Asm, PEFile File)> AtTier(int tier)
        => InReach.Where(o => _tier(o.Asm.Tree) == tier);

    private static IEnumerable<TypeHit> TypesIn(IEnumerable<(ResolvedAssembly Asm, PEFile File)> open)
    {
        foreach (var (asm, file) in open)
        {
            var md = file.Metadata;
            foreach (var h in md.TypeDefinitions)
            {
                var hit = Describe(asm, md, h);
                if (hit is not null) yield return hit;
            }
        }
    }

    public IReadOnlyList<TypeHit> FindTypes(string name)
    {
        if (name.Length == 0) return [];
        var wanted = name.Replace('/', '+');
        Func<TypeHit, bool>[] steps =
        [
            t => IsNamed(t, wanted),
            t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase),
            t => t.FullName.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)
              || t.FullName.EndsWith("+" + wanted, StringComparison.OrdinalIgnoreCase),
        ];
        var hiddenNoted = false;
        foreach (var step in steps)
            for (var tier = 0; tier <= LastTier; tier++)
            {
                var all = TypesIn(AtTier(tier)).Where(step).ToList();
                var hits = all.Where(t => TagOf(t.Assembly) != CodeTag.Hidden).ToList();
                if (!hiddenNoted && all.Count > hits.Count)
                {
                    hiddenNoted = true;
                    foreach (var t in all.Where(t => TagOf(t.Assembly) == CodeTag.Hidden)) Note(t.Assembly, "type", t.FullName);
                }
                if (hits.Count > 0)
                {
                    foreach (var t in hits) Note(t.Assembly, "type", t.FullName);
                    return hits;
                }
            }
        return [];
    }

    public IReadOnlyList<MethodHit> FindMethods(TypeHit type, string memberName)
    {
        var file = FileOf(type.Assembly);
        if (file is null) return [];
        var md = file.Metadata;
        var def = md.GetTypeDefinition(type.Handle);
        var result = new List<MethodHit>();

        var ctor = SymbolRef.IsConstructorName(memberName);
        var wantStatic = memberName is ".cctor" or "#cctor";

        foreach (var mh in def.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var il = md.GetString(m.Name);

            bool match;
            if (ctor)
                match = wantStatic ? il == ".cctor" : il == ".ctor";
            else
                match = string.Equals(il, memberName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(il, "get_" + memberName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(il, "set_" + memberName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(il, "add_" + memberName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(il, "remove_" + memberName, StringComparison.OrdinalIgnoreCase)
                     || il.EndsWith("." + memberName, StringComparison.OrdinalIgnoreCase);

            if (!match) continue;
            result.Add(Describe(type, md, mh, m));
        }

        return result;
    }

    public bool IsConstant(TypeHit type, FieldDefinitionHandle field, out bool inEnum)
    {
        inEnum = false;
        if (FileOf(type.Assembly) is not { } file) return false;
        var md = file.Metadata;
        if ((md.GetFieldDefinition(field).Attributes & FieldAttributes.Literal) == 0) return false;
        var baseType = md.GetTypeDefinition(type.Handle).BaseType;
        inEnum = baseType.Kind == HandleKind.TypeReference
              && md.GetTypeReference((TypeReferenceHandle)baseType) is var r
              && md.GetString(r.Name) == "Enum" && md.GetString(r.Namespace) == "System";
        return true;
    }

    public IReadOnlyList<(FieldDefinitionHandle Handle, string Name)> FindFields(TypeHit type, string name)
    {
        var file = FileOf(type.Assembly);
        if (file is null) return [];
        var md = file.Metadata;
        var result = new List<(FieldDefinitionHandle, string)>();
        foreach (var fh in md.GetTypeDefinition(type.Handle).GetFields())
        {
            var n = md.GetString(md.GetFieldDefinition(fh).Name);
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) result.Add((fh, n));
        }
        return result;
    }

    public IReadOnlyList<(string Name, IReadOnlyList<MethodDefinitionHandle> Accessors, FieldDefinitionHandle Backing)>
        FindProperties(TypeHit type, string name)
    {
        var file = FileOf(type.Assembly);
        if (file is null) return [];
        var md = file.Metadata;
        var def = md.GetTypeDefinition(type.Handle);
        var result = new List<(string, IReadOnlyList<MethodDefinitionHandle>, FieldDefinitionHandle)>();
        foreach (var ph in def.GetProperties())
        {
            var p = md.GetPropertyDefinition(ph);
            var n = md.GetString(p.Name);
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;

            var acc = p.GetAccessors();
            var accessors = new List<MethodDefinitionHandle>();
            if (!acc.Getter.IsNil) accessors.Add(acc.Getter);
            if (!acc.Setter.IsNil) accessors.Add(acc.Setter);

            var backing = default(FieldDefinitionHandle);
            foreach (var fh in def.GetFields())
                if (md.GetString(md.GetFieldDefinition(fh).Name) == "<" + n + ">k__BackingField") backing = fh;

            result.Add((n, accessors, backing));
        }
        return result;
    }

    public (IReadOnlyList<int> Methods, IReadOnlyList<int> Fields, IReadOnlyList<int> Setters) DeclaredTokens(TypeHit type)
    {
        var file = FileOf(type.Assembly);
        if (file is null) return ([], [], []);
        var md = file.Metadata;
        var def = md.GetTypeDefinition(type.Handle);
        return ([.. def.GetMethods().Select(h => MetadataTokens.GetToken(h))],
                [.. def.GetFields().Select(h => MetadataTokens.GetToken(h))],
                [.. def.GetProperties().Select(h => md.GetPropertyDefinition(h).GetAccessors().Setter)
                                       .Where(h => !h.IsNil).Select(h => MetadataTokens.GetToken(h))]);
    }

    public (TypeHit Type, string? Member)? ResolveTarget(string tree, string assemblyName, int token)
    {
        if (OpenFor(tree, assemblyName) is not { } open) return null;
        var (asm, file) = open;
        try
        {
            var md = file.Metadata;
            var handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.FieldDefinition:
                {
                    var f = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return Describe(asm, md, f.GetDeclaringType()) is { } t ? (t, md.GetString(f.Name)) : null;
                }
                case HandleKind.TypeDefinition:
                    return Describe(asm, md, (TypeDefinitionHandle)handle) is { } ty ? (ty, null) : null;
                case HandleKind.MethodDefinition:
                {
                    var m = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return Describe(asm, md, m.GetDeclaringType()) is { } t ? (t, md.GetString(m.Name)) : null;
                }
                default:
                    return null;
            }
        }
        catch { return null; }
    }

    public IReadOnlyList<MemberRow> Members(TypeHit type)
    {
        var file = FileOf(type.Assembly);
        if (file is null) return [];
        var md = file.Metadata;
        var def = md.GetTypeDefinition(type.Handle);
        var rows = new List<MemberRow>();

        foreach (var mh in def.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var a = m.Attributes;
            var name = md.GetString(m.Name);
            rows.Add(new MemberRow
            {
                Type = type,
                Kind = name is ".ctor" or ".cctor" ? "constructor" : "method",
                Name = name,
                Signature = SignatureText.Method(md, m, name),
                IsStatic = (a & MethodAttributes.Static) != 0,
                IsVirtual = (a & MethodAttributes.Virtual) != 0,
                IsAbstract = (a & MethodAttributes.Abstract) != 0,
                IsOverride = (a & MethodAttributes.Virtual) != 0 && (a & MethodAttributes.NewSlot) == 0,
                Accessibility = Access(a),
                Handle = mh,
            });
        }

        foreach (var ph in def.GetProperties())
        {
            var p = md.GetPropertyDefinition(ph);
            var acc = p.GetAccessors();
            var getter = acc.Getter.IsNil ? null : (MethodDefinition?)md.GetMethodDefinition(acc.Getter);
            var accessor = getter ?? (acc.Setter.IsNil ? null : (MethodDefinition?)md.GetMethodDefinition(acc.Setter));
            var attrs = accessor?.Attributes ?? default;
            rows.Add(new MemberRow
            {
                Type = type,
                Kind = "property",
                Name = md.GetString(p.Name),
                Signature = (acc.Getter.IsNil ? "" : "get; ") + (acc.Setter.IsNil ? "" : "set; "),
                IsStatic = (attrs & MethodAttributes.Static) != 0,
                IsVirtual = (attrs & MethodAttributes.Virtual) != 0,
                IsAbstract = (attrs & MethodAttributes.Abstract) != 0,
                IsOverride = (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.NewSlot) == 0,
                Accessibility = accessor is null ? "" : Access(attrs),
                Handle = acc.Getter.IsNil ? acc.Setter : acc.Getter,
            });
        }

        foreach (var fh in def.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            rows.Add(new MemberRow
            {
                Type = type,
                Kind = "field",
                Name = md.GetString(f.Name),
                Signature = "",
                IsStatic = (f.Attributes & FieldAttributes.Static) != 0,
                IsVirtual = false,
                IsAbstract = false,
                IsOverride = false,
                Accessibility = FieldAccess(f.Attributes),
                Handle = default,
            });
        }

        foreach (var eh in def.GetEvents())
        {
            var e = md.GetEventDefinition(eh);
            var acc = e.GetAccessors();
            var accessor = acc.Adder.IsNil ? acc.Remover : acc.Adder;
            var attrs = accessor.IsNil ? default : md.GetMethodDefinition(accessor).Attributes;
            rows.Add(new MemberRow
            {
                Type = type,
                Kind = "event",
                Name = md.GetString(e.Name),
                Signature = "",
                IsStatic = (attrs & MethodAttributes.Static) != 0,
                IsVirtual = (attrs & MethodAttributes.Virtual) != 0,
                IsAbstract = (attrs & MethodAttributes.Abstract) != 0,
                IsOverride = (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.NewSlot) == 0,
                Accessibility = accessor.IsNil ? "" : Access(attrs),
                Handle = default,
            });
        }

        return rows;
    }

    private static string Access(MethodAttributes a) => (a & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family => "protected",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.FamANDAssem => "private protected",
        MethodAttributes.Private => "private",
        _ => "",
    };

    private static string FieldAccess(FieldAttributes a) => (a & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family => "protected",
        FieldAttributes.Assembly => "internal",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.FamANDAssem => "private protected",
        FieldAttributes.Private => "private",
        _ => "",
    };

    public IReadOnlyList<MemberRow> FindMembersAnywhere(string name, int cap, bool substring = false)
    {
        var result = new List<MemberRow>();
        if (name.Length == 0) return result;

        for (var tier = 0; tier <= LastTier && result.Count == 0; tier++)
        foreach (var (asm, file) in AtTier(tier))
        {
            var md = file.Metadata;
            foreach (var th in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(th);
                if (!DeclaresName(md, def, name, substring)) continue;

                var type = Describe(asm, md, th);
                if (type is null || type.CompilerGenerated) continue;

                foreach (var row in Members(type))
                {
                    if (!Matches(row.Name, name, substring)) continue;
                    Note(asm, "member", $"{type.FullName}::{row.Name}");
                    if (TagOf(asm) == CodeTag.Hidden) continue;
                    result.Add(row);
                    if (result.Count >= cap) return result;
                }
            }
        }
        return result;
    }

    private static bool Matches(string have, string want, bool substring)
        => substring
            ? have.Contains(want, StringComparison.OrdinalIgnoreCase)
            : string.Equals(have, want, StringComparison.OrdinalIgnoreCase);

    private static bool DeclaresName(MetadataReader md, TypeDefinition def, string name, bool substring)
    {
        foreach (var mh in def.GetMethods())
            if (Matches(md.GetString(md.GetMethodDefinition(mh).Name), name, substring)) return true;
        foreach (var ph in def.GetProperties())
            if (Matches(md.GetString(md.GetPropertyDefinition(ph).Name), name, substring)) return true;
        foreach (var fh in def.GetFields())
            if (Matches(md.GetString(md.GetFieldDefinition(fh).Name), name, substring)) return true;
        return false;
    }

    public IReadOnlyList<MethodHit> FindMethodsAnywhere(string memberName, int cap)
    {
        var result = new List<MethodHit>();
        for (var tier = 0; tier <= LastTier && result.Count == 0; tier++)
        foreach (var (asm, file) in AtTier(tier))
        {
            var md = file.Metadata;
            foreach (var th in md.TypeDefinitions)
            {
                var type = Describe(asm, md, th);
                if (type is null) continue;
                var def = md.GetTypeDefinition(th);
                foreach (var mh in def.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (!string.Equals(md.GetString(m.Name), memberName, StringComparison.OrdinalIgnoreCase)) continue;
                    Note(asm, "method", $"{type.FullName}::{md.GetString(m.Name)}");
                    if (TagOf(asm) == CodeTag.Hidden) continue;
                    result.Add(Describe(type, md, mh, m));
                    if (result.Count >= cap) return result;
                }
            }
        }
        return result;
    }

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, Guid>>? _references;

    public void PinReferences(IReadOnlyDictionary<string, IReadOnlyDictionary<string, Guid>> references)
        => _references = references;

    public ResolvedAssembly? CopyFor(string tree, string assemblyName) => OpenFor(tree, assemblyName)?.Asm;

    private (ResolvedAssembly Asm, PEFile File)? OpenFor(string tree, string assemblyName)
    {
        Guid? want = _references?.GetValueOrDefault(tree) is { } r && r.TryGetValue(assemblyName, out var id) ? id : null;
        (ResolvedAssembly, PEFile)? first = null, recorded = null;
        foreach (var (asm, file) in _open)
        {
            if (!string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(asm.Tree, tree, StringComparison.OrdinalIgnoreCase)) return (asm, file);
            if (recorded is null && want is { } w && MvidOf(file) == w) recorded = (asm, file);
            first ??= (asm, file);
        }
        return recorded ?? first;
    }

    public IReadOnlyCollection<Guid> Mvids(string assemblyName)
        => [.. _open.Where(o => string.Equals(o.Asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    .Select(o => MvidOf(o.File)).OfType<Guid>().Distinct()];

    private static Guid? MvidOf(PEFile file)
    {
        try { return file.Metadata.GetGuid(file.Metadata.GetModuleDefinition().Mvid); }
        catch { return null; }
    }

    public MethodHit? ResolveToken(string tree, string assemblyName, int token)
    {
        if (OpenFor(tree, assemblyName) is not { } open) return null;
        var (asm, file) = open;
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.MethodDefinition) return null;

            var md = file.Metadata;
            var mh = (MethodDefinitionHandle)handle;
            var m = md.GetMethodDefinition(mh);
            var type = Describe(asm, md, m.GetDeclaringType());
            if (type is null) return null;

            var il = md.GetString(m.Name);
            return new MethodHit
            {
                Type = type,
                Handle = mh,
                Name = il,
                SourceName = SourceNameOf(il),
                Signature = SignatureText.Method(md, m, il),
                ParameterTypes = SignatureText.ParameterTypes(md, m),
                Overloaded = IsOverloaded(md, m, il),
                Bodyless = m.RelativeVirtualAddress == 0,
                BodyKind = BodyKindOf(m),
            };
        }
        catch { return null; }
    }

    private MethodHit Describe(TypeHit type, MetadataReader md, MethodDefinitionHandle mh, MethodDefinition m)
    {
        var il = md.GetString(m.Name);
        var hit = new MethodHit
        {
            Type = type,
            Handle = mh,
            Name = il,
            SourceName = SourceNameOf(il),
            Signature = SignatureText.Method(md, m, il),
            ParameterTypes = SignatureText.ParameterTypes(md, m),
            Overloaded = IsOverloaded(md, m, il),
            Bodyless = m.RelativeVirtualAddress == 0,
            BodyKind = BodyKindOf(m),
        };

        var sm = FindStateMachine(type, md, m);
        return sm is null ? hit : hit with { StateMachine = sm.Value.Hit, StateMachineKind = sm.Value.Kind };
    }

    private static bool IsOverloaded(MetadataReader md, MethodDefinition m, string il)
    {
        var same = 0;
        foreach (var h in md.GetTypeDefinition(m.GetDeclaringType()).GetMethods())
            if (md.StringComparer.Equals(md.GetMethodDefinition(h).Name, il) && ++same > 1) return true;
        return false;
    }

    internal static string? BodyKindOf(MethodDefinition m)
    {
        if (m.RelativeVirtualAddress != 0) return null;
        if ((m.Attributes & MethodAttributes.Abstract) != 0) return "abstract";
        if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return "extern";
        if ((m.ImplAttributes & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0) return "runtime";
        return "no-body";
    }

    private (MethodHit Hit, string Kind)? FindStateMachine(TypeHit type, MetadataReader md, MethodDefinition m)
    {
        foreach (var ah in m.GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(ah);
            var name = AttributeTypeName(md, attr);
            var kind = name switch
            {
                "IteratorStateMachineAttribute" => "迭代器",
                "AsyncStateMachineAttribute" => "异步",
                "AsyncIteratorStateMachineAttribute" => "异步迭代器",
                _ => null,
            };
            if (kind is null) continue;

            var declaring = md.GetTypeDefinition(type.Handle);
            foreach (var nh in declaring.GetNestedTypes())
            {
                var nested = md.GetTypeDefinition(nh);
                var nestedName = md.GetString(nested.Name);
                if (!nestedName.StartsWith('<')) continue;

                var close = nestedName.IndexOf('>');
                if (close <= 1) continue;
                if (!string.Equals(nestedName[1..close], md.GetString(m.Name), StringComparison.Ordinal)) continue;

                foreach (var smh in nested.GetMethods())
                {
                    var sm = md.GetMethodDefinition(smh);
                    if (md.GetString(sm.Name) != "MoveNext") continue;
                    var nestedType = Describe(type.Assembly, md, nh);
                    if (nestedType is null) continue;
                    return (new MethodHit
                    {
                        Type = nestedType,
                        Handle = smh,
                        Name = "MoveNext",
                        SourceName = "MoveNext",
                        Signature = SignatureText.Method(md, sm, "MoveNext"),
                        Bodyless = sm.RelativeVirtualAddress == 0,
                        BodyKind = BodyKindOf(sm),
                    }, kind);
                }
            }
        }
        return null;
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        try
        {
            return attr.Constructor.Kind switch
            {
                HandleKind.MemberReference =>
                    md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent is { Kind: HandleKind.TypeReference } p
                        ? md.GetString(md.GetTypeReference((TypeReferenceHandle)p).Name)
                        : null,
                HandleKind.MethodDefinition =>
                    md.GetString(md.GetTypeDefinition(
                        md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType()).Name),
                _ => null,
            };
        }
        catch { return null; }
    }

    public static string SourceNameOf(string ilName)
    {
        foreach (var p in new[] { "get_", "set_", "add_", "remove_" })
            if (ilName.StartsWith(p, StringComparison.Ordinal) && ilName.Length > p.Length)
                return ilName[p.Length..];
        return ilName;
    }

    private static TypeHit? Describe(ResolvedAssembly asm, MetadataReader md, TypeDefinitionHandle h)
    {
        try
        {
            var t = md.GetTypeDefinition(h);
            var name = md.GetString(t.Name);
            var ns = md.GetString(t.Namespace);

            if (t.IsNested)
            {
                var parts = new List<string> { name };
                var cur = t;
                while (cur.IsNested)
                {
                    var outer = md.GetTypeDefinition(cur.GetDeclaringType());
                    parts.Insert(0, md.GetString(outer.Name));
                    cur = outer;
                }
                ns = md.GetString(cur.Namespace);
                var full = string.Join("+", parts);
                return new TypeHit
                {
                    Assembly = asm,
                    Handle = h,
                    Name = name,
                    Namespace = ns,
                    FullName = ns.Length > 0 ? ns + "." + full : full,
                };
            }

            return new TypeHit
            {
                Assembly = asm,
                Handle = h,
                Name = name,
                Namespace = ns,
                FullName = ns.Length > 0 ? ns + "." + name : name,
            };
        }
        catch { return null; }
    }

    public void Dispose()
    {
        foreach (var (_, f) in _open) f.Dispose();
        _open.Clear();
    }
}

public static class SignatureText
{
    public static string Method(MetadataReader md, MethodDefinition m, string name)
    {
        try
        {
            var sig = m.DecodeSignature(new TypeNameProvider(), genericContext: null);
            var ps = string.Join(", ", sig.ParameterTypes);
            var prefix = (m.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
            var arity = m.GetGenericParameters().Count;
            var generic = arity > 0 ? $"`{arity}" : "";
            return $"{prefix}{sig.ReturnType} {name}{generic}({ps})";
        }
        catch { return name + "(?)"; }
    }

    public static IReadOnlyList<string> ParameterTypes(MetadataReader md, MethodDefinition m)
    {
        try { return m.DecodeSignature(new TypeNameProvider(), genericContext: null).ParameterTypes; }
        catch { return []; }
    }

    public static bool ParametersMatch(IReadOnlyList<string> have, string written)
    {
        var want = SplitTopLevel(written).Select(Canon).Where(p => p.Length > 0).ToList();
        return want.Count == have.Count && want.Zip(have).All(p => p.First == Canon(p.Second));
    }

    private static readonly Dictionary<string, string> ClrAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boolean"] = "bool", ["Byte"] = "byte", ["SByte"] = "sbyte", ["Char"] = "char", ["Int16"] = "short",
        ["UInt16"] = "ushort", ["Int32"] = "int", ["UInt32"] = "uint", ["Int64"] = "long", ["UInt64"] = "ulong",
        ["Single"] = "float", ["Double"] = "double", ["String"] = "string", ["Object"] = "object", ["Void"] = "void",
    };

    private static string Canon(string p)
    {
        var s = p.Trim();
        foreach (var mod in new[] { "ref ", "out ", "in ", "params ", "this " })
            if (s.StartsWith(mod, StringComparison.Ordinal)) s = s[mod.Length..].TrimStart() + (mod is "params " or "this " ? "" : "&");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\w`]+(?:\.[\w`]+)+", m => m.Value[(m.Value.LastIndexOf('.') + 1)..]);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\w+", m => ClrAliases.TryGetValue(m.Value, out var a) ? a : m.Value);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"`\d+", "");
        return s.Replace(" ", "").ToLowerInvariant();
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '<' or '[' or '(') depth++;
            else if (s[i] is '>' or ']' or ')') depth--;
            else if (s[i] == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string t, ArrayShape s) => t + "[]";
        public string GetByReferenceType(string t) => t + "&";
        public string GetFunctionPointerType(MethodSignature<string> s) => "delegate*";
        public string GetGenericInstantiation(string t, System.Collections.Immutable.ImmutableArray<string> args) => $"{t}<{string.Join(", ", args)}>";
        public string GetGenericMethodParameter(object? _, int i) => "!!" + i;
        public string GetGenericTypeParameter(object? _, int i) => "!" + i;
        public string GetModifiedType(string mod, string un, bool req) => un;
        public string GetPinnedType(string t) => t;
        public string GetPointerType(string t) => t + "*";
        public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => c.ToString(),
        };
        public string GetSZArrayType(string t) => t + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte _)
            => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte _)
            => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object? g, TypeSpecificationHandle h, byte _)
            => r.GetTypeSpecification(h).DecodeSignature(this, g);
    }
}
