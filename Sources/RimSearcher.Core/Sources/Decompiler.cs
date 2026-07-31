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
    /// 类型解析用的搜索目录。缺了它泛型约束和继承链会退化成 object ——
    /// 而那正是「看得懂代码」与「看得见代码」的差别。
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
/// 一个程序集 → 一棵 .cs 目录树。从旧世系 <c>DecompileService</c> 原样带走。
///
/// 这是那一支唯一非搬不可的东西:它只依赖 ICSharpCode.Decompiler 这个 NuGet 包,
/// 与 MCP 的任何管线都无关。旧世系剩下的九百行(事务、历史、自制 diff)不搬 ——
/// 版本间差异交给 git,那是它的本职,而且**严格强于**自制 diff(重命名检测、跨版本回溯)。
/// </summary>
public static class Decompiler
{
    /// <summary>
    /// RimWorld 跑在 Unity 2022.3 上,官方语言档位是 C# 9(record/init 因缺 IsExternalInit 实际不可用)。
    /// 锁在 CSharp9_0 是为了让产物贴近 Ludeon 真实能写出的形态,而不是让反编译器用 C# 11 语法糖重写。
    ///
    /// 它还是**字节级稳定**的前提:档位一变,整棵树的每个文件都会重排,git diff 里就是
    /// 一万四千个文件全红,而真正的游戏改动淹在里面。
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
    /// 反编译器每跑一次都生成一个新的随机 GUID,而那是整棵树里**唯一**不确定的东西:
    /// 实测一次「什么都没变」的重跑,一万四千个 .cs 逐字节相同,29 个 .csproj 全红,红的
    /// 只有这一行。留着它,每次同步的 diff 里都躺着二十九条假改动 —— 而这个仓存在的
    /// 唯一目的就是让真改动一眼可见。
    ///
    /// 不直接删掉 .csproj:它记着这个程序集引用了谁,而引用集合变了是**真**改动,
    /// 是值得在 diff 里看见的那一类。
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
