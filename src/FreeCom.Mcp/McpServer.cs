using System.Text;
using System.Text.Json;

namespace FreeCom.Mcp;

/// <summary>Control API HTTP 客户端（Bearer Token）。</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(string baseUrl, string token)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    public string BaseUrl { get; }

    public async Task<(int Status, string Body)> SendAsync(string method, string path, string? jsonBody = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, body);
    }
}

/// <summary>
/// MCP stdio 桥（PRD F7.2）：JSON-RPC 2.0 over stdin/stdout。
/// 自实现 JSON-RPC 层（技术方案风险 R4 的既定预案），工具表来自 /v1/capabilities。
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2024-11-05";

    /// <summary>宽松转义：工具返回的内嵌 JSON 保持可读（不转义引号），便于 AI 客户端直接阅读。</summary>
    private static readonly JsonSerializerOptions s_relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ApiClient _api;

    public McpServer(TextReader input, TextWriter output, ApiClient api)
    {
        _input = input;
        _output = output;
        _api = api;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break; // stdin 关闭
            if (string.IsNullOrWhiteSpace(line)) continue;
            string response;
            try
            {
                using var doc = JsonDocument.Parse(line);
                response = await DispatchAsync(doc.RootElement).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                response = Error(-32700, "解析错误：请求不是合法 JSON");
            }
            catch (Exception ex)
            {
                response = Error(-32603, $"内部错误: {ex.Message}");
            }
            if (response != null)
            {
                await _output.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
                await _output.FlushAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> DispatchAsync(JsonElement req)
    {
        bool hasId = req.TryGetProperty("id", out var idElem);
        var method = req.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : "";

        if (!hasId)
            return null; // notification（如 initialized）不回应

        string Respond(object result) => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = idElem,
            result,
        }, s_relaxed);

        try
        {
            switch (method)
            {
                case "initialize":
                    return Respond(new
                    {
                        protocolVersion = ProtocolVersion,
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "freecom-mcp", version = "1.0.0" },
                    });

                case "ping":
                    return Respond(new { });

                case "tools/list":
                {
                    var (status, body) = await _api.SendAsync("GET", "/v1/capabilities");
                    if (status != 200)
                        return Error(-32002, $"无法获取能力清单（HTTP {status}）：{body}");
                    using var cap = JsonDocument.Parse(body);
                    var tools = cap.RootElement.GetProperty("data").GetProperty("tools");
                    var list = new List<object>();
                    foreach (var t in tools.EnumerateArray())
                    {
                        list.Add(new
                        {
                            name = t.GetProperty("name").GetString(),
                            description = t.GetProperty("description").GetString(),
                            inputSchema = JsonSerializer.Deserialize<JsonElement>(
                                t.TryGetProperty("inputSchemaJson", out var schema) && schema.ValueKind == JsonValueKind.String
                                    ? schema.GetString()!
                                    : "{}"),
                        });
                    }
                    return Respond(new { tools = list });
                }

                case "tools/call":
                {
                    if (!req.TryGetProperty("params", out var p))
                        return Error(-32602, "缺少 params");
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name))
                        return Error(-32602, "缺少工具名 params.name");
                    JsonElement args = p.TryGetProperty("_arguments", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a : (p.TryGetProperty("arguments", out var a2) && a2.ValueKind == JsonValueKind.Object ? a2 : default);
                    var (ok, text) = await CallToolAsync(name, args).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = idElem,
                        result = new
                        {
                            content = new[] { new { type = "text", text } },
                            isError = !ok,
                        },
                    }, s_relaxed);
                }

                default:
                    return Error(-32601, $"未知方法: {method}");
            }
        }
        catch (Exception ex)
        {
            return Error(-32603, ex.Message);
        }
    }

    private async Task<(bool Ok, string Text)> CallToolAsync(string name, JsonElement args)
    {
        try
        {
            switch (name)
            {
                case "diag_connectivity":
                    return await GetAsync("/v1/health");
                case "serial_list":
                {
                    var q = BuildQuery(args, ("transport", "serial"), ("probe", "false"));
                    return await GetAsync("/v1/serial/ports" + q);
                }
                case "serial_open":
                    return await PostAsync("/v1/serial/open", Body(args));
                case "serial_close":
                    return await PostAsync("/v1/serial/close", "{}");
                case "serial_status":
                    return await GetAsync("/v1/serial/status");
                case "device_send":
                    return await PostAsync("/v1/device/send", Body(args));
                case "receive_read":
                {
                    var q = BuildQuery(args, ("since", "0"), ("limit", "100"), ("format", "text"));
                    return await GetAsync("/v1/device/receive" + q);
                }
                case "protocol_get":
                    return await GetAsync("/v1/protocol");
                case "protocol_set":
                    return await PutAsync("/v1/protocol", Body(args));
                case "plot_windows":
                    return await GetAsync("/v1/plot/windows");
                case "plot_data":
                {
                    if (args.ValueKind is not JsonValueKind.Object ||
                        !args.TryGetProperty("id", out var idv) || idv.ValueKind != JsonValueKind.String)
                        return (false, "缺少参数 id（窗口 id 或 title）");
                    var max = args.TryGetProperty("maxPoints", out var mp) ? mp.ToString() : "2000";
                    return await GetAsync($"/v1/plot/windows/{Uri.EscapeDataString(idv.GetString()!)}/data?maxPoints={max}");
                }
                case "curve_export":
                    return await PostAsync("/v1/export/curves", Body(args));
                case "app_info":
                    return await GetAsync("/v1/app/info");
                default:
                    return (false, $"未知工具: {name}");
            }
        }
        catch (HttpRequestException ex)
        {
            return (false, $"无法连接 FreeCom Control API（{ex.Message}）。请确认：1) FreeCom 已启动并开启 API；2) FREECOM_URL 正确（默认 http://127.0.0.1:17340）；3) FREECOM_TOKEN 与软件中 Device ID 一致。");
        }
    }

    private static string Body(JsonElement args)
        => args.ValueKind is JsonValueKind.Object ? args.GetRawText() : "{}";

    private static string BuildQuery(JsonElement args, params (string Key, string Def)[] items)
    {
        var parts = new List<string>();
        foreach (var (key, def) in items)
        {
            var value = def;
            if (args.ValueKind is JsonValueKind.Object && args.TryGetProperty(key, out var v))
                value = v.ToString();
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }
        return "?" + string.Join("&", parts);
    }

    private Task<(bool, string)> GetAsync(string path) => SendToolAsync("GET", path, null);
    private Task<(bool, string)> PostAsync(string path, string body) => SendToolAsync("POST", path, body);
    private Task<(bool, string)> PutAsync(string path, string body) => SendToolAsync("PUT", path, body);

    private async Task<(bool Ok, string Text)> SendToolAsync(string method, string path, string? body)
    {
        var (status, respBody) = await _api.SendAsync(method, path, body);
        return status switch
        {
            200 => (true, respBody),
            _ => (false, $"HTTP {status}: {respBody}"),
        };
    }

    private static string Error(int code, string message) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = (JsonElement?)null,
        error = new { code, message },
    });
}
