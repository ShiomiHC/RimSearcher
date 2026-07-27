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

    // 多次上报进度的工具。步数取得多一点，是为了让「通知晚于最终响应」这类竞态在
    // 修复缺失时稳定暴露出来——只报一次的话，赢下竞态纯属运气。
    private sealed class ProgressTool(int steps) : ITool
    {
        public string Name => "progress";
        public string Description => "test tool";
        public object JsonSchema => new { type = "object" };

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            for (var i = 1; i <= steps; i++) progress?.Report((double)i / steps);
            return Task.FromResult(new ToolResult("done"));
        }
    }

    // 写出的每一行单独留存。StringWriter 在另一条线程还在写的时候 ToString() 是不安全的，
    // 而下面几个用例必须在服务器仍在跑的过程中检查已发出的响应。
    private sealed class RecordingWriter : TextWriter
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            if (value != null) _lines.Enqueue(value);
            return Task.CompletedTask;
        }

        public string[] Raw => [.. _lines];

        public List<JsonElement> Parsed => [.. _lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    // 按脚本逐行喂入：有些用例必须等到「前一个请求确实已经登记并跑起来」才能发下一行，
    // 否则取消通知会打在一个还没登记的请求上，测的就不是要测的东西了。
    private sealed class ScriptedReader(params Func<Task<string?>>[] steps) : TextReader
    {
        private int _index;

        public override async Task<string?> ReadLineAsync()
            => _index < steps.Length ? await steps[_index++]() : null;
    }

    // 到达后挂起，直到被显式放行或被取消。用来把两个请求稳定地摆在「同时在途」的状态上。
    private sealed class GateTool : ITool
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        private int _cancellations;

        public string Name => "gate";
        public string Description => "test tool";
        public object JsonSchema => new { type = "object" };

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            var tag = arguments.GetProperty("tag").GetString();
            Interlocked.Increment(ref _arrivals);

            // 取消从捕获异常来记，不用 CancellationToken.Register：回调是后注册先执行的，
            // WaitAsync 自己那个回调会先抛出来，续体跑到 using 出作用域时把我们的回调注销掉，
            // 计数就永远加不上——那样等到的不是「没取消」，而是测试自己漏读了。
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancellations);
                throw;
            }

            return new ToolResult($"finished:{tag}");
        }

        public void Release() => _release.TrySetResult();

        public Task WaitForArrivalsAsync(int count) => WaitUntilAsync(() => Volatile.Read(ref _arrivals) >= count);

        public Task WaitForCancellationAsync() => WaitUntilAsync(() => Volatile.Read(ref _cancellations) >= 1);

        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("等待的条件始终没有成立");
                await Task.Delay(5);
            }
        }
    }

    private static async Task<List<JsonElement>> ExchangeAsync(params string[] requests)
        => await ExchangeWithAsync(null, requests);

    private static async Task<List<JsonElement>> ExchangeWithAsync(ITool? extraTool, params string[] requests)
    {
        var output = new StringWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);
        server.RegisterTool(new EchoTool());
        if (extraTool != null) server.RegisterTool(extraTool);

        await server.RunAsync(new StringReader(string.Join("\n", requests)));

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line.Trim()).RootElement.Clone())
            .ToList();
    }

    private static int ErrorCodeOf(JsonElement response)
        => response.GetProperty("error").GetProperty("code").GetInt32();

    private static bool IsMethod(JsonElement line, string method)
        => line.TryGetProperty("method", out var m) && m.GetString() == method;

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

    // 规范里 progressToken 是 string 或 integer，服务端必须原样回显。
    // 曾经数值 token 被 ToString() 成 "42" 发回去，严格的 client 关联不上自己的请求。
    [Fact]
    public async Task NumericProgressToken_IsEchoedAsANumber()
    {
        var lines = await ExchangeWithAsync(new ProgressTool(1),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"progress","arguments":{},"_meta":{"progressToken":42}}}""");

        var token = Assert.Single(lines, l => IsMethod(l, "notifications/progress"))
            .GetProperty("params").GetProperty("progressToken");

        Assert.Equal(JsonValueKind.Number, token.ValueKind);
        Assert.Equal(42, token.GetInt64());
    }

    [Fact]
    public async Task StringProgressToken_IsEchoedAsAString()
    {
        var lines = await ExchangeWithAsync(new ProgressTool(1),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"progress","arguments":{},"_meta":{"progressToken":"abc"}}}""");

        var token = Assert.Single(lines, l => IsMethod(l, "notifications/progress"))
            .GetProperty("params").GetProperty("progressToken");

        Assert.Equal(JsonValueKind.String, token.ValueKind);
        Assert.Equal("abc", token.GetString());
    }

    // 这条断言的是写出顺序本身：进度通知一旦落在最终响应之后，client 就收到了一个
    // 已经结束的请求的进度更新。顺带验证通知之间也保持 Report 的调用顺序。
    [Fact]
    public async Task ProgressNotifications_AreAllWrittenBeforeTheFinalResponse()
    {
        const int steps = 20;

        var lines = await ExchangeWithAsync(new ProgressTool(steps),
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"progress","arguments":{},"_meta":{"progressToken":1}}}""");

        var responseIndex = lines.FindIndex(l => l.TryGetProperty("result", out _));
        Assert.True(responseIndex >= 0, "没有找到最终响应");

        var progress = lines
            .Select((line, index) => (line, index))
            .Where(x => IsMethod(x.line, "notifications/progress"))
            .ToList();

        Assert.Equal(steps, progress.Count);
        Assert.All(progress, x => Assert.True(
            x.index < responseIndex,
            $"第 {x.index} 行的进度通知出现在最终响应（第 {responseIndex} 行）之后"));

        var values = progress.Select(x => x.line.GetProperty("params").GetProperty("progress").GetDouble()).ToList();
        Assert.Equal(values.Order().ToList(), values);
    }

    [Fact]
    public async Task ResourcesList_ReturnsAnEmptyResourcesArray()
    {
        var result = Assert.Single(await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}"""))
            .GetProperty("result");

        Assert.Equal(0, result.GetProperty("resources").GetArrayLength());
        Assert.False(result.TryGetProperty("resourceTemplates", out _));
    }

    // 模板列表的规范字段是 resourceTemplates；回成 resources 会让按规范解析的 client
    // 判定协议错误，而不是「这台服务器没有模板」。
    [Fact]
    public async Task ResourceTemplatesList_ReturnsAnEmptyResourceTemplatesArray()
    {
        var result = Assert.Single(await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"resources/templates/list"}"""))
            .GetProperty("result");

        Assert.Equal(0, result.GetProperty("resourceTemplates").GetArrayLength());
        Assert.False(result.TryGetProperty("resources", out _));
    }

    // 声明了 logging 能力就等于承诺按 setLevel 过滤；只回成功不做事的话，
    // client 设了 error 照样收到全部 info 噪音。
    [Fact]
    public async Task LoggingSetLevel_SuppressesLessSevereMessages()
    {
        var output = new StringWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);

        await server.RunAsync(new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"error"}}"""));

        await server.LogAsync("quiet one", "info");
        await server.LogAsync("loud one", "error");

        var logs = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line.Trim()).RootElement.Clone())
            .Where(line => IsMethod(line, "notifications/logging/message"))
            .ToList();

        Assert.Equal("loud one", Assert.Single(logs).GetProperty("params").GetProperty("data").GetString());
    }

    [Fact]
    public async Task WithoutSetLevel_InfoMessagesAreStillEmitted()
    {
        var output = new StringWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);

        await server.LogAsync("still here", "info");

        Assert.Contains("still here", output.ToString());
    }

    // 静默接受一个认不出的级别，会让 client 以为过滤已生效，实际一条也没过滤掉
    [Fact]
    public async Task LoggingSetLevel_WithUnknownLevel_ReturnsInvalidParams()
    {
        var responses = await ExchangeAsync(
            """{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"verbose"}}""");

        Assert.Equal(-32602, ErrorCodeOf(Assert.Single(responses)));
    }

    // 数值 id 1 和字符串 id "1" 是两个不同的请求。曾经的实现把 id 直接 ToString() 当字典键，
    // 两者撞成同一个键，于是取消通知打到了另一个请求头上。
    [Fact]
    public async Task Cancel_TargetsTheRequestWhoseIdTypeMatches()
    {
        var tool = new GateTool();
        var output = new RecordingWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);
        server.RegisterTool(tool);

        var reader = new ScriptedReader(
            () => Task.FromResult<string?>(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gate","arguments":{"tag":"number"}}}"""),
            async () =>
            {
                await tool.WaitForArrivalsAsync(1);
                return """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"gate","arguments":{"tag":"string"}}}""";
            },
            async () =>
            {
                await tool.WaitForArrivalsAsync(2);
                return """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}""";
            },
            async () =>
            {
                // 取消确实作用到某一个请求之后才放行另一个，否则两个都会正常跑完，测不出串线
                await tool.WaitForCancellationAsync();
                tool.Release();
                return null;
            });

        await server.RunAsync(reader);

        var responses = output.Parsed.Where(line => line.TryGetProperty("id", out _)).ToList();
        var numeric = Assert.Single(responses, r => r.GetProperty("id").ValueKind == JsonValueKind.Number);
        var textual = Assert.Single(responses, r => r.GetProperty("id").ValueKind == JsonValueKind.String);

        Assert.Equal(-32800, ErrorCodeOf(numeric));
        Assert.StartsWith("finished:string",
            textual.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    // 同一个 id 在活动期间重复出现：覆盖登记会让先来的请求丢掉取消通道，
    // 先跑完的那个还会把另一个的登记一并删掉。必须明确报错。
    [Fact]
    public async Task DuplicateInFlightId_IsRejectedInsteadOfOverwriting()
    {
        var tool = new GateTool();
        var output = new RecordingWriter();
        var server = new RimSearcher.Server.RimSearcher(output, registerGlobalLogger: false);
        server.RegisterTool(tool);

        var reader = new ScriptedReader(
            () => Task.FromResult<string?>(
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"gate","arguments":{"tag":"first"}}}"""),
            async () =>
            {
                await tool.WaitForArrivalsAsync(1);
                return """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"gate","arguments":{"tag":"second"}}}""";
            },
            async () =>
            {
                // 第一个请求必须一直挂着，否则它的登记先被摘掉，第二个就成了合法的新请求
                await GateTool.WaitUntilAsync(() => output.Raw.Any(line => line.Contains("-32600")));
                tool.Release();
                return null;
            });

        await server.RunAsync(reader);

        var responses = output.Parsed;
        Assert.Contains(responses, r => r.TryGetProperty("error", out _) && ErrorCodeOf(r) == -32600);
        Assert.Contains(responses, r => r.TryGetProperty("result", out _));
        Assert.DoesNotContain(output.Raw, line => line.Contains("finished:second"));
    }
}
