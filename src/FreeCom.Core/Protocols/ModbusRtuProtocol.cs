using System.Buffers.Binary;

namespace FreeCom.Core.Protocols;

/// <summary>
/// ModbusRTU 协议（PRD 附录 A.6）：仅解析 03 功能码应答帧。
/// 地址码(1) 功能码(1) 字节数(1) 数据(n) CRC_L(1) CRC_H(1)
/// CRC 校验失败逐字节重同步；按 地址码:功能码 自动分窗。
/// </summary>
public sealed class ModbusRtuProtocol : IProtocolParser
{
    private enum ParseResult { Parsed, NeedMore, Invalid }

    // The largest accepted response is 3 header + 246 payload + 2 CRC bytes.
    private readonly byte[] _buffer = new byte[251];
    private int _bufferCount;

    public string Name => "ModbusRTU";
    public long ErrorCount { get; private set; }

    public EasyHexDataType DataType { get; }
    public ByteOrder Order { get; }

    public static readonly ProtocolInfo Meta = new(
        "ModbusRTU",
        "03 功能码应答帧 + CRC16 校验，按地址码/功能码分窗",
        [
            new ProtocolOptionDef("type", "数据类型", "I16",
                ["U8", "I8", "U16", "I16", "U32", "I32", "F32"]),
            new ProtocolOptionDef("order", "字节序", "BE", ["LE", "BE"]),
        ]);

    private static int SizeOf(EasyHexDataType t) => t switch
    {
        EasyHexDataType.U8 or EasyHexDataType.I8 => 1,
        EasyHexDataType.U16 or EasyHexDataType.I16 => 2,
        _ => 4,
    };

    public ModbusRtuProtocol(EasyHexDataType type = EasyHexDataType.I16, ByteOrder order = ByteOrder.BigEndian)
    {
        DataType = type;
        Order = order;
    }

    public void Feed(ReadOnlySpan<byte> data, List<ProtocolFrame> output)
    {
        lock (_buffer)
        {
            do
            {
                int added = Math.Min(data.Length, _buffer.Length - _bufferCount);
                data[..added].CopyTo(_buffer.AsSpan(_bufferCount));
                _bufferCount += added;
                data = data[added..];
                var chunk = _buffer.AsSpan(0, _bufferCount);
                int consumed = 0;

                while (chunk.Length - consumed >= 3)
                {
                    var result = TryParseFrame(chunk[consumed..], out var frame, out int frameLen);
                    if (result == ParseResult.NeedMore) break;
                    if (result == ParseResult.Parsed)
                    {
                        consumed += frameLen;
                        if (frame != null) output.Add(frame);
                    }
                    else
                    {
                        consumed++; // 逐字节重同步，保持原有错误计数。
                        ErrorCount++;
                    }
                }

                chunk[consumed..].CopyTo(_buffer);
                _bufferCount -= consumed;
            } while (!data.IsEmpty);
        }
    }

    private ParseResult TryParseFrame(ReadOnlySpan<byte> chunk, out ProtocolFrame? frame, out int frameLen)
    {
        frame = null;
        frameLen = 0;
        byte addr = chunk[0];
        byte func = chunk[1];
        if (func != 0x03) return ParseResult.Invalid;
        int byteCount = chunk[2];
        int size = SizeOf(DataType);
        if (byteCount == 0 || byteCount > 246 || byteCount % size != 0) return ParseResult.Invalid;
        frameLen = 3 + byteCount + 2;
        if (chunk.Length < frameLen) return ParseResult.NeedMore;

        var crc = Crc16.Compute(chunk[..(frameLen - 2)]);
        if (chunk[frameLen - 2] != (byte)(crc & 0xFF) || chunk[frameLen - 1] != (byte)(crc >> 8))
            return ParseResult.Invalid;

        var payload = chunk.Slice(3, byteCount);
        int n = byteCount / size;
        var values = new double[n];
        for (int i = 0; i < n; i++)
        {
            var v = payload.Slice(i * size, size);
            bool bigEndian = Order == ByteOrder.BigEndian;
            values[i] = DataType switch
            {
                EasyHexDataType.U8 => v[0],
                EasyHexDataType.I8 => (sbyte)v[0],
                EasyHexDataType.U16 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(v) : BinaryPrimitives.ReadUInt16LittleEndian(v),
                EasyHexDataType.I16 => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(v) : BinaryPrimitives.ReadInt16LittleEndian(v),
                EasyHexDataType.U32 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(v) : BinaryPrimitives.ReadUInt32LittleEndian(v),
                EasyHexDataType.I32 => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(v) : BinaryPrimitives.ReadInt32LittleEndian(v),
                // Keep the switch expression in double precision for U32/I32 too.
                EasyHexDataType.F32 => (double)(bigEndian ? BinaryPrimitives.ReadSingleBigEndian(v) : BinaryPrimitives.ReadSingleLittleEndian(v)),
                _ => 0,
            };
        }

        frame = new ProtocolFrame
        {
            Kind = FrameKind.Plot,
            Window = $"ADDR:{addr:X2} FUNC:{func:X2}",
            Values = values,
        };
        return ParseResult.Parsed;
    }

    public void Reset()
    {
        lock (_buffer)
        {
            _bufferCount = 0;
            ErrorCount = 0;
        }
    }

    public void Dispose() { }
}
