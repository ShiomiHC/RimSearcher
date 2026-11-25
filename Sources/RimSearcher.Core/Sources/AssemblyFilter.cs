using System.Security.Cryptography;
using RimTooling.IO;

namespace RimSearcher.Sources;

public static class AssemblyFilter
{
    private static readonly string[] ExcludedExactNames =
    [
        "mscorlib", "netstandard", "System", "UnityEngine",
        "I18N", "Newtonsoft.Json", "websocket-sharp", "WindowsBase",
    ];

    private static readonly string[] ExcludedFamilyPrefixes =
    [
        "System.", "UnityEngine.", "I18N.", "Newtonsoft.Json.",
        "Microsoft.", "Unity.", "Mono.",
    ];

    public static bool IsRuntimeAssembly(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        foreach (var exact in ExcludedExactNames)
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var family in ExcludedFamilyPrefixes)
            if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static bool IsRuntimeAssemblyFile(string path)
        => IsRuntimeAssembly(path) || IsRuntimeAssembly(AssemblyName(path) + ".dll");

    public static bool HasPeSignature(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    public static bool IsManaged(string path) => PeImage.HasMetadata(path);

    public static HashSet<string> References(string assemblyPath)
        => PeImage.TryReadAssemblyReferences(assemblyPath, out var refs)
            ? new HashSet<string>(refs, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static string AssemblyName(string path)
        => PeImage.TryReadIdentity(path, out var identity) && identity.AssemblyName.Length > 0
            ? identity.AssemblyName
            : Path.GetFileNameWithoutExtension(path);

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
