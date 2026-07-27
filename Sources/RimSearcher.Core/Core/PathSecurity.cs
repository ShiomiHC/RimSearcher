using System.Runtime.InteropServices;

namespace RimSearcher.Core;

public static class PathSecurity
{
    private static readonly bool OnWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Windows 路径大小写不敏感，Unix 敏感。全平台一律 OrdinalIgnoreCase 的话，Linux 上
    // 允许 /home/src 就等于连 /home/SRC 一起放行——那是另一个目录，白名单凭空变宽了。
    private static readonly StringComparison PathComparison =
        OnWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OnWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // 链接可以指向链接。跟到这个次数还没到底就当成环，拒掉——比栈溢出或死循环体面
    private const int MaxLinkHops = 32;

    private static readonly List<string> AllowedRoots = new();
    private static bool _enabled = true;

    public static void Initialize(IEnumerable<string> paths, bool enabled = true)
    {
        _enabled = enabled;

        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;

            // 允许的根自身很可能就是 junction（把源码目录做成链接指向别的盘是常见做法）。
            // 这里必须一并解析成最终目标：请求路径在下面也是按最终目标算的，两边不一致的话
            // 合法的子路径会全部被误拒。
            var resolvedPath = ResolvePath(path);
            if (resolvedPath != null && !AllowedRoots.Contains(resolvedPath, PathComparer))
            {
                AllowedRoots.Add(resolvedPath);
            }
        }
    }

    public static bool IsPathSafe(string requestedPath)
    {
        if (!_enabled) return true;
        if (string.IsNullOrEmpty(requestedPath)) return false;

        try
        {
            var resolvedPath = ResolvePath(requestedPath);
            if (resolvedPath == null) return false;

            return AllowedRoots.Any(root =>
            {
                if (resolvedPath.Equals(root, PathComparison)) return true;

                // 必须比到「根 + 分隔符」：只比前缀的话，允许 C:\src 会连 C:\src2 一起放行
                var rootWithSlash = root + Path.DirectorySeparatorChar;
                if (resolvedPath.StartsWith(rootWithSlash, PathComparison)) return true;

                var rootWithAltSlash = root + Path.AltDirectorySeparatorChar;
                if (resolvedPath.StartsWith(rootWithAltSlash, PathComparison)) return true;

                return false;
            });
        }
        catch
        {
            return false;
        }
    }

    // 把调用方给的相对路径钉死在 root 内，返回规范化后的绝对路径，越界或形式非法时返回 null。
    // 历史归档的 diff 读取走的就是这条路：那里的相对路径完全由调用方指定。
    public static string? ResolveInsideRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return null;

        try
        {
            // Path.Combine 遇到 rooted 的第二段会直接丢掉第一段，"C:\Windows\win.ini" 就这么
            // 原样直通了。Windows 上 "C:foo"（驱动器相对）同样算 rooted，一并拒掉。
            if (Path.IsPathRooted(relativePath)) return null;

            var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var segments = relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            // ".." 是显式的穿越写法，直接拒而不是靠下面的规范化兜——报错点离原因更近
            if (segments.Any(segment => segment == "..")) return null;

            var combined = Path.GetFullPath(
                Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));

            // 前两道只挡已知写法；规范化后再复核一次，兜住没想到的那些（尾随点/空格、8.3 短名等）
            var normalizedRoot = Normalize(Path.GetFullPath(root));
            var normalizedCombined = Normalize(combined);

            return normalizedCombined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
                ? normalizedCombined
                : null;
        }
        catch
        {
            return null;
        }
    }

    // 仅供测试：AllowedRoots 是静态的，且 Initialize 只追加不清空，
    // 用例之间会互相污染（前一个用例的根让后一个用例的「应当拒绝」变成放行）。
    internal static void ResetForTests()
    {
        AllowedRoots.Clear();
        _enabled = true;
    }

    private static string? ResolvePath(string path)
    {
        try
        {
            return ResolveLinks(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    // Path.GetFullPath 只做字符串级规范化，不解析 junction / 符号链接；而只检查末段有没有
    // ReparsePoint 属性挡不住「根内某个祖先目录指向根外」——叶子文件自身没有该属性，
    // 于是白名单形同虚设。所以从盘符起逐段下行，遇到重解析点就换成它的最终目标再往下走。
    private static string? ResolveLinks(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(current)) return null;

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = fullPath[current.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries);

        var hops = 0;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            // 文件链也要跟：只跟目录链的话，一个指向根外的符号链接文件照样能被读出内容
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;

            // 尚不存在的段（还没建的目录、要写入的文件名）没有链可跟，按字面量继续拼
            if (info == null) continue;

            while ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (++hops > MaxLinkHops) return null;

                // returnFinalTarget 会一路跟到底，中途的链接也一并解析。拿不到目标
                // （悬空链接、无权限、环）时宁可拒绝，绝不退回未解析的字面路径
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target == null) return null;

                current = target.FullName;
                info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : null;

                if (info == null) break;
            }
        }

        return Normalize(current);
    }

    private static string Normalize(string fullPath)
    {
        if (OnWindows) fullPath = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
