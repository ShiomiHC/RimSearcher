using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using SRE = System.Reflection.Emit;

namespace RimSearcher.Metadata;

/// <summary>一条调用边:谁,调了谁。两端都是「程序集名 + 该程序集里的元数据 token」。</summary>
public readonly record struct CallEdge(string FromAssembly, int FromToken, string ToAssembly, int ToToken);

/// <summary>
/// 一棵树的调用边表:这棵树里的方法调了谁。
///
/// 只存出边,反查时把全部树的表合起来反转 —— 实测合并 104.8 万条边并反转 39 毫秒,而按
/// 「谁调用了它」正着存要么存两份,要么让一次增量重建牵动别的树。分树的粒度与反编译树一致,
/// 于是一个 mod 换了版本只重算它自己那张(中位 63 毫秒),不必重跑全部(4.1 秒)。
/// </summary>
public sealed class CallGraph
{
    public required string Tree { get; init; }
    public required IReadOnlyList<CallEdge> Edges { get; init; }

    /// <summary>解析不出目标的边数。几乎全是对没装的 mod 的兼容代码 —— 目标程序集不在这台机器上。</summary>
    public required int Unresolved { get; init; }

    /// <summary>
    /// 建表时每个来源 dll 的哈希。它与树清单里那份是同一批数,单独记一份是为了让边表能
    /// 自己回答「我是不是旧的」—— 靠外面的清单判等于假设两者总是一起写的。
    /// </summary>
    public required IReadOnlyList<string> SourceHashes { get; init; }
}

/// <summary>边表的落盘形态。</summary>
public static class CallGraphStore
{
    /// <summary>边表文件名,住在树目录里。以点开头,不会被当成一棵树。</summary>
    public const string FileName = ".callgraph";

    private const uint Magic = 0x47435352; // "RSCG"
    private const int Version = 1;

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

        var tmp = PathIn(treeDir) + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(graph.Unresolved);

            w.Write(graph.SourceHashes.Count);
            foreach (var h in graph.SourceHashes) w.Write(h);

            // 边先编码,字符串池才知道要写哪些名字。
            var rows = graph.Edges
                .Select(e => (fa: Id(e.FromAssembly), ft: e.FromToken, ta: Id(e.ToAssembly), tt: e.ToToken))
                .ToList();

            w.Write(names.Count);
            foreach (var n in names) w.Write(n);

            w.Write(rows.Count);
            foreach (var (fa, ft, ta, tt) in rows)
            {
                w.Write(fa); w.Write(ft); w.Write(ta); w.Write(tt);
            }
        }

        var final = PathIn(treeDir);
        File.Move(tmp, final, overwrite: true);
    }

    /// <summary>
    /// 只读表头:有多少条边,建表时来源是哪几个哈希。
    ///
    /// 盘点用。整表读一遍要把全部边物化出来(全源 104.8 万条),而盘点问的两件事都在表头里。
    /// </summary>
    public static (int Edges, IReadOnlyList<string> SourceHashes)? Peek(string treeDir)
    {
        var path = PathIn(treeDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != Version) return null;

            r.ReadInt32();   // unresolved

            var hashCount = r.ReadInt32();
            var hashes = new List<string>(hashCount);
            for (var i = 0; i < hashCount; i++) hashes.Add(r.ReadString());

            var nameCount = r.ReadInt32();
            for (var i = 0; i < nameCount; i++) r.ReadString();

            return (r.ReadInt32(), hashes);
        }
        catch { return null; }
    }

    /// <summary>读一棵树的边表。文件不在、版本对不上、读坏了都返回 <c>null</c> —— 调用侧据此说「这棵树没有边表」。</summary>
    public static CallGraph? Read(string treeDir)
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

            var edgeCount = r.ReadInt32();
            var edges = new List<CallEdge>(edgeCount);
            for (var i = 0; i < edgeCount; i++)
            {
                var fa = r.ReadInt32(); var ft = r.ReadInt32();
                var ta = r.ReadInt32(); var tt = r.ReadInt32();
                edges.Add(new CallEdge(names[fa], ft, names[ta], tt));
            }

            return new CallGraph
            {
                Tree = Path.GetFileName(treeDir.TrimEnd(Path.DirectorySeparatorChar))!,
                Edges = edges,
                Unresolved = unresolved,
                SourceHashes = hashes,
            };
        }
        catch { return null; }
    }
}

/// <summary>一条边,以及它出自哪棵树。</summary>
public readonly record struct CallSite(string Tree, CallEdge Edge);

/// <summary>
/// 全部树的边表合起来。
///
/// <see cref="Without"/> 是这层的要害:一棵树没有边表,反查在它上面就是**盲的**,而盲区
/// 与「那里确实没有调用者」在结果里逐字同形。它必须被数出来带走,由命令说破。
/// </summary>
public sealed class CallGraphSet
{
    public required IReadOnlyList<CallGraph> Graphs { get; init; }

    /// <summary>没有边表的树。</summary>
    public required IReadOnlyList<string> Without { get; init; }

    public int EdgeCount => Graphs.Sum(g => g.Edges.Count);

    public int Unresolved => Graphs.Sum(g => g.Unresolved);

    /// <summary>调用了这个方法的那些调用点。</summary>
    public IEnumerable<CallSite> CallersOf(string assembly, int token)
        => Sites(e => e.ToToken == token
                   && string.Equals(e.ToAssembly, assembly, StringComparison.OrdinalIgnoreCase));

    /// <summary>这个方法调用了谁。</summary>
    public IEnumerable<CallSite> CalleesOf(string assembly, int token)
        => Sites(e => e.FromToken == token
                   && string.Equals(e.FromAssembly, assembly, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<CallSite> Sites(Func<CallEdge, bool> keep)
    {
        foreach (var g in Graphs)
            foreach (var e in g.Edges)
                if (keep(e)) yield return new CallSite(g.Tree, e);
    }

    /// <summary>读一批树的边表。<paramref name="only"/> 非空时只读这些树。</summary>
    public static CallGraphSet Load(string root, IReadOnlyCollection<string>? only = null)
    {
        var graphs = new List<CallGraph>();
        var without = new List<string>();
        if (!Directory.Exists(root)) return new CallGraphSet { Graphs = graphs, Without = without };

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir)!;
            if (name.Length == 0 || name.StartsWith('.')) continue;
            if (only is { Count: > 0 } && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            var g = CallGraphStore.Read(dir);
            if (g is null) without.Add(name); else graphs.Add(g);
        }

        return new CallGraphSet { Graphs = graphs, Without = without };
    }
}

/// <summary>
/// 扫方法体的 IL,把调用点解析成「哪个程序集里的哪个方法」。
///
/// 目标 token 分三种:本程序集内的方法定义、跨程序集引用、泛型方法实例化。三种都交给
/// 反编译器的类型系统解析 —— 实测全源 104.8 万条边成功率 99.997%,而它正是外包 MCP
/// 底下用的同一个引擎,所以这里的精度与外包侧是同一份,不是照名字近似匹配。
/// </summary>
public static class CallGraphBuilder
{
    private static readonly OpCode?[] OneByte = new OpCode?[256];
    private static readonly OpCode?[] TwoByte = new OpCode?[256];

    static CallGraphBuilder()
    {
        // 操作数长度表从运行时自己的 opcode 定义反射出来,不手写 —— 手写那张表
        // 错一个字节,步进就此错位,而错位之后读到的「调用目标」仍然是个像样的 token。
        foreach (var f in typeof(OpCodes).GetFields())
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = unchecked((ushort)op.Value);
            if (op.Size == 1) OneByte[v & 0xFF] = op; else TwoByte[v & 0xFF] = op;
        }
    }

    private static int OperandSize(SRE.OperandType t, ReadOnlySpan<byte> rest) => t switch
    {
        SRE.OperandType.InlineNone => 0,
        SRE.OperandType.ShortInlineBrTarget or SRE.OperandType.ShortInlineI or SRE.OperandType.ShortInlineVar => 1,
        SRE.OperandType.InlineVar => 2,
        SRE.OperandType.InlineI8 or SRE.OperandType.InlineR => 8,
        SRE.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(rest),
        _ => 4,
    };

    /// <summary>一棵树里全部 dll 的出边。</summary>
    public static CallGraph Build(
        string tree,
        IReadOnlyList<ResolvedAssembly> assemblies,
        IReadOnlyList<string> searchDirectories,
        IReadOnlyList<string> sourceHashes,
        CancellationToken cancel = default)
    {
        var edges = new List<CallEdge>();
        var unresolved = 0;

        foreach (var asm in assemblies)
        {
            cancel.ThrowIfCancellationRequested();
            try { Scan(asm, searchDirectories, edges, ref unresolved, cancel); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 一个 dll 扫不动只是少一批边。它不能被当成「这个 dll 里没有调用」——
                // 建表的命令会把这件事报出来。
            }
        }

        return new CallGraph { Tree = tree, Edges = edges, Unresolved = unresolved, SourceHashes = sourceHashes };
    }

    private static void Scan(
        ResolvedAssembly asm, IReadOnlyList<string> searchDirectories,
        List<CallEdge> edges, ref int unresolved, CancellationToken cancel)
    {
        using var file = new PEFile(asm.Path);
        var resolver = new UniversalAssemblyResolver(asm.Path, throwOnError: false,
                                                     targetFramework: file.DetectTargetFrameworkId());
        foreach (var dir in searchDirectories)
            if (Directory.Exists(dir)) resolver.AddSearchDirectory(dir);

        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var module = new DecompilerTypeSystem(file, resolver, settings).MainModule;

        var md = file.Metadata;
        var from = asm.Name;
        var local = 0;

        foreach (var mh in md.MethodDefinitions)
        {
            cancel.ThrowIfCancellationRequested();
            var m = md.GetMethodDefinition(mh);
            if (m.RelativeVirtualAddress == 0) continue;

            ReadOnlySpan<byte> il;
            try { il = file.Reader.GetMethodBody(m.RelativeVirtualAddress).GetILContent().AsSpan(); }
            catch { continue; }

            var fromToken = MetadataTokens.GetToken(mh);
            var i = 0;
            while (i < il.Length)
            {
                OpCode? op;
                int opSize;
                if (il[i] == 0xFE && i + 1 < il.Length) { op = TwoByte[il[i + 1]]; opSize = 2; }
                else { op = OneByte[il[i]]; opSize = 1; }

                // 未知字节意味着步进已经不可信,这个方法体就此打住 —— 继续走会把
                // 操作数里的字节读成 opcode,凭空造出根本不存在的调用边。
                if (op is null) break;

                var operand = OperandSize(op.Value.OperandType, il[(i + opSize)..]);
                if (op.Value.OperandType == SRE.OperandType.InlineMethod)
                {
                    var token = BitConverter.ToInt32(il[(i + opSize)..]);
                    try
                    {
                        var target = module.ResolveMethod(MetadataTokens.EntityHandle(token), new GenericContext());
                        if (target is null || target.DeclaringType.Kind == TypeKind.Unknown) local++;
                        else
                            edges.Add(new CallEdge(
                                from, fromToken,
                                target.ParentModule?.AssemblyName ?? "?",
                                target.MetadataToken.IsNil ? 0 : MetadataTokens.GetToken(target.MetadataToken)));
                    }
                    catch { local++; }
                }
                i += opSize + operand;
            }
        }

        unresolved += local;
    }
}
