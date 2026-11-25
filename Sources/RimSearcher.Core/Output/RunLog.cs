using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimSearcher.Output;

public static class RunLog
{
    public const string SchemaId = "rimsearcher.run-log.v1";
    public const string EnvVar = "RIMSEARCHER_RUN_LOG";

    public const string CallerEnvVar = "RIMSEARCHER_CALLER";

    public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar));

    public static string? ArchiveDir { get; set; }

    private static string ResolveArchiveDir()
        => ArchiveDir ?? Path.Combine(
            Path.GetDirectoryName(Environment.GetEnvironmentVariable("RIMSEARCHER_CONFIG")
                                  ?? Config.RimConfig.DefaultPath)!, "runlog");

    public const int AtomicAppendLimitBytes = 64 * 1024;

    public static void TryWrite(IReadOnlyList<string> argv, int exit, Report? report,
                                string? snapshot, bool quiet, string? usageMessage, TextWriter stderr,
                                long? elapsedMs = null)
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(path)) return;

        var line = Format(argv, exit, report, snapshot, quiet, usageMessage, elapsedMs);
        try
        {
            AppendLine(path, line);
        }
        catch (Exception ex)
        {
            stderr.Write(OutputText.Finish(
                $"无法写入运行日志 '{path}'：{ex.Message}"));
        }

        var dir = ResolveArchiveDir();
        var archive = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
        try
        {
            Directory.CreateDirectory(dir);
            AppendLine(archive, line);
        }
        catch (Exception ex)
        {
            stderr.Write(OutputText.Finish(
                $"无法写入运行日志归档 '{archive}'：{ex.Message}"));
        }
    }

    public static string Format(IReadOnlyList<string> argv, int exit, Report? report, string? snapshot,
                                bool quiet = false, string? usageMessage = null, long? elapsedMs = null)
    {
        var notices = new List<Dictionary<string, object?>>();
        if (report is not null)
        {
            var entries = report.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not Notice n) continue;
                var row = new Dictionary<string, object?>
                {
                    ["seq"] = i,
                    ["kind"] = NoticeKindText.Of(n.Kind),
                    ["text"] = n.Text,
                    ["footnote"] = n.Footnote,
                    ["teach"] = n.Teach,
                    ["count"] = n.Count is { } c
                        ? new Dictionary<string, object?> { ["shown"] = c.Shown, ["total"] = c.Total }
                        : null,
                    ["data"] = n.Data is { } d ? new Dictionary<string, object?>(d) : null,
                    ["prevBlock"] = NearestBlock(entries, i, -1) is { } prev ? BlockRef(prev) : null,
                    ["nextBlock"] = NearestBlock(entries, i, 1) is { } next ? BlockRef(next) : null,
                };
                notices.Add(row);
            }
        }

        var record = new Dictionary<string, object?>
        {
            ["schema"] = SchemaId,
            ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
            ["pid"] = Environment.ProcessId,
            ["argv"] = argv.ToList(),
            ["exit"] = exit,
            ["elapsedMs"] = elapsedMs,
            ["snapshot"] = snapshot,
            ["caller"] = Environment.GetEnvironmentVariable(CallerEnvVar) is { Length: > 0 } caller ? caller : null,
            ["quiet"] = quiet,
            ["teachViaHook"] = report?.TeachViaHook ?? false,
            ["usageMessage"] = string.IsNullOrWhiteSpace(usageMessage) ? null : usageMessage.Trim(),
            ["notices"] = notices,
        };
        return JsonTree.Write(record, indented: false);
    }

    public static void AppendLine(string path, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(payload, 0, payload.Length);
    }

    private static Block? NearestBlock(IReadOnlyList<ReportEntry> entries, int from, int step)
    {
        for (var i = from + step; i >= 0 && i < entries.Count; i += step)
            if (entries[i] is Block b) return b;
        return null;
    }

    private static Dictionary<string, object?> BlockRef(Block block)
        => new()
        {
            ["name"] = block switch
            {
                TableBlock t => t.Name,
                DetailBlock d => d.Name,
                CompletenessBlock c => c.Name,
                TextBlock x => x.Name,
                _ => "",
            },
            ["collection"] = block.Collection,
            ["item"] = block.Item,
        };
}
