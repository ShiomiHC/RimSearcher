using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server;

public static class UpdateChecker
{
    // 版本号：唯一来源是 Sources/Directory.Build.props 的 <Version>，这里只是读程序集元数据。
    // 曾经这行是硬编码的 "2.7"，而 csproj 没设 <Version>，于是 exe 的文件版本停在 1.0.0.0——
    // 同一个二进制自报 2.7、属性页写 1.0.0.0，改版本号时也总有一处会被忘掉。
    // 取 InformationalVersion 而不是 AssemblyName.Version：后者被规范化成四段（2.7 → 2.7.0.0），
    // 而这个值要露给 initialize 的 serverInfo.version 和 User-Agent，保持 "2.7" 的原样写法更贴合。
    public static readonly string CurrentVersion = ReadCurrentVersion();

    // fork 时只改这一处：检测源和通知里给出的下载地址都由它推导，
    // 否则版本号一落后就会把本 fork 的用户导流到上游 releases 页。
    private const string Repo = "ShiomiHC/RimSearcher";

    private const string GitHubApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string ReleasesUrl = $"https://github.com/{Repo}/releases/latest";

    private static string CacheFilePath
    {
        get
        {
            var indexCacheDir = IndexCacheService.GetDefaultCacheDirectory();
            var parent = Path.GetDirectoryName(indexCacheDir) ?? indexCacheDir;
            // 带上仓库名：fork 与上游若共用缓存根目录，24h 窗口内会互相读到对方的版本记录
            var slug = Repo.Replace('/', '-');
            return Path.Combine(parent, $".update-cache-{slug}");
        }
    }
    
    public static async Task CheckAsync()
    {
        try
        {
            if (TryReadCache(out var cachedVersion, out var cachedTime))
            {
                if (DateTime.UtcNow - cachedTime < TimeSpan.FromHours(24))
                {
                    if (cachedVersion != null && IsNewer(cachedVersion, CurrentVersion))
                    {
                        await NotifyUpdate(cachedVersion);
                    }
                    return;
                }
            }

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"RimSearcher/{CurrentVersion}");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            var response = await httpClient.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                var latestVersion = tagProp.GetString()?.TrimStart('v', 'V');
                if (!string.IsNullOrEmpty(latestVersion))
                {
                    WriteCache(latestVersion);

                    if (IsNewer(latestVersion, CurrentVersion))
                    {
                        await NotifyUpdate(latestVersion);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static async Task NotifyUpdate(string latestVersion)
    {
        await ServerLogger.Warning("UpdateChecker", "New version is available",
            ("current", CurrentVersion),
            ("latest", latestVersion),
            ("repo", Repo),
            ("url", ReleasesUrl));
    }

    // InformationalVersion 可以带 SemVer 的预发布段与构建元数据（"2.7-rc.1+abc1234"），
    // 而 IsNewer 是 Split('.') 逐段 int.Parse——非数字段会抛异常并被它的 catch 吞掉，
    // 表现出来不是报错而是「更新提示永久静默」，最难查的那种坏法。所以在入口就把 '-'/'+'
    // 之后的部分切掉，只留纯数字点分串，IsNewer 的解析假设保持不变。
    // 元数据缺失（理论上不会：SDK 总会生成该特性）时退回 AssemblyName.Version，
    // 那是四段规范化形式，同样能被 IsNewer 正常比较。
    private static string ReadCurrentVersion()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('-', '+')[0].Trim();
        }

        return assembly.GetName().Version?.ToString() ?? "0.0";
    }

    private static bool IsNewer(string remote, string local)
    {
        try
        {
            var remoteParts = remote.Split('.').Select(int.Parse).ToArray();
            var localParts = local.Split('.').Select(int.Parse).ToArray();
            var maxLen = Math.Max(remoteParts.Length, localParts.Length);

            for (int i = 0; i < maxLen; i++)
            {
                var r = i < remoteParts.Length ? remoteParts[i] : 0;
                var l = i < localParts.Length ? localParts[i] : 0;
                if (r > l) return true;
                if (r < l) return false;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadCache(out string? version, out DateTime checkTime)
    {
        version = null;
        checkTime = DateTime.MinValue;

        try
        {
            if (!File.Exists(CacheFilePath)) return false;
            var lines = File.ReadAllLines(CacheFilePath);
            if (lines.Length < 2) return false;

            version = lines[0].Trim();
            checkTime = DateTime.Parse(lines[1].Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCache(string version)
    {
        try
        {
            var dir = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(CacheFilePath, new[]
            {
                version,
                DateTime.UtcNow.ToString("O")
            });
        }
        catch
        {
        }
    }
}
