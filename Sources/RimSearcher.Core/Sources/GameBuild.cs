using RimTooling.RimWorld;

namespace RimSearcher.Sources;

public static class GameBuild
{
    public static string? Format(Version assemblyVersion)
        => GameVersion.FromAssemblyVersion(assemblyVersion)?.FullString;

    public static string? Installed(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;
        try
        {
            var version = RimTooling.RimWorld.GameLayout.Of(gameDir).ReadVersion();
            return version is { Source: GameVersionSource.Assembly }
                ? version.FullString
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
