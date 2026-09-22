using FreeCom.Mcp;

var url = Environment.GetEnvironmentVariable("FREECOM_URL") ?? "http://127.0.0.1:17340";
var token = Environment.GetEnvironmentVariable("FREECOM_TOKEN");

if (args.Length >= 2 && args[0] == "--url")
{
    url = args[1];
    args = args[2..];
}
if (args.Length >= 2 && args[0] == "--token")
{
    token = args[1];
    args = args[2..];
}

if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("缺少 FREECOM_TOKEN 环境变量（FreeCom 设置 → MCP 服务 中的 Device ID）。");
    Console.Error.WriteLine($"目标: {url}");
    return 2;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var api = new ApiClient(url, token);
var server = new McpServer(Console.In, Console.Out, api);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await server.RunAsync(cts.Token);
return 0;
