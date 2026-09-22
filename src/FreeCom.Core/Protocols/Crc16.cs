namespace FreeCom.Core.Protocols;

/// <summary>CRC16-MODBUS：多项式 0xA001（反转 0x8005）、初值 0xFFFF、低字节在前。</summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (ushort b = 0; b < 256; b++)
        {
            ushort v = b;
            for (int i = 0; i < 8; i++)
                v = (ushort)((v & 1) != 0 ? (v >> 1) ^ 0xA001 : v >> 1);
            table[b] = v;
        }
        return table;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>帧尾两字节：低字节在前（ModbusRTU 规范）。</summary>
    public static byte[] ComputeLittleEndian(ReadOnlySpan<byte> data)
    {
        var crc = Compute(data);
        return [(byte)(crc & 0xFF), (byte)(crc >> 8)];
    }
}
