using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using RimSearcher.Sources;
using RimTooling.IO;
using SRE = System.Reflection.Emit;

namespace RimSearcher.Metadata;

public readonly record struct CallEdge(string FromAssembly, int FromToken, string ToAssembly, int ToToken);

public enum AccessKind : byte
{
    Read = 1,
    Write = 2,
    Address = 3,
    TypeCheck = 4,
    Cast = 5,
    TypeOf = 6,
    TypeArgument = 7,
    TypeOperand = 8,
}

public readonly record struct AccessEdge(string FromAssembly, int FromToken, string ToAssembly, int ToToken, AccessKind Kind);

public static class AccessKindText
{
    public static string Render(AccessKind kind) => kind switch
    {
        AccessKind.Read => "read",
        AccessKind.Write => "write",
        AccessKind.Address => "address",
        AccessKind.TypeCheck => "type-check",
        AccessKind.Cast => "cast",
        AccessKind.TypeOf => "typeof",
        AccessKind.TypeArgument => "type-argument",
        AccessKind.TypeOperand => "type-operand",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool IsWrite(AccessKind kind) => kind is AccessKind.Write or AccessKind.Address;

    public static string Shown(string access) => access switch
    {
        "call" => "调用",
        "read" => "读取",
        "write" => "写入",
        "address" => "取地址",
        "type-check" => "类型检查",
        "cast" => "强制转换",
        "type-argument" => "类型实参",
        "type-operand" => "类型操作数",
        _ => access,
    };
}

public sealed class CallGraph
{
    public required string Tree { get; init; }
    public required IReadOnlyList<CallEdge> Edges { get; init; }

    public required int Unresolved { get; init; }

    public IReadOnlyList<AccessEdge>? Accesses { get; init; }

    public int AccessUnresolved { get; init; }

    public IReadOnlyDictionary<(string Assembly, int Token), (string Type, string Member)> Targets { get; init; }
        = new Dictionary<(string, int), (string, string)>();

    public required IReadOnlyList<string> SourceHashes { get; init; }
}

public static class CallGraphFreshness
{
    public static List<string> Changed(
        IReadOnlyDictionary<string, Guid> recorded, Func<string, IReadOnlyCollection<Guid>?> current)
        => [.. recorded.Where(r => current(r.Key) is { Count: > 0 } now && !now.Contains(r.Value))
                       .Select(r => r.Key)
                       .Order(StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> Index(IEnumerable<string> dlls)
    {
        var map = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dlls.Distinct(StringComparer.OrdinalIgnoreCase))
            if (PeImage.TryReadIdentity(dll, out var id))
                foreach (var name in new[] { Path.GetFileNameWithoutExtension(dll), id.AssemblyName })
                {
                    if (!map.TryGetValue(name, out var set)) map[name] = set = [];
                    set.Add(id.Mvid);
                }
        return map.ToDictionary(p => p.Key, p => (IReadOnlyCollection<Guid>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static Guid? Mvid(string dll) => PeImage.TryReadIdentity(dll, out var id) ? id.Mvid : null;
}

public static class CallGraphStore
{
    public const string FileName = ".callgraph";

    private const uint Magic = 0x47435352;
    public const int Version = 6;

    public static string PathIn(string treeDir) => Path.Combine(treeDir, FileName);

    public static void Write(string treeDir, CallGraph graph)
    {
        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string n)
        {
            if (index.TryGetValue(n, out var v)) return v;
            index[n] = names.Count;
            names.Add(n);
            return names.Count - 1;
        }

        var rows = graph.Edges
            .Select(e => (fa: Id(e.FromAssembly), ft: e.FromToken, ta: Id(e.ToAssembly), tt: e.ToToken))
            .ToList();
        var accesses = (graph.Accesses ?? [])
            .Select(e => (fa: Id(e.FromAssembly), ft: e.FromToken, ta: Id(e.ToAssembly), tt: e.ToToken, k: (byte)e.Kind))
            .ToList();
        var targets = graph.Targets
            .Select(t => (a: Id(t.Key.Assembly), tt: t.Key.Token, ty: Id(t.Value.Type), m: Id(t.Value.Member)))
            .ToList();

        AtomicFile.Write(PathIn(treeDir), tmp =>
        {
            using var fs = File.Create(tmp);
            using var w = new BinaryWriter(fs);
            w.Write(Magic);
            w.Write(Version);
            w.Write(graph.Unresolved);

            w.Write(graph.SourceHashes.Count);
            foreach (var h in graph.SourceHashes) w.Write(h);

            w.Write(names.Count);
            foreach (var n in names) w.Write(n);

            w.Write(targets.Count);
            foreach (var (a, tt, ty, m) in targets)
            {
                w.Write(a); w.Write(tt); w.Write(ty); w.Write(m);
            }

            w.Write(rows.Count);
            foreach (var (fa, ft, ta, tt) in rows)
            {
                w.Write(fa); w.Write(ft); w.Write(ta); w.Write(tt);
            }

            w.Write(graph.AccessUnresolved);
            w.Write(accesses.Count);
            foreach (var (fa, ft, ta, tt, k) in accesses)
            {
                w.Write(fa); w.Write(ft); w.Write(ta); w.Write(tt); w.Write(k);
            }
        });
    }

    public sealed record Header(int Edges, int Accesses, IReadOnlyList<string> SourceHashes);

    public static Header? Peek(string treeDir)
    {
        var path = PathIn(treeDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != Version) return null;

            r.ReadInt32();

            var hashCount = r.ReadInt32();
            var hashes = new List<string>(hashCount);
            for (var i = 0; i < hashCount; i++) hashes.Add(r.ReadString());

            var nameCount = r.ReadInt32();
            for (var i = 0; i < nameCount; i++) r.ReadString();
            fs.Seek(16L * r.ReadInt32(), SeekOrigin.Current);

            var edges = r.ReadInt32();
            fs.Seek(16L * edges, SeekOrigin.Current);
            r.ReadInt32();
            return new Header(edges, r.ReadInt32(), hashes);
        }
        catch { return null; }
    }

    public static CallGraph? Read(string treeDir, bool withAccesses = false)
    {
        var path = PathIn(treeDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != Version) return null;

            var unresolved = r.ReadInt32();

            var hashCount = r.ReadInt32();
            var hashes = new List<string>(hashCount);
            for (var i = 0; i < hashCount; i++) hashes.Add(r.ReadString());

            var nameCount = r.ReadInt32();
            var names = new string[nameCount];
            for (var i = 0; i < nameCount; i++) names[i] = r.ReadString();

            var targetCount = r.ReadInt32();
            var targets = new Dictionary<(string, int), (string, string)>(targetCount, CallGraphBuilder.TargetKey.Instance);
            for (var i = 0; i < targetCount; i++)
            {
                var a = r.ReadInt32(); var tt = r.ReadInt32();
                var ty = r.ReadInt32(); var m = r.ReadInt32();
                targets[(names[a], tt)] = (names[ty], names[m]);
            }

            var edgeCount = r.ReadInt32();
            var edges = new List<CallEdge>(edgeCount);
            for (var i = 0; i < edgeCount; i++)
            {
                var fa = r.ReadInt32(); var ft = r.ReadInt32();
                var ta = r.ReadInt32(); var tt = r.ReadInt32();
                edges.Add(new CallEdge(names[fa], ft, names[ta], tt));
            }

            List<AccessEdge>? accesses = null;
            var accessUnresolved = 0;
            if (withAccesses)
            {
                accessUnresolved = r.ReadInt32();
                var count = r.ReadInt32();
                accesses = new List<AccessEdge>(count);
                for (var i = 0; i < count; i++)
                {
                    var fa = r.ReadInt32(); var ft = r.ReadInt32();
                    var ta = r.ReadInt32(); var tt = r.ReadInt32();
                    accesses.Add(new AccessEdge(names[fa], ft, names[ta], tt, (AccessKind)r.ReadByte()));
                }
            }

            return new CallGraph
            {
                Tree = Path.GetFileName(treeDir.TrimEnd(Path.DirectorySeparatorChar))!,
                Edges = edges,
                Unresolved = unresolved,
                Accesses = accesses,
                AccessUnresolved = accessUnresolved,
                SourceHashes = hashes,
                Targets = targets,
            };
        }
        catch { return null; }
    }
}

public readonly record struct CallSite(string Tree, CallEdge Edge);

public readonly record struct AccessSite(string Tree, AccessEdge Edge);

public sealed class CallGraphSet
{
    public required IReadOnlyList<CallGraph> Graphs { get; init; }

    public required IReadOnlyList<string> Without { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Guid>> References { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, Guid>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Guid>>> UnitReferences { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Guid>>>(StringComparer.OrdinalIgnoreCase);

    public int EdgeCount => Graphs.Sum(g => g.Edges.Count);

    public int Unresolved => Graphs.Sum(g => g.Unresolved);

    public IEnumerable<CallSite> CallersOf(string assembly, int token)
        => Sites(e => e.ToToken == token
                   && string.Equals(e.ToAssembly, assembly, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<CallSite> CalleesOf(string assembly, int token)
        => Sites(e => e.FromToken == token
                   && string.Equals(e.FromAssembly, assembly, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<CallSite> Sites(Func<CallEdge, bool> keep)
    {
        foreach (var g in Graphs)
            foreach (var e in g.Edges)
                if (keep(e)) yield return new CallSite(g.Tree, e);
    }

    public IEnumerable<AccessSite> AccessesTo(IReadOnlySet<(string Assembly, int Token)> targets)
    {
        foreach (var g in Graphs)
            foreach (var e in g.Accesses ?? [])
                if (targets.Contains((e.ToAssembly, e.ToToken))) yield return new AccessSite(g.Tree, e);
    }

    public (string Type, string Member)? TargetName(CallSite site)
    {
        foreach (var g in Graphs)
            if (string.Equals(g.Tree, site.Tree, StringComparison.OrdinalIgnoreCase))
                return g.Targets.TryGetValue((site.Edge.ToAssembly, site.Edge.ToToken), out var n) ? n : null;
        return null;
    }

    public IEnumerable<CallSite> CallsTo(IReadOnlySet<(string Assembly, int Token)> targets)
        => Sites(e => targets.Contains((e.ToAssembly, e.ToToken)));

    public static CallGraphSet Load(string root, IReadOnlyCollection<string>? only = null, bool withAccesses = false,
                                    IReadOnlyCollection<string>? also = null)
    {
        var graphs = new List<CallGraph>();
        var without = new List<string>();
        var references = new Dictionary<string, IReadOnlyDictionary<string, Guid>>(StringComparer.OrdinalIgnoreCase);
        var units = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Guid>>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return new CallGraphSet { Graphs = graphs, Without = without };

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir)!;
            if (name.Length == 0 || name.StartsWith('.')) continue;
            if (only is { Count: > 0 } ? !only.Contains(name, StringComparer.OrdinalIgnoreCase)
                                       : SourcePlanner.IsLastResort(name) && also?.Contains(name, StringComparer.OrdinalIgnoreCase) != true) continue;

            var g = CallGraphStore.Read(dir, withAccesses);
            if (g is null) { without.Add(name); continue; }
            var state = SourceTreeState.Read(dir);
            if (state is null || state.Assemblies.Any(a => state.ReferencesOf(a) is null)) { without.Add(name); continue; }
            graphs.Add(g);

            var byUnit = new Dictionary<string, IReadOnlyDictionary<string, Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in state.Assemblies) byUnit[KeptAssemblies.AssemblyName(dir, a)] = state.ReferencesOf(a)!;
            units[name] = byUnit;

            var merged = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (n, id) in byUnit.Values.SelectMany(r => r))
                if (merged.TryGetValue(n, out var seen) && seen != id) conflicting.Add(n);
                else merged[n] = id;
            foreach (var n in conflicting) merged.Remove(n);
            references[name] = merged;
        }

        return new CallGraphSet { Graphs = graphs, Without = without, References = references, UnitReferences = units };
    }
}

public static class CallGraphBuilder
{
    internal static readonly OpCode?[] OneByte = new OpCode?[256];
    internal static readonly OpCode?[] TwoByte = new OpCode?[256];

    static CallGraphBuilder()
    {
        foreach (var f in typeof(OpCodes).GetFields())
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = unchecked((ushort)op.Value);
            if (op.Size == 1) OneByte[v & 0xFF] = op; else TwoByte[v & 0xFF] = op;
        }
    }

    internal static int OperandSize(SRE.OperandType t, ReadOnlySpan<byte> rest) => t switch
    {
        SRE.OperandType.InlineNone => 0,
        SRE.OperandType.ShortInlineBrTarget or SRE.OperandType.ShortInlineI or SRE.OperandType.ShortInlineVar => 1,
        SRE.OperandType.InlineVar => 2,
        SRE.OperandType.InlineI8 or SRE.OperandType.InlineR => 8,
        SRE.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(rest),
        _ => 4,
    };

    public static CallGraph Build(
        string tree,
        IReadOnlyList<ResolvedAssembly> assemblies,
        ReferenceRecipe references,
        IReadOnlyList<string> sourceHashes,
        CancellationToken cancel = default)
        => Build(tree, assemblies, _ => references, sourceHashes, cancel);

    public static CallGraph Build(
        string tree,
        IReadOnlyList<ResolvedAssembly> assemblies,
        Func<ResolvedAssembly, ReferenceRecipe> references,
        IReadOnlyList<string> sourceHashes,
        CancellationToken cancel = default)
    {
        var sink = new Sink
        {
            Own = new HashSet<string>(assemblies.Select(a => a.Name), StringComparer.OrdinalIgnoreCase),
        };

        foreach (var asm in assemblies)
        {
            cancel.ThrowIfCancellationRequested();
            try { Scan(asm, references(asm), sink, cancel); }
            catch (OperationCanceledException) { throw; }
            catch
            {
            }
        }

        return new CallGraph
        {
            Tree = tree,
            Edges = sink.Calls,
            Unresolved = sink.CallUnresolved,
            Accesses = sink.Accesses,
            AccessUnresolved = sink.AccessUnresolved,
            SourceHashes = sourceHashes,
            Targets = sink.Targets,
        };
    }

    private sealed class Sink
    {
        public required HashSet<string> Own { get; init; }
        public readonly Dictionary<(string, int), (string, string)> Targets = new(TargetKey.Instance);
        public readonly List<CallEdge> Calls = [];
        public readonly List<AccessEdge> Accesses = [];
        public int CallUnresolved;
        public int AccessUnresolved;
    }

    private static int Token(IEntity e) => e.MetadataToken.IsNil ? 0 : MetadataTokens.GetToken(e.MetadataToken);

    private static bool CompilerOwned(IField f) => f.Name.StartsWith('<') && !f.Name.EndsWith("k__BackingField");

    private static AccessKind TypeKindOf(string opcode) => opcode switch
    {
        "isinst" => AccessKind.TypeCheck,
        "castclass" or "unbox" or "unbox.any" => AccessKind.Cast,
        "ldtoken" => AccessKind.TypeOf,
        _ => AccessKind.TypeOperand,
    };

    private static void Scan(
        ResolvedAssembly asm, ReferenceRecipe references, Sink sink, CancellationToken cancel)
    {
        using var file = new PEFile(asm.Path);
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        using var closing = references.Open(file);
        var module = new DecompilerTypeSystem(file, closing, settings).MainModule;

        var md = file.Metadata;
        var from = module.AssemblyName;
        var calls = new List<CallEdge>();
        var targets = new Dictionary<(string, int), (string, string)>(TargetKey.Instance);
        var accesses = new List<AccessEdge>();
        var callUnresolved = 0;
        var accessUnresolved = 0;

        foreach (var mh in md.MethodDefinitions)
        {
            cancel.ThrowIfCancellationRequested();
            var m = md.GetMethodDefinition(mh);
            if (m.RelativeVirtualAddress == 0) continue;

            ReadOnlySpan<byte> il;
            try { il = file.Reader.GetMethodBody(m.RelativeVirtualAddress).GetILContent().AsSpan(); }
            catch { continue; }

            var fromToken = MetadataTokens.GetToken(mh);

            void Access(IEntity target, AccessKind kind)
                => accesses.Add(new AccessEdge(from, fromToken, target.ParentModule?.AssemblyName ?? "?", Token(target), kind));

            void TypeArguments(IEnumerable<IType> args)
            {
                var stack = new Stack<IType>(args);
                while (stack.Count > 0)
                {
                    var a = stack.Pop();
                    foreach (var x in a.TypeArguments) stack.Push(x);
                    if (a is TypeWithElementType el) stack.Push(el.ElementType);
                    if (a.GetDefinition() is { } d) Access(d, AccessKind.TypeArgument);
                }
            }

            var i = 0;
            while (i < il.Length)
            {
                OpCode? op;
                int opSize;
                if (il[i] == 0xFE && i + 1 < il.Length) { op = TwoByte[il[i + 1]]; opSize = 2; }
                else { op = OneByte[il[i]]; opSize = 1; }

                if (op is null) break;

                var ot = op.Value.OperandType;
                var operand = OperandSize(ot, il[(i + opSize)..]);
                if (ot is SRE.OperandType.InlineMethod or SRE.OperandType.InlineField
                       or SRE.OperandType.InlineType or SRE.OperandType.InlineTok)
                {
                    var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il[(i + opSize)..]));
                    var kind = ot == SRE.OperandType.InlineTok ? TokenKind(md, handle) : ot;
                    if (kind == SRE.OperandType.InlineMethod)
                    {
                        try
                        {
                            var target = module.ResolveMethod(handle, new GenericContext());
                            if (target is null || target.DeclaringType.Kind == TypeKind.Unknown) callUnresolved++;
                            else
                            {
                                if (ot == SRE.OperandType.InlineMethod)
                                {
                                    var to = target.ParentModule?.AssemblyName ?? "?";
                                    calls.Add(new CallEdge(from, fromToken, to, Token(target)));
                                    if (!sink.Own.Contains(to) && target.DeclaringTypeDefinition is { } declaring)
                                        targets.TryAdd((to, Token(target)), (declaring.ReflectionName, target.Name));
                                }
                                TypeArguments(target.TypeArguments.Concat(target.DeclaringType.TypeArguments));
                            }
                        }
                        catch { callUnresolved++; }
                    }
                    else if (kind == SRE.OperandType.InlineField)
                    {
                        try
                        {
                            if (module.ResolveEntity(handle, new GenericContext()) is not IField f
                                || f.DeclaringType.Kind == TypeKind.Unknown) accessUnresolved++;
                            else
                            {
                                var def = (IField)f.MemberDefinition;
                                if (!CompilerOwned(def))
                                    Access(def, op.Value.Name switch
                                    {
                                        "stfld" or "stsfld" => AccessKind.Write,
                                        "ldflda" or "ldsflda" => AccessKind.Address,
                                        "ldtoken" => AccessKind.TypeOf,
                                        _ => AccessKind.Read,
                                    });
                                TypeArguments(f.DeclaringType.TypeArguments);
                            }
                        }
                        catch { accessUnresolved++; }
                    }
                    else
                    {
                        try
                        {
                            var ty = module.ResolveType(handle, new GenericContext());
                            if (ty.Kind == TypeKind.Unknown) accessUnresolved++;
                            else
                            {
                                var core = ty;
                                while (core is TypeWithElementType el) core = el.ElementType;
                                if (core.GetDefinition() is { } d) Access(d, TypeKindOf(op.Value.Name!));
                                TypeArguments(core.TypeArguments);
                            }
                        }
                        catch { accessUnresolved++; }
                    }
                }
                i += opSize + operand;
            }
        }

        sink.Calls.AddRange(calls);
        foreach (var t in targets) sink.Targets.TryAdd(t.Key, t.Value);
        sink.Accesses.AddRange(accesses);
        sink.CallUnresolved += callUnresolved;
        sink.AccessUnresolved += accessUnresolved;
    }

    internal sealed class TargetKey : IEqualityComparer<(string, int)>
    {
        public static readonly TargetKey Instance = new();
        public bool Equals((string, int) a, (string, int) b)
            => a.Item2 == b.Item2 && string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, int) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), k.Item2);
    }

    private static SRE.OperandType TokenKind(MetadataReader md, EntityHandle h) => h.Kind switch
    {
        HandleKind.FieldDefinition => SRE.OperandType.InlineField,
        HandleKind.MethodDefinition or HandleKind.MethodSpecification => SRE.OperandType.InlineMethod,
        HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)h).GetKind() == MemberReferenceKind.Field
            ? SRE.OperandType.InlineField : SRE.OperandType.InlineMethod,
        _ => SRE.OperandType.InlineType,
    };
}
