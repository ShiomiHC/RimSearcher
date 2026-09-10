using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimSearcher.Output;

/// <summary>
/// 一次运行的旁路记录:全部 notice 的结构化副本,不进 stdout。
///
/// 只在环境变量 <see cref="EnvVar"/> 指向一个路径时才写。未设 = 零 IO。
/// 落盘失败往 stderr 写一行(路径 + 原因),不改退出码,也不吞。
/// </summary>
public static class RunLog
{
    public const string SchemaId = "rimsearcher.run-log.v1";
    public const string EnvVar = "RIMSEARCHER_RUN_LOG";

    /// <summary>
    /// 并发形态:文件以 append 打开,一次 <see cref="FileStream.Write(ReadOnlySpan{byte})"/>
    /// 写出整行 UTF-8(含末尾 LF),不 seek、不拆成多次 write。
    ///
    /// Windows / NTFS 上,FILE_APPEND_DATA 的单次 WriteFile 在载荷 ≤ 64 KiB
    /// (65536 字节)时,与其他同样形态的写入互不交错;超过这个长度的一行没有原子性保证,
    /// 可能与并发写入的字节搅在一起。POSIX 侧 O_APPEND 对单次 write 的偏移是原子的,
    /// 但超长 write 仍可能被内核拆开发出交错。
    /// </summary>
    public const int AtomicAppendLimitBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 环境变量未设则立刻返回。写出失败时往 <paramref name="stderr"/> 写一行,然后返回。
    /// </summary>
    public static void TryWrite(IReadOnlyList<string> argv, int exit, Report? report,
                                string? snapshot, string? usageMessage, TextWriter stderr)
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            AppendLine(path, Format(argv, exit, report, snapshot, usageMessage));
        }
        catch (Exception ex)
        {
            stderr.Write(OutputText.Finish(
                $"Could not write the run log at '{path}': {ex.Message}"));
        }
    }

    /// <summary>一行 JSON(无末尾换行)。测试与落盘共用这一份。</summary>
    public static string Format(IReadOnlyList<string> argv, int exit, Report? report, string? snapshot,
                                string? usageMessage = null)
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
                    ["kind"] = JsonRenderer.SnakeCase(n.Kind.ToString()),
                    ["text"] = n.Text,
                    ["footnote"] = n.Footnote,
                    ["count"] = n.Count is { } c
                        ? new Dictionary<string, object?> { ["shown"] = c.Shown, ["total"] = c.Total }
                        : null,
                    ["data"] = n.Data is { } d ? new Dictionary<string, object?>(d) : null,
                    ["prevBlock"] = NearestBlock(entries, i, -1) is { } prev ? BlockRef(prev) : null,
                    ["nextBlock"] = NearestBlock(entries, i, 1) is { } next ? BlockRef(next) : null,
                };
                // 文本渲染器会给声明行加快照标签 / 连跑记号,这里只记正文。
                // 消费方拿 text 与 stdout 做的是包含判定,前缀不参与。
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
            ["snapshot"] = snapshot,
            // 用法错那一句不经 Report,自己一个键。null = 这次不是用法错
            ["usageMessage"] = string.IsNullOrWhiteSpace(usageMessage) ? null : usageMessage.Trim(),
            ["notices"] = notices,
        };
        return JsonSerializer.Serialize(record, JsonOptions);
    }

    /// <summary>
    /// 打开为 append,一次写出 <paramref name="json"/> + LF。
    /// 见 <see cref="AtomicAppendLimitBytes"/> 的原子性限度。
    /// </summary>
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
