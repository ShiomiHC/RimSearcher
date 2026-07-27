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
    private readonly SessionUpdateNotice? _updateNotice = SourceWatcher.CreateSessionNotice();

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
                requestKey = requestId?.ToString();

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
                        var idToCancel = ScalarIdToString(cancelId);
                        if (idToCancel != null && _activeRequests.TryRemove(idToCancel, out var targetCts))
                            targetCts.Cancel();
                    }
                    return;
                }

                await _concurrencyLimit.WaitAsync();
                limitAcquired = true;

                if (requestKey != null)
                {
                    cts = new CancellationTokenSource();
                    _activeRequests[requestKey] = cts;
                }

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
            if (requestKey != null) _activeRequests.TryRemove(requestKey, out _);
            cts?.Dispose();
            if (limitAcquired) _concurrencyLimit.Release();
        }
    }

    // JSON-RPC id 只能是 string / number / null
    private static object? ExtractId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProp)) return null;

        return idProp.ValueKind switch
        {
            JsonValueKind.String => idProp.GetString(),
            JsonValueKind.Number => idProp.TryGetInt64(out var l) ? l : idProp.GetDouble(),
            _ => null
        };
    }

    private static string? ScalarIdToString(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.ToString(),
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
                        version = UpdateChecker.CurrentVersion,
                        description = "Specialized MCP server for deep RimWorld source code and XML Def analysis."
                    }
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
            else if (method is "resources/list" or "resources/templates/list")
            {
                if (id != null) await SendResponseAsync(id, new { resources = Array.Empty<object>() });
            }
            else if (method == "prompts/list")
            {
                if (id != null) await SendResponseAsync(id, new { prompts = Array.Empty<object>() });
            }
            else if (method is "logging/setLevel" or "notifications/roots/list_changed" or "completion/complete")
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
                        description = t.Description,
                        inputSchema = t.JsonSchema
                    })
                });
            }
            else if (method == "call_tool" || method == "tools/call")
            {
                if (id == null) return;
                var paramsElem = root.GetProperty("params");
                var toolName = paramsElem.GetProperty("name").GetString();

                if (toolName != null && _tools.TryGetValue(toolName, out var tool))
                {
                    // progressToken 只在 client 明确请求时才存在；无 token 时发 progress 通知
                    // 会让规范实现收到无法归属的 token。
                    IProgress<double>? progressReporter = null;
                    if (paramsElem.TryGetProperty("_meta", out var meta)
                        && meta.TryGetProperty("progressToken", out var tokenElem)
                        && tokenElem.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        var progressToken = tokenElem.ValueKind == JsonValueKind.String
                            ? tokenElem.GetString()
                            : (object?)tokenElem.ToString();

                        progressReporter = new Progress<double>(p =>
                        {
                            _ = SendNotificationAsync("notifications/progress", new
                            {
                                progressToken,
                                progress = p,
                                total = 1.0
                            });
                        });
                    }

                    var arguments = paramsElem.TryGetProperty("arguments", out var argsElem)
                        ? argsElem
                        : default;

                    ToolResult result;
                    try
                    {
                        // 索引重建期间挂起：读锁保证不会查到清空到一半的索引。
                        // sync_sources 自己要触发重建，不能被这把锁挡住。
                        if (tool is Tools.SyncSourcesTool)
                        {
                            result = await tool.ExecuteAsync(arguments, ct, progressReporter);
                        }
                        else
                        {
                            using (IndexGate.EnterRead())
                            {
                                result = await tool.ExecuteAsync(arguments, ct, progressReporter);
                            }
                        }
                    }
                    // 参数契约错误是调用方可自行修正的，必须作为工具结果回去（带纠正提示），
                    // 不能变成 -32603 —— 那会被读成服务器故障。
                    catch (ToolArgumentException argEx)
                    {
                        result = new ToolResult(argEx.Message, true);
                    }

                    var notice = _updateNotice?.Consume(toolName, arguments, result.Content);

                    await SendResponseAsync(id, new
                    {
                        content = new[]
                        {
                            new { type = "text", text = notice == null ? result.Content : result.Content + notice }
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
