using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：合并 XML 超长被截断后，提示写的是「use read_code on file path above」。
// 但被截断的是**沿 ParentName 链合并后**的 XML，它不对应磁盘上任何一个文件——上面那行
// `File:` 指的是子 def 自己那份未合并的源文件，里面恰恰没有继承来的字段。照着提示走，
// 拿回来的是另一份文档，且缺的正是 inspect def 模式唯一的存在理由。
// 续读只能由 inspect 自己提供，于是有了 xmlStartLine。
public class InspectXmlPagingTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // 父 def 摆一大堆字段，子 def 只有 defName + ParentName：
    // 合并结果远长于源文件，两者不可互相替代这一点因此可断言。
    private InspectTool BuildTool(int parentFieldCount)
    {
        var root = _workspace.Dir("Defs");

        var fields = string.Join("\n", Enumerable.Range(0, parentFieldCount)
            .Select(i => $"    <zzField{i}>{i}</zzField{i}>"));

        _workspace.WriteFile(Path.Combine("Defs", "Base.xml"),
            $"<Defs>\n  <ThingDef Name=\"ZzBase\" Abstract=\"True\">\n{fields}\n  </ThingDef>\n</Defs>\n");

        _workspace.WriteFile(Path.Combine("Defs", "Child.xml"),
            "<Defs>\n  <ThingDef ParentName=\"ZzBase\">\n    <defName>ZzChild</defName>\n  </ThingDef>\n</Defs>\n");

        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        defIndexer.FreezeIndex();

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.FreezeIndex();

        return new InspectTool(sourceIndexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    [Fact]
    public async Task ShortMergedXml_IsShownWholeWithNoContinuationHint()
    {
        var content = await Run(BuildTool(5), """{"name":"ZzChild"}""");

        Assert.Contains("zzField0", content);
        Assert.Contains("zzField4", content);
        Assert.DoesNotContain("xmlStartLine", content);
    }

    // 截断提示必须指回 inspect 自己，且说清 File: 那一行不是这份 XML 的来源
    [Fact]
    public async Task TruncatedMergedXml_PointsBackAtInspect_NotAtTheFile()
    {
        var content = await Run(BuildTool(400), """{"name":"ZzChild"}""");

        Assert.Contains("Truncated", content);
        Assert.Contains("call inspect again with xmlStartLine:", content);
        Assert.Contains("un-merged", content);
        Assert.DoesNotContain("use read_code on file path above", content);
    }

    // 续读拿到的必须是被截掉的那一段，而不是又一次头尾
    [Fact]
    public async Task ContinuingWithXmlStartLine_ReturnsTheSkippedMiddle()
    {
        var tool = BuildTool(400);

        var first = await Run(tool, """{"name":"ZzChild"}""");
        Assert.DoesNotContain("<zzField300>", first);

        var second = await Run(tool, """{"name":"ZzChild","xmlStartLine":201}""");

        Assert.Contains("<zzField300>", second);
        Assert.Contains("lines 201-", second);
    }

    // 走到尾就说走到尾，不要再给一个指向空白的续读值
    [Fact]
    public async Task ReachingTheEnd_SaysSoInsteadOfOfferingAnotherPage()
    {
        var content = await Run(BuildTool(400), """{"name":"ZzChild","xmlStartLine":300}""");

        Assert.Contains("End of the merged XML", content);
        Assert.DoesNotContain("call inspect again with xmlStartLine:", content);
    }

    // 越界起点不该炸，也不该回一段空白
    [Fact]
    public async Task StartLineBeyondTheEnd_ClampsToTheLastLine()
    {
        var content = await Run(BuildTool(20), """{"name":"ZzChild","xmlStartLine":99999}""");

        Assert.Contains("End of the merged XML", content);
    }

    // 源文件按真实 vanilla Defs 的样子写：行距宽、缩进深、嵌套 li。此前保留了纯空白文本
    // 节点，于是 XElement.ToString() 不再重排缩进，同一份合并结果里两种坏形态并存——
    // 搬自文件的分支带着原文空行，合并时新插入的节点被整排挤进一行。
    private InspectTool BuildNestedTool()
    {
        var root = _workspace.Dir("Defs");

        _workspace.WriteFile(Path.Combine("Defs", "Base.xml"),
            """
            <Defs>

              <ThingDef Name="ZzBase" Abstract="True">

                <statBases>

                    <Mass>60</Mass>

                    <Flammability>0.7</Flammability>

                  </statBases>

                <race>

                    <litterSizeCurve>

                      <points>

                        <li>(0.5, 0)</li>

                        <li>(1, 1)</li>

                      </points>

                    </litterSizeCurve>

                  </race>

              </ThingDef>

            </Defs>
            """);

        // 子 def 在父的 statBases 上追加兄弟节点：合并时新插入的那些正是被挤成一行的那批
        _workspace.WriteFile(Path.Combine("Defs", "Child.xml"),
            """
            <Defs>
              <ThingDef ParentName="ZzBase">
                <defName>ZzChild</defName>
                <statBases>
                  <MarketValue>1750</MarketValue>
                  <MoveSpeed>4.6</MoveSpeed>
                  <LeatherAmount>75</LeatherAmount>
                </statBases>
              </ThingDef>
            </Defs>
            """);

        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        defIndexer.FreezeIndex();

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.FreezeIndex();

        return new InspectTool(sourceIndexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static string[] MergedXmlLines(string content)
    {
        // 不在这里 TrimEnd('\r')：尾随 CR 正是下面一条用例要断言不存在的东西
        var lines = content.Split('\n');
        var open = Array.FindIndex(lines, l => l.StartsWith("```xml", StringComparison.Ordinal));
        Assert.True(open >= 0, "返回里没有合并 XML 块");
        var close = Array.FindIndex(lines, open + 1, l => l.StartsWith("```", StringComparison.Ordinal));
        Assert.True(close > open, "合并 XML 块没有闭合");
        return lines[(open + 1)..close];
    }

    // 空行本身只值一个换行符，真正的代价是 inspect 的截断与 xmlStartLine 续读**以行计**：
    // 源文件的行距一变，「首屏 200 行」给出的内容就跟着变，而调用方看不出这件事。
    [Fact]
    public async Task MergedXml_CarriesNoBlankLinesFromTheSourceFiles()
    {
        var xml = MergedXmlLines(await Run(BuildNestedTool(), """{"name":"ZzChild"}"""));

        Assert.NotEmpty(xml);
        Assert.DoesNotContain(xml, line => line.Trim().Length == 0);
    }

    // 同一件事的另一头：合并时新插入的兄弟节点此前没有空白节点隔开，被整排挤进一行
    // （实测 vanilla 的 ThingDef Human 有一行 968 字符）。一行一个元素，行才是个稳定的量。
    [Fact]
    public async Task MergedXml_PutsOneElementPerLine_NoMatterWhichSideItCameFrom()
    {
        var xml = MergedXmlLines(await Run(BuildNestedTool(), """{"name":"ZzChild"}"""));

        // 父来的 Mass 与子来的 MarketValue 都要各占一行，且都在 statBases 里
        Assert.Contains(xml, line => line.Trim() == "<Mass>60</Mass>");
        Assert.Contains(xml, line => line.Trim() == "<MarketValue>1750</MarketValue>");

        foreach (var line in xml)
        {
            // 一行里出现第二个闭合标签就说明兄弟节点被挤在一起了
            var closings = System.Text.RegularExpressions.Regex.Matches(line, "</").Count;
            Assert.True(closings <= 1, $"一行挤了多个元素: {line}");
        }
    }

    // 上面两条走的是「短 XML 整块印」那条路径。截断与续读是**逐行 AppendLine** 重新拼的，
    // 而 XElement.ToString() 的行尾是 CRLF：裸按 '\n' 切会给每行留一个尾随 '\r'，
    // AppendLine 再补一个换行，ToolResult 收口时那个孤立的 '\r' 又被换成 '\n'——每行后面
    // 多一个空行，行数翻倍。偏偏截断窗口与 xmlStartLine 都是按行数算的。
    [Theory]
    [InlineData("""{"name":"ZzChild"}""")]              // 截断：头 200 + 尾 50 两段
    [InlineData("""{"name":"ZzChild","xmlStartLine":201}""")]  // 续读：连续窗口一段
    public async Task PagedMergedXml_DoesNotDoubleUpItsLineCount(string request)
    {
        var xml = MergedXmlLines(await Run(BuildTool(400), request));

        Assert.NotEmpty(xml);
        // 截断分隔行前后各有一个有意为之的空行，其余一个都不该有
        Assert.True(xml.Count(l => l.Trim().Length == 0) <= 2,
            "合并 XML 的行数翻倍了：" + string.Join("|", xml.Take(6)));
        Assert.DoesNotContain(xml, l => l.EndsWith('\r'));
    }

    // 缩进由 XLinq 统一给，故层级深度可以从行首空白读出来——这正是原样保留源文件空白时
    // 拿不到的性质（父来的分支保留原缩进，子来的分支一个空格都没有）。
    [Fact]
    public async Task MergedXml_IndentsByDepth_SoNestingIsReadable()
    {
        var xml = MergedXmlLines(await Run(BuildNestedTool(), """{"name":"ZzChild"}"""));

        int IndentOf(string needle)
        {
            var line = Assert.Single(xml, l => l.Contains(needle, StringComparison.Ordinal));
            return line.Length - line.TrimStart().Length;
        }

        Assert.True(IndentOf("<race>") < IndentOf("<litterSizeCurve>"));
        Assert.True(IndentOf("<litterSizeCurve>") < IndentOf("<points>"));
        Assert.True(IndentOf("<points>") < IndentOf("(0.5, 0)"));
    }
}
