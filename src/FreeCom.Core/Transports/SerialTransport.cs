using System.IO.Ports;

namespace FreeCom.Core.Transports;

/// <summary>串口传输实现：BaseStream 异步读循环 + 自管缓冲（技术方案 R1 缓解策略）。</summary>
public sealed class SerialTransport : ITransport
{
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public string Kind => "serial";
    public TransportState State { get; private set; } = TransportState.Closed;
    public string Description { get; private set; } = "";
    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    private readonly TransportOptions _options;

    public SerialTransport(TransportOptions options) => _options = options;

    public void Open()
    {
        if (State == TransportState.Open) return;
        var name = _options.Get("port") ?? throw new InvalidOperationException("未指定串口号");
        var baud = _options.GetInt("baud", 115200);
        var dataBits = _options.GetInt("dataBits", 8);
        var parity = _options.Get("parity")?.ToLowerInvariant() switch
        {
            "odd" => Parity.Odd,
            "even" => Parity.Even,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.None,
        };
        var stopBits = _options.Get("stop")?.ToLowerInvariant() switch
        {
            "2" or "two" => StopBits.Two,
            "1.5" or "onepointfive" => StopBits.OnePointFive,
            _ => StopBits.One,
        };
        var handshake = _options.Get("flow")?.ToLowerInvariant() switch
        {
            "xon" or "xonorxoff" or "software" => Handshake.XOnXOff,
            "rtscts" or "hardware" => Handshake.RequestToSend,
            "both" => Handshake.RequestToSendXOnXOff,
            _ => Handshake.None,
        };

        var port = new SerialPort(name, baud, parity, dataBits, stopBits)
        {
            Handshake = handshake,
            DtrEnable = _options.GetBool("dtr", false),
            RtsEnable = _options.GetBool("rts", false),
            ReadBufferSize = Math.Clamp(_options.GetInt("rxBuffer", 1 << 16), 4096, 1 << 24),
            WriteBufferSize = 1 << 16,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 5000,
        };

        // 端口刚被关闭（含拔插/快速重连）时句柄可能延迟释放：短暂重试
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                port.Open();
                lastError = null;
                break;
            }
            catch (UnauthorizedAccessException ex) when (attempt < 4)
            {
                lastError = ex;
                Thread.Sleep(150);
            }
            catch (IOException ex) when (attempt < 4)
            {
                lastError = ex;
                Thread.Sleep(150);
            }
        }
        if (lastError != null)
            throw new InvalidOperationException($"串口 {name} 打开失败（可能被占用或刚被释放）: {lastError.Message}");
        _port = port;
        Description = $"{name} @ {baud}";
        State = TransportState.Open;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var buf = new byte[64 * 1024];
        _readLoop = Task.Run(async () =>
        {
            try
            {
                var stream = port.BaseStream;
                while (!token.IsCancellationRequested && port.IsOpen)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), token).ConfigureAwait(false);
                    if (n <= 0) break;
                    var copy = new byte[n];
                    Buffer.BlockCopy(buf, 0, copy, 0, n);
                    DataReceived?.Invoke(copy);
                }
            }
            catch (OperationCanceledException) { /* 正常关闭 */ }
            catch (ObjectDisposedException) { /* 端口已关闭 */ }
            catch (Exception)
            {
                State = TransportState.Error;
            }
        }, token);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开");
        await port.BaseStream.WriteAsync(data, ct).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Close()
    {
        _cts?.Cancel();
        try { _readLoop?.Wait(300); } catch { /* 忽略收尾异常 */ }
        // 先关端口强制挂起的异步读退出，再等读循环收尾，句柄确定释放
        var port = _port;
        _port = null;
        if (port != null)
        {
            try { if (port.IsOpen) port.Close(); } catch { /* 拔线等场景 */ }
            try { port.Dispose(); } catch { } // 独立 try：即使 Close 抛异常也必须释放句柄
        }
        try { _readLoop?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
        _port = null;
        State = TransportState.Closed;
        Description = "";
    }

    public void Dispose() => Close();
}

/// <summary>串口工厂：枚举 COM 口；busy 探测可选（默认关闭以保证扫描速度）。</summary>
public sealed class SerialTransportFactory : ITransportFactory
{
    public string Kind => "serial";

    public IEnumerable<PortDescriptor> ListPorts(bool probeBusy = false)
    {
        var names = SerialPort.GetPortNames().Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var status = PortStatus.Idle;
            if (probeBusy)
            {
                try
                {
                    using var probe = new SerialPort(name);
                    probe.Open();
                    probe.Close();
                }
                catch
                {
                    status = PortStatus.Busy;
                }
            }
            yield return new PortDescriptor(name, "", status, Kind);
        }
    }

    public ITransport Create(TransportOptions options) => new SerialTransport(options);
}
