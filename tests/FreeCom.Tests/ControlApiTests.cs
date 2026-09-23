using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FreeCom.Core.ControlApi;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

/// <summary>
/// API/MCP 测试共用宿主：真实 Kestrel + com0com 真实串口对（App 侧 SerialTransport + 设备侧 SerialPeer 回显）。
/// </summary>
public sealed class ApiFixture : IAsyncDisposable, IAsyncLifetime
{
    public DataPipeline Pipeline { get; }
    public PipelineSurface Surface { get; }
    public ControlApiServer Server { get; }
    public HttpClient Http { get; }
    public string Token { get; } = $"test-{Guid.NewGuid():N}";
    public string BaseUrl { get; }
    public string AppPort { get; }
    public string DevicePort { get; }
    public SerialPeer? Peer { get; private set; }
    private readonly PortPool.PairLease _lease;

    public ApiFixture()
    {
        _lease = PortPool.Lease();
        AppPort = _lease.App;
        DevicePort = _lease.Device;
        Pipeline = new DataPipeline(ProtocolRegistry.Create("TEXT"));
        Surface = new PipelineSurface(Pipeline);

        var port = GetFreePort();
        Server = new ControlApiServer(Surface, new ControlApiOptions { Port = port, Token = Token });
        Server.StartAsync().GetAwaiter().GetResult();
        BaseUrl = Server.BaseUrl;

        Http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        Http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
    }

    /// <summary>打开默认连接：App 侧串口（经 Surface）+ 设备侧回显对端。</summary>
    public async Task OpenDefaultsAsync()
    {
        await Surface.OpenTransportAsync(new OpenTransportRequest("serial",
            new Dictionary<string, string> { ["port"] = AppPort, ["baud"] = "115200" }));
        if (Peer is null)
        {
            Peer = new SerialPeer(DevicePort) { Echo = true };
            Peer.Open();
        }
    }

    public void InjectText(string text) => Peer?.SendText(text);

    public async Task<JsonElement> GetAsync(string path)
    {
        using var resp = await Http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return Parse(await resp.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, JsonElement Json)> SendAsync(HttpMethod method, string path, string? body)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        return (resp.StatusCode, Parse(await resp.Content.ReadAsStringAsync()));
    }

    /// <summary>POST 并断言 200，返回已解包的 data。</summary>
    public async Task<JsonElement> PostAsync(string path, string body)
    {
        var (status, json) = await SendAsync(HttpMethod.Post, path, body);
        Assert.True(status == HttpStatusCode.OK, $"POST {path} -> {status}");
        return json.GetProperty("data");
    }

    /// <summary>GET 并断言 200，返回已解包的 data（McpToolsTests 用）。</summary>
    public async Task<JsonElement> GetDataAsync(string path)
    {
        var json = await GetAsync(path);
        return json.GetProperty("data");
    }

    /// <summary>关闭设备侧回显端口（模拟器测试需独占对端）。</summary>
    public void ClosePeer()
    {
        Peer?.Dispose();
        Peer = null;
    }

    /// <summary>仅打开 App 侧串口（不建回显对端），并返回设备侧端口名。</summary>
    public async Task OpenAppOnlyAsync()
    {
        if (Pipeline.TransportState != TransportState.Open)
            await Surface.OpenTransportAsync(new OpenTransportRequest("serial",
                new Dictionary<string, string> { ["port"] = AppPort, ["baud"] = "115200" }));
    }

    /// <summary>带状态码的快捷方法（McpToolsTests 用）。</summary>
    public Task<(HttpStatusCode, JsonElement)> PostExpectStatusAsync(string path, string body)
        => SendAsync(HttpMethod.Post, path, body);

    public Task<(HttpStatusCode, JsonElement)> GetExpectStatusAsync(string path)
        => SendAsync(HttpMethod.Get, path, null);

    public Task<(HttpStatusCode, JsonElement)> DeleteExpectStatusAsync(string path)
        => SendAsync(HttpMethod.Delete, path, null);

    /// <summary>原始请求：返回 (状态码, 原始 body)（探针/调查用）。</summary>
    public async Task<(HttpStatusCode Status, string Body)> RawSendAsync(HttpMethod method, string path)
    {
        using var req = new HttpRequestMessage(method, path);
        using var resp = await Http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        Peer?.Dispose();
        await Server.DisposeAsync();
        await Surface.CloseTransportAsync();
        Pipeline.Dispose();
        PortPool.Release(_lease);
    }

    // xUnit v2 calls IAsyncLifetime, not standalone IAsyncDisposable fixtures.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public async Task WaitWindowAsync(string title, int minPoints = 1)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            var win = Pipeline.Plots.Find(title);
            if (win != null && win.Curves.Count > 0 && win.Curves[0].Count >= minPoints) return;
            await Task.Delay(20);
        }
        Assert.Fail($"等待窗口 {title}（≥{minPoints} 点）超时。诊断: proto={Pipeline.ProtocolName} " +
                    $"rx={Pipeline.Counters.RxBytes} frames={Pipeline.Counters.FramesParsed} " +
                    $"peer={(Peer is null ? "未打开" : "已打开")}");
    }

    public async Task WaitFramesAsync(long n)
        => await Wait.FramesAsync(Pipeline, n);
}

[Collection("SerialBasis")]
public class ControlApiTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;

    public ControlApiTests(ApiFixture fx) => _fx = fx;

    // ---------- 鉴权 ----------

    [Fact]
    public async Task NoToken_401_AllEndpoints()
    {
        using var noAuth = new HttpClient { BaseAddress = new Uri(_fx.BaseUrl) };
        foreach (var path in new[] { "/v1/health", "/v1/app/info", "/v1/plot/windows" })
        {
            using var resp = await noAuth.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("unauthorized", body);
        }
    }

    [Fact]
    public async Task WrongToken_401()
    {
        using var wrong = new HttpClient { BaseAddress = new Uri(_fx.BaseUrl) };
        wrong.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token");
        using var resp = await wrong.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---------- health / capabilities ----------

    [Fact]
    public async Task Health_ReturnsAppAndVersion()
    {
        var json = await _fx.GetAsync("/v1/health");
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("FreeCom", json.GetProperty("data").GetProperty("app").GetString());
    }

    [Fact]
    public async Task Capabilities_EndpointsToolsProtocolsTransports()
    {
        var json = await _fx.GetAsync("/v1/capabilities");
        var data = json.GetProperty("data");
        Assert.Equal(27, data.GetProperty("endpoints").GetArrayLength());
        Assert.Equal(25, data.GetProperty("tools").GetArrayLength());
        Assert.Equal(5, data.GetProperty("protocols").GetArrayLength());
        var transports = data.GetProperty("transports").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(["serial"], transports); // v0.1.1：唯一传输类型为串口（虚拟串口对亦走串口）
    }

    // ---------- 端口 / 连接（真实串口路径） ----------

    [Fact]
    public async Task ListPorts_IncludesVirtualComPairs()
    {
        var json = await _fx.GetAsync("/v1/serial/ports?transport=serial");
        var ports = json.GetProperty("data").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains(_fx.AppPort, ports);   // com0com 虚拟对在列
        Assert.Contains(_fx.DevicePort, ports);
    }

    [Fact]
    public async Task OpenSerialViaApi_StatusOpen()
    {
        var openBody = $"{{\"transport\":\"serial\",\"params\":{{\"port\":\"{_fx.AppPort}\",\"baud\":\"115200\"}}}}";
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/serial/open", openBody);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Open", json.GetProperty("data").GetProperty("state").GetString());

        var cur = await _fx.GetAsync("/v1/serial/status");
        Assert.Equal("serial", cur.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("Open", cur.GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task CloseViaApi_StateClosed()
    {
        var openBody = $"{{\"transport\":\"serial\",\"params\":{{\"port\":\"{_fx.AppPort}\"}}}}";
        await _fx.SendAsync(HttpMethod.Post, "/v1/serial/open", openBody);
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/serial/close", "{}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Closed", json.GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task OpenUnknownTransport_400()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/serial/open",
            """{"transport":"bluetooth"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_param", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpenSerialMissingPort_FailsGracefully()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/serial/open",
            """{"transport":"serial","params":{"baud":"115200"}}""");
        Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError
            or HttpStatusCode.Conflict, $"实际状态: {status}");
        Assert.False(json.GetProperty("ok").GetBoolean());
    }

    // ---------- 收发（真实串口回显） ----------

    [Fact]
    public async Task DeviceSend_TextLoopback_VisibleInReceive()
    {
        await _fx.OpenDefaultsAsync();
        var (status, _) = await _fx.SendAsync(HttpMethod.Post, "/v1/device/send",
            """{"data":"{loop}1,2\n","format":"text"}""");
        Assert.Equal(HttpStatusCode.OK, status);

        await _fx.WaitFramesAsync(1);
        var receive = await _fx.GetAsync("/v1/device/receive?since=0&limit=100&format=text");
        var items = receive.GetProperty("data").GetProperty("items");
        Assert.Contains(items.EnumerateArray(), i => (i.GetProperty("data").GetString() ?? "").Contains("{loop}1,2"));
    }

    [Fact]
    public async Task DeviceSend_HexFormat()
    {
        await _fx.OpenDefaultsAsync();
        var (status, _) = await _fx.SendAsync(HttpMethod.Post, "/v1/device/send",
            """{"data":"AA 55 01","format":"hex"}""");
        Assert.Equal(HttpStatusCode.OK, status);

        await Wait.RxAsync(_fx.Pipeline, 3);
        var receive = await _fx.GetAsync("/v1/device/receive?since=0&limit=100&format=hex");
        var items = receive.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => (i.GetProperty("data").GetString() ?? "").Replace(" ", "").Contains("AA5501"));
    }

    [Fact]
    public async Task DeviceSend_BadFormat_400()
    {
        var (status, _) = await _fx.SendAsync(HttpMethod.Post, "/v1/device/send",
            """{"data":"x","format":"morse"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task DeviceSend_MissingData_400()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/device/send", """{}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_param", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Receive_Paging_WithSince()
    {
        await _fx.OpenDefaultsAsync();
        long baseline = _fx.Pipeline.Raw.LastSeq;
        long target = _fx.Pipeline.Counters.FramesParsed + 3; // 注入前定基线，避免竞态
        _fx.InjectText("{page}1\n");
        _fx.InjectText("{page}2\n");
        _fx.InjectText("{page}3\n");
        await _fx.WaitFramesAsync(target);
        var p1 = await _fx.GetAsync($"/v1/device/receive?since={baseline}&limit=2&format=text");
        var items1 = p1.GetProperty("data").GetProperty("items");
        Assert.Equal(2, items1.GetArrayLength());
        var next = p1.GetProperty("data").GetProperty("nextSeq").GetInt64();
        Assert.True(next > baseline);
        var p2 = await _fx.GetAsync($"/v1/device/receive?since={next}&limit=10&format=text");
        Assert.True(p2.GetProperty("data").GetProperty("items").GetArrayLength() >= 1);
    }

    // ---------- 协议 ----------

    [Fact]
    public async Task ProtocolGetSet()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Put, "/v1/protocol",
            """{"name":"CSV","options":{"window":"sensor"}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("CSV", json.GetProperty("data").GetProperty("name").GetString());

        var cur = await _fx.GetAsync("/v1/protocol");
        Assert.Equal("CSV", cur.GetProperty("data").GetProperty("name").GetString());

        await _fx.SendAsync(HttpMethod.Put, "/v1/protocol", """{"name":"TEXT"}"""); // 还原
    }

    [Fact]
    public async Task ProtocolSet_Unknown_400()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Put, "/v1/protocol", """{"name":"NOPE"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("未知协议", json.GetProperty("error").GetProperty("message").GetString());
    }

    // ---------- 绘图 ----------

    [Fact]
    public async Task PlotWindows_ListAndData()
    {
        await _fx.OpenDefaultsAsync();
        _fx.InjectText("{plotdemo}1,2,3\n{plotdemo}4,5,6\n");
        await _fx.WaitWindowAsync("plotdemo", minPoints: 2);

        var windows = (await _fx.GetAsync("/v1/plot/windows")).GetProperty("data");
        var win = windows.EnumerateArray().FirstOrDefault(w => w.GetProperty("title").GetString() == "plotdemo");
        Assert.True(win.ValueKind == JsonValueKind.Object, "窗口 plotdemo 未创建");
        Assert.Equal(3, win.GetProperty("curves").GetArrayLength());

        var id = win.GetProperty("id").GetString()!;
        var data = (await _fx.GetAsync($"/v1/plot/windows/{id}/data?maxPoints=100")).GetProperty("data");
        Assert.Equal(3, data.GetProperty("curves").GetArrayLength());
        var c0 = data.GetProperty("curves")[0];
        Assert.Equal([0, 1], c0.GetProperty("xs").EnumerateArray().Select(x => x.GetDouble()).ToArray());
        Assert.Equal([1, 4], c0.GetProperty("ys").EnumerateArray().Select(y => y.GetDouble()).ToArray());
    }

    [Fact]
    public async Task PlotData_ByTitleAlsoWorks()
    {
        await _fx.OpenDefaultsAsync();
        _fx.InjectText("{byTitle}9\n");
        await _fx.WaitWindowAsync("byTitle");
        var data = await _fx.GetAsync("/v1/plot/windows/byTitle/data");
        Assert.Equal("byTitle", data.GetProperty("data").GetProperty("title").GetString());
    }

    [Fact]
    public async Task PlotData_UnknownWindow_404()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Get, "/v1/plot/windows/nope/data", null);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---------- 导出 ----------

    [Fact]
    public async Task ExportCurves_ReturnsPath_FileHasContent()
    {
        await _fx.OpenDefaultsAsync();
        _fx.InjectText("{export}1,2\n{export}3,4\n");
        await _fx.WaitWindowAsync("export", minPoints: 2);
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/export/curves", "{}");
        Assert.Equal(HttpStatusCode.OK, status);
        var path = json.GetProperty("data").GetProperty("path").GetString()!;
        Assert.True(File.Exists(path));
        var csv = File.ReadAllText(path);
        Assert.Contains("window,curve,x,y", csv);
        Assert.StartsWith("export,#1", csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
    }

    // ---------- 应用信息 ----------

    [Fact]
    public async Task AppInfo_ContainsCounters()
    {
        await _fx.OpenDefaultsAsync();
        _fx.InjectText("{info}1\n");
        await _fx.WaitFramesAsync(_fx.Pipeline.Counters.FramesParsed + 1);
        var info = (await _fx.GetAsync("/v1/app/info")).GetProperty("data");
        Assert.Equal("0.1.0", info.GetProperty("version").GetString());
        Assert.True(info.GetProperty("rxBytes").GetInt64() > 0);
        Assert.True(info.GetProperty("windowCount").GetInt32() >= 1);
    }

    // ---------- 坏 JSON ----------

    [Fact]
    public async Task MalformedJson_400()
    {
        var (status, json) = await _fx.SendAsync(HttpMethod.Post, "/v1/serial/open", "{ broken");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_json", json.GetProperty("error").GetProperty("code").GetString());
    }
}
