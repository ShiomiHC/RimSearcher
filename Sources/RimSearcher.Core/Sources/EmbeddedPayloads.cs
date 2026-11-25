using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using RimTooling.IO;

namespace RimSearcher.Sources;

public static class EmbeddedPayloads
{
    public const string DirName = ".payloads";

    public static string Directory(string decompiledDir) => Path.Combine(decompiledDir, DirName);

    public static List<string> Extract(IEnumerable<string> containers, IReadOnlySet<Guid> wanted, string cacheDir)
    {
        var result = new List<string>();
        if (wanted.Count == 0) return result;
        var done = new HashSet<Guid>();
        foreach (var container in containers)
            foreach (var bytes in Candidates(container))
                if (Identity(bytes) is var (mvid, name) && wanted.Contains(mvid) && done.Add(mvid) &&
                    Store(bytes, name, cacheDir) is { } path)
                    result.Add(path);
        return result;
    }

    private static List<byte[]> Candidates(string container)
    {
        var found = new List<byte[]>();
        try
        {
            using var pe = new PEReader(File.OpenRead(container));
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { ResourcesDirectory: { Size: > 0 } dir }) return found;
            var md = pe.GetMetadataReader();
            var section = pe.GetSectionData(dir.RelativeVirtualAddress);
            foreach (var handle in md.ManifestResources)
            {
                var resource = md.GetManifestResource(handle);
                if (!resource.Implementation.IsNil) continue;
                var reader = section.GetReader((int)resource.Offset, dir.Size - (int)resource.Offset);
                var data = reader.ReadBytes(reader.ReadInt32());
                if (IsPe(data)) found.Add(data);
                else if (data is [(byte)'P', (byte)'K', 3, 4, ..]) found.AddRange(ZipEntries(data));
            }
        }
        catch {  }
        return found;
    }

    private static IEnumerable<byte[]> ZipEntries(byte[] zip)
    {
        var found = new List<byte[]>();
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip));
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                if (IsPe(ms.GetBuffer().AsSpan(0, (int)ms.Length))) found.Add(ms.ToArray());
            }
        }
        catch {  }
        return found;
    }

    private static bool IsPe(ReadOnlySpan<byte> data) => data is [(byte)'M', (byte)'Z', ..];

    private static (Guid Mvid, string Name)? Identity(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata || pe.GetMetadataReader() is not { IsAssembly: true } md) return null;
            return (md.GetGuid(md.GetModuleDefinition().Mvid), md.GetString(md.GetAssemblyDefinition().Name));
        }
        catch { return null; }
    }

    private static string? Store(byte[] bytes, string name, string cacheDir)
    {
        var path = Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(bytes))[..16], name + ".dll");
        if (File.Exists(path)) return path;
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, bytes.AsSpan());
            return path;
        }
        catch (IOException) when (File.Exists(path)) { return path; }
        catch { return null; }
    }
}
