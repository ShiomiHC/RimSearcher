using System.Text.Json;
using System.Collections.Concurrent;
using RimSearcher.Server.Tools;

namespace RimSearcher.Server;

public sealed class RimSearcher
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TextWriter _protocolOut;
    
    private readonly SemaphoreSlim _concurrencyLimit = new(10, 10);

    // 每个会话独立：宿主下多个 client 共享同一批 tool，但「我问过什么」不能串味
    private readonly SessionUpdateNotice? _updateNotice = SourceChangeProbe.CreateSessionNotice();

    // 同上按会话计：启动期诊断只落 stderr，LLM 调用方看不到，得由工具返回捎带出去
    private readonly StartupHealth.SessionNotice _startupNotice = new();

    // logging/setLevel 的门槛同样是每会话的：宿主下 A 设成 error 不该让 B 也跟着噤声。
    // 0 = debug，即默认不过滤——client 没表过态时少发日志比多发更容易掩盖真问题。
    // 跨线程读写（日志来自工具执行的线程池线程，setLevel 来自另一个请求任务），故用 Volatile。
    private int _minLogSeverity;

    // RFC 5424 的严重度序，MCP 直接沿用这套名字。数越大越严重，只发 >= 门槛的。
    private static readonly Dictionary<string, int> LogSeverities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = 0,
        ["info"] = 1,
        ["notice"] = 2,
        ["warning"] = 3,
        ["error"] = 4,
        ["critical"] = 5,
        ["alert"] = 6,
        ["emergency"] = 7
    };

    // 跨工具的用法说明放在这里发一次，而不是在七份 description 里各塞半句。
    // 每个工具的 description 只回答「它做什么」，工具之间怎么接力是这一段的事。
    private const string ServerInstructions =
        """
        RimSearcher indexes RimWorld's decompiled C# and its XML Defs, plus whichever mods and DLCs are
        configured on this machine. Everything is read from local disk; nothing is fetched or modified
        except by sync_sources.

        Typical path from a vague name to an answer:
          1. locate  — fuzzy name -> exact names. Start here whenever the spelling is not already known.
          2. inspect — exact DefName (XML merged along ParentName) or exact C# type (inheritance + member outline).
          3. read_code — one member, one class body, or a raw line range out of a specific file.
          4. trace / search_regex — who references a symbol, or free-form pattern search.

        Two behaviours worth knowing before reading any result:
          - scope: every query tool takes it, and the server has a configured default that may be narrower
            than everything installed. When a result footer says N hits fell outside the scope, the symbol
            does exist — re-run with scope:'all' before concluding it does not.
          - trace mode:'usages' and search_regex are textual, not semantic: they match identifiers by text,
            so same-named members of unrelated types land in the same result set.

        After the game or a mod updates, sync_sources check -> sync -> diff re-decompiles and reports what
        changed; the index is rebuilt in place, no restart needed.
        """;

    // registerGlobalLogger：宿主为每个管道连接各开一个会话，只有直连 stdio 的那个会话
    // 才接管静态日志钩子，否则后建的会话会把日志抢到别人的连接上。
    public RimSearcher(TextWriter? protocolOut = null, bool registerGlobalLogger = true)
    {
        _protocolOut = protocolOut ?? Console.Out;
        if (registerGlobalLogger)
            ServerLogger.OnLogAsync = (msg, level) => this.LogAsync(msg, level);
    }

    public void RegisterTool(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public Task RunAsync() => RunAsync(Console.In);

    public async Task RunAsync(TextReader input)
    {
        var pending = new List<Task>();

        while (true)
        {
            var line = await input.ReadLineAsync();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ProcessGuard.NotifyActivity();

            var task = Task.Run(() => DispatchLineAsync(line));
            pending.Add(task);
            pending.RemoveAll(t => t.IsCompleted);
        }

        // 连接关闭后仍要把在途请求写完，否则代理端会看到响应缺失
        await Task.WhenAll(pending.Where(t => !t.IsCompleted));
    }

    // 每条消息一个独立任务：并发闸在任务内部等待，故读取循环恒不被慢请求阻塞
    // ——否则 10 个在跑的请求会让后续的取消通知也读不进来。
    private async Task DispatchLineAsync(string line)
    {
        // id 必须在 JsonDocument 生命周期内取成值类型：持有 JsonElement 会在 doc 释放后
        // 指向已归还池的缓冲，令异常路径的错误响应自身抛异常，请求就永久悬挂。
        object? requestId = null;
        string? requestKey = null;
        CancellationTokenSource? cts = null;
        var limitAcquired = false;

        try
        {
            string? method;
            JsonDocument doc;

            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                await SendResponseAsync(null, error: new { code = -32700, message = "Parse error" });
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                requestId = ExtractId(root);
                requestKey = RequestKeyOf(requestId);

                if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
                {
                    if (requestId != null)
                        await SendResponseAsync(requestId, error: new { code = -32600, message = "Invalid Request: missing 'method'" });
                    return;
                }

                method = methodProp.GetString();

                // MCP 规范的取消是 notifications/cancelled；$.cancelRequest 是 LSP 的，留作兼容
                if (method is "notifications/cancelled" or "cancelled" or "$/cancelRequest" or "$.cancelRequest")
                {
                    if (root.TryGetProperty("params", out var cancelParams)
                        && (cancelParams.TryGetProperty("requestId", out var cancelId) || cancelParams.TryGetProperty("id", out cancelId)))
                    {
                        var idToCancel = RequestKeyOf(ScalarIdValue(cancelId));
                        if (idToCancel != null && _activeRequests.TryRemove(idToCancel, out var targetCts))
                        {
                            // 目标请求可能刚好跑完并在 finally 里把 cts 释放掉了
                            try { targetCts.Cancel(); }
                            catch (ObjectDisposedException) { }
                        }
                    }
                    return;
                }

                // 注册必须早于排队。并发闸只放 10 个进去，第 11 个在这里等着；若此时收到
                // 针对它的取消通知，_activeRequests 里还没有它的条目，那条通知会被直接丢掉，
                // 客户端只能干等它排到队并整个跑完。
                if (requestKey != null)
                {
                    cts = new CancellationTokenSource();

                    // 必须是 TryAdd 而非直接赋值：同一个 id 在活动期间重复出现时，覆盖登记会让
                    // 先来的请求丢掉自己的取消通道（此后取消通知只能打到后来者），而且先跑完的
                    // 那个会在 finally 里把另一个的条目一并删掉。宁可明确报错也不要静默串线。
                    if (!_activeRequests.TryAdd(requestKey, cts))
                    {
                        cts.Dispose();
                        cts = null;
                        requestKey = null;  // 置空后 finally 不会误删占着这个键的那个请求
                        await SendResponseAsync(requestId, error: new
                        {
                            code = -32600,
                            message = "Invalid Request: another request with this id is still in flight"
                        });
                        return;
                    }
                }

                await _concurrencyLimit.WaitAsync(cts?.Token ?? CancellationToken.None);
                limitAcquired = true;

                await HandleRequestAsync(method, requestId, root, cts?.Token ?? CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            if (requestId != null)
                await SendResponseAsync(requestId, error: new { code = -32800, message = "Request cancelled" });
        }
        catch (Exception ex)
        {
            if (requestId != null)
                await SendResponseAsync(requestId, error: new { code = -32603, message = $"Internal error: {ex.Message}" });
        }
        finally
        {
            // 按键值对移除：取消通知已经把条目摘走时，同一个 id 可能已被下一个请求重新登记，
            // 只按键删会把那个无辜的新请求的取消通道扔掉。
            if (requestKey != null && cts != null)
                _activeRequests.TryRemove(new KeyValuePair<string, CancellationTokenSource>(requestKey, cts));
            cts?.Dispose();
            if (limitAcquired) _concurrencyLimit.Release();
        }
    }

    // JSON-RPC id 只能是 string / number / null
    private static object? ExtractId(JsonElement root)
        => root.TryGetProperty("id", out var idProp) ? ScalarIdValue(idProp) : null;

    // 整数分支要显式装箱成 long：不加 (object) 的话三元表达式的公共类型是 double，
    // 整数 id 会被静默转成浮点，超过 2^53 的 id 回显时就丢精度了。
    private static object? ScalarIdValue(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.TryGetInt64(out var l) ? l : (object)id.GetDouble(),
            _ => null
        };

    // 键必须带上 JSON 类型标记：JSON-RPC 里数值 id 1 和字符串 id "1" 是两个不同的请求，
    // 只用 ToString() 会把它们撞成同一个字典键，取消通知于是打到另一个请求头上。
    // 数值一律走 ScalarIdValue 归一化后再格式化，保证登记侧与取消侧算出同一个键。
    private static string? RequestKeyOf(object? id)
        => id switch
        {
            string s => "s:" + s,
            long l => "n:" + l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => "n:" + d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };

    private async Task HandleRequestAsync(string? method, object? id, JsonElement root, CancellationToken ct)
    {
        try
        {
            if (method == "initialize")
            {
                await SendResponseAsync(id, new
                {
                    protocolVersion = NegotiateProtocolVersion(root),
                    capabilities = new
                    {
                        tools = new { },
                        logging = new { }
                    },
                    serverInfo = new
                    {
                        name = "RimSearcher-Server",
                        // 规范的 Implementation 只有 name/title/version；description 不是字段名，
                        // 严格的 client 会忽略它，展示名于是回落到那个带连字符的标识符。
                        title = "RimSearcher",
                        version = UpdateChecker.CurrentVersion
                    },
                    instructions = ServerInstructions
                });
            }
            else if (method == "notifications/initialized" || method == "initialized")
            {
                await LogAsync("RimSearcher: Server initialized and ready to handle requests.", "info");
            }
            // 保活探针。不应答会让 client 判定连接已死，进而整体弃用本服务器。
            else if (method == "ping")
            {
                if (id != null) await SendResponseAsync(id, new { });
            }
            // 未实现的可选能力回空列表，而非让 client 收到错误或干等
            else if (method == "resources/list")
            {
                if (id != null) await SendResponseAsync(id, new { resources = Array.Empty<object>() });
            }
            // 模板列表的规范字段是 resourceTemplates，不是 resources。字段名错了，
            // 严格按规范解析的 client 会把这条响应判成协议错误，而不是「没有模板」。
            else if (method == "resources/templates/list")
            {
                if (id != null) await SendResponseAsync(id, new { resourceTemplates = Array.Empty<object>() });
            }
            else if (method == "prompts/list")
            {
                if (id != null) await SendResponseAsync(id, new { prompts = Array.Empty<object>() });
            }
            else if (method == "logging/setLevel")
            {
                // capabilities 里声明了 logging 就等于承诺按这个级别过滤；只回成功不做事，
                // client 设了 error 照样收到全部 info 噪音，比不声明该能力更糟。
                var level = root.TryGetProperty("params", out var levelParams)
                    && levelParams.ValueKind == JsonValueKind.Object
                    && levelParams.TryGetProperty("level", out var levelElem)
                    && levelElem.ValueKind == JsonValueKind.String
                        ? levelElem.GetString()
                        : null;

                // 无法识别的级别静默接受会让 client 以为过滤已生效，实际一条也没过滤掉
                if (level == null || !LogSeverities.TryGetValue(level, out var severity))
                {
                    if (id != null)
                        await SendResponseAsync(id, error: new
                        {
                            code = -32602,
                            message = $"Invalid params: 'level' must be one of {string.Join(", ", LogSeverities.Keys)}"
                        });
                    return;
                }

                Volatile.Write(ref _minLogSeverity, severity);
                if (id != null) await SendResponseAsync(id, new { });
            }
            else if (method is "notifications/roots/list_changed" or "completion/complete")
            {
                if (id != null) await SendResponseAsync(id, new { });
            }
            else if (method == "list_tools" || method == "tools/list")
            {
                if (id == null) return;
                await SendResponseAsync(id, new
                {
                    tools = _tools.Values.Select(t => new
                    {
                        name = t.Name,
                        title = t.Title,
                        description = t.Description,
                        inputSchema = t.JsonSchema,
                        annotations = new
                        {
                            title = t.Title,
                            readOnlyHint = t.ReadOnlyHint,
                            // 只读工具不做破坏性操作也天然幂等；sync_sources 会改磁盘且不可重放
                            destructiveHint = !t.ReadOnlyHint,
                            idempotentHint = t.ReadOnlyHint,
                            // 全部数据来自本机已配置的源目录，没有外部世界可以开放
                            openWorldHint = false
                        }
                    })
                });
            }
            else if (method == "call_tool" || method == "tools/call")
            {
                if (id == null) return;

                // 缺 params / name 是调用方把请求写错了，属于 -32602 Invalid params。
                // 直接 GetProperty 会抛 KeyNotFoundException 落进外层 catch 变成 -32603，
                // 而那个码的意思是「服务器坏了」，会误导 client 去重试或整体弃用本服务器。
                if (!root.TryGetProperty("params", out var paramsElem)
                    || paramsElem.ValueKind != JsonValueKind.Object
                    || !paramsElem.TryGetProperty("name", out var nameElem)
                    || nameElem.ValueKind != JsonValueKind.String)
                {
                    await SendResponseAsync(id, error: new { code = -32602, message = "Invalid params: 'params.name' is required and must be a string" });
                    return;
                }

                var toolName = nameElem.GetString();

                if (toolName != null && _tools.TryGetValue(toolName, out var tool))
                {
                    // progressToken 只在 client 明确请求时才存在；无 token 时发 progress 通知
                    // 会让规范实现收到无法归属的 token。
                    SerialProgressReporter? progressReporter = null;
                    if (paramsElem.TryGetProperty("_meta", out var meta)
                        && meta.TryGetProperty("progressToken", out var tokenElem)
                        && tokenElem.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        // 规范要求原样回显 token：数字 token 被转成字符串后，严格的 client
                        // 关联不上自己的请求，进度条直接断掉。这里和 ExtractId 用同一套取值方式。
                        var progressToken = ScalarIdValue(tokenElem);

                        progressReporter = new SerialProgressReporter(p =>
                            SendNotificationAsync("notifications/progress", new
                            {
                                progressToken,
                                progress = p,
                                total = 1.0
                            }));
                    }

                    var arguments = paramsElem.TryGetProperty("arguments", out var argsElem)
                        ? argsElem
                        : default;

                    ToolResult result;
                    try
                    {
                        // 索引重建期间挂起：读权保证不会查到清空到一半的索引。
                        // 必须走异步门——工具体内有真实挂起点，线程绑定的锁在这里解不掉（见 IndexGate）。
                        if (tool.BypassIndexGate)
                        {
                            result = await tool.ExecuteAsync(arguments, ct, progressReporter);
                        }
                        else
                        {
                            var indexScope = await IndexGate.TryEnterReadAsync(ct);
                            if (indexScope != null)
                            {
                                using (indexScope)
                                {
                                    result = await tool.ExecuteAsync(arguments, ct, progressReporter);
                                }
                            }
                            else
                            {
                                // 重建是原地进行的，等不到读权时索引里没有可退而求其次的旧数据。
                                // 报错让调用方重试，好过回一份看起来成功的空结果。
                                result = new ToolResult(
                                    "The index is being rebuilt and the request timed out waiting for it. Retry in a few seconds.",
                                    true);
                            }
                        }
                    }
                    // 参数契约错误是调用方可自行修正的，必须作为工具结果回去（带纠正提示），
                    // 不能变成 -32603 —— 那会被读成服务器故障。
                    catch (ToolArgumentException argEx)
                    {
                        result = new ToolResult(argEx.Message, true);
                    }
                    finally
                    {
                        // 排空必须在本请求的任何一条响应写出之前，异常路径也要——那些路径同样会写响应。
                        // 否则 client 会收到一个已经结束的请求的进度更新：轻则日志噪音，重则状态机错乱。
                        if (progressReporter != null) await progressReporter.DrainAsync();
                    }

                    var notice = tool.SuppressStalenessNotice
                        ? null
                        : _updateNotice?.Consume(toolName, arguments, result.Content);

                    // 启动期健康提示不受 SuppressStalenessNotice 影响：那面旗关的是「源变了」
                    // 这类时效提示，而「索引根本是空的」与时效无关，任何工具都得照说
                    var health = _startupNotice.Consume();

                    await SendResponseAsync(id, new
                    {
                        content = new[]
                        {
                            new { type = "text", text = result.Content + health + notice }
                        },
                        isError = result.IsError
                    });
                }
                else
                {
                    var known = string.Join(", ", _tools.Keys);
                    await SendResponseAsync(id, error: new
                    {
                        code = -32602,
                        message = $"Unknown tool '{toolName}'. Available tools: {known}"
                    });
                }
            }
            else if (id != null)
            {
                await SendResponseAsync(id, error: new { code = -32601, message = $"Method not found: '{method}'" });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (id != null)
                await SendResponseAsync(id, error: new { code = -32603, message = $"Internal error: {ex.Message}" });
        }
    }

    // client 请求的版本若在支持集内就回显它；否则回本服务器最高版本，由 client 决定去留。
    private static readonly string[] SupportedProtocolVersions =
        ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private static string NegotiateProtocolVersion(JsonElement root)
    {
        if (root.TryGetProperty("params", out var p)
            && p.TryGetProperty("protocolVersion", out var v)
            && v.ValueKind == JsonValueKind.String)
        {
            var requested = v.GetString();
            if (requested != null && SupportedProtocolVersions.Contains(requested))
                return requested;
        }

        return SupportedProtocolVersions[0];
    }

    
    public async Task LogAsync(string message, string level = "info", string? logger = "RimSearcher")
    {
        // 认不出的级别一律放行：宁可多发一条，也不要因为写错了一个级别名就把告警吞掉
        if (LogSeverities.TryGetValue(level, out var severity) && severity < Volatile.Read(ref _minLogSeverity))
            return;

        if (string.Equals(logger, "RimSearcher", StringComparison.Ordinal) && TrySplitComponentMessage(message, out var component, out var normalizedMessage))
        {
            logger = component;
            message = normalizedMessage;
        }

        await SendNotificationAsync("notifications/logging/message", new
        {
            level = level,
            logger = logger,
            data = message
        });
    }

    private static bool TrySplitComponentMessage(string message, out string component, out string normalizedMessage)
    {
        component = string.Empty;
        normalizedMessage = message;

        if (string.IsNullOrWhiteSpace(message)) return false;

        var separatorIndex = message.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex > 40) return false;

        var prefix = message[..separatorIndex];
        if (prefix.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.' && ch != '_' && ch != '-'))
            return false;

        var suffix = message[(separatorIndex + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(suffix)) return false;

        component = prefix;
        normalizedMessage = suffix;
        return true;
    }

    // 刻意不用 Progress<T>：它的回调是异步调度的（控制台进程没有 SynchronizationContext，
    // 回调被丢进线程池），加上原来发送侧还是 `_ = SendNotificationAsync(...)` 火后不管，
    // 于是 (a) 多次 Report 的通知可能乱序落到 stdout，(b) 最终响应写完之后还会继续冒出
    // 这个请求的进度通知。这里把每次 Report 串成一条任务链，并留一个排空点给响应前调用。
    //
    // 链上一环的异常必须吞掉：某次通知写失败不该让后续通知和 DrainAsync 全部跟着炸——
    // 那会把一个纯粹的进度问题升级成请求失败。
    private sealed class SerialProgressReporter(Func<double, Task> send) : IProgress<double>
    {
        private readonly Lock _sync = new();
        private Task _tail = Task.CompletedTask;

        public void Report(double value)
        {
            lock (_sync)
            {
                _tail = SendAfterAsync(_tail, value);
            }
        }

        private async Task SendAfterAsync(Task previous, double value)
        {
            await previous.ConfigureAwait(false);
            try { await send(value).ConfigureAwait(false); }
            catch { /* 见类型注释：单条通知失败不牵连整条链 */ }
        }

        // 返回的是「到此刻为止已提交的通知」的完成点。之后再 Report 的不算，
        // 但工具已经返回了，不会再有新的 Report。
        public Task DrainAsync()
        {
            lock (_sync) return _tail;
        }
    }

    private async Task SendNotificationAsync(string method, object? @params = null)
    {
        var notification = new { jsonrpc = "2.0", method = method, @params = @params };
        var json = JsonSerializer.Serialize(notification);

        await _writeLock.WaitAsync();
        try
        {
            await _protocolOut.WriteLineAsync(json);
            await _protocolOut.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendResponseAsync(object? id, object? result = null, object? error = null)
    {
        if (id == null && error == null) return;

        object response = error != null
            ? new { jsonrpc = "2.0", id = id, error = error }
            : new { jsonrpc = "2.0", id = id, result = result };

        var json = JsonSerializer.Serialize(response);

        await _writeLock.WaitAsync();
        try
        {
            await _protocolOut.WriteLineAsync(json);
            await _protocolOut.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
