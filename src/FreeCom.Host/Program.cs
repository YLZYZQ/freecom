using System.IO.Ports;
using FreeCom.Core.ControlApi;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;

namespace FreeCom.Host;

/// <summary>
/// 无头测试宿主（v0.1.1，真实串口路径）：
/// 在一对虚拟串口上同时扮演两端——App 侧（SerialTransport + 管线 + Control API）
/// 与设备侧（对端 COM 口持续发送协议帧），用于端到端验证与无硬件演示。
/// 前置：com0com 端口对已存在（由测试脚本/虚拟串口管理器创建）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string GetStr(string name, string def) =>
            args.Select((a, i) => (a, i)).FirstOrDefault(t => t.a == $"--{name}") is { } found && found.i + 1 < args.Length
                ? args[found.i + 1] : def;
        int GetArg(string name, int def) =>
            args.Select((a, i) => (a, i)).FirstOrDefault(t => t.a == $"--{name}") is { } found && found.i + 1 < args.Length
                && int.TryParse(args[found.i + 1], out var v) ? v : def;

        int port = GetArg("port", 17340);
        int intervalMs = Math.Max(1, GetArg("interval", 50));
        int durationSec = GetArg("duration", 0);
        bool flood = args.Contains("--flood"); // 洪泛模式：无间隔紧环发送（吞吐压力测试）
        string protocol = GetStr("protocol", "TEXT");
        string appPortName = GetStr("app-port", "COM22");     // App 侧打开的端口
        string devPortName = GetStr("dev-port", "COM23");     // 设备侧（对端）端口
        string token = GetStr("token", $"dev-{Guid.NewGuid():N}");

        if (!ProtocolRegistry.Exists(protocol))
        {
            Console.Error.WriteLine($"未知协议 {protocol}，可用: {string.Join(", ", ProtocolRegistry.Names)}");
            return 1;
        }

        using var cts = durationSec > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(durationSec))
            : new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // App 侧：经 Surface 打开串口（Surface 持有连接，API 才能正确关闭/重开）
        var pipeline = new DataPipeline(ProtocolRegistry.Create(protocol));
        var surface = new PipelineSurface(pipeline);
        var appPortParams = new Dictionary<string, string> { ["port"] = appPortName, ["baud"] = "115200" };
        try
        {
            await surface.OpenTransportAsync(new OpenTransportRequest("serial", appPortParams));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"App 侧串口 {appPortName} 打开失败（端口对是否存在/被占用?）: {ex.Message}");
            return 1;
        }

        using var server = new ControlApiServer(surface, new ControlApiOptions { Port = port, Token = token });
        try
        {
            await server.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"API 启动失败（端口 {port} 可能被占用）: {ex.Message}");
            return 1;
        }

        // 设备侧：对端串口持续发协议帧（--flood 为吞吐压测模式）
        var device = new SerialPort(devPortName, 115200) { WriteTimeout = 5000 };
        device.Open();
        long frameIndex = 0;
        var traffic = Task.Run(async () =>
        {
            try
            {
                if (flood)
                {
                    // 预生成 500 帧批次，紧环批量写入（最大化线路压力）
                    var batch = new byte[500][];
                    for (int i = 0; i < batch.Length; i++) batch[i] = TrafficFrames.FrameFor(protocol, i + 1);
                    while (!cts.IsCancellationRequested)
                    {
                        for (int i = 0; i < batch.Length; i++)
                        {
                            device.Write(batch[i], 0, batch[i].Length);
                            frameIndex += 1;
                            if ((frameIndex & 0x3FF) == 0) await Task.Yield(); // 周期让出，避免饿死
                        }
                    }
                }
                else
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var frame = TrafficFrames.FrameFor(protocol, ++frameIndex);
                        device.Write(frame, 0, frame.Length);
                        await Task.Delay(intervalMs, cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"设备侧发送异常: {ex.Message}"); }
        });

        Console.WriteLine($"FreeCom 无头测试宿主已启动（协议 {protocol}，真实串口路径{(flood ? "，FLOOD 洪泛模式" : "")}）");
        Console.WriteLine($"  App 侧串口 : {appPortName}    设备侧串口: {devPortName}");
        Console.WriteLine($"  Control API: {server.BaseUrl}  (Bearer {token})");
        Console.WriteLine($"  流量        : 每 {intervalMs}ms 一帧, Ctrl+C 退出" + (durationSec > 0 ? $", {durationSec}s 后自动退出" : ""));

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        try { await traffic; } catch { }
        await server.StopAsync();
        try { device.Close(); } catch { }
        Console.WriteLine($"退出统计: 设备侧发送={frameIndex}帧 | App侧 RX={pipeline.Counters.RxBytes}B " +
                          $"帧={pipeline.Counters.FramesParsed} 错误={pipeline.Counters.ParseErrors} " +
                          $"窗口={pipeline.Plots.SnapshotWindows().Count} 曲线点=" +
                          $"{pipeline.Plots.SnapshotWindows().Sum(w => w.TotalPoints)}");
        return 0;
    }
}
