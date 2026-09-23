using System.Net;
using System.Text.Json;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FreeCom.Core.ControlApi;

public sealed class ControlApiOptions
{
    public int Port { get; set; } = 17340;
    public string Token { get; set; } = "";
    public string BindHost { get; set; } = "127.0.0.1"; // 仅本机（PRD F7.1 安全要求）
}

/// <summary>MCP 工具目录：工具名 → 端点映射（能力发现的单一事实来源）。</summary>
public static class McpToolCatalog
{
    public sealed record ToolDef(string Name, string Description, string Method, string Path, string InputSchemaJson);

    public static readonly ToolDef[] Tools =
    [
        new("diag_connectivity", "检查 FreeCom Control API 是否可用，返回状态与版本", "GET", "/v1/health",
            """{"type":"object","properties":{},"required":[]}"""),
        new("serial_list", "列出可用串口（含虚拟串口对创建的 COM 口），含占用状态", "GET", "/v1/serial/ports",
            """{"type":"object","properties":{"transport":{"type":"string","default":"serial"},"probe":{"type":"boolean","default":false}},"required":[]}"""),
        new("serial_open", "打开串口。虚拟串口对（com0com）创建的 COM 口与物理口用法一致", "POST", "/v1/serial/open",
            """{"type":"object","properties":{"transport":{"type":"string","default":"serial"},"params":{"type":"object","properties":{"port":{"type":"string"},"baud":{"type":"integer"}}}},"required":[]}"""),
        new("serial_close", "关闭当前端口", "POST", "/v1/serial/close", """{"type":"object","properties":{},"required":[]}"""),
        new("serial_status", "查询当前连接状态", "GET", "/v1/serial/status", """{"type":"object","properties":{},"required":[]}"""),
        new("device_send", "直发数据（不经 UI）。format=text|hex", "POST", "/v1/device/send",
            """{"type":"object","properties":{"data":{"type":"string"},"format":{"type":"string","enum":["text","hex"],"default":"text"}},"required":["data"]}"""),
        new("receive_read", "读取接收缓冲（since 游标 + limit + format）", "GET", "/v1/device/receive",
            """{"type":"object","properties":{"since":{"type":"integer","default":0},"limit":{"type":"integer","default":100},"format":{"type":"string","enum":["text","hex"],"default":"text"}},"required":[]}"""),
        new("protocol_get", "读取当前绘图协议", "GET", "/v1/protocol", """{"type":"object","properties":{},"required":[]}"""),
        new("protocol_set", "切换绘图协议（TEXT/CSV/STAMP/EasyHex/ModbusRTU）", "PUT", "/v1/protocol",
            """{"type":"object","properties":{"name":{"type":"string"},"options":{"type":"object"}},"required":["name"]}"""),
        new("plot_windows", "列出绘图窗口与曲线清单", "GET", "/v1/plot/windows", """{"type":"object","properties":{},"required":[]}"""),
        new("plot_data", "读取某窗口曲线数据（id 或 title，maxPoints 抽稀）", "GET", "/v1/plot/windows/{id}/data",
            """{"type":"object","properties":{"id":{"type":"string"},"maxPoints":{"type":"integer","default":2000}},"required":["id"]}"""),
        new("curve_export", "导出曲线为 CSV，返回文件路径", "POST", "/v1/export/curves",
            """{"type":"object","properties":{"windowId":{"type":"string"},"path":{"type":"string"}},"required":[]}"""),
        new("app_info", "应用状态：版本/收发计数/解析错误/窗口数", "GET", "/v1/app/info", """{"type":"object","properties":{},"required":[]}"""),

        // ---------------- MCP v0.2 新增（P0/P1） ----------------
        new("receive_wait", "阻塞等待匹配的接收行（关键字/方向/超时），替代轮询 receive_read", "POST", "/v1/device/wait",
            """{"type":"object","properties":{"contains":{"type":"string","description":"匹配子串（text 按解码文本，hex 按十六进制串）"},"direction":{"type":"string","enum":["rx","tx","any"],"default":"any"},"since":{"type":"integer","default":0},"timeoutMs":{"type":"integer","default":3000,"maximum":10000},"limit":{"type":"integer","default":50},"format":{"type":"string","enum":["text","hex"],"default":"text"}},"required":[]}"""),
        new("send_expect", "发送数据并等待应答（命令-应答一步完成）：如发 AT+VER? 等 OK", "POST", "/v1/device/expect",
            """{"type":"object","properties":{"send":{"type":"object","properties":{"data":{"type":"string"},"format":{"type":"string","enum":["text","hex"],"default":"text"},"encoding":{"type":"string"},"newline":{"type":"string"}},"required":["data"]},"contains":{"type":"string"},"direction":{"type":"string","enum":["rx","tx","any"],"default":"rx"},"timeoutMs":{"type":"integer","default":3000,"maximum":10000},"format":{"type":"string","enum":["text","hex"],"default":"text"}},"required":["send"]}"""),
        new("vcom_list", "列出 com0com 虚拟串口对", "GET", "/v1/vcom/pairs",
            """{"type":"object","properties":{},"required":[]}"""),
        new("vcom_create", "创建虚拟串口对（需管理员会弹 UAC；省略端口自动挑空闲号）", "POST", "/v1/vcom/pairs",
            """{"type":"object","properties":{"portA":{"type":"string"},"portB":{"type":"string"}},"required":[]}"""),
        new("vcom_remove", "删除虚拟串口对（需管理员会弹 UAC；pairNumber 见 vcom_list）", "DELETE", "/v1/vcom/pairs/{n}",
            """{"type":"object","properties":{},"required":[]}"""),
        new("simulator_start", "设备模拟器：以下位机身份占用端口对另一端并持续发送协议帧（无硬件自测）", "POST", "/v1/simulator/start",
            """{"type":"object","properties":{"port":{"type":"string","description":"端口对的另一端（主程序连 COM24 则填 COM25）"},"protocol":{"type":"string","default":"TEXT"},"intervalMs":{"type":"integer","default":100}},"required":["port"]}"""),
        new("simulator_stop", "停止设备模拟器", "POST", "/v1/simulator/stop",
            """{"type":"object","properties":{},"required":[]}"""),
        new("curve_stats", "曲线统计（count/min/max/mean/first/last），无需拉全量数据自己算", "GET", "/v1/plot/windows/{id}/stats",
            """{"type":"object","properties":{"id":{"type":"string","description":"窗口 id 或标题"}},"required":["id"]}"""),
        new("protocol_help", "协议格式速查：帧格式/示例/下位机 C 例程（生成设备端代码前先取规格）", "GET", "/v1/protocol/help",
            """{"type":"object","properties":{"name":{"type":"string","description":"省略返回全部五协议"}},"required":[]}"""),
        new("export_raw", "导出原始字节日志为 DAT（可选方向 rx/tx/all）", "POST", "/v1/export/raw",
            """{"type":"object","properties":{"path":{"type":"string"},"direction":{"type":"string","enum":["rx","tx","all"],"default":"all"}},"required":[]}"""),
        new("export_display", "导出显示文本为 TXT（可含时间戳/HEX 格式）", "POST", "/v1/export/display",
            """{"type":"object","properties":{"path":{"type":"string"},"timestamp":{"type":"boolean","default":true},"hex":{"type":"boolean","default":false}},"required":[]}"""),
        new("send_history", "发送审计：最近发送的数据（UI 与 API 的发送都记录，含 text/hex 预览）", "GET", "/v1/device/send-history",
            """{"type":"object","properties":{"limit":{"type":"integer","default":20,"maximum":50}},"required":[]}"""),
    ];
}

/// <summary>本地 Control HTTP API（PRD F7.1）：仅 127.0.0.1 + Bearer Token。</summary>
public sealed class ControlApiServer : IAsyncDisposable, IDisposable
{
    private readonly IControlSurface _surface;
    private readonly ControlApiOptions _options;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private WebApplication? _app;

    public ControlApiServer(IControlSurface surface, ControlApiOptions options)
    {
        _surface = surface;
        _options = options;
        if (string.IsNullOrEmpty(_options.Token))
            throw new ArgumentException("Control API 必须配置 Token（Bearer）");
    }

    public int Port => _options.Port;
    public string BaseUrl => $"http://{_options.BindHost}:{_options.Port}";

    public Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Parse(_options.BindHost), _options.Port);
            o.AddServerHeader = false;
        });

        var app = builder.Build();
        _app = app;

        // 鉴权中间件：Bearer Token，失败统一 401
        app.Use(async (ctx, next) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(auth["Bearer ".Length..].Trim(), _options.Token, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("""{"ok":false,"error":{"code":"unauthorized","message":"缺少或错误的 Bearer Token"}}""", ct);
                return;
            }
            await next(ctx);
        });

        MapEndpoints(app);
        return app.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private static IResult Ok(object data) => Results.Json(new { ok = true, data });

    private static IResult Err(string code, string message, int status = 400)
        => Results.Json(new { ok = false, error = new { code, message } }, statusCode: status);

    private static async Task<JsonElement?> ReadJsonAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/v1/health", () => Ok(new
        {
            app = "FreeCom",
            version = PipelineSurface.AppVersion,
            uptimeSeconds = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
        }));

        app.MapGet("/v1/capabilities", () => Ok(new CapabilitiesDto(
            "FreeCom",
            PipelineSurface.AppVersion,
            EndpointCatalog(),
            McpToolCatalog.Tools.Select(t => new McpToolDto(t.Name, t.Description, t.InputSchemaJson)).ToList(),
            ProtocolRegistry.Infos(),
            TransportRegistry.All.Select(f => f.Kind).ToList())));

        app.MapGet("/v1/serial/ports", (HttpContext ctx) =>
        {
            var transport = ctx.Request.Query["transport"].ToString();
            var probe = bool.TryParse(ctx.Request.Query["probe"], out var b) && b;
            return Safe(() => Ok(_surface.ListPorts(transport, probe)));
        });

        app.MapPost("/v1/serial/open", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                string transport = "serial";
                if (json.Value.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.String)
                    transport = t.GetString() ?? "serial";
                Dictionary<string, string>? parameters = null;
                if (json.Value.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                    parameters = p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString());
                await _surface.OpenTransportAsync(new OpenTransportRequest(transport, parameters));
                return Ok(_surface.GetTransportStatus());
            });
        });

        app.MapPost("/v1/serial/close", () => SafeAsync(async () =>
        {
            await _surface.CloseTransportAsync();
            return Ok(_surface.GetTransportStatus());
        }));

        app.MapGet("/v1/serial/status", () => Safe(() => Ok(_surface.GetTransportStatus())));

        app.MapPost("/v1/device/send", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                if (!json.Value.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String)
                    return Err("invalid_param", "缺少字符串字段 data");
                var format = json.Value.TryGetProperty("format", out var f) ? f.GetString() : "text";
                var encoding = json.Value.TryGetProperty("encoding", out var e) ? e.GetString() : null;
                var newline = json.Value.TryGetProperty("newline", out var n) ? n.GetString() : null;
                await _surface.SendAsync(new SendRequest(d.GetString()!, format ?? "text", encoding, newline));
                return Ok(new { sent = true });
            });
        });

        app.MapGet("/v1/device/receive", (HttpContext ctx) => Safe(() =>
        {
            long since = long.TryParse(ctx.Request.Query["since"], out var s) ? s : 0;
            int limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 100;
            var format = ctx.Request.Query["format"].ToString();
            if (string.IsNullOrEmpty(format)) format = "text";
            return Ok(_surface.ReadReceive(since, limit, format));
        }));

        app.MapGet("/v1/protocol", () => Safe(() => Ok(_surface.GetProtocol())));

        app.MapPut("/v1/protocol", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                if (!json.Value.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
                    return Err("invalid_param", "缺少字符串字段 name");
                Dictionary<string, string>? options = null;
                if (json.Value.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object)
                    options = o.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString());
                await _surface.SetProtocolAsync(n.GetString()!, options);
                return Ok(_surface.GetProtocol());
            });
        });

        app.MapGet("/v1/plot/windows", () => Safe(() => Ok(_surface.GetPlotWindows())));

        app.MapGet("/v1/plot/windows/{id}/data", (HttpContext ctx, string id) => Safe(() =>
        {
            int maxPoints = int.TryParse(ctx.Request.Query["maxPoints"], out var m) ? m : 2000;
            var data = _surface.GetPlotWindowData(id, maxPoints);
            return data is null ? Err("not_found", $"窗口不存在: {id}", 404) : Ok(data);
        }));

        app.MapPost("/v1/export/curves", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                string? windowId = json.Value.TryGetProperty("windowId", out var w) && w.ValueKind == JsonValueKind.String
                    ? w.GetString() : null;
                string? path = json.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;
                var fullPath = await _surface.ExportCurvesAsync(windowId, path);
                return Ok(new { path = fullPath });
            });
        });

        app.MapGet("/v1/app/info", () => Safe(() => Ok(_surface.GetAppInfo())));

        // ---------------- MCP v0.2 新增端点（P0/P1） ----------------

        app.MapPost("/v1/device/wait", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                var req = ParseWait(json.Value);
                var result = await _surface.WaitReceiveAsync(req, ctx.RequestAborted);
                return Ok(result);
            });
        });

        app.MapPost("/v1/device/expect", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                var root = json.Value;
                if (!root.TryGetProperty("send", out var sendEl) || sendEl.ValueKind != JsonValueKind.Object ||
                    !sendEl.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
                    return Err("invalid_param", "必须提供 send.data");
                var send = new SendRequest(
                    dataEl.GetString()!,
                    sendEl.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "text",
                    sendEl.TryGetProperty("encoding", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                    sendEl.TryGetProperty("newline", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
                var (contains, direction, timeoutMs, _, format) = ParseWaitCore(root);
                var result = await _surface.SendExpectAsync(new ExpectRequest(send, contains, direction, timeoutMs, format), ctx.RequestAborted);
                return Ok(result);
            });
        });

        app.MapGet("/v1/protocol/help", (HttpContext ctx) => Safe(() =>
        {
            var name = ctx.Request.Query["name"].ToString();
            return Ok(_surface.GetProtocolHelp(string.IsNullOrWhiteSpace(name) ? null : name));
        }));

        app.MapGet("/v1/plot/windows/{id}/stats", (HttpContext ctx) => Safe(() =>
        {
            var stats = _surface.GetPlotWindowStats(ctx.Request.RouteValues["id"]?.ToString() ?? "");
            return stats is null ? Err("not_found", "窗口不存在（用 plot_windows 查询可用窗口）", 404) : Ok(stats);
        }));

        app.MapGet("/v1/device/send-history", (HttpContext ctx) => Safe(() =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 20;
            return Ok(_surface.GetSendHistory(limit));
        }));

        app.MapPost("/v1/export/raw", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                string? path = json.Value.TryGetProperty("path", out var p2) && p2.ValueKind == JsonValueKind.String ? p2.GetString() : null;
                string? dir = json.Value.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var fullPath = await _surface.ExportRawAsync(path, dir);
                return Ok(new { path = fullPath });
            });
        });

        app.MapPost("/v1/export/display", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                string? path = json.Value.TryGetProperty("path", out var p2) && p2.ValueKind == JsonValueKind.String ? p2.GetString() : null;
                bool ts = json.Value.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.True;
                bool hex = json.Value.TryGetProperty("hex", out var x) && x.ValueKind == JsonValueKind.True;
                var fullPath = await _surface.ExportDisplayAsync(path, ts, hex);
                return Ok(new { path = fullPath });
            });
        });

        app.MapGet("/v1/vcom/pairs", () => Safe(() => Ok(_surface.ListVcomPairs())));

        app.MapPost("/v1/vcom/pairs", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return await SafeAsync(async () =>
            {
                string? a = json.Value.TryGetProperty("portA", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null;
                string? b = json.Value.TryGetProperty("portB", out var pb) && pb.ValueKind == JsonValueKind.String ? pb.GetString() : null;
                var created = await _surface.CreateVcomPairAsync(a, b);
                return Ok(created);
            });
        });

        // 注意：外层 lambda 必须 async——非 async 的 (HttpContext ctx) => Task<IResult>
        // 会被 Minimal API 当作 RequestDelegate 执行并丢弃返回的 IResult（表现为 200 空 body）。
        app.MapDelete("/v1/vcom/pairs/{n}", async (HttpContext ctx) =>
        {
            return await SafeAsync(async () =>
            {
                await _surface.RemoveVcomPairAsync(ctx.Request.RouteValues["n"]?.ToString() ?? "");
                return Ok(new { removed = true });
            });
        });

        app.MapPost("/v1/simulator/start", async (HttpContext ctx) =>
        {
            var json = await ReadJsonAsync(ctx);
            if (json is null) return Err("invalid_json", "请求体不是合法 JSON");
            return Safe(() =>
            {
                if (!json.Value.TryGetProperty("port", out var portEl) || portEl.ValueKind != JsonValueKind.String)
                    return Err("invalid_param", "必须提供 port（虚拟串口对的另一端）");
                var protocol = json.Value.TryGetProperty("protocol", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString()! : "TEXT";
                var interval = json.Value.TryGetProperty("intervalMs", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 100;
                return Ok(_surface.StartSimulator(new SimulatorStartRequest(portEl.GetString()!, protocol, interval)));
            });
        });

        app.MapPost("/v1/simulator/stop", () => Safe(() => Ok(_surface.StopSimulator())));

        app.MapGet("/v1/simulator/status", () => Safe(() => Ok(_surface.GetSimulatorStatus())));
    }

    private static (string? Contains, string? Direction, int TimeoutMs, int Limit, string? Format) ParseWaitCore(JsonElement root)
        => (
            root.TryGetProperty("contains", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            root.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            root.TryGetProperty("timeoutMs", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 3000,
            root.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 50,
            root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null
        );

    private static WaitRequest ParseWait(JsonElement root)
    {
        var (contains, direction, timeoutMs, limit, format) = ParseWaitCore(root);
        var since = root.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0L;
        return new WaitRequest(since, contains, direction, timeoutMs, limit, format);
    }

    private static IReadOnlyList<EndpointDto> EndpointCatalog() =>
    [
        new EndpointDto("GET", "/v1/health", "健康检查"),
        new EndpointDto("GET", "/v1/capabilities", "能力发现（端点/工具/协议/传输）"),
        new EndpointDto("GET", "/v1/serial/ports", "端口列表"),
        new EndpointDto("POST", "/v1/serial/open", "打开端口"),
        new EndpointDto("POST", "/v1/serial/close", "关闭端口"),
        new EndpointDto("GET", "/v1/serial/status", "连接状态"),
        new EndpointDto("POST", "/v1/device/send", "直发数据"),
        new EndpointDto("GET", "/v1/device/receive", "读取接收缓冲"),
        new EndpointDto("GET", "/v1/protocol", "当前协议"),
        new EndpointDto("PUT", "/v1/protocol", "切换协议"),
        new EndpointDto("GET", "/v1/plot/windows", "绘图窗口列表"),
        new EndpointDto("GET", "/v1/plot/windows/{id}/data", "窗口曲线数据"),
        new EndpointDto("POST", "/v1/export/curves", "导出曲线 CSV"),
        new EndpointDto("GET", "/v1/app/info", "应用状态"),
        new EndpointDto("POST", "/v1/device/wait", "阻塞等待匹配的接收行"),
        new EndpointDto("POST", "/v1/device/expect", "发送并等待应答"),
        new EndpointDto("GET", "/v1/protocol/help", "协议格式速查（帧格式/示例/C 例程）"),
        new EndpointDto("GET", "/v1/plot/windows/{id}/stats", "曲线统计（min/max/mean/last）"),
        new EndpointDto("GET", "/v1/device/send-history", "发送审计（最近 50 条）"),
        new EndpointDto("POST", "/v1/export/raw", "导出原始字节 DAT"),
        new EndpointDto("POST", "/v1/export/display", "导出显示文本 TXT"),
        new EndpointDto("GET", "/v1/vcom/pairs", "虚拟串口对列表"),
        new EndpointDto("POST", "/v1/vcom/pairs", "创建端口对（需管理员/UAC）"),
        new EndpointDto("DELETE", "/v1/vcom/pairs/{n}", "删除端口对（需管理员/UAC）"),
        new EndpointDto("POST", "/v1/simulator/start", "设备模拟器开始发送"),
        new EndpointDto("POST", "/v1/simulator/stop", "设备模拟器停止"),
        new EndpointDto("GET", "/v1/simulator/status", "设备模拟器状态"),
    ];

    private IResult Safe(Func<IResult> f)
    {
        try { return f(); }
        catch (ArgumentException ex) { return Err("invalid_param", ex.Message); }
        catch (InvalidOperationException ex) { return Err("invalid_state", ex.Message, 409); }
        catch (Exception ex) { return Err("internal", ex.Message, 500); }
    }

    private async Task<IResult> SafeAsync(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (ArgumentException ex) { return Err("invalid_param", ex.Message); }
        catch (InvalidOperationException ex) { return Err("invalid_state", ex.Message, 409); }
        catch (Exception ex) { return Err("internal", ex.Message, 500); }
    }
}
