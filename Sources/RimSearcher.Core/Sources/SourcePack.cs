using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RimSearcher.Search;

namespace RimSearcher.Sources;

public sealed class SourcePack : IDisposable
{
    public const string Dir = ".packs";

    private const ulong Magic = 0x314B_5053_4352_5352;
    private const int Version = 3;
    private const int HeaderSize = 64;
    private const int BucketSize = 16;
    private const int ChunkSize = 4 << 20;

    private readonly SafeFileHandle _handle;
    private readonly Dictionary<string, Entry> _entries;
    private readonly long _dataEnd;
    private byte[] _chunk = [];
    private long _chunkStart;
    private int _chunkLength;

    private readonly record struct Entry(long Offset, int Length, long Ticks, uint Crc);

    private SourcePack(SafeFileHandle handle, Dictionary<string, Entry> entries, long dataEnd)
    {
        _handle = handle;
        _entries = entries;
        _dataEnd = dataEnd;
    }

    public int Count => _entries.Count;

    public static string PathFor(string root, string tree) => Path.Combine(root, Dir, tree + ".pack");

    public static SourcePack? Open(string root, string tree)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(PathFor(root, tree), FileMode.Open, FileAccess.Read,
                                     FileShare.Read | FileShare.Delete);
            var size = RandomAccess.GetLength(handle);
            if (size < HeaderSize) return Fail(handle);

            var head = new byte[HeaderSize];
            if (!ReadFully(handle, head, 0)) return Fail(handle);
            var count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
            var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(16));
            var indexLength = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(24));
            if (!HeadIsSound(head)
                || count < 0 || indexOffset < HeaderSize || indexLength < 0 || indexLength > int.MaxValue
                || indexOffset + indexLength != size)
                return Fail(handle);

            var index = new byte[indexLength];
            if (!ReadFully(handle, index, indexOffset)
                || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32)) != Crc(index))
                return Fail(handle);

            var entries = new Dictionary<string, Entry>(count, StringComparer.Ordinal);
            var at = 0;
            for (var i = 0; i < count; i++)
            {
                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(at));
                var name = Encoding.UTF8.GetString(index, at + 4, nameLength);
                at += 4 + nameLength;
                var offset = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(at));
                var length = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(at + 8));
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(at + 16));
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(at + 24));
                at += 28;
                if (offset < HeaderSize || length < 0 || length > int.MaxValue || offset + length > indexOffset)
                    return Fail(handle);
                entries[name] = new Entry(offset, (int)length, ticks, crc);
            }
            if (at != index.Length) return Fail(handle);

            return new SourcePack(handle, entries, indexOffset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(handle);
        }
    }

    private static bool HeadIsSound(ReadOnlySpan<byte> head)
        => BinaryPrimitives.ReadUInt64LittleEndian(head) == Magic
           && BinaryPrimitives.ReadInt32LittleEndian(head[8..]) == Version
           && BinaryPrimitives.ReadUInt32LittleEndian(head[60..]) == Crc(head[..60]);

    private static SourcePack? Fail(SafeFileHandle? handle)
    {
        handle?.Dispose();
        return null;
    }

    public string[]? TryReadLines(string treeRelative, long length, long ticks)
    {
        if (!_entries.TryGetValue(treeRelative, out var e) || e.Length != length || e.Ticks != ticks) return null;
        if (!TryLoad(e, out var offset)) return null;

        return Lines(_chunk, offset, e.Length);
    }

    private static string[] Lines(byte[] bytes, int offset, int length)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, offset, length, writable: false),
                                            Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    public sealed record Member(string Name, string Type, string Kind, int Line, string Declaration);

    public sealed record MemberHit(string Tree, string File, Member Member);

    private const int DeclarationCap = 200;

    internal static IEnumerable<Member> MembersOf(string[] lines)
    {
        var decls = CsOutline.Scan(lines);
        var containers = decls.Where(d => d.Kind == "namespace" || CsOutline.IsType(d.Kind)).ToList();
        foreach (var d in decls)
        {
            if (d.Kind is not ("field" or "property" or "method")) continue;
            var type = string.Join(".", containers.Where(c => c.StartLine <= d.StartLine && c.EndLine >= d.EndLine)
                                                 .OrderBy(c => c.StartLine).Select(c => c.Name));
            var line = d.StartLine;
            for (var i = d.StartLine; i <= Math.Min(d.EndLine, lines.Length); i++)
                if (ContainsWord(lines[i - 1], d.Name)) { line = i; break; }
            var text = line - 1 < lines.Length ? lines[line - 1].Trim() : "";
            if (text.Length > DeclarationCap) text = text[..DeclarationCap] + "…";
            yield return new Member(d.Name, type, d.Kind, line, text);
        }
    }

    private static bool ContainsWord(string line, string word)
    {
        for (var at = line.IndexOf(word, StringComparison.Ordinal); at >= 0;
             at = line.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !IsIdent(line[at - 1]);
            var after = at + word.Length >= line.Length || !IsIdent(line[at + word.Length]);
            if (before && after) return true;
        }
        return false;

        static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    public static List<MemberHit>? FindMembers(string root, string tree, string name)
    {
        if (name.Length == 0) return [];
        List<(Member M, string Rel, long Length, long Ticks)> found;
        try
        {
            using var handle = File.OpenHandle(PathFor(root, tree), FileMode.Open, FileAccess.Read,
                                               FileShare.Read | FileShare.Delete);
            var head = new byte[HeaderSize];
            if (!ReadFully(handle, head, 0) || !HeadIsSound(head)) return null;
            var bucketsOffset = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(36));
            var bucketCount = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(44));
            var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(16));
            if (bucketCount <= 0 || (bucketCount & (bucketCount - 1)) != 0 || bucketsOffset < HeaderSize
                || bucketsOffset + (long)bucketCount * BucketSize > indexOffset)
                return null;

            var entry = new byte[BucketSize];
            if (!ReadFully(handle, entry, bucketsOffset + (long)Bucket(name, bucketCount) * BucketSize)) return null;
            var offset = BinaryPrimitives.ReadInt64LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(8));
            if (length == 0) return [];
            if (offset < HeaderSize || length < 0 || offset + length > bucketsOffset) return null;
            var block = new byte[length];
            if (!ReadFully(handle, block, offset)
                || BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(12)) != Crc(block))
                return null;

            found = [];
            using var br = new BinaryReader(new MemoryStream(block, writable: false), Encoding.UTF8);
            while (br.BaseStream.Position < block.Length)
            {
                var (n, t, k, rel) = (Str(br), Str(br), Str(br), Str(br));
                var (len, ticks, line, decl) = (br.ReadInt64(), br.ReadInt64(), br.ReadInt32(), Str(br));
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    found.Add((new Member(n, t, k, line, decl), rel, len, ticks));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException
                                       or ArgumentException)
        {
            return null;
        }

        var hits = new List<MemberHit>();
        foreach (var file in found.GroupBy(f => f.Rel, StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(root, tree, file.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists) continue;
            if (file.All(f => f.Length == info.Length && f.Ticks == info.LastWriteTimeUtc.Ticks))
            {
                hits.AddRange(file.Select(f => new MemberHit(tree, file.Key, f.M)));
                continue;
            }
            try
            {
                var bytes = File.ReadAllBytes(info.FullName);
                hits.AddRange(MembersOf(Lines(bytes, 0, bytes.Length))
                              .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                              .Select(m => new MemberHit(tree, file.Key, m)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return hits;
    }

    private static int Bucket(string name, int bucketCount)
    {
        var h = 2166136261u;
        foreach (var c in name)
        {
            h ^= char.ToUpperInvariant(c);
            h *= 16777619u;
        }
        return (int)(h & (uint)(bucketCount - 1));
    }

    private static void Str(BinaryWriter bw, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        bw.Write((ushort)Math.Min(bytes.Length, ushort.MaxValue));
        bw.Write(bytes, 0, Math.Min(bytes.Length, ushort.MaxValue));
    }

    private static string Str(BinaryReader br) => Encoding.UTF8.GetString(br.ReadBytes(br.ReadUInt16()));

    private bool TryLoad(Entry e, out int offset)
    {
        offset = 0;
        try
        {
            if (e.Offset < _chunkStart || e.Offset + e.Length > _chunkStart + _chunkLength)
            {
                var want = (int)Math.Max(e.Length, Math.Min(ChunkSize, _dataEnd - e.Offset));
                if (_chunk.Length < want) _chunk = new byte[Math.Max(want, ChunkSize)];
                _chunkLength = 0;
                if (!ReadFully(_handle, _chunk.AsSpan(0, want), e.Offset)) return false;
                _chunkStart = e.Offset;
                _chunkLength = want;
            }
            offset = (int)(e.Offset - _chunkStart);
            return Crc(_chunk.AsSpan(offset, e.Length)) == e.Crc;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose() => _handle.Dispose();

    private static List<(string Rel, string Abs, long Length, long Ticks)> Scan(string treeDir)
        => new DirectoryInfo(treeDir).EnumerateFiles("*", SearchOption.AllDirectories)
                                     .Where(f => f.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                                     .Select(f => (Rel: Path.GetRelativePath(treeDir, f.FullName).Replace('\\', '/'),
                                                   Abs: f.FullName, f.Length, f.LastWriteTimeUtc.Ticks))
                                     .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

    public static bool IsCurrent(string root, string tree)
    {
        using var pack = Open(root, tree);
        if (pack is null) return false;
        List<(string Rel, string Abs, long Length, long Ticks)> files;
        try { files = Scan(Path.Combine(root, tree)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        if (files.Count != pack._entries.Count) return false;
        foreach (var f in files)
            if (!pack._entries.TryGetValue(f.Rel, out var e) || e.Length != f.Length || e.Ticks != f.Ticks
                || !pack.TryLoad(e, out _))
                return false;
        return true;
    }

    public static int Build(string root, string tree)
    {
        var treeDir = Path.Combine(root, tree);
        var files = Scan(treeDir);
        Directory.CreateDirectory(Path.Combine(root, Dir));
        var final = PathFor(root, tree);
        var tmp = $"{final}.{Environment.ProcessId}.tmp";

        var entries = new List<(byte[] Name, long Offset, long Length, long Ticks, uint Crc)>(files.Count);
        var members = new List<(Member M, string Rel, long Length, long Ticks)>();
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                fs.Write(new byte[HeaderSize]);
                foreach (var f in files)
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(f.Abs); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    var now = new FileInfo(f.Abs);
                    if (!now.Exists || now.Length != f.Length || bytes.Length != f.Length
                        || now.LastWriteTimeUtc.Ticks != f.Ticks)
                        continue;
                    entries.Add((Encoding.UTF8.GetBytes(f.Rel), fs.Position, bytes.Length, f.Ticks, Crc(bytes)));
                    fs.Write(bytes);
                    foreach (var m in MembersOf(Lines(bytes, 0, bytes.Length)))
                        members.Add((m, f.Rel, f.Length, f.Ticks));
                }

                var bucketCount = 1;
                while (bucketCount < members.Count / 8 && bucketCount < 1 << 20) bucketCount <<= 1;
                var buckets = new byte[bucketCount * BucketSize];
                foreach (var group in members.GroupBy(m => Bucket(m.M.Name, bucketCount)))
                {
                    using var block = new MemoryStream();
                    using (var bw = new BinaryWriter(block, Encoding.UTF8, leaveOpen: true))
                        foreach (var (m, rel, length, ticks) in group)
                        {
                            Str(bw, m.Name); Str(bw, m.Type); Str(bw, m.Kind); Str(bw, rel);
                            bw.Write(length); bw.Write(ticks); bw.Write(m.Line); Str(bw, m.Declaration);
                        }
                    var bytes = block.ToArray();
                    var slot = group.Key * BucketSize;
                    BinaryPrimitives.WriteInt64LittleEndian(buckets.AsSpan(slot), fs.Position);
                    BinaryPrimitives.WriteInt32LittleEndian(buckets.AsSpan(slot + 8), bytes.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(buckets.AsSpan(slot + 12), Crc(bytes));
                    fs.Write(bytes);
                }
                var bucketsOffset = fs.Position;
                fs.Write(buckets);

                var indexOffset = fs.Position;
                var index = new byte[entries.Sum(e => 4 + e.Name.Length + 28)];
                var at = 0;
                foreach (var e in entries)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(at), e.Name.Length);
                    e.Name.CopyTo(index, at + 4);
                    at += 4 + e.Name.Length;
                    BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(at), e.Offset);
                    BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(at + 8), e.Length);
                    BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(at + 16), e.Ticks);
                    BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(at + 24), e.Crc);
                    at += 28;
                }
                fs.Write(index);

                var head = new byte[HeaderSize];
                BinaryPrimitives.WriteUInt64LittleEndian(head, Magic);
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), Version);
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(12), entries.Count);
                BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(16), indexOffset);
                BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(24), index.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(32), Crc(index));
                BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(36), bucketsOffset);
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(44), bucketCount);
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(48), members.Count);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(60), Crc(head.AsSpan(0, 60)));
                fs.Position = 0;
                fs.Write(head);
            }

            try { File.Move(tmp, final, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Delete(final);
                File.Move(tmp, final);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return entries.Count;
    }

    public static int Prune(string root, IReadOnlyCollection<string> trees)
    {
        var dir = Path.Combine(root, Dir);
        if (!Directory.Exists(dir)) return 0;
        var keep = trees.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.pack"))
        {
            if (keep.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static bool ReadFully(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            var n = RandomAccess.Read(handle, buffer, offset);
            if (n <= 0) return false;
            buffer = buffer[n..];
            offset += n;
        }
        return true;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = ~0u;
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }
        foreach (var b in data) crc = BitOperations.Crc32C(crc, b);
        return ~crc;
    }
}
