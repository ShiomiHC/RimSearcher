using RimTooling.IO;
using RimTooling.RimWorld;

namespace RimSearcher.Sources;

public sealed record GameLayout
{
    private const string CoreAssembly = RimTooling.RimWorld.GameLayout.CoreAssemblyFileName;

    private const string HostPattern = "RimWorld*.dll";

    public const string ExeNames = "RimWorld.exe（1.7 起）或 RimWorldWin64.exe（1.6 及以前）";

    public required RimTooling.RimWorld.GameLayout Core { get; init; }

    public GameEngine Engine => Core.Engine;

    public string AssemblyDir => Core.AssemblyDir;

    public string Exe => Core.ExePath;

    public static GameLayout Detect(string gameDir) => new() { Core = RimTooling.RimWorld.GameLayout.Of(gameDir) };

    public static GameLayout Of(string gameDir)
    {
        try
        {
            return Detect(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            string fallbackDir;
            try { fallbackDir = PathIdentity.Normalize(gameDir); }
            catch { fallbackDir = gameDir; }
            return new()
            {
                Core = new RimTooling.RimWorld.GameLayout
                {
                    GameDir = fallbackDir,
                    Engine = GameEngine.Unity,
                    ExePath = Path.Combine(fallbackDir, "RimWorldWin64.exe"),
                    AssemblyDir = Path.Combine(fallbackDir, "RimWorldWin64_Data", "Managed"),
                },
            };
        }
    }

    public string CoreAssemblyPath => Core.CoreAssemblyPath;

    public string? UnityStubsPath => Core.UnityStubsPath;

    public string? CoreLibPath
        => Engine == GameEngine.Waylith && Path.Combine(AssemblyDir, "System.Private.CoreLib.dll") is var p && File.Exists(p)
           && AssemblyFilter.IsManaged(p)
            ? PathIdentity.Normalize(p) : null;

    public List<string> EnginePaths()
    {
        if (Engine != GameEngine.Waylith || !Directory.Exists(AssemblyDir)) return [];
        return new[] { "Waylith*.dll", "Brutal.*.dll" }
                        .SelectMany(p => Directory.EnumerateFiles(AssemblyDir, p, SearchOption.TopDirectoryOnly))
                        .Where(AssemblyFilter.IsManaged)
                        .Select(p => PathIdentity.Normalize(p))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> GameAssemblies()
    {
        if (!Directory.Exists(AssemblyDir)) return [];
        var dlls = Engine == GameEngine.Waylith
            ? Directory.EnumerateFiles(AssemblyDir, HostPattern, SearchOption.TopDirectoryOnly)
                       .Where(AssemblyFilter.IsManaged)
                       .Prepend(CoreAssemblyPath).Where(File.Exists)
            : Directory.EnumerateFiles(AssemblyDir, "*.dll", SearchOption.TopDirectoryOnly)
                       .Where(f => !AssemblyFilter.IsRuntimeAssembly(f));
        return dlls.Select(p => PathIdentity.Normalize(p)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> LibraryPaths()
    {
        if (Engine != GameEngine.Waylith || !Directory.Exists(AssemblyDir)) return [];
        var claimed = GameAssemblies().Concat(EnginePaths())
                                      .Concat(new[] { UnityStubsPath, CoreLibPath }.OfType<string>())
                                      .ToHashSet(PathComparison.DefaultComparer);
        return Directory.EnumerateFiles(AssemblyDir, "*.dll", SearchOption.TopDirectoryOnly)
                        .Select(p => PathIdentity.Normalize(p))
                        .Where(f => !claimed.Contains(f) && !AssemblyFilter.IsRuntimeAssemblyFile(f) && AssemblyFilter.IsManaged(f))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
