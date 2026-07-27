using System.Text.Json;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 走完整的 stdio 协议路径：一行进、一行出。registerGlobalLogger 必须关掉，
// 否则会话会抢走静态日志钩子，把日志灌进别的测试的 writer。
//
// 与 IndexGateTests 同一 collection：工具调用要过 IndexGate 读锁，而那边的用例会真的
// 攥住写锁，并行跑的话这里会撞上重建窗口。
[Collection("IndexGate")]
public class ProtocolTests
{
    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "test tool";
        public object JsonSchema => new { type = "object" };

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null)
            => Task.FromResult(new ToolResult("ok"));
    }

    private static async Task<List<JsonElement>> ExchangeAsync(params string[] requests)
    {
        var output = new StringWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);
        server.RegisterTool(new EchoTool());

        await server.RunAsync(new StringReader(string.Join("\n", requests)));

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line.Trim()).RootElement.Clone())
            .ToList();
    }

    private static int ErrorCodeOf(JsonElement response)
        => response.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task ToolsCall_WithoutParams_ReturnsInvalidParams()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""");

        // -32603 的意思是「服务器内部坏了」，会让 client 去重试甚至整体弃用本服务器；
        // 缺参数是调用方能自行改正的，必须是 -32602。
        Assert.Equal(-32602, ErrorCodeOf(Assert.Single(responses)));
    }

    [Fact]
    public async Task ToolsCall_WithoutName_ReturnsInvalidParams()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");

        Assert.Equal(-32602, ErrorCodeOf(Assert.Single(responses)));
    }

    [Fact]
    public async Task ToolsCall_WithNonStringName_ReturnsInvalidParams()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":42}}""");

        Assert.Equal(-32602, ErrorCodeOf(Assert.Single(responses)));
    }

    [Fact]
    public async Task ToolsCall_WithKnownTool_ReturnsContent()
    {
        var responses = await ExchangeAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");

        var result = Assert.Single(responses).GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("ok", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_WithUnknownTool_ReportsTheKnownOnes()
    {
        var responses = await ExchangeAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}""");

        var error = Assert.Single(responses).GetProperty("error");
        Assert.Contains("echo", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MalformedJson_ReturnsParseError()
    {
        var responses = await ExchangeAsync("{ not json");

        Assert.Equal(-32700, ErrorCodeOf(Assert.Single(responses)));
    }

    [Fact]
    public async Task MissingMethod_ReturnsInvalidRequest()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":1}""");

        Assert.Equal(-32600, ErrorCodeOf(Assert.Single(responses)));
    }

    // 通知（无 id）不该收到响应。服务器仍会主动发 notifications/message 之类的日志行，
    // 那些是无 id 的通知，不算响应。
    [Fact]
    public async Task Notification_GetsNoResponse()
    {
        var lines = await ExchangeAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.All(lines, line =>
        {
            Assert.False(line.TryGetProperty("result", out _));
            Assert.False(line.TryGetProperty("error", out _));
            Assert.True(line.TryGetProperty("method", out _));
        });
    }

    [Fact]
    public async Task Ping_IsAnswered()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        Assert.Equal(3, Assert.Single(responses).GetProperty("id").GetInt32());
    }
}
