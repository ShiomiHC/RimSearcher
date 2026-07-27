using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace RimSearcher.Core;

// 把逐行比较结果压成 unified diff 的 hunk 形式。整文件逐行输出对反编译产物毫无用处——
// 单个类动几行、文件却有上千行时，未变部分会把真正的改动淹掉。
public static class UnifiedDiffFormatter
{
    public static string Format(
        string oldText,
        string newText,
        string label,
        int contextLines = 3,
        int maxLines = 400)
    {
        var diff = InlineDiffBuilder.Diff(oldText, newText);
        var lines = diff.Lines;

        var changed = new bool[lines.Count];
        var anyChange = false;
        for (var i = 0; i < lines.Count; i++)
        {
            changed[i] = lines[i].Type is ChangeType.Inserted or ChangeType.Deleted or ChangeType.Modified;
            anyChange |= changed[i];
        }

        if (!anyChange) return $"--- {label}\n(no textual differences)";

        // 变更行向两侧扩 contextLines，重叠区间自然合并成一个 hunk
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!changed[i]) continue;
            var from = Math.Max(0, i - contextLines);
            var to = Math.Min(lines.Count - 1, i + contextLines);
            for (var j = from; j <= to; j++) keep[j] = true;
        }

        var builder = new StringBuilder();
        builder.Append("--- ").Append(label).Append('\n');

        var emitted = 0;
        var inGap = false;
        var truncated = false;
        int oldLine = 0, newLine = 0;

        // DiffPlex 按行位置交错吐出增删，读起来不像 git。同一变更块内攒起来，
        // 先出全部删除行再出全部新增行，改动前后的对照才立得住。
        var pendingRemoved = new List<string>();
        var pendingAdded = new List<string>();

        void FlushPending()
        {
            foreach (var text in pendingRemoved)
            {
                if (emitted >= maxLines) { truncated = true; break; }
                builder.Append('-').Append(text).Append('\n');
                emitted++;
            }

            foreach (var text in pendingAdded)
            {
                if (emitted >= maxLines) { truncated = true; break; }
                builder.Append('+').Append(text).Append('\n');
                emitted++;
            }

            pendingRemoved.Clear();
            pendingAdded.Clear();
        }

        for (var i = 0; i < lines.Count && !truncated; i++)
        {
            var line = lines[i];

            // 行号要按两侧各自推进，删除行不占新文件行号，新增行不占旧文件行号
            if (line.Type != ChangeType.Inserted) oldLine++;
            if (line.Type != ChangeType.Deleted) newLine++;

            if (!keep[i])
            {
                FlushPending();
                inGap = true;
                continue;
            }

            if (inGap)
            {
                FlushPending();
                builder.Append("@@ line ").Append(newLine).Append(" @@\n");
                inGap = false;
            }

            switch (line.Type)
            {
                case ChangeType.Deleted:
                    pendingRemoved.Add(line.Text);
                    break;
                case ChangeType.Inserted:
                    pendingAdded.Add(line.Text);
                    break;
                case ChangeType.Modified:
                    pendingRemoved.Add(line.Text);
                    break;
                default:
                    FlushPending();
                    if (emitted >= maxLines) { truncated = true; break; }
                    builder.Append(' ').Append(line.Text).Append('\n');
                    emitted++;
                    break;
            }
        }

        FlushPending();
        if (truncated) builder.Append("... (truncated, raise limit to see the rest)\n");

        return builder.ToString().TrimEnd();
    }
}
