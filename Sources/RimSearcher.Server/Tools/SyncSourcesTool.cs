using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

// 手动触发的源同步。放成工具而非只做启动检测，是为了让「发现有更新」和「把更新拉进来」
// 发生在同一个对话里，不必切到终端去跑反编译再重启。
public class SyncSourcesTool : ITool
{
    private readonly SourceSyncService _syncService;
    private readonly IndexRebuilder? _rebuilder;

    public SyncSourcesTool(SourceSyncService syncService, IndexRebuilder? rebuilder = null)
    {
        _syncService = syncService;
        _rebuilder = rebuilder;
    }

    public string Name => "rimworld-searcher__sync_sources";

    // 本工具会调 IndexRebuilder 拿写锁，被读锁挡住就是自己等自己
    public bool BypassIndexGate => true;

    // 返回里已列了本次同步的逐类型 diff，不需要再追加一条过期提示
    public bool SuppressStalenessNotice => true;

    public string Description =>
        "Check whether the configured RimWorld/mod assemblies changed since the last decompile, and optionally "
        + "re-decompile them into the indexed source directories. action='check' is read-only and fast; "
        + "action='sync' performs decompilation and may take from seconds to a few minutes.";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[] { "check", "sync", "diff" },
                description =
                    "'check' (default) only reports which assemblies changed. 'sync' re-decompiles the changed sources. "
                    + "'diff' lists the source files added/modified/removed by the last sync (requires SourceHistoryDepth > 0).",
                @default = "check"
            },
            sources = new
            {
                type = "string",
                description =
                    "Optional comma-separated source names to limit the operation, matching the 'name' of configured "
                    + "C# sources. Omit to cover every followable source."
            },
            file = new
            {
                type = "string",
                description =
                    "For action='diff': a relative path from the diff listing (e.g. 'RimWorld\\CompShield.cs'). "
                    + "Given, returns the line-level unified diff for that one file instead of the file list."
            },
            version = new
            {
                type = "string",
                description =
                    "For action='diff': which archived version to compare against (e.g. 'v0002'). Defaults to the most recent."
            },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = 2000,
                description = "For action='diff': max changed files to list, or max diff lines when 'file' is given.",
                @default = 100
            }
        },
        required = Array.Empty<string>()
    };

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__sync_sources",
        "no required parameters.",
        "action ('check' | 'sync' | 'diff', default 'check'), sources (comma-separated names, optional), limit (diff only, default 100).");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var action = (ToolArgs.GetOptionalString(args, "action", "mode", "op") ?? "check").ToLowerInvariant();
        var rawSources = ToolArgs.GetOptionalString(args, "sources", "source", "scope", "name");

        var only = string.IsNullOrWhiteSpace(rawSources)
            ? null
            : rawSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var followable = _syncService.FollowableSources;
        if (followable.Count == 0)
        {
            return new ToolResult(
                "No followable sources configured.\n"
                + "Add an assemblies path to a [[sources]] block in config.toml, e.g.\n"
                + "  [[sources]]\n"
                + "  name       = \"Core\"\n"
                + "  csharp     = 'S:\\RimWorldSource\\Core'\n"
                + "  assemblies = 'D:\\SteamLibrary\\steamapps\\common\\RimWorld\\RimWorldWin64_Data\\Managed'");
        }

        try
        {
            return action switch
            {
                "sync" or "update" or "run" => await RunSyncAsync(only, cancellationToken),
                "diff" or "changes" => RunDiff(
                    only,
                    ToolArgs.GetInt(args, 100, "limit", "maxResults"),
                    ToolArgs.GetOptionalString(args, "file", "path", "filePath"),
                    ToolArgs.GetOptionalString(args, "version", "versionId")),
                _ => RunCheck()
            };
        }
        catch (OperationCanceledException)
        {
            return new ToolResult("Sync cancelled.", true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Sync failed: {ex.Message}", true);
        }
    }

    private ToolResult RunCheck()
    {
        var report = _syncService.Check();
        var builder = new StringBuilder();

        builder.AppendLine($"Source check ({report.ElapsedMs} ms, game version {_syncService.GameVersion ?? "unknown"}):");
        foreach (var change in report.Changes) builder.AppendLine($"  {change.Describe()}");

        builder.AppendLine(report.AnyChanges
            ? "\nChanges detected. Run this tool again with action='sync' to re-decompile."
            : "\nAll followable sources are up to date.");

        return new ToolResult(builder.ToString().TrimEnd());
    }

    private ToolResult RunDiff(string[]? only, int limit, string? file, string? version)
    {
        if (!_syncService.History.Enabled)
        {
            return new ToolResult(
                "Source history is disabled. Set source_history_depth to 1 or more in config.toml "
                + "to keep previous decompiled versions for diffing.", true);
        }

        if (!string.IsNullOrWhiteSpace(file)) return RunFileDiff(only, file!, version, limit);

        var builder = new StringBuilder();
        var any = false;

        foreach (var entry in _syncService.FollowableSources)
        {
            if (only is { Length: > 0 }
                && !only.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;

            var versions = _syncService.History.ListVersions(entry.Name);
            if (versions.Count == 0) continue;

            var latest = versions[^1];
            var diff = _syncService.History.DiffAgainst(entry.Name, entry.Path);
            if (diff == null) continue;

            any = true;
            builder.AppendLine(
                $"## {entry.Name} — since {latest.Id} ({latest.CapturedAtUtc:yyyy-MM-dd HH:mm} UTC)");
            builder.AppendLine(
                $"{diff.Added} added, {diff.Modified} modified, {diff.Removed} removed "
                + $"({versions.Count} version(s) kept, {latest.ArchivedBytes / 1024} KB archived)");

            foreach (var change in diff.Changes.Take(limit))
            {
                var mark = change.Kind switch
                {
                    FileChangeKind.Added => "+",
                    FileChangeKind.Removed => "-",
                    _ => "~"
                };
                builder.AppendLine($"  {mark} {change.RelativePath}");
            }

            if (diff.Changes.Count > limit)
                builder.AppendLine($"  ... {diff.Changes.Count - limit} more (raise limit)");

            builder.AppendLine();
        }

        return new ToolResult(any
            ? builder.ToString().TrimEnd()
            : "No recorded history yet. Run action='sync' first.");
    }

    // 归档里的旧内容 vs 磁盘上的当前内容。文件在某一侧缺失即为纯新增/纯删除。
    //
    // file 与 version 都是调用方给的裸串，且直接参与路径拼接：不校验的话
    // 「归档 里没有 / 当前有」这一支就是一个任意绝对路径的存在性探针，配上存在的 version
    // 更能把源根与历史根之外的文本读出来。两个根各自独立校验，不共用结论。
    private ToolResult RunFileDiff(string[]? only, string file, string? version, int limit)
    {
        foreach (var entry in _syncService.FollowableSources)
        {
            if (only is { Length: > 0 }
                && !only.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;

            var versions = _syncService.History.ListVersions(entry.Name);
            if (versions.Count == 0) continue;

            // 只认索引里真实存在的 Id。放任任意串进去等于让调用方指定历史根下的任意子目录，
            // 而 files/ 那一层的相对路径校验只保证「不出这个子目录」，管不了子目录选在哪
            string versionId;
            if (string.IsNullOrWhiteSpace(version))
            {
                versionId = versions[^1].Id;
            }
            else
            {
                var matched = versions.FirstOrDefault(
                    v => string.Equals(v.Id, version, StringComparison.OrdinalIgnoreCase));

                if (matched == null)
                {
                    return new ToolResult(
                        $"Unknown version '{version}' for source '{entry.Name}'. "
                        + $"Kept versions: {string.Join(", ", versions.Select(v => v.Id))}. "
                        + "Omit 'version' to compare against the most recent one.", true);
                }

                versionId = matched.Id;
            }

            var currentPath = PathSecurity.ResolveInsideRoot(entry.Path, file);
            var archivedPath = _syncService.History.ResolveArchivedPath(entry.Name, versionId, file);

            if (currentPath == null || archivedPath == null)
            {
                return new ToolResult(
                    $"'{file}' is not a relative path inside a source directory — absolute paths and '..' are refused. "
                    + "Pass the path exactly as printed by action='diff' without 'file' "
                    + @"(e.g. 'RimWorld\CompShield.cs').", true);
            }

            // 当前文件此前完全不过白名单，是全仓唯一一条这样的读路径。源根被配成指向别处的
            // 链接时，只靠上面的「不出源根」校验并不能保证读到的东西在允许的目录内
            if (!PathSecurity.IsPathSafe(currentPath))
            {
                return new ToolResult(
                    $"'{file}' resolves outside the allowed directories. "
                    + "Only files under the configured source paths can be diffed.", true);
            }

            var relative = Path.GetRelativePath(entry.Path, currentPath);

            if (TooLarge(archivedPath) || TooLarge(currentPath))
            {
                return new ToolResult(
                    $"'{relative}' is larger than the {SourceHistoryStore.MaxComparableFileSize / (1024 * 1024)} MB "
                    + "diff limit. Read the archived and current copies separately instead of diffing them.", true);
            }

            var archived = _syncService.History.ReadArchived(entry.Name, versionId, file);

            string? current = null;
            try
            {
                if (File.Exists(currentPath)) current = File.ReadAllText(currentPath);
            }
            catch (Exception ex)
            {
                return new ToolResult($"Failed to read current file: {ex.Message}", true);
            }

            if (archived == null && current == null) continue;

            if (archived == null)
                return new ToolResult($"--- {entry.Name}/{relative} @ {versionId}\n(added in this version — no previous content)");

            if (current == null)
                return new ToolResult($"--- {entry.Name}/{relative} @ {versionId}\n(removed — only the archived copy remains)");

            return new ToolResult(UnifiedDiffFormatter.Format(
                archived, current, $"{entry.Name}/{relative} @ {versionId}", contextLines: 3, maxLines: limit));
        }

        return new ToolResult(
            $"'{file}' not found in any source's history. Run action='diff' without 'file' to see the changed file list.",
            true);
    }

    private static bool TooLarge(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > SourceHistoryStore.MaxComparableFileSize;
        }
        catch
        {
            // 拿不到大小就当它超限：读不出大小的文件更没理由硬读
            return true;
        }
    }

    private async Task<ToolResult> RunSyncAsync(string[]? only, CancellationToken cancellationToken)
    {
        var report = await _syncService.SyncAsync(only, cancellationToken);

        // 必须在打印任何统计之前拦掉：拒绝执行时 Changes 是空的，落到下面的循环里
        // 会输出一句「Sync finished in 0 ms」外加零条变更——看起来像「跑完了，没事可做」，
        // 而真相是压根没跑。
        if (report.AlreadyRunning)
        {
            return new ToolResult(
                "A sync is already running in this server process. Nothing was done — retry once it finishes.",
                true);
        }

        var builder = new StringBuilder();

        builder.AppendLine($"Sync finished in {report.ElapsedMs} ms (game version {_syncService.GameVersion ?? "unknown"}):");
        foreach (var change in report.Changes) builder.AppendLine($"  {change.Describe()}");

        if (report.Outcomes.Count > 0)
        {
            var succeeded = report.Outcomes.Count(o => o.Success);
            var files = report.Outcomes.Sum(o => o.FileCount);
            builder.AppendLine($"\nDecompiled {succeeded}/{report.Outcomes.Count} assemblies, {files} source files.");

            foreach (var changeSet in report.FileChanges.Where(c => c.Any))
            {
                builder.AppendLine(
                    $"  {changeSet.SourceName}: {changeSet.Added} added, {changeSet.Modified} modified, "
                    + $"{changeSet.Removed} removed — use action='diff' for the file list.");
            }

            foreach (var failure in report.Outcomes.Where(o => !o.Success).Take(10))
                builder.AppendLine($"  FAILED {Path.GetFileName(failure.AssemblyPath)}: {failure.Error}");
        }

        // XML 变了不需要反编译，但索引仍是旧的，同样得重扫一遍
        var xmlChanged = SourceChangeProbe.Pending?.ChangedXmlSources.Count > 0;
        if (xmlChanged && !report.AnyPromoted)
        {
            builder.AppendLine($"\nXML defs changed in: {string.Join(", ", SourceChangeProbe.Pending!.ChangedXmlSources)}"
                             + " — no decompile needed, reindexing only.");
        }

        // 反编译改的是磁盘，内存里的索引还是旧的，就地重扫一遍；重建期间其它查询会挂起等待。
        // 判据是「有源真的转正了」而不是「有程序集反编译成功」：事务化之后，一个源可以
        // 逐个程序集都成功、却在提交阶段失败并整体回滚，此时磁盘没变，重建只是白白
        // 清空重扫约 4 秒，期间所有查询挂起。
        if (report.AnyPromoted || xmlChanged)
        {
            if (_rebuilder == null)
            {
                builder.AppendLine(
                    "\nThe in-memory index still reflects the previous sources. Restart the MCP server to rebuild it.");
            }
            else
            {
                var rebuild = _rebuilder.Rebuild(TimeSpan.FromMinutes(2));
                builder.AppendLine(rebuild.Succeeded
                    ? $"\nIndex rebuilt in {rebuild.ElapsedMs} ms "
                      + $"({rebuild.CsharpPaths} C# path(s), {rebuild.XmlPaths} XML path(s)). No restart needed."
                    : "\nIndex rebuild skipped: another rebuild was already running. Retry, or restart the server.");

                SourceChangeProbe.RecordSync(report.FileChanges);
            }
        }

        // 有源整体回滚时必须标成错误：正文里那行「同步失败，已整体回滚」很容易被
        // 淹在成功源的统计里，而调用方据此决定要不要重试。
        return new ToolResult(builder.ToString().TrimEnd(), report.Failures.Count > 0);
    }
}
