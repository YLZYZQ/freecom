using System.Text;

namespace FreeCom.Core.Protocols;

/// <summary>
/// 五协议示例帧生成（下位机模拟）：供设备模拟器面板与无头宿主复用。
/// 全部返回"一帧"的完整字节。
/// </summary>
public static class TrafficFrames
{
    public static byte[] Text(long i) => Encoding.UTF8.GetBytes($"{{demo}}{i % 100},{(i * 7) % 50},{Math.Sin(i * 0.1):F3}\n");

    public static byte[] Csv(long i) => Encoding.UTF8.GetBytes($"{i % 100},{(i * 3) % 80},{Math.Cos(i * 0.05):F3}\n");

    public static byte[] Stamp(long i) => Encoding.UTF8.GetBytes($"<{i * 0.01:F2}>{{demo}}{i % 100},{Math.Sin(i * 0.1):F3}\n");

    /// <summary>EasyHex：I16 小端双值 + 帧尾 0x8000（LE: 00 80）。</summary>
    public static byte[] EasyHex(long i)
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((short)(i % 1000)));
        ms.Write(BitConverter.GetBytes((short)(i % 500)));
        ms.WriteByte(0x00);
        ms.WriteByte(0x80);
        return ms.ToArray();
    }

    /// <summary>ModbusRTU 03 应答帧：I16 大端双值 + CRC16（低字节在前）。</summary>
    public static byte[] ModbusRtu(long i, byte addr = 0x01)
    {
        var body = new List<byte> { addr, 0x03, 0x04 };
        short a = (short)(i % 1000), b = (short)(i % 500);
        body.Add((byte)(a >> 8)); body.Add((byte)(a & 0xFF));
        body.Add((byte)(b >> 8)); body.Add((byte)(b & 0xFF));
        body.AddRange(Crc16.ComputeLittleEndian([.. body]));
        return [.. body];
    }

    public static byte[] FrameFor(string protocol, long index) => protocol.ToUpperInvariant() switch
    {
        "CSV" => Csv(index),
        "STAMP" => Stamp(index),
        "EASYHEX" => EasyHex(index),
        "MODBUSRTU" => ModbusRtu(index),
        _ => Text(index),
    };
}
