using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

// ---------------- EasyHex 协议 ----------------
public class EasyHexProtocolTests
{
    [Fact]
    public void DocExample_I16_LE_TwoValues()
    {
        // PRD 附录 A.5：300(0x012C), 500(0x01F4) 小端 + 帧尾 0x8000(小端 00 80)
        var p = new EasyHexProtocol(EasyHexDataType.I16, ByteOrder.LittleEndian);
        var frames = p.FeedBytes(0x2C, 0x01, 0xF4, 0x01, 0x00, 0x80);
        Assert.Single(frames);
        Assert.Equal([300, 500], frames[0].Values);
        Assert.Equal("easyhex", frames[0].Window);
    }

    [Fact]
    public void DocExample_I16_BE_TwoValues()
    {
        var p = new EasyHexProtocol(EasyHexDataType.I16, ByteOrder.BigEndian);
        var frames = p.FeedBytes(0x01, 0x2C, 0x01, 0xF4, 0x80, 0x00);
        Assert.Single(frames);
        Assert.Equal([300, 500], frames[0].Values);
    }

    [Fact]
    public void FrameSplit_AcrossFeeds()
    {
        var p = new EasyHexProtocol(EasyHexDataType.I16, ByteOrder.LittleEndian);
        Assert.Empty(p.FeedBytes(0x2C, 0x01, 0xF4)); // 半帧
        Assert.Empty(p.FeedBytes(0x01, 0x00));       // 仍是帧尾前缀
        var frames = p.FeedBytes(0x80);              // 帧尾完成
        Assert.Single(frames);
        Assert.Equal([300, 500], frames[0].Values);
    }

    [Fact]
    public void BytesKeptWhileWaitingForTail()
    {
        // 载荷字节必须完整保留直到帧尾到达，不能因“疑似前缀”而丢失
        var p = new EasyHexProtocol(EasyHexDataType.I16, ByteOrder.LittleEndian);
        Assert.Empty(p.FeedBytes(0x10, 0x27, 0x00));       // 10000=0x2710 的 LE + 下一值的低字节
        var frames = p.FeedBytes(0x00, 0x00, 0x80);        // 值 0(00 00) + 帧尾(00 80)
        // 缓冲 = 10 27 00 00 00 80，帧尾在 idx4，载荷 10 27 00 00 → [10000, 0]
        Assert.Single(frames);
        Assert.Equal([10000, 0], frames[0].Values);
    }

    [Fact]
    public void U8_TailFF_BoundaryOccupied()
    {
        // U8 帧尾 0xFF：值 0xFF 无法表达（协议固有），其余值正常
        var p = new EasyHexProtocol(EasyHexDataType.U8, ByteOrder.LittleEndian);
        var frames = p.FeedBytes(0x01, 0x02, 0xFF);
        Assert.Single(frames);
        Assert.Equal([1, 2], frames[0].Values);
    }

    [Fact]
    public void U8_ValueEqualTail_CutsFrameEarly_ProtocolInherent()
    {
        var p = new EasyHexProtocol(EasyHexDataType.U8, ByteOrder.LittleEndian);
        // 0x03 0xFF → 0xFF 视为帧尾 → 第一帧 [3]；随后 0x04 0xFF → 第二帧 [4]
        var frames = p.FeedBytes(0x03, 0xFF, 0x04, 0xFF);
        Assert.Equal(2, frames.Count);
        Assert.Single(frames[0].Values);
        Assert.Equal(3, frames[0].Values[0]);
        Assert.Equal(4, frames[1].Values[0]);
    }

    [Fact]
    public void MultipleFrames_OneChunk()
    {
        var p = new EasyHexProtocol(EasyHexDataType.U16, ByteOrder.LittleEndian);
        var frames = p.FeedBytes(
            0x01, 0x00, 0xFF, 0xFF,   // [1]
            0x02, 0x00, 0xFF, 0xFF);  // [2]
        Assert.Equal(2, frames.Count);
        Assert.Equal([1], frames[0].Values);
        Assert.Equal([2], frames[1].Values);
    }

    [Fact]
    public void F32_FloatValues()
    {
        var p = new EasyHexProtocol(EasyHexDataType.F32, ByteOrder.LittleEndian);
        var f = 3.14f;
        var bytes = BitConverter.GetBytes(f).ToList();
        bytes.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        var frames = p.FeedBytes([.. bytes]);
        Assert.Single(frames);
        Assert.Equal(f, (float)frames[0].Values[0], 5);
    }

    [Fact]
    public void EmptyPayloadTail_ErrorCounted()
    {
        var p = new EasyHexProtocol(EasyHexDataType.U16, ByteOrder.LittleEndian);
        var frames = p.FeedBytes(0xFF, 0xFF, 0x01, 0x00, 0xFF, 0xFF);
        // 第一组 FF FF 是空载荷帧 → 1 错误；第二组 → [1]
        Assert.Single(frames);
        Assert.Equal([1], frames[0].Values);
        Assert.Equal(1, p.ErrorCount);
    }

    [Fact]
    public void MisalignedPayload_ErrorCounted()
    {
        var p = new EasyHexProtocol(EasyHexDataType.I16, ByteOrder.LittleEndian);
        // 3 字节载荷（奇数）+ 帧尾 → 长度不对齐
        var frames = p.FeedBytes(0x2C, 0x01, 0xF4, 0x00, 0x80);
        Assert.Empty(frames);
        Assert.True(p.ErrorCount >= 1);
    }
}

// ---------------- ModbusRTU 协议 ----------------
public class ModbusRtuProtocolTests
{
    private static byte[] Frame(byte addr, byte func, params short[] valuesBE)
    {
        var body = new List<byte> { addr, func, (byte)(valuesBE.Length * 2) };
        foreach (var v in valuesBE)
        {
            body.Add((byte)(v >> 8));
            body.Add((byte)(v & 0xFF));
        }
        var crc = Crc16.ComputeLittleEndian([.. body]);
        body.AddRange(crc);
        return [.. body];
    }

    [Fact]
    public void DocExample_Addr01_Func03()
    {
        var p = new ModbusRtuProtocol(EasyHexDataType.I16, ByteOrder.BigEndian);
        var frames = p.FeedBytes(Frame(0x01, 0x03, 300, 500));
        Assert.Single(frames);
        Assert.Equal("ADDR:01 FUNC:03", frames[0].Window);
        Assert.Equal([300, 500], frames[0].Values);
    }

    [Fact]
    public void MultiWindow_ByAddr()
    {
        var p = new ModbusRtuProtocol();
        var input = Frame(0x01, 0x03, 1).Concat(Frame(0x02, 0x03, 2)).ToArray();
        var frames = p.FeedBytes(input);
        Assert.Equal(2, frames.Count);
        Assert.Equal("ADDR:01 FUNC:03", frames[0].Window);
        Assert.Equal("ADDR:02 FUNC:03", frames[1].Window);
    }

    [Fact]
    public void CorruptedCrc_Dropped_ResyncToNextValid()
    {
        var p = new ModbusRtuProtocol();
        var bad = Frame(0x01, 0x03, 10);
        bad[^1] ^= 0xFF; // 破坏 CRC 高字节
        var good = Frame(0x01, 0x03, 20);
        var frames = p.FeedBytes([.. bad, .. good]);
        Assert.Single(frames);
        Assert.Equal([20], frames[0].Values);
        Assert.True(p.ErrorCount >= 1);
    }

    [Fact]
    public void LeadingGarbage_Resyncs()
    {
        var p = new ModbusRtuProtocol();
        var good = Frame(0x01, 0x03, 7);
        var input = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.Concat(good).ToArray();
        var frames = p.FeedBytes(input);
        Assert.Single(frames);
        Assert.Equal([7], frames[0].Values);
    }

    [Fact]
    public void FuncNot03_Skipped()
    {
        var p = new ModbusRtuProtocol();
        var frames = p.FeedBytes(Frame(0x01, 0x04, 300)); // 04 功能码不支持
        Assert.Empty(frames);
    }

    [Fact]
    public void PartialFrame_WaitsNotDrops()
    {
        var p = new ModbusRtuProtocol();
        var frame = Frame(0x01, 0x03, 300, 500);
        Assert.Empty(p.FeedBytes(frame[..^1]));  // 去掉最后一个 CRC 字节
        var frames = p.FeedBytes(frame[^1]);     // 补上
        Assert.Single(frames);
        Assert.Equal([300, 500], frames[0].Values);
    }

    [Fact]
    public void ByteSplit_AcrossFeeds_Everywhere()
    {
        var p = new ModbusRtuProtocol();
        var frame = Frame(0x01, 0x03, -5, 32767);
        var all = new List<ProtocolFrame>();
        foreach (var b in frame) all.AddRange(p.FeedBytes(b));
        Assert.Single(all);
        Assert.Equal([-5, 32767], all[0].Values);
    }

    [Fact]
    public void Float32_BE()
    {
        var p = new ModbusRtuProtocol(EasyHexDataType.F32, ByteOrder.BigEndian);
        float v = -2.5f;
        var be = BitConverter.GetBytes(v).Reverse().ToArray();
        var body = new List<byte> { 0x01, 0x03, 0x04 };
        body.AddRange(be);
        body.AddRange(Crc16.ComputeLittleEndian([.. body]));
        var frames = p.FeedBytes([.. body]);
        Assert.Single(frames);
        Assert.Equal(v, (float)frames[0].Values[0], 6);
    }

    [Fact]
    public void LittleEndianOption()
    {
        var p = new ModbusRtuProtocol(EasyHexDataType.I16, ByteOrder.LittleEndian);
        var body = new List<byte> { 0x01, 0x03, 0x02, 0x2C, 0x01 }; // 300 小端
        body.AddRange(Crc16.ComputeLittleEndian([.. body]));
        var frames = p.FeedBytes([.. body]);
        Assert.Equal([300], frames[0].Values);
    }
}

// ---------------- 协议注册表 ----------------
public class ProtocolRegistryTests
{
    [Fact]
    public void FiveMvpProtocolsRegistered()
    {
        Assert.Equal(
            new HashSet<string> { "TEXT", "CSV", "STAMP", "EasyHex", "ModbusRTU" },
            ProtocolRegistry.Names.ToHashSet());
    }

    [Fact]
    public void Create_WithOptions()
    {
        var csv = ProtocolRegistry.Create("CSV", new Dictionary<string, string> { ["window"] = "sensor" });
        Assert.Equal("sensor", csv.FeedText("1\n")[0].Window);

        var hex = ProtocolRegistry.Create("EasyHex", new Dictionary<string, string> { ["type"] = "U8", ["order"] = "BE" });
        Assert.IsType<EasyHexProtocol>(hex);
    }

    [Fact]
    public void UnknownProtocol_Throws()
        => Assert.Throws<ArgumentException>(() => ProtocolRegistry.Create("NOPE"));

    [Fact]
    public void Infos_CoverAllProtocols()
        => Assert.Equal(5, ProtocolRegistry.Infos().Count);
}
