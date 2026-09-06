using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Metadata;

/// <summary>元数据里的一个类型。</summary>
public sealed record TypeHit
{
    public required ResolvedAssembly Assembly { get; init; }
    public required TypeDefinitionHandle Handle { get; init; }

    /// <summary>命名空间加类型名,嵌套类型用 <c>+</c> 连。</summary>
    public required string FullName { get; init; }

    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>编译器生成的(迭代器状态机、闭包、lambda 宿主)。这些在反编译树里没有对应文件。</summary>
    public bool CompilerGenerated => Name.StartsWith('<');
}

/// <summary>元数据里的一个方法。</summary>
public sealed record MethodHit
{
    public required TypeHit Type { get; init; }
    public required MethodDefinitionHandle Handle { get; init; }

    /// <summary>IL 里的名字。属性访问器是 <c>get_X</c>,构造函数是 <c>.ctor</c>。</summary>
    public required string Name { get; init; }

    /// <summary>C# 里的名字。属性访问器在这里是属性名本身;其余与 <see cref="Name"/> 相同。</summary>
    public required string SourceName { get; init; }

    public required string Signature { get; init; }

    /// <summary>没有方法体:抽象、extern、或引擎内部调用。这与「没找到」是两件事。</summary>
    public required bool Bodyless { get; init; }

    /// <summary>
    /// 这个方法的指令实际住在别处 —— 迭代器与 async 的方法体只是个壳,真正的指令在状态机的
    /// <c>MoveNext</c> 里。<c>null</c> = 不是这种方法。
    /// </summary>
    public MethodHit? StateMachine { get; init; }
}

/// <summary>元数据里的一个成员,不限于方法。</summary>
public sealed record MemberRow
{
    public required TypeHit Type { get; init; }

    /// <summary>method / constructor / property / field / event。</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }
    public required string Signature { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsAbstract { get; init; }

    /// <summary>覆写了基类的成员。判据是 virtual 且非 newslot —— 同名遮蔽不算覆写。</summary>
    public required bool IsOverride { get; init; }

    public required string Accessibility { get; init; }

    /// <summary>方法与属性访问器有句柄;字段与事件没有,取默认值。</summary>
    public required MethodDefinitionHandle Handle { get; init; }
}

/// <summary>
/// 打开一批程序集,按名字找里面的类型与方法。
///
/// 全程走裸元数据,不建反编译器的类型系统:实测线性扫一个 16158 个类型的程序集不到 1 毫秒,
/// 而类型系统每个程序集要 8~40 毫秒。语义问题(继承、调用图)另有各自的入口,它们才付那笔钱。
/// </summary>
public sealed class MetadataLookup : IDisposable
{
    private readonly List<(ResolvedAssembly Asm, PEFile File)> _open = [];

    public MetadataLookup(IEnumerable<ResolvedAssembly> assemblies)
    {
        foreach (var a in assemblies)
        {
            try { _open.Add((a, new PEFile(a.Path))); }
            catch
            {
                // 打不开的 dll(损坏、不是托管程序集)只是少一个来源。
                // 它不该让整次查询失败,但也不能假装它被搜过 —— 见 Unreadable。
                Unreadable.Add(a);
            }
        }
    }

    /// <summary>打不开的那些。查空时要说出来,否则「搜遍了都没有」是句假话。</summary>
    public List<ResolvedAssembly> Unreadable { get; } = [];

    public IReadOnlyList<ResolvedAssembly> Assemblies => _open.Select(o => o.Asm).ToList();

    public int AssemblyCount => _open.Count;

    public PEFile? FileOf(ResolvedAssembly asm)
        => _open.FirstOrDefault(o => ReferenceEquals(o.Asm, asm)).File;

    /// <summary>全部类型,一次遍历。</summary>
    public IEnumerable<TypeHit> AllTypes()
    {
        foreach (var (asm, file) in _open)
        {
            var md = file.Metadata;
            foreach (var h in md.TypeDefinitions)
            {
                var hit = Describe(asm, md, h);
                if (hit is not null) yield return hit;
            }
        }
    }

    /// <summary>
    /// 按名字找类型。依次试:全名精确 → 裸名精确 → 全名以它结尾。
    ///
    /// 三级都试完才算落空,而每一级都可能命中多个(同名类型在不同 mod 里很常见) ——
    /// 一律全给,不挑一个,挑一个等于替调用方做了它看不见的选择。
    /// </summary>
    public IReadOnlyList<TypeHit> FindTypes(string name)
    {
        if (name.Length == 0) return [];
        var wanted = name.Replace('/', '+');

        var exact = AllTypes().Where(t => string.Equals(t.FullName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;

        var bare = AllTypes().Where(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (bare.Count > 0) return bare;

        return AllTypes()
            .Where(t => t.FullName.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)
                     || t.FullName.EndsWith("+" + wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// 在一个类型里按名字找方法。
    ///
    /// 三件 C# 名字与 IL 名字对不上的事在这里收口,漏掉任何一件,结果都是「查无此成员」——
    /// 而那句话与「这个成员真的不存在」逐字同形:
    ///   - 属性 <c>Faction</c> 在 IL 里是 <c>get_Faction</c> / <c>set_Faction</c> 两个方法;
    ///   - 构造函数写作 <c>.ctor</c>,而调用方会写 <c>ctor</c> / <c>#ctor</c>;
    ///   - 同名重载全给(实测全库 1560 组同名方法,3706 个方法卷在里面)。
    /// </summary>
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
                     // 显式接口实现的 IL 名字带着接口全名:`Verse.IThingHolder.GetDirectlyHeldThings`
                     || il.EndsWith("." + memberName, StringComparison.OrdinalIgnoreCase);

            if (!match) continue;
            result.Add(Describe(type, md, mh, m));
        }

        return result;
    }

    /// <summary>
    /// 一个类型里的全部成员。方法、字段、属性、事件各是元数据里独立的一张表,只走方法表
    /// 会让「有没有这个字段」这个问题落进「没找到」。
    ///
    /// 属性与事件同时以自己的形态和访问器方法的形态出现 —— 前者是 C# 里那个名字,后者是
    /// IL 里真正存在的东西。两个都给,由 kind 分开。
    /// </summary>
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
            var attrs = getter?.Attributes ?? default;
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
                Accessibility = getter is null ? "" : Access(attrs),
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
            rows.Add(new MemberRow
            {
                Type = type,
                Kind = "event",
                Name = md.GetString(e.Name),
                Signature = "",
                IsStatic = false,
                IsVirtual = false,
                IsAbstract = false,
                IsOverride = false,
                Accessibility = "",
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

    /// <summary>整个程序集范围内按裸成员名找 —— 调用方只写了一个词时的那条路。</summary>
    public IReadOnlyList<MethodHit> FindMethodsAnywhere(string memberName, int cap)
    {
        var result = new List<MethodHit>();
        foreach (var (asm, file) in _open)
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
                    result.Add(Describe(type, md, mh, m));
                    if (result.Count >= cap) return result;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 边表存的是「程序集名 + 元数据 token」,印出来要变回名字。
    ///
    /// 不找状态机 —— 反查一次会命中成百上千条边,而每条都去翻一遍宿主类型的嵌套类型,
    /// 那笔钱要花在一列没人问的信息上。
    /// </summary>
    public MethodHit? ResolveToken(string assemblyName, int token)
    {
        foreach (var (asm, file) in _open)
        {
            if (!string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase)) continue;
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
                    Bodyless = m.RelativeVirtualAddress == 0,
                };
            }
            catch { return null; }
        }
        return null;
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
            Bodyless = m.RelativeVirtualAddress == 0,
        };

        var sm = FindStateMachine(type, md, m);
        return sm is null ? hit : hit with { StateMachine = sm };
    }

    /// <summary>
    /// 迭代器 / async 方法的指令住在编译器生成的状态机里,方法本身只剩个壳。
    ///
    /// 实测 <c>Pawn.GetGizmos</c> 的方法体 20 行,真正的 1704 行在
    /// <c>Pawn/&lt;GetGizmos&gt;d__346::MoveNext</c> —— 而那 20 行看上去是个完整答案。
    /// 特性上记着状态机是哪个类型,照它走,不猜名字。
    /// </summary>
    private MethodHit? FindStateMachine(TypeHit type, MetadataReader md, MethodDefinition m)
    {
        foreach (var ah in m.GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(ah);
            var name = AttributeTypeName(md, attr);
            if (name is not ("IteratorStateMachineAttribute" or "AsyncStateMachineAttribute"
                             or "AsyncIteratorStateMachineAttribute"))
                continue;

            // 这一族特性的唯一构造参数是状态机类型。值在 blob 里编码成一个类型名字符串,
            // 而它的形态随编译器变;稳的是「宿主类型的嵌套类型里,名字带着这个方法名的那个」。
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
                    return new MethodHit
                    {
                        Type = nestedType,
                        Handle = smh,
                        Name = "MoveNext",
                        SourceName = "MoveNext",
                        Signature = SignatureText.Method(md, sm, "MoveNext"),
                        Bodyless = sm.RelativeVirtualAddress == 0,
                    };
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

    /// <summary>IL 名字 → C# 里那个名字。访问器脱掉前缀,其余原样。</summary>
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

            // 嵌套类型的 Namespace 是空的,全名要从外层拼起来。
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

/// <summary>方法签名的文本形态。给人读,也给「哪个重载」这个问题当答案。</summary>
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

    /// <summary>签名里的类型只要个短名 —— 全名会把一行撑到读不动,而消歧靠的是参数个数与顺序。</summary>
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
