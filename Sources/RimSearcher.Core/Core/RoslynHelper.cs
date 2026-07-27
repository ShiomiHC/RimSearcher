using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace RimSearcher.Core;

// 一个类型声明抽出来的继承信息。两份数据刻意分开：
//   PrimaryBase       —— 唯一的「主基类」，供需要向上一路走链路的场景（inspect 的继承图）。
//   DirectSuperTypes  —— 基类型列表的全集（基类 + 全部接口），供「谁派生/实现了它」的反查。
// 原先两者共用一份「基类型列表第一项」，于是 `class Worker : BaseWorker, IDisposable`
// 永远不会被记成 IDisposable 的实现者——而按接口找实现是这个工具的主要用途之一。
public sealed record TypeInheritance(string FullName, string? PrimaryBase, string[] DirectSuperTypes);

// *Async 取正文的结果。原先失败是用 "File not found." 这类人读字符串表示的，调用方靠
// Contains("not found") 判断——而反编译产物里 Log.Error("... not found")、
// throw new Exception("def not found") 这类字面量遍地都是，成功取到的正文一旦含这段
// 文本就会被误报成「类不存在」。故把失败原因抬成显式状态，调用方按状态判断。
public enum SourceLookupStatus
{
    Ok,
    FileNotFound,
    FileTooLarge,
    TargetNotFound
}

public readonly record struct SourceLookupResult(
    SourceLookupStatus Status, string Content, string LocationLine = "")
{
    public bool IsOk => Status == SourceLookupStatus.Ok;

    // 正文（不含开头那行位置注释）。位置注释是本服务加的一行，不是源码：调用方要按类
    // 自身的行数做截断与报数，把它算进去会让「'Pawn' is 4729 lines」比真实行数多。
    public string Body => LocationLine.Length == 0 ? Content : Content[(LocationLine.Length + 1)..];

    public static SourceLookupResult Ok(string content) => new(SourceLookupStatus.Ok, content);

    public static SourceLookupResult Ok(string locationLine, string body) =>
        new(SourceLookupStatus.Ok, locationLine + "\n" + body, locationLine);

    public static SourceLookupResult Failed(SourceLookupStatus status) => new(status, string.Empty);
}

public static class RoslynHelper
{
    // 单文件解析上限。反编译产物里偶有几十 MB 的巨型 .cs，语法树建起来内存与耗时都不划算。
    public const long MaxParseFileSize = 10 * 1024 * 1024;

    /// <summary>
    /// Parses a C# file once and extracts both inheritance info and all members.
    /// Avoids double parsing by extracting inheritance and members in one pass.
    /// </summary>
    public static (List<TypeInheritance> Types, List<(string TypeName, string MemberName, string MemberType)> Members)
        GetClassInfoCombined(string path)
    {
        var emptyTypes = new List<TypeInheritance>();
        var emptyMembers = new List<(string, string, string)>();

        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxParseFileSize)
                return (emptyTypes, emptyMembers);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var code = reader.ReadToEnd();

            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();

            var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

            // 同名声明合并：partial 类可以把基类型列表拆到多处（`partial class A : B` +
            // `partial class A : IC`），取并集才不丢边。
            var inheritance = types
                .Select(t => new
                {
                    FullName = GetFullTypeName(t),
                    PrimaryBase = GetPrimaryBaseType(t),
                    SuperTypes = GetDirectSuperTypes(t)
                })
                .GroupBy(x => x.FullName)
                .Select(g => new TypeInheritance(
                    g.Key,
                    g.Select(x => x.PrimaryBase).FirstOrDefault(b => !string.IsNullOrEmpty(b)),
                    g.SelectMany(x => x.SuperTypes).Distinct(StringComparer.Ordinal).ToArray()))
                .ToList();

            // enum 与 delegate 同样是「按名字能查到的类型」，但它们不是 TypeDeclarationSyntax，
            // 原先整类缺席：inspect('ShieldState') 回「不存在」，read_code(extractClass) 回
            // 「不是类」，两条提示互相指，而 ShieldState.cs 就在索引里躺着。
            // 继承信息留空——enum 的 `: byte` 是底层类型不是基类，delegate 没有基类型列表。
            foreach (var declaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
                inheritance.Add(new TypeInheritance(GetFullTypeName(declaration), null, []));

            foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
                inheritance.Add(new TypeInheritance(GetFullTypeName(declaration), null, []));

            var members = new List<(string TypeName, string MemberName, string MemberType)>();

            // enum 的取值就是它唯一的成员，也是调用方查 enum 时真正想要的东西
            // （「ShieldState 有哪几个值」）。不收的话 locate 只能靠文件名兜底。
            foreach (var enumDeclaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var enumName = GetFullTypeName(enumDeclaration);
                foreach (var value in enumDeclaration.Members)
                    members.Add((enumName, value.Identifier.Text, "EnumMember"));
            }

            foreach (var type in types)
            {
                var typeName = GetFullTypeName(type);
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                    members.Add((typeName, method.Identifier.Text, "Method"));
                foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
                    members.Add((typeName, prop.Identifier.Text, "Property"));
                foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                    foreach (var variable in field.Declaration.Variables)
                        members.Add((typeName, variable.Identifier.Text, "Field"));
                foreach (var evt in type.Members.OfType<EventFieldDeclarationSyntax>())
                    foreach (var variable in evt.Declaration.Variables)
                        members.Add((typeName, variable.Identifier.Text, "Event"));
            }

            return (inheritance, members);
        }
        catch
        {
            return (emptyTypes, emptyMembers);
        }
    }

    // 直接超类型的全集（基类与接口在语法层面无从区分，这里也不区分）。
    // 泛型实参一并剥掉：GetFullTypeName 给出的类型名从来不带实参，留着
    // `IEnumerable<Thing>` 这样的键谁都查不到——按 `IEnumerable` 查会漏，
    // 拿它去 _typeMap 反查定义文件也永远解析不到。
    private static string[] GetDirectSuperTypes(TypeDeclarationSyntax type)
    {
        if (type.BaseList == null || type.BaseList.Types.Count == 0) return [];

        return type.BaseList.Types
            .Select(baseType => NormalizeTypeName(baseType.Type.ToString()))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    // 主基类。这是启发式，不是语义解析——只建语法树不建 Compilation（为上千个 dll 的
    // 反编译产物做语义解析代价不可接受），故 `class A : B` 里的 B 到底是类还是接口，
    // 这里无从知道，只能按命名猜。
    // 规则：C# 要求基类必须写在基类型列表第一位，所以只看第一项；第一项若长得像接口名
    // （I + 大写字母，如 IDisposable / IExposable）就认为这个类型没有基类。
    // 代价是 IPAddress 这类「真的是类却按接口命名」的类型会丢一层链路；换来的是
    // `class X : IExposable` 不再被记成「X 继承自 IExposable」。
    // 接口自身例外：`interface IFoo : IBar` 那一列全是接口，第一项就是它扩展的接口，
    // 向上走链路时这条边是有意义的。
    private static string? GetPrimaryBaseType(TypeDeclarationSyntax type)
    {
        var first = type.BaseList?.Types.FirstOrDefault();
        if (first == null) return null;

        var name = NormalizeTypeName(first.Type.ToString());
        if (name.Length == 0) return null;
        if (type is InterfaceDeclarationSyntax) return name;

        return LooksLikeInterfaceName(name) ? null : name;
    }

    private static string NormalizeTypeName(string raw)
    {
        var text = raw.Trim();
        var generic = text.IndexOf('<');
        if (generic >= 0) text = text[..generic].TrimEnd();
        return text;
    }

    private static bool LooksLikeInterfaceName(string name)
    {
        var lastDot = name.LastIndexOf('.');
        var simple = lastDot >= 0 ? name[(lastDot + 1)..] : name;
        return simple.Length >= 2 && simple[0] == 'I' && char.IsUpper(simple[1]);
    }

    // 接收 SyntaxNode 而不是 TypeDeclarationSyntax：enum 与 delegate 也要能算出全名，
    // 而它们不在那条继承线上（EnumDeclarationSyntax 只是 BaseTypeDeclarationSyntax）。
    private static string GetFullTypeName(SyntaxNode declaration)
    {
        var nameStack = new Stack<string>();
        nameStack.Push(IdentifierOf(declaration));
        var parent = declaration.Parent;
        while (parent != null)
        {
            if (parent is BaseTypeDeclarationSyntax p) nameStack.Push(p.Identifier.Text);
            else if (parent is NamespaceDeclarationSyntax ns) nameStack.Push(ns.Name.ToString());
            else if (parent is FileScopedNamespaceDeclarationSyntax fns) nameStack.Push(fns.Name.ToString());
            parent = parent.Parent;
        }
        return string.Join(".", nameStack);
    }

    private static string IdentifierOf(SyntaxNode declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax baseType => baseType.Identifier.Text,
        DelegateDeclarationSyntax del => del.Identifier.Text,
        _ => string.Empty
    };

    // 大纲与类体提取共用的「一个文件里全部可按名字查到的类型声明」。
    // BaseTypeDeclarationSyntax 覆盖 class/struct/interface/record/enum，delegate 单列。
    private static IEnumerable<SyntaxNode> AllTypeDeclarations(SyntaxNode root)
        => root.DescendantNodes()
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

    private static bool MatchesTypeName(SyntaxNode declaration, string name)
        => GetFullTypeName(declaration).Equals(name, StringComparison.OrdinalIgnoreCase)
           || IdentifierOf(declaration).Equals(name, StringComparison.OrdinalIgnoreCase);

    // 大纲的体积上限，按成员类别各给一份配额。Pawn 这类巨型类型的成员数以百计，全量渲染
    // 一次就是几千 token，而 inspect 是 locate 之后的必经一站，这份开销每次查询都要付。
    // 配额不做成「总数顺序截断」：一个有两百个字段的类会把 Method 整段挤掉，而方法签名
    // 恰恰是大纲最常被用到的部分（照着写调用、写 Harmony patch）。三类各 40 条 ≈ 120 行，
    // 落在 ScopeArgs.HardLimit 那笔体积账之内；超出的由 locate / read_code 按名精取。
    public const int DefaultMaxOutlineMembersPerKind = 40;

    public static async Task<SourceLookupResult> GetClassOutlineAsync(
        string filePath,
        string? targetTypeName = null,
        int maxMembersPerKind = DefaultMaxOutlineMembersPerKind)
    {
        if (!File.Exists(filePath)) return SourceLookupResult.Failed(SourceLookupStatus.FileNotFound);
        if (new FileInfo(filePath).Length > MaxParseFileSize)
            return SourceLookupResult.Failed(SourceLookupStatus.FileTooLarge);

        string code;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            code = await reader.ReadToEndAsync();
        }

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = await tree.GetRootAsync();

        var sb = new StringBuilder();

        foreach (var declaration in AllTypeDeclarations(root))
        {
            var fullName = GetFullTypeName(declaration);
            if (!string.IsNullOrEmpty(targetTypeName) && !MatchesTypeName(declaration, targetTypeName))
            {
                continue;
            }

            // enum / delegate 没有成员大纲可列：enum 的取值就是它的全部内容，delegate 只有签名。
            // 两者都在这里就地渲染完，免得下面按 TypeDeclarationSyntax 取成员的代码空转。
            if (declaration is EnumDeclarationSyntax enumDeclaration)
            {
                var underlying = enumDeclaration.BaseList?.Types.FirstOrDefault()?.Type.ToString();
                sb.AppendLine($"Enum: {fullName}{(underlying != null ? " : " + underlying : string.Empty)}");

                // 不逐行印 `Value: `：一个 enum 声明只有一种成员，上一行的 `Enum:` 已经把
                // 下面每一行是什么说完了。AltitudeLayer 有 41 个取值，那就是 41 遍。
                foreach (var value in enumDeclaration.Members)
                {
                    var assigned = value.EqualsValue != null ? $" {value.EqualsValue}" : string.Empty;
                    sb.AppendLine($"  {value.Identifier.Text}{assigned}");
                }
                sb.AppendLine();
                continue;
            }

            if (declaration is DelegateDeclarationSyntax delegateDeclaration)
            {
                var parameters = string.Join(", ",
                    delegateDeclaration.ParameterList.Parameters.Select(FormatParameter));

                // 类型参数表必须跟着一起渲染，否则返回类型和形参里的 T/F 在整行里没有声明处，
                // 照抄编译不过；而且 AccessTools.FieldRef<in T, F> 与 FieldRef<F> 这类
                // 只有 arity 不同的重载会渲染成一模一样的一行，调用方分不出自己要的是哪个。
                var delegateTypeParams = delegateDeclaration.TypeParameterList?.ToString() ?? string.Empty;
                var constraints = string.Concat(
                    delegateDeclaration.ConstraintClauses.Select(clause => " " + clause.ToString().Trim()));

                sb.AppendLine(
                    $"Delegate: {delegateDeclaration.ReturnType} {fullName}{delegateTypeParams}({parameters}){constraints}");
                sb.AppendLine();
                continue;
            }

            if (declaration is not TypeDeclarationSyntax type) continue;

            string kind = type switch
            {
                ClassDeclarationSyntax => "Class",
                StructDeclarationSyntax => "Struct",
                InterfaceDeclarationSyntax => "Interface",
                RecordDeclarationSyntax => "Record",
                _ => "Type"
            };

            // 非泛型类型的 TypeParameterList 为 null，直接插值会在行尾留一个空格
            // （`Class: RimWorld.CompShield `）。大纲是要被人和模型逐行读的，
            // 行尾空格既碍眼又会让「按行比对上一版大纲」凭空多出差异。
            var typeParams = type.TypeParameterList?.ToString() ?? string.Empty;
            sb.AppendLine($"{kind}: {fullName}{(typeParams.Length > 0 ? " " + typeParams : string.Empty)}");
            var properties = type.Members.OfType<PropertyDeclarationSyntax>().ToList();
            foreach (var prop in properties.Take(maxMembersPerKind))
                sb.AppendLine($"  Property: {Modifiers(prop.Modifiers)}{prop.Type} {prop.Identifier.Text}");
            AppendOutlineFold(sb, properties.Count, maxMembersPerKind, "properties");

            var fields = type.Members.OfType<FieldDeclarationSyntax>().ToList();
            foreach (var field in fields.Take(maxMembersPerKind))
            {
                var fieldName = string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                sb.AppendLine($"  Field: {Modifiers(field.Modifiers)}{field.Declaration.Type} {fieldName}");
            }
            AppendOutlineFold(sb, fields.Count, maxMembersPerKind, "fields");

            var methods = type.Members.OfType<MethodDeclarationSyntax>().ToList();
            foreach (var method in methods.Take(maxMembersPerKind))
            {
                var parameters = string.Join(", ",
                    method.ParameterList.Parameters.Select(FormatParameter));
                sb.AppendLine(
                    $"  Method: {Modifiers(method.Modifiers)}{method.ReturnType} {method.Identifier.Text}({parameters})");
            }
            AppendOutlineFold(sb, methods.Count, maxMembersPerKind, "methods");

            sb.AppendLine();
        }

        return sb.Length > 0
            ? SourceLookupResult.Ok(sb.ToString())
            : SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);
    }

    // 折叠行要同时说清「还剩多少」和「怎么拿到它们」。只写 +N 的话，调用方唯一想得到的动作
    // 是把整个文件读出来——那正是大纲想省掉的开销。
    //
    // 两条旧出路对 Pawn 这类巨型类型都不成立，必须给一条真的能走通的：locate 只能按已知
    // 名字找，而调用方恰恰是不知道剩下那些叫什么才来看大纲的；read_code extractClass 的
    // 上限是 2000 行，Pawn.cs 有 4740 行，照做只会先烧掉 2000 行源码再收到二次截断。
    // 现在 inspect 自己带 limit，全量大纲三百来行，比读一遍类体便宜得多。
    private static void AppendOutlineFold(StringBuilder sb, int total, int shownCap, string kindPlural)
    {
        if (total <= shownCap) return;
        // 措辞对齐全服统一的截断脚注文法「... +N more <什么> (<怎么拿到>)」——见 ScopeArgs.FoldLine。
        // "not shown" 是 "more" 已经说过的话。
        sb.AppendLine(
            $"  ... +{total - shownCap} more {kindPlural} "
            + "(pass limit:'all' for the whole list, or read one with read_code methodName)");
    }

    // 与 FormatParameter 同一条判据：大纲是「照着它写调用或写 Harmony patch」的抄写样本，
    // 丢掉修饰符就等于给出错的样本。private 与 public、static 与实例、const 与可写字段
    // 原先渲染成逐字相同的一行，于是调用方会写出 `comp.ApparelScorePerEnergyMax`（private，
    // 编译不过）、`someVec.FromVector3(v)`（static，编译不过），或对一个 const 做
    // AccessTools.FieldRefAccess（const 没有字段槽，运行期炸）；写 patch 时更要靠
    // static 与否决定要不要 `__instance` 形参。
    private static string Modifiers(SyntaxTokenList modifiers)
        => modifiers.Count == 0 ? string.Empty : string.Join(" ", modifiers.Select(m => m.Text)) + " ";

    // 原先大纲只渲染 `{类型} {形参名}`，把 out/ref/in/params/this 和默认值全丢了：
    // `PostPreApplyDamage(DamageInfo, out bool absorbed)` 显示成 `(DamageInfo, bool absorbed)`。
    // 大纲的用途就是「照着它写调用或写 Harmony patch」，签名失真等于直接给出错的抄写样本——
    // 少一个 out 编译不过还算好的，params/默认值缺失则会静默地选错重载。
    private static string FormatParameter(ParameterSyntax parameter)
    {
        var sb = new StringBuilder();

        // 修饰符列表常为空，故由它自己在后面补空格，而不是在拼接处硬塞一个分隔符——
        // 否则无修饰符的参数会渲染成 " int x"，进而在参数间拼出双空格。
        foreach (var modifier in parameter.Modifiers) sb.Append(modifier.Text).Append(' ');

        sb.Append(parameter.Type).Append(' ').Append(parameter.Identifier.Text);

        // Default 的 ToString() 不含节点自身的前后 trivia，形如 `= 3200`，故这里要补空格
        if (parameter.Default != null) sb.Append(' ').Append(parameter.Default);

        return sb.ToString();
    }

    public static async Task<SourceLookupResult> GetMemberBodyAsync(string filePath, string memberName, string? typeName = null)
    {
        if (!File.Exists(filePath)) return SourceLookupResult.Failed(SourceLookupStatus.FileNotFound);
        if (new FileInfo(filePath).Length > MaxParseFileSize)
            return SourceLookupResult.Failed(SourceLookupStatus.FileTooLarge);

        string code;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            code = await reader.ReadToEndAsync();
        }

        return FormatMemberBody(code, memberName, typeName, filePath);
    }

    // 解析主体，与 GetMemberBodyAsync 共用。单独暴露收 code 的入口，是因为历史归档里的
    // 旧版本只以内存字符串存在（SourceHistoryStore.ReadArchived），没有可读的磁盘路径，
    // 而为解析一次落一份临时文件并不划算。
    public static SourceLookupResult FormatMemberBody(string code, string memberName, string? typeName, string fileLabel)
    {
        var candidates = FindMembers(code, memberName, typeName);

        if (candidates.Count == 0) return SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);

        if (candidates.Count == 1)
        {
            var (node, kind) = candidates[0];
            return SourceLookupResult.Ok(
                LocationLine(node, kind, memberName, fileLabel, null), node.ToFullString());
        }

        var sb = new StringBuilder();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (node, kind) = candidates[i];

            // 两条正文之间空一行。原先这里放的是 `// --- NEXT MATCH ---`：它只说「后面还有」，
            // 说不出还有几条，末尾那条之后又什么都没有，读者只能读成被截断了。
            // 换成每条自带的 `[i/n]` 之后，同一个位置既分了段又给了进度。
            if (i > 0) sb.AppendLine();

            sb.AppendLine(LocationLine(node, kind, memberName, fileLabel, (i + 1, candidates.Count)));
            sb.AppendLine(node.ToFullString());
        }
        return SourceLookupResult.Ok(sb.ToString());
    }

    // 正文之前的**唯一**一行位置注释。原先这里是三行（`// File: …` / `// Method, starts at
    // line: …`，再加 read_code 自己补的一行目标名回显），三行说的是同一件事的三个字段，
    // 而 `path:line` 是所有工具、编辑器、日志共用的写法，一行就够，还能直接复制去跳转。
    // ownerType 只在多命中时印：同名成员分属不同类型正是这时唯一要分辨的东西；
    // 单命中时调用方已经点了名，再重复一遍类型没有增量。
    private static string LocationLine(
        SyntaxNode node, string kind, string requestedName, string fileLabel, (int Index, int Total)? position)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var name = MemberDisplayName(node, requestedName);
        var prefix = position is { } p ? $"[{p.Index}/{p.Total}] " : string.Empty;

        // 构造函数的名字就是它所属类型的短名，`Constructor IntVec3 in Verse.IntVec3` 里
        // 后半截一个字都没多说。只有归属类型的短名与成员名不同时才补 `in …`。
        var owner = position != null ? OwnerTypeName(node) : string.Empty;
        var ownerNote = owner.Length > 0 && !ShortName(owner).Equals(name, StringComparison.Ordinal)
            ? $" in {owner}"
            : string.Empty;

        return $"// {prefix}{kind} {name}{ownerNote} — {fileLabel}:{line}";
    }

    // 位置行里印的名字。取语法节点自己的标识符而不是调用方传进来的字符串：`.ctor` /
    // `this` / `indexer` 这几个约定写法要还原成源码里真正写着的名字，否则位置行说的名字
    // 在正文里根本找不到。字段和事件例外——一条声明可带多个变量（`public int a, b;`），
    // 节点没有单一名字，此时调用方点的那个名字才是对的。
    private static string MemberDisplayName(SyntaxNode node, string requestedName) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        IndexerDeclarationSyntax => "this",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}",
        EnumMemberDeclarationSyntax e => e.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        _ => requestedName
    };

    private static string ShortName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot >= 0 && dot < qualified.Length - 1 ? qualified[(dot + 1)..] : qualified;
    }

    // 成员原文，不带文件名与行号注释头。给 diff 用：这两样在新旧两版之间本来就会不同，
    // 混进去只会变成一片与改动无关的差异行。
    public static SourceLookupResult ExtractMemberText(string code, string memberName, string? typeName = null)
    {
        var candidates = FindMembers(code, memberName, typeName);
        if (candidates.Count == 0) return SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);

        if (candidates.Count == 1) return SourceLookupResult.Ok(candidates[0].Node.ToFullString());

        // 多份匹配（重载、或未用 typeName 消歧的同名成员）按稳定键排序后拼接：源码顺序
        // 在两个版本之间可能变动，照原顺序拼会把「顺序调换」显示成一整片增删。
        var sb = new StringBuilder();
        foreach (var (node, kind) in candidates.OrderBy(c => MemberSortKey(c.Node), StringComparer.Ordinal))
        {
            sb.AppendLine($"// {kind} in {OwnerTypeName(node)}");
            sb.AppendLine(node.ToFullString());
        }
        return SourceLookupResult.Ok(sb.ToString());
    }

    // typeName 过滤掉光时，调用方看到的是「这个成员不在这个文件里」。为了把「过滤没了」
    // 和「真的没有」分开，需要不带过滤再问一次这个成员到底声明在哪几个类型上。
    public static async Task<IReadOnlyList<(string Owner, int Line)>> FindMemberOwnersAsync(
        string filePath, string memberName)
    {
        if (!File.Exists(filePath)) return [];
        if (new FileInfo(filePath).Length > MaxParseFileSize) return [];

        string code;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            code = await reader.ReadToEndAsync();
        }

        return FindMembers(code, memberName, null)
            .Select(c => (OwnerTypeName(c.Node), c.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
            .Distinct()
            .OrderBy(x => x.Item2)
            .ToArray();
    }

    // 成员的宿主类型要按 BaseTypeDeclarationSyntax 取：enum 取值也是成员，而 enum 声明
    // 不是 TypeDeclarationSyntax，按后者取会把宿主判成 Unknown。
    private static string OwnerTypeName(SyntaxNode node)
    {
        var parentType = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return parentType != null ? GetFullTypeName(parentType) : "Unknown";
    }

    private static string MemberSortKey(SyntaxNode node) => OwnerTypeName(node) + ParameterSignature(node);

    private static string ParameterSignature(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ParameterList.ToString(),
        ConstructorDeclarationSyntax c => c.ParameterList.ToString(),
        IndexerDeclarationSyntax i => i.ParameterList.ToString(),
        OperatorDeclarationSyntax o => o.ParameterList.ToString(),
        _ => string.Empty
    };

    private static List<(SyntaxNode Node, string Kind)> FindMembers(string code, string memberName, string? typeName)
    {
        var candidates = new List<(SyntaxNode Node, string Kind)>();

        var root = TryParse(code);
        if (root == null) return candidates;

        bool TypeFilter(SyntaxNode node)
        {
            if (string.IsNullOrEmpty(typeName)) return true;
            var parentType = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            return parentType != null && (
                parentType.Identifier.Text.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                GetFullTypeName(parentType).Equals(typeName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                     .Where(m => m.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase) && TypeFilter(m)))
            candidates.Add((m, "Method"));

        foreach (var p in root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                     .Where(p => p.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase) && TypeFilter(p)))
            candidates.Add((p, "Property"));

        foreach (var c in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                     .Where(c => (c.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase) ||
                                  memberName.Equals(".ctor", StringComparison.OrdinalIgnoreCase)) && TypeFilter(c)))
            candidates.Add((c, "Constructor"));

        if (memberName.Equals("this", StringComparison.OrdinalIgnoreCase) ||
            memberName.Equals("indexer", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var idx in root.DescendantNodes().OfType<IndexerDeclarationSyntax>().Where(TypeFilter))
                candidates.Add((idx, "Indexer"));
        }

        foreach (var op in root.DescendantNodes().OfType<OperatorDeclarationSyntax>()
                     .Where(o => o.OperatorToken.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase) && TypeFilter(o)))
            candidates.Add((op, "Operator"));

        // enum 取值现在既进成员索引（locate 会推荐 `EnumMembers: ShieldState.Resetting`）
        // 又进成员级 diff（列出后明说「pass 'method' with one of these names」），
        // 这里不认它的话，两处给出的下一步都会回一句「找不到这个成员」。
        foreach (var value in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>()
                     .Where(v => v.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase) && TypeFilter(v)))
            candidates.Add((value, "EnumMember"));

        // 字段与事件同理，而且这条死路一直都在：locate 早就在回 `Fields: CompShield.energy`，
        // 成员级 diff 也早就在列字段。一条声明可以带多个变量（`public int a, b;`），
        // 单个变量没有可独立成立的原文，故整条声明一起给。
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                     .Where(f => f.Declaration.Variables.Any(
                                     v => v.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                                 && TypeFilter(f)))
            candidates.Add((field, "Field"));

        foreach (var evt in root.DescendantNodes().OfType<EventFieldDeclarationSyntax>()
                     .Where(e => e.Declaration.Variables.Any(
                                     v => v.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                                 && TypeFilter(e)))
            candidates.Add((evt, "Event"));

        // add/remove 访问器写法的事件是另一种语法节点
        foreach (var evt in root.DescendantNodes().OfType<EventDeclarationSyntax>()
                     .Where(e => e.Identifier.Text.Equals(memberName, StringComparison.OrdinalIgnoreCase) && TypeFilter(e)))
            candidates.Add((evt, "Event"));

        return candidates;
    }

    // 文件里全部可命名成员及其原文，供逐成员比对得出「这个文件里是哪几个方法变了」。
    // 键含参数列表，重载之间不会互相顶掉。
    public static IReadOnlyDictionary<string, string> ListMemberTexts(string code)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        var root = TryParse(code);
        if (root == null) return results;

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var owner = GetFullTypeName(type);

            foreach (var member in type.Members)
            {
                var key = member switch
                {
                    MethodDeclarationSyntax m => $"{owner}.{m.Identifier.Text}{m.ParameterList}",
                    ConstructorDeclarationSyntax c => $"{owner}..ctor{c.ParameterList}",
                    PropertyDeclarationSyntax p => $"{owner}.{p.Identifier.Text}",
                    IndexerDeclarationSyntax i => $"{owner}.this{i.ParameterList}",
                    OperatorDeclarationSyntax o => $"{owner}.operator {o.OperatorToken.Text}{o.ParameterList}",
                    FieldDeclarationSyntax f =>
                        $"{owner}.{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
                    EventFieldDeclarationSyntax e =>
                        $"{owner}.{string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.Text))}",
                    _ => null
                };

                // 同键重复在合法 C# 里不该出现，但反编译产物不保证；取先见到的那份即可，
                // 两侧行为一致就不会凭空比出差异
                if (key != null) results.TryAdd(key, member.ToFullString());
            }
        }

        // enum 的取值同样是成员：不收的话，一个只改了枚举值的文件在成员粒度 diff 里
        // 会被报成「改在任何成员声明之外」，而它明明有确切的改动位置。
        foreach (var enumDeclaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var owner = GetFullTypeName(enumDeclaration);
            foreach (var value in enumDeclaration.Members)
                results.TryAdd($"{owner}.{value.Identifier.Text}", value.ToFullString());
        }

        return results;
    }

    private static CompilationUnitSyntax? TryParse(string code)
    {
        try
        {
            return CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<SourceLookupResult> GetClassBodyAsync(string filePath, string className)
    {
        if (!File.Exists(filePath)) return SourceLookupResult.Failed(SourceLookupStatus.FileNotFound);
        if (new FileInfo(filePath).Length > MaxParseFileSize)
            return SourceLookupResult.Failed(SourceLookupStatus.FileTooLarge);

        string code;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            code = await reader.ReadToEndAsync();
        }

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = await tree.GetRootAsync();

        // enum / delegate 也走这条路：调用方拿着一个正确的类型名过来，回「不是类」
        // 而不给正文，等于让它去 inspect 再问一遍——而那边原先同样查不到。
        var typeMatch = AllTypeDeclarations(root)
            .FirstOrDefault(declaration => MatchesTypeName(declaration, className));

        if (typeMatch == null) return SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);

        // 与成员模式同一行式：`// <种类> <全名> — <路径>:<行>`。印全限定名而不是调用方
        // 传进来的短名，是因为同名类型分属不同命名空间时，返回里必须能看出取的是哪一个。
        var lineSpan = typeMatch.GetLocation().GetLineSpan();
        return SourceLookupResult.Ok(
            $"// {TypeKindOf(typeMatch)} {GetFullTypeName(typeMatch)} — {filePath}:{lineSpan.StartLinePosition.Line + 1}",
            typeMatch.ToFullString());
    }

    private static string TypeKindOf(SyntaxNode declaration) => declaration switch
    {
        RecordDeclarationSyntax => "Record",
        ClassDeclarationSyntax => "Class",
        StructDeclarationSyntax => "Struct",
        InterfaceDeclarationSyntax => "Interface",
        EnumDeclarationSyntax => "Enum",
        DelegateDeclarationSyntax => "Delegate",
        _ => "Type"
    };

}
