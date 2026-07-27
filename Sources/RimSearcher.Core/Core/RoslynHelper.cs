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

public readonly record struct SourceLookupResult(SourceLookupStatus Status, string Content)
{
    public bool IsOk => Status == SourceLookupStatus.Ok;

    public static SourceLookupResult Ok(string content) => new(SourceLookupStatus.Ok, content);

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

            var members = new List<(string TypeName, string MemberName, string MemberType)>();
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

    private static string GetFullTypeName(TypeDeclarationSyntax typeDeclaration)
    {
        var nameStack = new Stack<string>();
        nameStack.Push(typeDeclaration.Identifier.Text);
        var parent = typeDeclaration.Parent;
        while (parent != null)
        {
            if (parent is TypeDeclarationSyntax p) nameStack.Push(p.Identifier.Text);
            else if (parent is NamespaceDeclarationSyntax ns) nameStack.Push(ns.Name.ToString());
            else if (parent is FileScopedNamespaceDeclarationSyntax fns) nameStack.Push(fns.Name.ToString());
            parent = parent.Parent;
        }
        return string.Join(".", nameStack);
    }

    public static async Task<SourceLookupResult> GetClassOutlineAsync(string filePath, string? targetTypeName = null)
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
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

        foreach (var type in types)
        {
            var fullName = GetFullTypeName(type);
            if (!string.IsNullOrEmpty(targetTypeName) &&
                !fullName.Equals(targetTypeName, StringComparison.OrdinalIgnoreCase) &&
                !type.Identifier.Text.Equals(targetTypeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string kind = type switch
            {
                ClassDeclarationSyntax => "Class",
                StructDeclarationSyntax => "Struct",
                InterfaceDeclarationSyntax => "Interface",
                RecordDeclarationSyntax => "Record",
                _ => "Type"
            };

            sb.AppendLine($"{kind}: {fullName} {type.TypeParameterList}");
            foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
                sb.AppendLine($"  Property: {prop.Type} {prop.Identifier.Text}");
            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
            {
                var fieldName = string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                sb.AppendLine($"  Field: {field.Declaration.Type} {fieldName}");
            }

            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                var parameters = string.Join(", ",
                    method.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier.Text}"));
                sb.AppendLine($"  Method: {method.ReturnType} {method.Identifier.Text}({parameters})");
            }
            sb.AppendLine();
        }

        return sb.Length > 0
            ? SourceLookupResult.Ok(sb.ToString())
            : SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);
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

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = await tree.GetRootAsync();

        bool TypeFilter(SyntaxNode node)
        {
            if (string.IsNullOrEmpty(typeName)) return true;
            var parentType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            return parentType != null && (
                parentType.Identifier.Text.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                GetFullTypeName(parentType).Equals(typeName, StringComparison.OrdinalIgnoreCase));
        }

        var candidates = new List<(SyntaxNode Node, string Kind)>();

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

        if (candidates.Count == 0) return SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);

        if (candidates.Count == 1)
        {
            var (node, kind) = candidates[0];
            var lineSpan = node.GetLocation().GetLineSpan();
            return SourceLookupResult.Ok(
                $"// File: {filePath}\n// {kind}, starts at line: {lineSpan.StartLinePosition.Line + 1}\n{node.ToFullString()}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"/* Found {candidates.Count} matching members */");
        foreach (var (node, kind) in candidates)
        {
            var lineSpan = node.GetLocation().GetLineSpan();
            var parentType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            sb.AppendLine($"// {kind} in {(parentType != null ? GetFullTypeName(parentType) : "Unknown")}");
            sb.AppendLine($"// Starts at line: {lineSpan.StartLinePosition.Line + 1}");
            sb.AppendLine(node.ToFullString());
            sb.AppendLine("\n// --- NEXT MATCH ---\n");
        }
        return SourceLookupResult.Ok(sb.ToString());
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

        var typeMatch = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t =>
                t.Identifier.Text.Equals(className, StringComparison.OrdinalIgnoreCase) ||
                GetFullTypeName(t).Equals(className, StringComparison.OrdinalIgnoreCase));

        if (typeMatch == null) return SourceLookupResult.Failed(SourceLookupStatus.TargetNotFound);

        var lineSpan = typeMatch.GetLocation().GetLineSpan();
        return SourceLookupResult.Ok(
            $"// File: {filePath}\n// Starts at line: {lineSpan.StartLinePosition.Line + 1}\n{typeMatch.ToFullString()}");
    }

}
