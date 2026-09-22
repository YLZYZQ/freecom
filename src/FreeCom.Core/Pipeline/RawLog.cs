namespace FreeCom.Core.Pipeline;

public sealed record RawEntry(long Seq, DateTime TimeUtc, DataDirection Dir, ReadOnlyMemory<byte> Data)
{
    /// <summary>兼容可写数组调用方；返回独立副本，内部只读访问使用 Data。</summary>
    public byte[] Bytes => Data.ToArray();
}

/// <summary>原始字节日志（环形，收发都记录）：供 /v1/device/receive 与原始数据导出。
/// 条数与字节双上限：突发大块写入（如批量灌数）时按字节预算淘汰，内存与条目大小无关。</summary>
public sealed class RawLog
{
    private readonly object _lock = new();
    private readonly Queue<RawEntry> _entries = new();
    private long _seq;
    private long _totalBytes;
    private readonly int _cap;
    private readonly long _byteBudget;

    public const int DefaultCapacity = 50_000;
    public const long DefaultByteBudget = 32L << 20; // 32MB

    public RawLog(int capacity = DefaultCapacity, long byteBudget = DefaultByteBudget)
    {
        _cap = Math.Max(16, capacity);
        _byteBudget = byteBudget;
    }

    public long Append(DataDirection dir, ReadOnlySpan<byte> data)
        => AppendOwned(dir, data.ToArray());

    // 调用方交付数组所有权；共享载荷在日志和解析器中均只读。
    internal long AppendOwned(DataDirection dir, byte[] data)
    {
        lock (_lock)
        {
            var entry = new RawEntry(++_seq, DateTime.UtcNow, dir, data);
            _entries.Enqueue(entry);
            _totalBytes += entry.Data.Length;
            while (_entries.Count > _cap || _totalBytes > _byteBudget)
            {
                if (_entries.Count == 0) break;
                _totalBytes -= _entries.Peek().Data.Length;
                _entries.Dequeue();
            }
            return entry.Seq;
        }
    }

    public long LastSeq { get { lock (_lock) return _seq; } }

    public List<RawEntry> Snapshot(long sinceSeq = 0, int limit = int.MaxValue, DataDirection? dir = null)
    {
        lock (_lock)
        {
            return _entries
                .Where(e => e.Seq > sinceSeq && (dir is null || e.Dir == dir))
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>拼接指定方向的全部原始字节（导出 DAT 用）。</summary>
    public byte[] CollectBytes(DataDirection? dir = null)
    {
        lock (_lock)
        {
            int length = 0;
            foreach (var e in _entries)
                if (dir is null || e.Dir == dir)
                    length = checked(length + e.Data.Length);
            var bytes = new byte[length];
            int offset = 0;
            foreach (var e in _entries)
            {
                if (dir is not null && e.Dir != dir) continue;
                e.Data.Span.CopyTo(bytes.AsSpan(offset));
                offset += e.Data.Length;
            }
            return bytes;
        }
    }

    public void Clear() { lock (_lock) { _entries.Clear(); _totalBytes = 0; } }
}
