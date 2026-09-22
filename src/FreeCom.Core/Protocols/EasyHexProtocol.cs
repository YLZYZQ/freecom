using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FreeCom.Core.Protocols;

public enum EasyHexDataType { U8, I8, U16, I16, U32, I32, F32 }

public enum ByteOrder { LittleEndian, BigEndian }

/// <summary>
/// EasyHex 协议（PRD 附录 A.5）：原始字节 + 固定帧尾。
/// 帧尾按数据类型固定（如 UINT16=0xFFFF）；字节序可选。
/// 注意协议固有特性：数据中出现帧尾序列会提前截帧（帧尾值被占用）。
/// </summary>
public sealed class EasyHexProtocol : IProtocolParser
{
    private readonly List<byte> _buffer = new();
    private readonly byte[] _tail;
    private readonly int _valueSize;
    private const int MaxBufferedBytes = 1 << 20;
    private int _tailSearchOffset;

    public string Name => "EasyHex";
    public long ErrorCount { get; private set; }

    public EasyHexDataType DataType { get; }
    public ByteOrder Order { get; }

    public static readonly ProtocolInfo Meta = new(
        "EasyHex",
        "十六进制协议：数据+类型帧尾，无校验",
        [
            new ProtocolOptionDef("type", "数据类型", "U16",
                ["U8", "I8", "U16", "I16", "U32", "I32", "F32"]),
            new ProtocolOptionDef("order", "字节序", "LE", ["LE", "BE"]),
        ]);

    public EasyHexProtocol(EasyHexDataType type = EasyHexDataType.U16, ByteOrder order = ByteOrder.LittleEndian)
    {
        DataType = type;
        Order = order;
        var (tail, size) = type switch
        {
            EasyHexDataType.U8 => (new byte[] { 0xFF }, 1),
            EasyHexDataType.I8 => (new byte[] { 0x80 }, 1),
            EasyHexDataType.U16 => ([0xFF, 0xFF], 2),
            EasyHexDataType.I16 => ([0x00, 0x80], 2),
            EasyHexDataType.U32 => ([0xFF, 0xFF, 0xFF, 0xFF], 4),
            EasyHexDataType.I32 => ([0x00, 0x00, 0x00, 0x80], 4),
            EasyHexDataType.F32 => ([0xFF, 0xFF, 0xFF, 0xFF], 4),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        // 大端模式下帧尾按大端字节序出现在流中（如 I16 帧尾 0x8000 → 80 00）
        if (order == ByteOrder.BigEndian && type is EasyHexDataType.I16 or EasyHexDataType.I32)
            tail = tail.Reverse().ToArray();
        _tail = tail;
        _valueSize = size;
    }

    public void Feed(ReadOnlySpan<byte> data, List<ProtocolFrame> output)
    {
        lock (_buffer)
        {
            // Complete frames in the caller's span need no intermediate byte buffer.
            bool buffered = _buffer.Count != 0;
            ReadOnlySpan<byte> chunk = data;
            if (buffered)
            {
                int previousCount = _buffer.Count;
                CollectionsMarshal.SetCount(_buffer, checked(previousCount + data.Length));
                data.CopyTo(CollectionsMarshal.AsSpan(_buffer)[previousCount..]);
                chunk = CollectionsMarshal.AsSpan(_buffer);
            }

            int consumed = 0;
            int searchOffset = _tailSearchOffset;
            while (consumed < chunk.Length)
            {
                var remaining = chunk[consumed..];
                int relativeTail = IndexOfTail(remaining[searchOffset..]);
                if (relativeTail < 0) break;
                int payloadLen = searchOffset + relativeTail;
                var payload = remaining[..payloadLen];
                consumed += payloadLen + _tail.Length;
                searchOffset = 0;

                if (payloadLen == 0 || payloadLen % _valueSize != 0)
                {
                    ErrorCount++; // 空载荷或长度不对齐
                    continue;
                }

                int count = payloadLen / _valueSize;
                var values = new double[count];
                bool ok = true;
                for (int i = 0; i < count && ok; i++)
                    values[i] = DecodeValue(payload.Slice(i * _valueSize, _valueSize), out ok);
                if (!ok) { ErrorCount++; continue; }
                output.Add(new ProtocolFrame { Kind = FrameKind.Plot, Window = "easyhex", Values = values });
            }

            int pending = chunk.Length - consumed;
            if (pending > MaxBufferedBytes)
            {
                // Preserve the existing end-of-feed 1 MiB unterminated-frame limit.
                _buffer.Clear();
                pending = 0;
                ErrorCount++;
            }
            else if (buffered)
            {
                _buffer.RemoveRange(0, consumed);
            }
            else if (pending != 0)
            {
                CollectionsMarshal.SetCount(_buffer, pending);
                chunk[consumed..].CopyTo(CollectionsMarshal.AsSpan(_buffer));
            }
            // Recheck only possible split-tail prefixes on the next Feed.
            _tailSearchOffset = Math.Max(0, pending - _tail.Length + 1);
            if (_buffer.Capacity > MaxBufferedBytes) _buffer.Capacity = _buffer.Count;
        }
    }

    private int IndexOfTail(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < _tail.Length) return -1;
        for (int i = 0; i <= chunk.Length - _tail.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < _tail.Length; j++)
                if (chunk[i + j] != _tail[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private double DecodeValue(ReadOnlySpan<byte> b, out bool ok)
    {
        ok = true;
        bool bigEndian = Order == ByteOrder.BigEndian;
        switch (DataType)
        {
            case EasyHexDataType.U8: return b[0];
            case EasyHexDataType.I8: return (sbyte)b[0];
            case EasyHexDataType.U16: return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
            case EasyHexDataType.I16: return bigEndian ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b);
            case EasyHexDataType.U32: return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
            case EasyHexDataType.I32: return bigEndian ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
            case EasyHexDataType.F32: return bigEndian ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b);
            default: ok = false; return 0;
        }
    }

    public void Reset()
    {
        lock (_buffer)
        {
            _buffer.Clear();
            _tailSearchOffset = 0;
            ErrorCount = 0;
        }
    }

    public void Dispose() { }
}
