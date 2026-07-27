using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace RimSearcher.Core;

public sealed record DecompileRequest
{
    public required string AssemblyPath { get; init; }
    public required string OutputDirectory { get; init; }

    // 类型解析用的搜索目录。缺了它泛型约束和继承链会退化成 object，
    // 直接影响 inspect / trace 两个工具的准确度。
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];
}

public sealed record DecompileOutcome
{
    public required string AssemblyPath { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public int FileCount { get; init; }
    public long ElapsedMs { get; init; }
}

public static class DecompileService
{
    // RimWorld 跑在 Unity 2022.3 上，官方语言档位是 C# 9（record/init 因缺 IsExternalInit 实际不可用）。
    // 锁在 CSharp9_0 是为了让产物贴近 Ludeon 真实能写出的形态，而不是让反编译器用 C# 11 语法糖重写。
    public static DecompilerSettings CreateSettings() => new(LanguageVersion.CSharp9_0)
    {
        ThrowOnAssemblyResolveErrors = false,
        RemoveDeadCode = true,
        RemoveDeadStores = true,
        UseSdkStyleProjectFormat = false,
        UseNestedDirectoriesForNamespaces = true
    };

    public static DecompileOutcome Decompile(DecompileRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(request.OutputDirectory);

            using var file = new PEFile(request.AssemblyPath);
            var resolver = new UniversalAssemblyResolver(
                request.AssemblyPath,
                throwOnError: false,
                targetFramework: file.DetectTargetFrameworkId());

            foreach (var path in request.ReferencePaths)
            {
                if (Directory.Exists(path)) resolver.AddSearchDirectory(path);
            }

            var decompiler = new WholeProjectDecompiler(
                CreateSettings(),
                resolver,
                assemblyReferenceClassifier: null,
                debugInfoProvider: null);

            decompiler.DecompileProject(file, request.OutputDirectory, cancellationToken);

            stopwatch.Stop();
            return new DecompileOutcome
            {
                AssemblyPath = request.AssemblyPath,
                Success = true,
                FileCount = CountSourceFiles(request.OutputDirectory),
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DecompileOutcome
            {
                AssemblyPath = request.AssemblyPath,
                Success = false,
                Error = ex.Message,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private static int CountSourceFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    // 从 AssemblyRef 推出该程序集需要的搜索目录：引用名命中的 dll 在哪个目录，就把那个目录加进来。
    // 比读 About.xml 更贴合反编译——后者还含纯 XML patch 依赖，对类型解析没有意义。
    public static List<string> ResolveReferencePaths(
        string assemblyPath,
        IReadOnlyList<AssemblyEntry> candidates,
        IEnumerable<string> alwaysInclude)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            if (seen.Add(directory)) paths.Add(directory);
        }

        foreach (var path in alwaysInclude) Add(path);
        Add(Path.GetDirectoryName(assemblyPath));

        var metadata = ReadMetadataSafe(assemblyPath);
        if (metadata == null) return paths;

        var wanted = metadata.References.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var name = Path.GetFileNameWithoutExtension(candidate.Path);
            if (wanted.Contains(name)) Add(Path.GetDirectoryName(candidate.Path));
        }

        return paths;
    }

    private static AssemblyMetadata? ReadMetadataSafe(string path)
    {
        try { return AssemblyScanner.ReadMetadata(path); }
        catch { return null; }
    }
}
