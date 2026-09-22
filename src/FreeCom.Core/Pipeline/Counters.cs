namespace FreeCom.Core.Pipeline;

public enum DataDirection
{
    Rx,
    Tx,
}

/// <summary>收发计数与速率（最近 3 秒滑动窗口）。</summary>
public sealed class Counters
{
    private long _rx, _tx, _frames, _errors;
    private readonly object _rateLock = new();
    private readonly Queue<(DateTime T, int Bytes)> _rxSamples = new();

    public void AddRx(int bytes)
    {
        Interlocked.Add(ref _rx, bytes);
        lock (_rateLock)
        {
            _rxSamples.Enqueue((DateTime.UtcNow, bytes));
            var cutoff = DateTime.UtcNow.AddSeconds(-3);
            while (_rxSamples.Count > 0 && _rxSamples.Peek().T < cutoff) _rxSamples.Dequeue();
        }
    }

    public void AddTx(int bytes) => Interlocked.Add(ref _tx, bytes);
    public void AddFrames(int n) => Interlocked.Add(ref _frames, n);
    public void SetParseErrors(long v) => Interlocked.Exchange(ref _errors, v);

    public long RxBytes => Interlocked.Read(ref _rx);
    public long TxBytes => Interlocked.Read(ref _tx);
    public long FramesParsed => Interlocked.Read(ref _frames);
    public long ParseErrors => Interlocked.Read(ref _errors);

    public double RxRatePerSecond
    {
        get
        {
            lock (_rateLock)
            {
                var cutoff = DateTime.UtcNow.AddSeconds(-3);
                while (_rxSamples.Count > 0 && _rxSamples.Peek().T < cutoff) _rxSamples.Dequeue();
                if (_rxSamples.Count == 0) return 0;
                double total = 0;
                foreach (var s in _rxSamples) total += s.Bytes;
                double secs = Math.Max(0.05, (DateTime.UtcNow - _rxSamples.Peek().T).TotalSeconds);
                return total / secs;
            }
        }
    }
}
