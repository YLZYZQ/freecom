using FreeCom.Core;
using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

public class Crc16Tests
{
    [Theory]
    [InlineData("123456789", 0x4B37)]      // CRC-16/MODBUS 标准测试向量
    [InlineData("", 0xFFFF)]
    public void StandardVectors(string input, ushort expected)
    {
        Assert.Equal(expected, Crc16.Compute(System.Text.Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void LittleEndianLayout_LowByteFirst()
    {
        var body = new byte[] { 0x01, 0x03, 0x04, 0x01, 0x2C, 0x01, 0xF4 };
        var crc = Crc16.Compute(body);
        var le = Crc16.ComputeLittleEndian(body);
        Assert.Equal((byte)(crc & 0xFF), le[0]); // 低字节在前
        Assert.Equal((byte)(crc >> 8), le[1]);
    }

    [Fact]
    public void DocExampleFrame_CrcMatches()
    {
        // PRD 附录 A.6 示例帧：01 03 04 01 2C 01 F4 → CRC_L=0x3A CRC_H=0x11
        var body = new byte[] { 0x01, 0x03, 0x04, 0x01, 0x2C, 0x01, 0xF4 };
        var le = Crc16.ComputeLittleEndian(body);
        Assert.Equal(0x3A, le[0]);
        Assert.Equal(0x11, le[1]);
    }
}

public class LineAssemblerTests
{
    [Fact]
    public void SplitsByNewline_StripsCr()
    {
        var asm = new LineAssembler();
        var lines = asm.Feed("{w}1,2\r\n{w}3,4\n"u8);
        Assert.Equal(2, lines.Count);
        Assert.Equal("{w}1,2"u8.ToArray(), lines[0]);
        Assert.Equal("{w}3,4"u8.ToArray(), lines[1]);
    }

    [Fact]
    public void KeepsPartialLine_AcrossFeeds()
    {
        var asm = new LineAssembler();
        Assert.Empty(asm.Feed("{plo"u8));
        Assert.Empty(asm.Feed("tter}1,2"u8));
        var lines = asm.Feed(",3\n"u8);
        Assert.Single(lines);
        Assert.Equal("{plotter}1,2,3"u8.ToArray(), lines[0]);
    }

    [Fact]
    public void MultipleLinesInOneChunk_AllEmitted()
    {
        var asm = new LineAssembler();
        var lines = asm.Feed("a\nb\nc\n"u8);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void OverlongPendingLine_Dropped_Defensively()
    {
        var asm = new LineAssembler { MaxLineLength = 8 };
        asm.Feed("0123456789ABCDEF"u8); // 无换行、超过上限
        var lines = asm.Feed("x\n"u8);
        Assert.Single(lines); // 超长部分被防御性丢弃，仅保留新行
        Assert.Equal("x"u8.ToArray(), lines[0]);
    }
}

public class HexParseTests
{
    [Fact]
    public void LooseInput_ToleratesSeparatorsAndPrefix()
    {
        var bytes = HexParse.ParseLoose("AA 55, 0x01\tff 0X0A");
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x01, 0xFF, 0x0A }, bytes);
    }

    [Fact]
    public void OddLength_ThrowsWithMessage()
    {
        var ex = Assert.Throws<FormatException>(() => HexParse.ParseLoose("AAB"));
        Assert.Contains("偶数", ex.Message);
    }

    [Fact]
    public void InvalidChar_ReportsPosition()
    {
        var ex = Assert.Throws<FormatException>(() => HexParse.ParseLoose("AA ZZ"));
        Assert.Contains("位置 3", ex.Message);
    }

    [Fact]
    public void ToHexSpaced_Formatting()
    {
        Assert.Equal("AA 55 01", HexParse.ToHexSpaced([0xAA, 0x55, 0x01]));
        Assert.Equal("", HexParse.ToHexSpaced([]));
    }

    [Theory]
    [InlineData("AA 55", true)]
    [InlineData("AA,5", false)]
    public void TryParse(string input, bool expected)
        => Assert.Equal(expected, HexParse.TryParse(input, out _));
}
