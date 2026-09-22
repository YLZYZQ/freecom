using System.IO.Ports;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

/// <summary>
/// SerialTransport 专项套件（v0.1.1 新增）：真实串口路径上的传输层行为验证。
/// 此前该类代码从未被自动化测试执行（旧基座为进程内回环）。
/// </summary>
[Collection("SerialBasis")]
public class SerialTransportTests
{
    private static SerialTransport OpenTransport(string port, int baud = 115200, int rxBuffer = 1 << 16)
    {
        var transport = new SerialTransport(new TransportOptions
        {
            Kind = "serial",
            Parameters = new Dictionary<string, string>
            {
                ["port"] = port,
                ["baud"] = baud.ToString(),
                ["rxBuffer"] = rxBuffer.ToString(),
            },
        });
        transport.Open();
        return transport;
    }

    [Fact]
    public void OpenClose_StateTransitions()
    {
        var lease = PortPool.Lease();
        try
        {
            var t = OpenTransport(lease.App);
            Assert.Equal(TransportState.Open, t.State);
            Assert.Contains(lease.App, t.Description);
            t.Close();
            Assert.Equal(TransportState.Closed, t.State);
            t.Close(); // 幂等
        }
        finally { PortPool.Release(lease); }
    }

    [Fact]
    public void PortOccupiedByPeer_OpenThrows()
    {
        var lease = PortPool.Lease();
        try
        {
            using var blocker = new SerialPort(lease.App, 115200);
            blocker.Open(); // 占住 App 侧端口
            Assert.ThrowsAny<Exception>(() => OpenTransport(lease.App)); // 必须失败而非挂死
        }
        finally { PortPool.Release(lease); }
    }

    [Fact]
    public void MissingPort_ThrowsWithClearMessage()
        => Assert.ThrowsAny<Exception>(() => OpenTransport("COM9999"));

    [Fact]
    public async Task Write_ReachesPeerOverWire()
    {
        var lease = PortPool.Lease();
        try
        {
            using var transport = OpenTransport(lease.App);
            using var peer = new SerialPeer(lease.Device) { Echo = false };
            peer.Open();

            var bytes = System.Text.Encoding.UTF8.GetBytes("ping-serial");
            await transport.WriteAsync(bytes);

            await Wait.UntilAsync(() => peer.ReceivedCount >= bytes.Length, what: "对端收到写入字节");
            var got = peer.DrainReceived();
            Assert.Equal(bytes, got);
            transport.Close();
        }
        finally { PortPool.Release(lease); }
    }

    [Fact]
    public async Task Receive_ChunkedFragments_ArriveComplete()
    {
        var lease = PortPool.Lease();
        try
        {
            using var transport = OpenTransport(lease.App);
            var received = new System.Collections.Concurrent.ConcurrentQueue<byte>();
            transport.DataReceived += data =>
            {
                foreach (var b in data.ToArray()) received.Enqueue(b);
            };

            using var peer = new SerialPeer(lease.Device);
            peer.Open();
            var full = " fragmented-line-0123456789 \n"u8.ToArray();
            // 拆三段发送（真实串口任意分片语义）
            peer.Send(full.AsSpan(0, 8));
            await Task.Delay(60);
            peer.Send(full.AsSpan(8, 9));
            await Task.Delay(60);
            peer.Send(full.AsSpan(17));

            await Wait.UntilAsync(() => received.Count >= full.Length, what: "分片数据完整到达");
        }
        finally { PortPool.Release(lease); }
    }

    [Fact]
    public async Task Throughput_HighRateFrames_OverRealSerial()
    {
        var lease = PortPool.Lease();
        try
        {
            using var pipeline = new DataPipeline(ProtocolRegistry.Create("TEXT"));
            using var transport = OpenTransport(lease.App, rxBuffer: 1 << 18);
            pipeline.AttachTransport(transport);
            using var peer = new SerialPeer(lease.Device);
            peer.Open();

            const int frames = 500;
            for (int i = 0; i < frames; i++)
                peer.SendText($"{{tp}}{i},{i * 2},{i % 7}\n");

            await Wait.FramesAsync(pipeline, frames, timeoutMs: 20_000);
            Assert.Equal(0, pipeline.Counters.ParseErrors);
        }
        finally { PortPool.Release(lease); }
    }

    [Fact]
    public async Task PeerCloseWhileOpen_TransportSurvivesThenRecoverable()
    {
        // 设备侧中途关闭再重开：App 侧传输不崩溃，数据恢复到达
        var lease = PortPool.Lease();
        try
        {
            using var pipeline = new DataPipeline(ProtocolRegistry.Create("TEXT"));
            using var transport = OpenTransport(lease.App);
            pipeline.AttachTransport(transport);

            var peer = new SerialPeer(lease.Device);
            peer.Open();
            peer.SendText("{s1}1\n");
            await Wait.FramesAsync(pipeline, 1);
            peer.Dispose(); // 设备侧消失

            using var peer2 = new SerialPeer(lease.Device);
            peer2.Open();
            peer2.SendText("{s2}2\n");
            await Wait.FramesAsync(pipeline, 2, timeoutMs: 10_000);
        }
        finally { PortPool.Release(lease); }
    }
}
