using System.Buffers;
using System.Text;

namespace FreeCom.Core.Protocols;

/// <summary>
/// 行组装器：把任意分片的字节流按 '\n' 切分为完整行（兼容 \r\n）。
/// 供文本类协议（TEXT/STAMP/CSV）复用。
/// </summary>
public sealed class LineAssembler
{
    private byte[] _pending = [];
    private int _pendingLength;
    public int MaxLineLength { get; set; } = 64 * 1024;

    internal delegate void ByteLineHandler<TState>(ReadOnlySpan<byte> line, TState state);
    internal delegate void TextLineHandler<TState>(ReadOnlySpan<char> line, TState state);

    /// <summary>输入字节，返回本批次切出的完整行（不含换行符）。</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        var lines = new List<byte[]>();
        Feed(data, lines, static (line, output) => output.Add(line.ToArray()));
        return lines;
    }

    // Callbacks consume the borrowed span synchronously; only a split line needs copying.
    internal void Feed<TState>(ReadOnlySpan<byte> data, TState state, ByteLineHandler<TState> handler)
    {
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;

            if (_pendingLength == 0)
            {
                handler(TrimCr(data.Slice(start, i - start)), state);
            }
            else
            {
                Append(data.Slice(start, i - start));
                var line = _pending.AsSpan(0, _pendingLength);
                _pendingLength = 0;
                handler(TrimCr(line), state);
            }
            start = i + 1;
        }

        if (start < data.Length)
        {
            // Preserve the existing policy: discard an over-limit unfinished line.
            // Check before growing so a huge unterminated chunk is not retained.
            if ((long)_pendingLength + data.Length - start > MaxLineLength) _pendingLength = 0;
            else Append(data.Slice(start));
        }
    }

    internal void FeedUtf8<TState>(ReadOnlySpan<byte> data, TState state, TextLineHandler<TState> handler)
        => Feed(data, (State: state, Handler: handler), static (line, context) =>
        {
            char[]? rented = null;
            Span<char> chars = line.Length <= 512
                ? stackalloc char[512]
                : (rented = ArrayPool<char>.Shared.Rent(line.Length));
            try
            {
                int count = Encoding.UTF8.GetChars(line, chars);
                context.Handler(chars[..count], context.State);
            }
            finally
            {
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
            }
        });

    private static ReadOnlySpan<byte> TrimCr(ReadOnlySpan<byte> line)
        => !line.IsEmpty && line[^1] == (byte)'\r' ? line[..^1] : line;

    private void Append(ReadOnlySpan<byte> data)
    {
        int required = checked(_pendingLength + data.Length);
        if (required > _pending.Length)
            Array.Resize(ref _pending, Math.Max(required, Math.Max(256, _pending.Length * 2)));
        data.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength = required;
    }

    public void Reset() => _pendingLength = 0;

    /// <summary>把一行字节按 UTF-8 解码为字符串（坏字符替换，不抛异常）。</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> line)
        => Encoding.UTF8.GetString(line);
}
