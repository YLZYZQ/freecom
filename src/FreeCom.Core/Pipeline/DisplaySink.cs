using System.Text;

namespace FreeCom.Core.Pipeline;

public sealed record DisplayEntry(long Seq, DateTime TimeUtc, DataDirection Dir, ReadOnlyMemory<byte> Data)
{
    /// <summary>兼容可写数组调用方；返回独立副本，内部只读访问使用 Data。</summary>
    public byte[] Bytes => Data.ToArray();
}

/// <summary>
/// 显示缓冲（环形）：接收区文本/HEX 渲染的数据源。
/// 条数与字节双上限：突发大块写入时按字节预算淘汰，内存与条目大小无关。
/// </summary>
public sealed class DisplaySink
{
    private readonly object _lock = new();
    private readonly Queue<DisplayEntry> _entries = new();
    private long _seq;
    private long _totalBytes;
    private readonly int _cap;
    private readonly long _byteBudget;

    public const int DefaultCapacity = 20_000;
    public const long DefaultByteBudget = 8L << 20; // 8MB

    public DisplaySink(int capacity = DefaultCapacity, long byteBudget = DefaultByteBudget)
    {
        _cap = Math.Max(16, capacity);
        _byteBudget = byteBudget;
    }

    public long LastSeq { get { lock (_lock) return _seq; } }

    public void Append(DataDirection dir, ReadOnlySpan<byte> data)
        => AppendOwned(dir, data.ToArray());

    // 仅管线可交付自己持有、之后不再写入的数组；日志之间可共享只读载荷。
    internal void AppendOwned(DataDirection dir, byte[] bytes)
    {
        lock (_lock)
        {
            _entries.Enqueue(new DisplayEntry(++_seq, DateTime.UtcNow, dir, bytes));
            _totalBytes += bytes.Length;
            while (_entries.Count > _cap || _totalBytes > _byteBudget)
            {
                if (_entries.Count == 0) break;
                _totalBytes -= _entries.Peek().Data.Length;
                _entries.Dequeue();
            }
        }
    }

    public List<DisplayEntry> Snapshot(long sinceSeq = 0, int limit = int.MaxValue)
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Seq > sinceSeq).Take(limit).ToList();
        }
    }

    /// <summary>实时界面取最新一批；旧数据仍由完整日志/导出接口访问。
    /// 至少保留最后一条（串口单块可能大于本次预算）。</summary>
    public List<DisplayEntry> SnapshotRecent(long sinceSeq, int byteBudget = 32 * 1024, int limit = 256,
        Func<DisplayEntry, bool>? predicate = null)
    {
        if (byteBudget <= 0 || limit <= 0) return [];
        lock (_lock)
        {
            var recent = new Queue<DisplayEntry>();
            long bytes = 0;
            foreach (var entry in _entries)
            {
                if (entry.Seq <= sinceSeq) continue;
                if (predicate is not null && !predicate(entry)) continue;
                recent.Enqueue(entry);
                bytes += entry.Data.Length;
                while (recent.Count > 1 && (bytes > byteBudget || recent.Count > limit))
                    bytes -= recent.Dequeue().Data.Length;
            }
            return recent.ToList();
        }
    }

    /// <summary>条目文本规范化：①剥掉帧头换行（分片边界切在行尾之后时，帧以 CR/LF 开头，
    /// 会让 "<< " 前缀后紧跟空行）；②吸收数据自带的尾部换行为条目分隔（组帧后每帧多含
    /// 行尾 CR/LF，与条目分隔换行叠加会产生空行）；③纯空白条目（行尾换行独立成帧，
    /// 设备行数据与行尾间隔超过组帧间隔时出现）返回空串，调用方跳过渲染。</summary>
    public static string NormalizeEntryText(string s)
    {
        s = s.TrimStart('\r', '\n');
        if (s.Length == 0) return "";
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s[..^2] + "\n";
        if (s.EndsWith("\n", StringComparison.Ordinal)) return s;
        return s + "\n";
    }

    /// <summary>兼容旧名（仅吸收尾换行）。</summary>
    public static string WithEntryNewline(string s) => NormalizeEntryText(s);

    public string RenderText(bool includeTimestamp = true, long sinceSeq = 0, int limit = int.MaxValue)
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot(sinceSeq, limit))
        {
            var text = NormalizeEntryText(Encoding.UTF8.GetString(e.Data.Span));
            if (text.Length == 0) continue; // 纯空白条目（行尾独立成帧）：不渲染
            if (includeTimestamp)
                sb.Append('[').Append(e.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff")).Append("] ");
            sb.Append(e.Dir == DataDirection.Tx ? ">> " : "<< ");
            sb.Append(text);
        }
        return sb.ToString();
    }

    public string RenderHex(bool includeTimestamp = true, long sinceSeq = 0, int limit = int.MaxValue)
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot(sinceSeq, limit))
        {
            if (includeTimestamp)
                sb.Append('[').Append(e.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff")).Append("] ");
            sb.Append(e.Dir == DataDirection.Tx ? ">> " : "<< ");
            sb.Append(HexParse.ToHexSpaced(e.Data.Span)).Append('\n');
        }
        return sb.ToString();
    }

    public void Clear() { lock (_lock) { _entries.Clear(); _totalBytes = 0; } }
}
