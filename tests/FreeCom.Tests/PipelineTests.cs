using System.Text;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

/// <summary>管线测试上下文：真实串口路径（com0com 端口对）——App 侧 SerialTransport + 设备侧 SerialPeer。</summary>
public sealed class PipelineCtx : IDisposable
{
    public DataPipeline Pipeline = null!;
    public SerialTransport Transport = null!;
    public SerialPeer Peer = null!;
    public PortPool.PairLease Lease = null!;

    public static PipelineCtx Create(string protocol = "TEXT", Dictionary<string, string>? options = null)
    {
        var lease = PortPool.Lease();
        try
        {
            var ctx = new PipelineCtx
            {
                Lease = lease,
                Pipeline = new DataPipeline(ProtocolRegistry.Create(protocol, options)),
                Transport = new SerialTransport(new TransportOptions
                {
                    Kind = "serial",
                    Parameters = new Dictionary<string, string> { ["port"] = lease.App, ["baud"] = "115200" },
                }),
                Peer = new SerialPeer(lease.Device) { Echo = true },
            };
            ctx.Transport.Open();
            ctx.Pipeline.AttachTransport(ctx.Transport);
            ctx.Peer.Open();
            return ctx;
        }
        catch
        {
            PortPool.Release(lease);
            throw;
        }
    }

    public void Dispose()
    {
        Peer.Dispose();
        Pipeline.Dispose();
        Transport.Dispose();
        PortPool.Release(Lease);
    }
}

[Collection("SerialBasis")]
public class PipelineTests
{
    [Fact]
    public async Task EndToEnd_TextTraffic_CreatesPlotWindow()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        ctx.Peer.SendText("{demo}1,2,3\n{demo}4,5,6\n");
        await Wait.FramesAsync(ctx.Pipeline, 2);

        var windows = ctx.Pipeline.Plots.SnapshotWindows();
        Assert.Single(windows);
        Assert.Equal("demo", windows[0].Title);
        Assert.Equal(3, windows[0].Curves.Count);
        Assert.Equal(6, windows[0].TotalPoints);
        Assert.Equal(24, ctx.Pipeline.Counters.RxBytes);
        Assert.Equal(0, ctx.Pipeline.Counters.ParseErrors);
    }

    [Fact]
    public async Task LoopbackSend_TxAndRxCounted_DisplayMarked()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        await ctx.Pipeline.SendTextAsync("hello");
        await Wait.RxAsync(ctx.Pipeline, 5); // Peer 回显 → RX 5B

        Assert.Equal(5, ctx.Pipeline.Counters.TxBytes);
        Assert.Equal(5, ctx.Pipeline.Counters.RxBytes);
        var text = ctx.Pipeline.Display.RenderText(includeTimestamp: false);
        Assert.StartsWith(">> hello", text);
        Assert.Contains("\n<< hello", text); // RX 回显带方向标记
    }

    [Fact]
    public async Task SendHex_BytesExact()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        await ctx.Pipeline.SendHexAsync("AA 55 0x01");
        await Wait.RxBytesAsync(ctx.Pipeline, [0xAA, 0x55, 0x01]);
        // 线路侧证据：设备端收到 App 侧 TX（用单调计数等待，再取内容断言）
        await Wait.UntilAsync(() => ctx.Peer.ReceivedCount >= 3, what: "设备端收到 TX 字节");
        Assert.Equal([0xAA, 0x55, 0x01], ctx.Peer.DrainReceived());
    }

    [Fact]
    public async Task SendHex_InvalidInput_ThrowsWithoutCounting()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        await Assert.ThrowsAsync<FormatException>(() => ctx.Pipeline.SendHexAsync("AA Z"));
        Assert.Equal(0, ctx.Pipeline.Counters.TxBytes);
    }

    [Fact]
    public async Task SendText_EncodingGbk_AppendNewlineCrlf()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        ctx.Pipeline.DefaultEncoding = "gbk";
        ctx.Pipeline.DefaultNewline = "crlf";
        await ctx.Pipeline.SendTextAsync("你好");
        var expected = System.Text.Encoding.GetEncoding("GBK").GetBytes("你好\r\n");
        await Wait.RxBytesAsync(ctx.Pipeline, expected); // 回显字节与 GBK 编码一致
    }

    [Fact]
    public async Task ProtocolSwitch_MidStream()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        ctx.Peer.SendText("{a}1\n");
        await Wait.FramesAsync(ctx.Pipeline, 1);

        ctx.Pipeline.SetParser(ProtocolRegistry.Create("CSV"));
        ctx.Peer.SendText("5,6\n");
        await Wait.FramesAsync(ctx.Pipeline, 2);

        var titles = ctx.Pipeline.Plots.SnapshotWindows().Select(w => w.Title).ToHashSet();
        Assert.Equal(["a", "csv"], titles);
    }

    [Fact]
    public async Task BadData_NoCrash_ValidFrameStillParses()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        var garbage = new byte[10_000];
        new Random(42).NextBytes(garbage);
        ctx.Peer.Send(garbage);                     // 杂散随机字节：不崩溃；纯文本不属协议错误
        ctx.Peer.SendText("\n{ok}1,2\n");           // 随后正常协议帧照常解析
        await Wait.FramesAsync(ctx.Pipeline, 1);
        Assert.False(ctx.Pipeline.ConsumerTask.IsFaulted);
        Assert.True(ctx.Pipeline.Counters.ParseErrors <= 50); // 随机数据偶发 '{' 行的容差
    }

    [Fact]
    public async Task HighRate_Bursts_DoNotDeadlock()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        var sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                ctx.Peer.SendText($"{{burst}}{i},{i * 2}\n");
                await ctx.Pipeline.SendTextAsync($"cmd{i}", newline: "lf");
            }
        });
        await sendTask;
        await Wait.FramesAsync(ctx.Pipeline, 200, timeoutMs: 15_000);
        Assert.True(ctx.Pipeline.Counters.TxBytes >= 200 * 5);
        Assert.False(ctx.Pipeline.ConsumerTask.IsFaulted);
    }

    [Fact]
    public async Task DetachTransport_StopsRx()
    {
        using var ctx = PipelineCtx.Create("TEXT");
        ctx.Peer.SendText("{a}1\n");
        await Wait.FramesAsync(ctx.Pipeline, 1);
        ctx.Pipeline.DetachTransport();
        var rxBefore = ctx.Pipeline.Counters.RxBytes;
        ctx.Peer.SendText("{b}2\n"); // 已解绑 → 管线收不到
        await Task.Delay(300);
        Assert.Equal(rxBefore, ctx.Pipeline.Counters.RxBytes);
        Assert.Single(ctx.Pipeline.Plots.SnapshotWindows());
    }

    [Fact]
    public async Task MixedStream_NonPlotFrames_SkippedByPlotter_ShownInDisplay()
    {
        // 用户场景：绘图数据中混入非绘图帧（设备日志/杂散文本）
        using var ctx = PipelineCtx.Create("TEXT");
        long target = ctx.Pipeline.Counters.FramesParsed + 3;
        ctx.Peer.SendText(
            "system boot ok\n" +
            "{mix}1,10\n" +
            "run mode=auto\n" +
            "{mix}2,20\n" +
            "{log}voltage=3.3, status=ok\n" +   // 以{开头但非数字 → 计 1 错误
            "{mix}3,30\n" +
            "temp warning!\n");
        await Wait.FramesAsync(ctx.Pipeline, target);

        var win = ctx.Pipeline.Plots.Find("mix")!;
        Assert.Equal(2, win.Curves.Count);
        Assert.Equal(6, win.TotalPoints);          // 仅 3 行绘图帧 × 2 值

        var text = ctx.Pipeline.Display.RenderText(includeTimestamp: false);
        Assert.Contains("system boot ok", text);   // 非绘图帧在接收区完整显示
        Assert.Contains("run mode=auto", text);
        Assert.Contains("{log}voltage=3.3, status=ok", text);
        Assert.Contains("temp warning!", text);
        Assert.Contains("{mix}1,10", text);        // 绘图帧同样完整显示

        Assert.Equal(1, ctx.Pipeline.Counters.ParseErrors); // 纯文本 0 错，坏协议行 1 错
    }

    [Fact]
    public async Task EasyHexPipeline_BinaryTraffic()
    {
        using var ctx = PipelineCtx.Create("EasyHex", new Dictionary<string, string> { ["type"] = "I16", ["order"] = "LE" });
        // 值 300, 500（I16 LE）+ 帧尾 00 80
        ctx.Peer.Send(new byte[] { 0x2C, 0x01, 0xF4, 0x01, 0x00, 0x80 });
        await Wait.FramesAsync(ctx.Pipeline, 1);
        var w = ctx.Pipeline.Plots.SnapshotWindows().Single();
        Assert.Equal("easyhex", w.Title);
        var ys = w.Curves.Select(c => c.Snapshot().Ys).ToList();
        Assert.Equal([300], ys[0]);
        Assert.Equal([500], ys[1]);
    }

    [Fact]
    public async Task ModbusPipeline_AddrWindowSplit()
    {
        using var ctx = PipelineCtx.Create("ModbusRTU");
        ctx.Peer.Send(TestFrames.Modbus(0x01, 300, 500));
        ctx.Peer.Send(TestFrames.Modbus(0x02, 7));
        await Wait.FramesAsync(ctx.Pipeline, 2);
        var titles = ctx.Pipeline.Plots.SnapshotWindows().Select(w => w.Title).OrderBy(t => t).ToList();
        Assert.Equal(["ADDR:01 FUNC:03", "ADDR:02 FUNC:03"], titles);
    }
}

public static class TestFrames
{
    /// <summary>构造 ModbusRTU 03 应答帧（I16 大端 + CRC）。</summary>
    public static byte[] Modbus(byte addr, params short[] values)
    {
        var body = new List<byte> { addr, 0x03, (byte)(values.Length * 2) };
        foreach (var v in values)
        {
            body.Add((byte)(v >> 8));
            body.Add((byte)(v & 0xFF));
        }
        var crc = Crc16.ComputeLittleEndian([.. body]);
        body.AddRange(crc);
        return [.. body];
    }
}
