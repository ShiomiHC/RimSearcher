using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Sources;

public sealed record DecompileRequest
{
    public required string AssemblyPath { get; init; }
    public required string OutputDirectory { get; init; }

    public ReferenceRecipe References { get; init; } = new();

    public LanguageVersion? Language { get; init; }
}

public sealed record DecompileOutcome
{
    public required string AssemblyPath { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public int FileCount { get; init; }

    public IReadOnlyDictionary<string, Guid> References { get; init; } = new Dictionary<string, Guid>();
}

public static class Decompiler
{
    public static string Version { get; } =
        typeof(WholeProjectDecompiler).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Identity { get; } = $"{Version}+{SettingsFingerprint()}";

    public static DecompilerSettings CreateSettings(LanguageVersion language) => new(language)
    {
        ThrowOnAssemblyResolveErrors = false,
        RemoveDeadCode = true,
        RemoveDeadStores = true,
        UseSdkStyleProjectFormat = false,
        UseNestedDirectoriesForNamespaces = true,
        UsePrimaryConstructorSyntaxForNonRecordTypes = false,
        FileScopedNamespaces = false,
        StructDefaultConstructorsAndFieldInitializers = false,
    };

    private static string SettingsFingerprint()
    {
        var settings = CreateSettings(LanguageLevel.Latest);
        var text = string.Join(";", typeof(DecompilerSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && p.CanRead)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.GetValue(settings)}"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8];
    }

    public static DecompileOutcome Decompile(DecompileRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(request.OutputDirectory);

            using var file = new PEFile(request.AssemblyPath);
            using var recording = request.References.Open(file);
            var decompiler = new WholeProjectDecompiler(
                CreateSettings(request.Language ?? LanguageLevel.Of(request.AssemblyPath)),
                recording, projectWriter: null,
                assemblyReferenceClassifier: null, debugInfoProvider: null);

            decompiler.DecompileProject(file, request.OutputDirectory, cancellationToken);
            StabilizeProjectGuids(request.OutputDirectory);

            return new DecompileOutcome
            {
                AssemblyPath = request.AssemblyPath,
                Success = true,
                FileCount = CountSourceFiles(request.OutputDirectory),
                References = recording.Resolved(),
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
            catch {  }
        }
    }

    public static string StableProjectGuid(string projectName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("rimsearcher/" + projectName));
        return new Guid(hash.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex StateMachineClass =
        new(@"\bclass _003C\w*_003Ed__\d+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static int CountUnrestoredStateMachineFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                            .Count(f => StateMachineClass.IsMatch(File.ReadAllText(f)));
        }
        catch { return 0; }
    }

    public const string UnknownTypeMarker = "Unknown result type (might be due to invalid IL or missing references)";

    public static int CountUnknownTypeLines(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                            .Sum(f => File.ReadLines(f).Count(l => l.Contains(UnknownTypeMarker, StringComparison.Ordinal)));
        }
        catch { return 0; }
    }

    private static int CountSourceFiles(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }
}
