using System.Collections.Concurrent;
using System.IO.Ports;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

/// <summary>
/// 测试端口对池：基于 com0com 预建的真实 COM 对（默认 COM20↔21 / 22↔23 / 24↔25，
/// 可用环境变量 FREECOM_TEST_PAIRS="A:B,C:D" 覆盖）。所有串口测试经 [Collection("SerialBasis")]
/// 串行执行并从这里租用端口对。
/// </summary>
public static class PortPool
{
    public sealed record PairLease(string App, string Device);

    private static readonly object s_lock = new();
    private static readonly Queue<PairLease> s_free;

    static PortPool()
    {
        var spec = Environment.GetEnvironmentVariable("FREECOM_TEST_PAIRS")
            ?? "COM20:COM21,COM22:COM23,COM24:COM25";
        var pairs = spec.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(':'))
            .Where(parts => parts.Length == 2)
            .Select(parts => new PairLease(parts[0].Trim(), parts[1].Trim()))
            .ToList();

        var available = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usable = pairs.Where(p => available.Contains(p.App) && available.Contains(p.Device)).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException(
                $"未发现可用的测试端口对（期望 {spec}，实际端口: {string.Join(",", available)}）。" +
                "请先安装 com0com 并创建测试端口对（见 README/虚拟串口管理器）。");
        s_free = new Queue<PairLease>(usable);
    }

    public static PairLease Lease()
    {
        lock (s_lock)
        {
            if (s_free.Count == 0)
                throw new InvalidOperationException("测试端口对已全部租出（同类测试应串行执行）");
            return s_free.Dequeue();
        }
    }

    public static void Release(PairLease lease)
    {
        lock (s_lock) s_free.Enqueue(lease);
    }
}

/// <summary>
/// 真实"设备侧"串口：打开 COM 对的另一端。
/// - Echo=true：收到即回发（回环语义，供收发回显类测试）
/// - 收到的字节进入队列供断言（验证 App 侧 TX 真正到达线路）
/// </summary>
public sealed class SerialPeer : IDisposable
{
    private readonly SerialPort _port;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private readonly ConcurrentQueue<byte> _received = new();

    public bool Echo { get; set; }
    public long ReceivedCount { get; private set; }
    public string PortName => _port.PortName;

    public SerialPeer(string portName)
    {
        _port = new SerialPort(portName, 115200) { ReadTimeout = Timeout.Infinite, WriteTimeout = 3000 };
    }

    public void Open()
    {
        // com0com may release a just-closed overlapped handle asynchronously.
        // Match the production transport's bounded reopen retry, then fail normally.
        for (int attempt = 0; ; attempt++)
        {
            try { _port.Open(); break; }
            catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(100); }
        }
        var buf = new byte[8192];
        _readLoop = Task.Run(async () =>
        {
            try
            {
                var stream = _port.BaseStream;
                while (!_cts.IsCancellationRequested && _port.IsOpen)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++) _received.Enqueue(buf[i]);
                    ReceivedCount += n;
                    if (Echo) await stream.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        });
    }

    public void Send(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        _port.BaseStream.Write(copy);
        _port.BaseStream.Flush();
    }

    public void SendText(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>取出并清空累计收到的字节（App 侧 TX 的线路侧证据）。</summary>
    public byte[] DrainReceived()
    {
        var list = new List<byte>();
        while (_received.TryDequeue(out var b)) list.Add(b);
        return list.ToArray();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { if (_readLoop is not null) _readLoop.Wait(500); } catch { }
        try { if (_port.IsOpen) _port.Close(); _port.Dispose(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>等待工具（串口 IO 有真实延迟，全部轮询等待）。</summary>
public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(20);
        Assert.True(condition(), $"等待超时: {what ?? "条件"}");
    }

    public static Task FramesAsync(DataPipeline pipeline, long minFrames, int timeoutMs = 8000)
        => UntilAsync(() => pipeline.Counters.FramesParsed >= minFrames, timeoutMs,
            $"已解析 {pipeline.Counters.FramesParsed} 帧（期望 ≥{minFrames}）");

    public static Task RxAsync(DataPipeline pipeline, long minBytes, int timeoutMs = 8000)
        => UntilAsync(() => pipeline.Counters.RxBytes >= minBytes, timeoutMs,
            $"RX {pipeline.Counters.RxBytes}B（期望 ≥{minBytes}B）");

    /// <summary>等待 App 侧 RX 拼接字节与期望一致（容忍串口分块）。</summary>
    public static Task RxBytesAsync(DataPipeline pipeline, byte[] expected, int timeoutMs = 8000)
        => UntilAsync(() => CollectRx(pipeline).Take(expected.Length).SequenceEqual(expected), timeoutMs,
            $"RX 字节前缀不匹配（期望 {FreeCom.Core.HexParse.ToHex(expected)}，实际 {FreeCom.Core.HexParse.ToHex(CollectRx(pipeline))}）");

    private static byte[] CollectRx(DataPipeline pipeline)
        => [.. pipeline.Raw.Snapshot(dir: DataDirection.Rx).SelectMany(e => e.Bytes)];
}

/// <summary>串口基座集合：加入的测试类串行执行（共享物理资源：COM 端口对）。</summary>
[CollectionDefinition("SerialBasis")]
public sealed class SerialBasisCollection { }
