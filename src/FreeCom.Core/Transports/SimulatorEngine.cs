using System.IO.Ports;

namespace FreeCom.Core.Transports;

/// <summary>模拟器状态快照（API/UI 共用）。</summary>
public sealed record SimulatorStatus(
    bool Running,
    string? Port,
    string? Protocol,
    int IntervalMs,
    long SentFrames,
    long SentBytes,
    string? LastError);

/// <summary>
/// 设备模拟器引擎（MCP/Control API 用，与 UI 面板独立）：
/// 以"下位机"身份占用端口对的另一端，按间隔发送五协议示例帧。
/// 线程安全；Start 幂等（重复 Start 先停旧配置）。
/// </summary>
public sealed class SimulatorEngine : IDisposable
{
    private readonly object _lock = new();
    private SerialPort? _port;
    private Timer? _timer;
    private long _frameIndex;
    private long _sentFrames;
    private long _sentBytes;
    private string? _lastError;

    public SimulatorStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new SimulatorStatus(
                    _timer is not null,
                    _port?.PortName,
                    _protocol, _intervalMs,
                    _sentFrames, _sentBytes, _lastError);
            }
        }
    }

    private string _protocol = "TEXT";
    private int _intervalMs = 100;

    /// <summary>打开端口并开始按间隔发送协议帧。</summary>
    public SimulatorStatus Start(string portName, string protocol, int intervalMs)
    {
        if (string.IsNullOrWhiteSpace(portName)) throw new ArgumentException("必须指定端口（虚拟串口对的另一端）");
        protocol = string.IsNullOrWhiteSpace(protocol) ? "TEXT" : protocol.ToUpperInvariant();
        intervalMs = Math.Clamp(intervalMs <= 0 ? 100 : intervalMs, 10, 60_000);

        lock (_lock)
        {
            StopLocked();
            var port = new SerialPort(portName, 115200) { WriteTimeout = 3000 };
            try
            {
                // com0com 对刚关闭的对端句柄可能异步释放：与生产传输一致，做有界重试打开
                for (int attempt = 0; ; attempt++)
                {
                    try { port.Open(); break; }
                    catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(100); }
                }
            }
            catch (Exception ex)
            {
                port.Dispose();
                _lastError = $"打开 {portName} 失败：{ex.Message}";
                throw new InvalidOperationException(_lastError, ex);
            }
            _port = port;
            _protocol = protocol;
            _intervalMs = intervalMs;
            _frameIndex = 0;
            _sentFrames = 0;
            _sentBytes = 0;
            _lastError = null;
            _timer = new Timer(_ =>
            {
                try
                {
                    var frame = Protocols.TrafficFrames.FrameFor(protocol, Interlocked.Increment(ref _frameIndex));
                    lock (_lock) port.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref _sentFrames);
                    Interlocked.Add(ref _sentBytes, frame.Length);
                }
                catch (Exception ex)
                {
                    _lastError = $"发送失败：{ex.Message}";
                    lock (_lock) StopLocked(); // 端口故障自动停止
                }
            }, null, 0, intervalMs);
            return Status;
        }
    }

    public SimulatorStatus Stop()
    {
        lock (_lock) StopLocked();
        return Status;
    }

    private void StopLocked()
    {
        _timer?.Dispose();
        _timer = null;
        try { if (_port is { IsOpen: true }) _port.Close(); } catch { /* 忽略关闭异常 */ }
        _port?.Dispose();
        _port = null;
    }

    public void Dispose()
    {
        lock (_lock) StopLocked();
    }
}
