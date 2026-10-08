namespace FreeCom.Core.Pipeline;

/// <summary>
/// 静默间隔组帧器（inter-frame gap）：数据流静默超过 gapMs 判定一帧结束。
/// 串口数据按驱动分片到达（物理口下 ReadAsync 在传输途中即返回已到部分），
/// 显示/日志/MCP 匹配需要按"静默"边界合并分片——与 SSCOM"分包间隔"同语义。
/// 线程安全；Dispose 冲刷尾帧。gapMs &lt;= 0 时直通（不组帧）。
/// </summary>
public sealed class FrameAssembler : IDisposable
{
    private readonly object _lock = new();
    private readonly System.IO.MemoryStream _buffer = new();
    private readonly Timer? _timer;
    private readonly Action<byte[]> _deliver;
    private readonly int _gapMs;
    private bool _hasData;

    public FrameAssembler(int gapMs, Action<byte[]> deliver)
    {
        _gapMs = gapMs;
        _deliver = deliver;
        if (gapMs > 0)
            _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Passthrough => _timer is null;

    /// <summary>喂入一个到达分片；随后静默 gapMs 才会成帧交付。</summary>
    public void Feed(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        if (Passthrough) { _deliver(chunk); return; }
        lock (_lock)
        {
            _buffer.Write(chunk, 0, chunk.Length);
            _hasData = true;
            _timer!.Change(_gapMs, Timeout.Infinite);
        }
    }

    /// <summary>立即成帧交付缓冲中的数据（无数据则空操作）。</summary>
    public void Flush()
    {
        byte[]? frame = null;
        lock (_lock)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (!_hasData || _buffer.Length == 0) { _hasData = false; return; }
            frame = _buffer.ToArray();
            _buffer.SetLength(0);
            _hasData = false;
        }
        if (frame is not null) _deliver(frame);
    }

    public void Dispose()
    {
        Flush();
        _timer?.Dispose();
    }
}
