using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Sources;

public sealed record DecompileRequest
{
    public required string AssemblyPath { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 类型解析用的搜索目录。缺了它泛型约束和继承链会退化成 object。
    /// </summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];
}

public sealed record DecompileOutcome
{
    public required string AssemblyPath { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public int FileCount { get; init; }
}

/// <summary>
/// 一个程序集 → 一棵 .cs 目录树。只依赖 ICSharpCode.Decompiler 这个 NuGet 包。
///
/// 反编译到此为止:事务、历史、版本间 diff 一概不做 —— 差异交给 git。
/// </summary>
public static class Decompiler
{
    /// <summary>
    /// RimWorld 对应 Unity 2022.3 - C# 9(缺 IsExternalInit),锁 CSharp9_0。
    /// 档位一变整棵树重排,字节级稳定也依赖它锁死。
    /// </summary>
    public static DecompilerSettings CreateSettings() => new(LanguageVersion.CSharp9_0)
    {
        ThrowOnAssemblyResolveErrors = false,
        RemoveDeadCode = true,
        RemoveDeadStores = true,
        UseSdkStyleProjectFormat = false,
        UseNestedDirectoriesForNamespaces = true,
    };

    public static DecompileOutcome Decompile(DecompileRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(request.OutputDirectory);

            using var file = new PEFile(request.AssemblyPath);
            var resolver = new UniversalAssemblyResolver(
                request.AssemblyPath,
                throwOnError: false,
                targetFramework: file.DetectTargetFrameworkId());

            foreach (var path in request.ReferencePaths)
                if (Directory.Exists(path)) resolver.AddSearchDirectory(path);

            var decompiler = new WholeProjectDecompiler(
                CreateSettings(), resolver,
                assemblyReferenceClassifier: null, debugInfoProvider: null);

            decompiler.DecompileProject(file, request.OutputDirectory, cancellationToken);
            StabilizeProjectGuids(request.OutputDirectory);

            return new DecompileOutcome
            {
                AssemblyPath = request.AssemblyPath,
                Success = true,
                FileCount = CountSourceFiles(request.OutputDirectory),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DecompileOutcome
            {
                AssemblyPath = request.AssemblyPath,
                Success = false,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// 把 <c>.csproj</c> 里的 <c>ProjectGuid</c> 换成由项目名算出的固定值。
    ///
    /// 反编译器每跑一次都生成新的随机 GUID,是整棵树里唯一不确定的东西,会在每次同步的
    /// diff 里造出假改动。不删 .csproj:它记着程序集引用了谁,引用集合变了是真改动。
    /// </summary>
    private static void StabilizeProjectGuids(string directory)
    {
        IEnumerable<string> projects;
        try { projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var project in projects)
        {
            try
            {
                var text = File.ReadAllText(project);
                var stable = StableProjectGuid(Path.GetFileNameWithoutExtension(project));
                var replaced = System.Text.RegularExpressions.Regex.Replace(
                    text, @"<ProjectGuid>\{[^}]*\}</ProjectGuid>", $"<ProjectGuid>{{{stable}}}</ProjectGuid>");
                if (replaced != text) File.WriteAllText(project, replaced);
            }
            catch { /* 稳不下来只是 diff 多一行噪音,不该让整次反编译失败 */ }
        }
    }

    /// <summary>项目名 → 固定 GUID。同名必同值,不同名几乎必不同值。</summary>
    public static string StableProjectGuid(string projectName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("rimsearcher/" + projectName));
        return new Guid(hash.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }

    private static int CountSourceFiles(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }
}
