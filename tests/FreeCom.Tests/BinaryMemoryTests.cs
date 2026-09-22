using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

public class BinaryMemoryTests
{
    [Fact]
    public void Modbus_Invalid64KiB_HasBoundedAllocationAndExactResynchronization()
    {
        using var parser = new ModbusRtuProtocol();
        var frames = new List<ProtocolFrame>();
        var noise = new byte[65536];
        parser.Feed(noise.AsSpan(0, 16), frames);
        parser.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        parser.Feed(noise, frames);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(frames);
        Assert.Equal(65534, parser.ErrorCount);
        Assert.InRange(allocated, 0, 4096);

        parser.Feed(ModbusFrame([0, 42]), frames);
        Assert.Single(frames);
        Assert.Equal(42, frames[0].Values[0]);
        Assert.Equal(65536, parser.ErrorCount);
    }

    [Fact]
    public void EasyHex_Dense64KiB_AllocatesOnlyLinearOutputStorage()
    {
        using var parser = new EasyHexProtocol();
        var frames = new List<ProtocolFrame>();
        var input = new byte[65536];
        for (int i = 0; i < input.Length; i += 4)
        {
            input[i] = 42;
            input[i + 2] = input[i + 3] = 255;
        }
        parser.Feed(input.AsSpan(0, 4), frames);
        frames.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        parser.Feed(input, frames);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(16384, frames.Count);
        Assert.All(frames, frame => Assert.Equal(42, Assert.Single(frame.Values)));
        Assert.Equal(0, parser.ErrorCount);
        Assert.InRange(allocated, 0, 3_000_000);
    }

    [Fact]
    public void EasyHex_BytewisePartialPayload_DoesNotCopyEveryExistingByte()
    {
        using var parser = new EasyHexProtocol(EasyHexDataType.U8);
        var frames = new List<ProtocolFrame>();
        byte[] oneByte = [1];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) parser.Feed(oneByte, frames);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 32_768);
        parser.Feed([255], frames);
        Assert.Equal(4096, Assert.Single(frames).Values.Length);
        Assert.Equal(0, parser.ErrorCount);
    }

    [Fact]
    public void Modbus_MaximumFrames_WorkAtEverySplitAndAcrossInternalBufferBoundaries()
    {
        byte[] payload = Enumerable.Repeat((byte)1, 246).ToArray();
        byte[] frame = ModbusFrame(payload);
        byte[] stream = [0, 0, .. frame, .. frame];
        for (int split = 0; split <= stream.Length; split++)
        {
            using var parser = new ModbusRtuProtocol(EasyHexDataType.U8);
            var frames = new List<ProtocolFrame>();
            parser.Feed(stream.AsSpan(0, split), frames);
            parser.Feed(stream.AsSpan(split), frames);
            Assert.Equal(2, frames.Count);
            Assert.All(frames, f => Assert.Equal(246, f.Values.Length));
            Assert.Equal(2, parser.ErrorCount);
        }
    }

    [Theory]
    [InlineData(EasyHexDataType.U8, 42d, new byte[] { 42 }, new byte[] { 255 })]
    [InlineData(EasyHexDataType.I8, -2d, new byte[] { 254 }, new byte[] { 128 })]
    [InlineData(EasyHexDataType.U16, 4660d, new byte[] { 0x12, 0x34 }, new byte[] { 255, 255 })]
    [InlineData(EasyHexDataType.I16, -2d, new byte[] { 255, 254 }, new byte[] { 128, 0 })]
    [InlineData(EasyHexDataType.U32, 305419896d, new byte[] { 0x12, 0x34, 0x56, 0x78 }, new byte[] { 255, 255, 255, 255 })]
    [InlineData(EasyHexDataType.I32, -2d, new byte[] { 255, 255, 255, 254 }, new byte[] { 128, 0, 0, 0 })]
    [InlineData(EasyHexDataType.I32, -305419896d, new byte[] { 0xed, 0xcb, 0xa9, 0x88 }, new byte[] { 128, 0, 0, 0 })]
    [InlineData(EasyHexDataType.F32, -2.5d, new byte[] { 0xc0, 0x20, 0, 0 }, new byte[] { 255, 255, 255, 255 })]
    public void BothProtocols_PreserveEveryDataTypeAndByteOrder(
        EasyHexDataType type, double value, byte[] payloadBE, byte[] tailBE)
    {
        foreach (var order in new[] { ByteOrder.BigEndian, ByteOrder.LittleEndian })
        {
            byte[] payload = order == ByteOrder.BigEndian ? payloadBE : payloadBE.Reverse().ToArray();
            byte[] tail = order == ByteOrder.BigEndian ? tailBE : tailBE.Reverse().ToArray();
            byte[] stream = [.. payload, .. tail, .. payload, .. tail];
            for (int split = 0; split <= stream.Length; split++)
            {
                using var parser = new EasyHexProtocol(type, order);
                var frames = new List<ProtocolFrame>();
                parser.Feed(stream.AsSpan(0, split), frames);
                parser.Feed(stream.AsSpan(split), frames);
                Assert.Equal(2, frames.Count);
                Assert.All(frames, f => Assert.Equal(value, Assert.Single(f.Values)));
                Assert.Equal(0, parser.ErrorCount);
            }
            using var modbus = new ModbusRtuProtocol(type, order);
            var modbusFrames = new List<ProtocolFrame>();
            modbus.Feed(ModbusFrame(payload), modbusFrames);
            Assert.Equal(value, Assert.Single(Assert.Single(modbusFrames).Values));
            Assert.Equal(0, modbus.ErrorCount);
        }
    }

    [Fact]
    public void EasyHex_UnterminatedLimit_DropsOnceAndAcceptsNextFrame()
    {
        using var parser = new EasyHexProtocol(EasyHexDataType.U8);
        var frames = new List<ProtocolFrame>();
        var payload = new byte[1 << 20];
        parser.Feed(payload, frames);
        Assert.Equal(0, parser.ErrorCount);
        parser.Feed([1], frames);
        Assert.Equal(1, parser.ErrorCount);
        Assert.Empty(frames);
        parser.Feed([42, 255], frames);
        Assert.Equal(42, Assert.Single(Assert.Single(frames).Values));
        Assert.Equal(1, parser.ErrorCount);
    }

    [Fact]
    public void EasyHex_CompleteLargeFrame_IsNotSubjectToUnterminatedLimit()
    {
        using var parser = new EasyHexProtocol(EasyHexDataType.U8);
        var frames = new List<ProtocolFrame>();
        var payload = new byte[(1 << 20) + 2];
        payload[^1] = 255;
        parser.Feed([0], frames);
        parser.Feed(payload, frames);
        Assert.Equal((1 << 20) + 2, Assert.Single(frames).Values.Length);
        Assert.Equal(0, parser.ErrorCount);
    }

    [Fact]
    public void Reset_DiscardsPartialFramesAndSearchCursor()
    {
        using var parser = new EasyHexProtocol(EasyHexDataType.I16);
        var frames = new List<ProtocolFrame>();
        parser.Feed([1, 0, 2, 0, 0], frames);
        parser.Reset();
        parser.Feed([42, 0, 0, 128], frames);
        Assert.Equal(42, Assert.Single(Assert.Single(frames).Values));
        Assert.Equal(0, parser.ErrorCount);
    }

    private static byte[] ModbusFrame(byte[] payload)
    {
        byte[] body = [1, 3, (byte)payload.Length, .. payload];
        return [.. body, .. Crc16.ComputeLittleEndian(body)];
    }
}
