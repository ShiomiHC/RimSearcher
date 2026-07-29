using System.Runtime.CompilerServices;

namespace RimSearcher.Tests;

// 字节级基线的**机制**：存哪、怎么比、缺基线时怎么办、diff 怎么印。判据本身不在这里——
// 每道基线闸各自决定「被比的是哪一份文本」与「哪些字随环境变、要归一化掉」。
//
// 提出来共用的理由与本轮重构本身同型：呈现层那 73 份（OutputSnapshotTests）与参数层这份
// tools/list 基线要的是同一套机制，各写一遍的话，「缺基线判红」「diff 只印前 12 处」
// 这类决定就有了两个产地，而它们坏起来是静默的——一道在基线被删掉之后照样绿的闸，
// 绿的时候没人知道那是「输出没变」还是「没有东西可比」。
internal static class SnapshotGate
{
    // 首次落地与故意改文案时的流程：
    //   RIMSEARCHER_SNAPSHOTS=update dotnet test --filter <闸的类名>
    // 生成/更新基线，人工核对 diff，再重跑一次拿全绿。
    public static bool UpdateMode =>
        string.Equals(
            Environment.GetEnvironmentVariable("RIMSEARCHER_SNAPSHOTS"), "update", StringComparison.OrdinalIgnoreCase);

    // 基线跟着**测试源文件**放，不进构建输出：它是要被人读、被 git diff 审的东西，
    // 拷进 bin/ 只会让「改了输出」这件事在 review 里看不见。
    private static string SnapshotPath(string name) => Path.Combine(Root(), $"{name}.txt");

    private static string Root([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Snapshots");

    // ---- 已入库的基线本身也是语料 ----
    //
    // 每一份基线都是一段被人审过的真实产品输出，且**形态是指定出来的**——这正是台账 §七当年
    // 判成「做不了」的那份东西（「语料来自本机 RimWorld 安装，不能进仓」）。它早就在仓里了，
    // 只是一直没有第二道闸读它：字节级基线只回答「这次改动动了哪些字」，不回答「这些字合不合
    // 共用文法」。消费者见 SnapshotGrammarGateTests。
    //
    // 名字与 Verify 收的那个 name 同形（相对 Snapshots/、不带扩展名、分隔符归一成 /），
    // 于是「枚举出来的名字」与「写基线时用的名字」是同一个东西，中间没有第二套拼法。

    public static IReadOnlyList<string> Names()
        => [.. Directory.EnumerateFiles(Root(), "*.txt", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root(), path).Replace('\\', '/')[..^".txt".Length])
            .OrderBy(name => name, StringComparer.Ordinal)];

    public static string Read(string name) => File.ReadAllText(SnapshotPath(name));

    // content 必须是**已经归一化过**的文本：随环境变的那几段（路径、耗时、时刻）由调用方
    // 各自处理——它们知道自己那份输出里哪些字是噪音，这里不知道。
    public static void Verify(string name, string content)
    {
        var path = SnapshotPath(name);

        if (UpdateMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return;
        }

        // 基线不存在时**判红**而不是静默生成。一道在基线被删掉之后照样绿的闸比没有闸更糟。
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            Assert.Fail(
                $"基线 Snapshots/{name}.txt 不存在，已按本次输出生成。人工核对后重跑；"
                + "本次判红是故意的——缺基线时判绿的闸没有判据。");
        }

        var expected = File.ReadAllText(path);
        if (expected == content) return;

        Assert.Fail($"Snapshots/{name}.txt 与本次输出不一致：\n{Diff(expected, content)}");
    }

    // 逐行 diff。整份贴出来的话，一个空行的差异要在几十行里靠肉眼找。
    private static string Diff(string expected, string actual)
    {
        var want = expected.Split('\n');
        var got = actual.Split('\n');
        var lines = new List<string>();

        for (var i = 0; i < Math.Max(want.Length, got.Length); i++)
        {
            var a = i < want.Length ? want[i] : "<无此行>";
            var b = i < got.Length ? got[i] : "<无此行>";
            if (a == b) continue;

            lines.Add($"  第 {i + 1} 行\n    基线: {Quote(a)}\n    本次: {Quote(b)}");
            if (lines.Count >= 12) { lines.Add("  …（差异过多，只列前 12 处）"); break; }
        }

        return lines.Count == 0
            // 逐行相同却整体不等 = 差在行尾空白或结尾换行上，那正是最该被这道闸抓住的一类
            ? $"  逐行相同但整体不等（行尾空白或收尾差异）：基线 {expected.Length} 字符、本次 {actual.Length} 字符"
            : string.Join("\n", lines);
    }

    private static string Quote(string s) => "\"" + s.Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
}
