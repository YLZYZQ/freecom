using System.Text;
using System.Text.Json;
using FreeCom.Mcp;
using Xunit;
using static FreeCom.Tests.McpTestScript;

namespace FreeCom.Tests;

/// <summary>MCP stdio 桥测试：StringReader 剧本 → 真实 HTTP → 断言 JSON-RPC 响应行。</summary>
[Collection("SerialBasis")]
public class McpServerTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;

    public McpServerTests(ApiFixture fx) => _fx = fx;

    private async Task<List<JsonElement>> RunScriptAsync(params string[] requests)
    {
        var script = string.Join("\n", requests) + "\n";
        var output = new StringWriter();
        var api = new ApiClient(_fx.BaseUrl, _fx.Token);
        var server = new McpServer(new StringReader(script), output, api);
        await server.RunAsync();
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement.Clone();
        }).ToList();
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolInfo()
    {
        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        var r = Assert.Single(responses);
        Assert.Equal(1, r.GetProperty("id").GetInt32());
        Assert.Equal(McpServer.ProtocolVersion, r.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("freecom-mcp", r.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ToolsList_FromCapabilities()
    {
        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = responses.Single().GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        Assert.Subset(
            names,
            new HashSet<string> { "serial_list", "serial_open", "device_send", "plot_data", "curve_export" });
        // inputSchema 已是结构化 JSON
        Assert.Equal("object", tools[0].GetProperty("inputSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task DiagConnectivity_HealthOk()
    {
        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"diag_connectivity","_arguments":{}}}""");
        var result = responses.Single().GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"ok\":true", text);
        Assert.Contains("FreeCom", text);
    }

    [Fact]
    public async Task FullDebugFlow_OpenSendReadPlotExport()
    {
        await _fx.OpenDefaultsAsync();
        _fx.InjectText("{mcpflow}1,2\n");
        await _fx.WaitWindowAsync("mcpflow");

        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"serial_status","_arguments":{}}}""",
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"device_send","_arguments":{"data":"{echo}5\n","format":"text"}}}""",
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"plot_windows","_arguments":{}}}""",
            """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"curve_export","_arguments":{}}}""");
        Assert.Equal(4, responses.Count);
        Assert.All(responses, r => Assert.False(r.GetProperty("result").GetProperty("isError").GetBoolean()));

        var plotText = responses[2].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("mcpflow", plotText);

        var exportText = responses[3].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains(".csv", exportText);
    }

    [Fact]
    public async Task Notifications_ProduceNoResponse()
    {
        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Empty(responses);
    }

    [Fact]
    public async Task UnknownMethod_Minus32601()
    {
        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","id":20,"method":"resources/list"}""");
        var err = responses.Single().GetProperty("error");
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task InvalidJsonLine_Minus32700_NoCrash()
    {
        var responses = await RunScriptAsync(
            "{ this is not json",
            """{"jsonrpc":"2.0","id":21,"method":"ping"}""");
        Assert.Equal(2, responses.Count);
        Assert.Equal(-32700, responses[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(responses[1].TryGetProperty("result", out _));
    }

    [Fact]
    public async Task UnknownTool_IsErrorContent()
    {
        var responses = await RunScriptAsync(
            """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"nonexistent_tool","_arguments":{}}}""");
        var result = responses.Single().GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task WrongToken_ToolReturnsDiagnosticHint()
    {
        var script = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"app_info","_arguments":{}}}""" + "\n";
        var output = new StringWriter();
        var api = new ApiClient(_fx.BaseUrl, "wrong-token");
        var server = new McpServer(new StringReader(script), output, api);
        await server.RunAsync();
        var line = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        using var doc = JsonDocument.Parse(line);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("401", result.GetProperty("content")[0].GetProperty("text").GetString());
    }
}

public static class McpTestScript { }
