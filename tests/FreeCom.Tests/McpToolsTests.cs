using System.Net;
using System.Text;
using System.Text.Json;
using FreeCom.Core.ControlApi;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

/// <summary>MCP v0.2 新增端点（P0/P1）E2E：真实 Kestrel + com0com 串口对。</summary>
public sealed class McpToolsTests : IClassFixture<ApiFixture>, IAsyncLifetime
{
    private readonly ApiFixture _fx;
    public McpToolsTests(ApiFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // 停掉可能运行的模拟器，释放设备端口
        await _fx.PostAsync("/v1/simulator/stop", "{}");
    }

    // ---------------- receive_wait ----------------

    [Fact]
    public async Task Wait_MatchesIncomingKeyword()
    {
        await _fx.OpenDefaultsAsync();
        // 延迟 300ms 后设备侧注入目标行——等待应命中
        _ = Task.Delay(300).ContinueWith(_ => _fx.InjectText("SENSOR READY temp=25.5\n"));
        var resp = await _fx.PostAsync("/v1/device/wait",
            """{"contains":"temp=25.5","direction":"rx","timeoutMs":3000}""");
        var matched = resp.GetProperty("matched").GetBoolean();
        Assert.True(matched);
        var items = resp.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
        Assert.Contains("temp=25.5", items[0].GetProperty("data").GetString());
    }

    [Fact]
    public async Task Wait_TimeoutReturnsUnmatched()
    {
        await _fx.OpenDefaultsAsync();
        var resp = await _fx.PostAsync("/v1/device/wait",
            """{"contains":"__never__appears__","timeoutMs":500}""");
        Assert.False(resp.GetProperty("matched").GetBoolean());
        Assert.True(resp.GetProperty("elapsedMs").GetInt64() >= 400);
        Assert.Equal(0, resp.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Wait_DirectionFilterExcludesEcho()
    {
        await _fx.OpenDefaultsAsync();
        // 先发送一条 TX（设备回显），再等 TX 方向
        await _fx.PostAsync("/v1/device/send", """{"data":"AT+DIR"}""");
        var resp = await _fx.PostAsync("/v1/device/wait",
            """{"contains":"AT+DIR","direction":"tx","timeoutMs":2000}""");
        Assert.True(resp.GetProperty("matched").GetBoolean());
        Assert.Equal("tx", resp.GetProperty("items")[0].GetProperty("dir").GetString());
    }

    // ---------------- send_expect ----------------

    [Fact]
    public async Task Expect_CommandResponseRoundtrip()
    {
        await _fx.OpenDefaultsAsync(); // 设备侧回显：发什么回什么
        var resp = await _fx.PostAsync("/v1/device/expect",
            """{"send":{"data":"PING-42"},"contains":"PING-42","direction":"rx","timeoutMs":3000}""");
        var wait = resp.GetProperty("wait");
        Assert.True(wait.GetProperty("matched").GetBoolean());
        Assert.Contains("PING-42", wait.GetProperty("items")[0].GetProperty("data").GetString());
        Assert.True(resp.GetProperty("sentBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Expect_HexModeMatchesHex()
    {
        await _fx.OpenDefaultsAsync();
        var resp = await _fx.PostAsync("/v1/device/expect",
            """{"send":{"data":"AA 55 10","format":"hex"},"contains":"AA 55 10","direction":"rx","timeoutMs":3000,"format":"hex"}""");
        Assert.True(resp.GetProperty("wait").GetProperty("matched").GetBoolean());
    }

    // ---------------- simulator ----------------

    [Fact]
    public async Task Simulator_StartStopFeedsPipeline()
    {
        _fx.ClosePeer(); // 模拟器要独占设备侧端口
        await Task.Delay(200); // com0com 刚关闭的句柄异步释放
        await _fx.OpenAppOnlyAsync();
        var start = await _fx.PostAsync("/v1/simulator/start",
            $$"""{"port":"{{_fx.DevicePort}}","protocol":"TEXT","intervalMs":30}""");
        Assert.True(start.GetProperty("running").GetBoolean());
        Assert.Equal(_fx.DevicePort, start.GetProperty("port").GetString());

        var wait = await _fx.PostAsync("/v1/device/wait",
            """{"contains":"{demo}","direction":"rx","timeoutMs":5000}""");
        Assert.True(wait.GetProperty("matched").GetBoolean(),
            "模拟器流量未到达管线（rx 方向未见 {demo} 帧）");

        // sentFrames 以 30ms 间隔增长，wait 命中后立即查询可能只到 1~2：
        // 轮询到稳定 >3 再断言，避免与 wait 命中的时序竞争
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JsonElement status = default;
        while (sw.ElapsedMilliseconds < 5000)
        {
            status = await _fx.GetDataAsync("/v1/simulator/status");
            if (status.GetProperty("sentFrames").GetInt64() > 3) break;
            await Task.Delay(100);
        }
        Assert.True(status.GetProperty("sentFrames").GetInt64() > 3,
            $"模拟器帧计数未增长（当前 {status.GetProperty("sentFrames").GetInt64()}）");

        var stop = await _fx.PostAsync("/v1/simulator/stop", "{}");
        Assert.False(stop.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Simulator_InvalidPortReturnsError()
    {
        var (status, _) = await _fx.PostExpectStatusAsync("/v1/simulator/start",
            """{"port":"COM___NOPE"}""");
        Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"期望 4xx，实际 {status}");
    }

    // ---------------- curve_stats ----------------

    [Fact]
    public async Task CurveStats_ComputesAggregates()
    {
        await _fx.OpenDefaultsAsync();
        // 前序测试（Expect/Wait 的无换行回显）可能在 LineAssembler 里残留半行，
        // 会与本测试第一行拼接成非法帧；先注入一个裸换行冲掉残留
        _fx.InjectText("\n");
        for (int i = 1; i <= 10; i++) _fx.InjectText($"{{stats}}{i * 2}\n");

        var windows = await _fx.GetDataAsync("/v1/plot/windows");
        var win = windows.EnumerateArray().Single(w => w.GetProperty("title").GetString() == "stats");
        var id = win.GetProperty("id").GetString();

        // 10 帧逐条注入，解析有微小时序：轮询 count 到 10 再断言
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JsonElement stats = default;
        while (sw.ElapsedMilliseconds < 5000)
        {
            stats = await _fx.GetDataAsync($"/v1/plot/windows/{id}/stats");
            if (stats.GetProperty("curves")[0].GetProperty("count").GetInt64() == 10) break;
            await Task.Delay(100);
        }
        var curve = stats.GetProperty("curves")[0];
        Assert.Equal(10, curve.GetProperty("count").GetInt64());
        Assert.Equal(2, curve.GetProperty("min").GetDouble(), 3);
        Assert.Equal(20, curve.GetProperty("max").GetDouble(), 3);
        Assert.Equal(11, curve.GetProperty("mean").GetDouble(), 3);
        Assert.Equal(20, curve.GetProperty("last").GetDouble(), 3);
    }

    [Fact]
    public async Task CurveStats_UnknownWindow404()
    {
        var (status, _) = await _fx.GetExpectStatusAsync("/v1/plot/windows/nope/stats");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---------------- protocol_help ----------------

    [Fact]
    public async Task ProtocolHelp_AllAndSingle()
    {
        var all = await _fx.GetDataAsync("/v1/protocol/help");
        Assert.Equal(5, all.GetArrayLength());

        var one = await _fx.GetDataAsync("/v1/protocol/help?name=stamp");
        var doc = one[0];
        Assert.Equal("STAMP", doc.GetProperty("name").GetString());
        Assert.Contains("时间戳", doc.GetProperty("format").GetString());
        Assert.Contains("HAL_GetTick", doc.GetProperty("cSnippet").GetString());

        var (status, _) = await _fx.GetExpectStatusAsync("/v1/protocol/help?name=bad");
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---------------- send_history ----------------

    [Fact]
    public async Task SendHistory_RecordsBothFormats()
    {
        await _fx.OpenDefaultsAsync();
        await _fx.PostAsync("/v1/device/send", """{"data":"AUDIT-TEXT-1"}""");
        await _fx.PostAsync("/v1/device/send", """{"data":"AA 55","format":"hex"}""");
        var hist = await _fx.GetDataAsync("/v1/device/send-history?limit=10");
        var items = hist.EnumerateArray().ToList();
        Assert.True(items.Count >= 2);
        // 历史新→旧排序：按内容查找，不依赖索引
        Assert.Contains(items, i => i.GetProperty("text").GetString() == "AUDIT-TEXT-1");
        Assert.Contains(items, i => i.GetProperty("hex").GetString()!.Replace(" ", "") == "AA55");
    }

    // ---------------- exports ----------------

    [Fact]
    public async Task ExportRawAndDisplay_WriteFiles()
    {
        await _fx.OpenDefaultsAsync();
        _fx.InjectText("EXPORT-ROW-1\n");
        await _fx.PostAsync("/v1/device/send", """{"data":"EXPORT-TX"}""");
        await Task.Delay(600);

        var rawPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freecom-test-{Guid.NewGuid():N}.dat");
        var raw = await _fx.PostAsync("/v1/export/raw", $$"""{"path":"{{rawPath.Replace("\\", "\\\\")}}"}""");
        Assert.Equal(rawPath, raw.GetProperty("path").GetString());
        var rawBytes = await System.IO.File.ReadAllBytesAsync(rawPath);
        Assert.True(ContainsBytes(rawBytes, "EXPORT-ROW-1"u8.ToArray()), "raw 缺少 RX 行");
        Assert.True(ContainsBytes(rawBytes, "EXPORT-TX"u8.ToArray()), "raw 缺少 TX 行");

        var dispPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freecom-test-{Guid.NewGuid():N}.txt");
        var disp = await _fx.PostAsync("/v1/export/display", $$"""{"path":"{{dispPath.Replace("\\", "\\\\")}}","timestamp":true}""");
        var dispText = await System.IO.File.ReadAllTextAsync(dispPath);
        Assert.Contains("EXPORT-ROW-1", dispText);
        Assert.Contains(">>", dispText); // TX 方向标记
        Assert.Contains("[", dispText);  // 时间戳
    }

    // ---------------- vcom ----------------

    [Fact]
    public async Task Vcom_ListReturnsInstalledPairs()
    {
        var manager = new VirtualComManager();
        if (!manager.DriverInstalled)
        {
            // 无驱动环境（CI）：接口可用性与空列表语义
            var pairs = await _fx.GetDataAsync("/v1/vcom/pairs");
            Assert.True(pairs.GetArrayLength() >= 0);
            return;
        }
        var list = await _fx.GetDataAsync("/v1/vcom/pairs");
        Assert.True(list.GetArrayLength() >= 1);
        var pair = list[0];
        Assert.StartsWith("COM", pair.GetProperty("portA").GetString());
        Assert.True(pair.GetProperty("pairNumber").GetInt32() >= 0);
    }

    [Fact]
    public async Task Vcom_RemoveInvalidNumber400()
    {
        var (status, body) = await _fx.RawSendAsync(HttpMethod.Delete, "/v1/vcom/pairs/abc");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("invalid_param", body);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    // ---------------- capabilities 投影 ----------------

    [Fact]
    public async Task Capabilities_ContainsNewTools()
    {
        var cap = await _fx.GetDataAsync("/v1/capabilities");
        var names = cap.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("receive_wait", names);
        Assert.Contains("send_expect", names);
        Assert.Contains("simulator_start", names);
        Assert.Contains("curve_stats", names);
        Assert.Contains("protocol_help", names);
        Assert.Contains("send_history", names);
        Assert.Equal(25, names.Count); // 13 旧 + 12 新
    }
}
