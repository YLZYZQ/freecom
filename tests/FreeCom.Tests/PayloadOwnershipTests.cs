using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

public class PayloadOwnershipTests
{
    [Fact]
    public async Task PipelineLogsShareOwnedReadOnlyDataAndIsolateCallerAndLegacyCopies()
    {
        using var pipeline = new DataPipeline(new TextProtocol());
        byte[] input = [1, 2, 3];
        await pipeline.SendBytesAsync(input);
        input[0] = 99;
        var raw = Assert.Single(pipeline.Raw.Snapshot());
        var display = Assert.Single(pipeline.Display.Snapshot());
        Assert.True(raw.Data.Equals(display.Data));
        Assert.Equal(new byte[] { 1, 2, 3 }, raw.Data.ToArray());
        var legacyCopy = raw.Bytes;
        legacyCopy[0] = 77;
        var displayCopy = display.Bytes;
        displayCopy[1] = 77;
        Assert.Equal(new byte[] { 1, 2, 3 }, display.Data.ToArray());
        pipeline.Raw.Clear();
        pipeline.Display.Clear();
        Assert.Equal(new byte[] { 1, 2, 3 }, raw.Data.ToArray()); // stable snapshots survive clear
    }

    [Fact]
    public void PublicAppendRetainsIndependentCopyOfCallerData()
    {
        var raw = new RawLog();
        var display = new DisplaySink();
        byte[] input = [42];
        raw.Append(DataDirection.Rx, input);
        display.Append(DataDirection.Rx, input);
        input[0] = 99;
        Assert.Equal(42, Assert.Single(raw.Snapshot()).Data.Span[0]);
        Assert.Equal(42, Assert.Single(display.Snapshot()).Data.Span[0]);
    }

    [Fact]
    public async Task PipelineAllocatesOnePayloadCopyPerSend()
    {
        using var pipeline = new DataPipeline(new TextProtocol());
        byte[] block = new byte[65536];
        await pipeline.SendBytesAsync(block);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) await pipeline.SendBytesAsync(block);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 64 * block.Length, 64 * block.Length + 128 * 1024);
    }
}

[Collection("SerialBasis")]
public class ReceivePayloadOwnershipTests
{
    [Fact]
    public async Task ReceivedLogsSharePayloadAndRemainReadableAfterFurtherSerialReads()
    {
        using var ctx = PipelineCtx.Create();
        ctx.Peer.SendText("{owned}1,2,3\n");
        await Wait.FramesAsync(ctx.Pipeline, 1);
        var first = ctx.Pipeline.Raw.Snapshot()[0];
        var display = ctx.Pipeline.Display.Snapshot()[0];
        Assert.True(first.Data.Equals(display.Data));
        var copy = first.Data.ToArray();
        ctx.Peer.SendText("{owned}4,5,6\n");
        await Wait.FramesAsync(ctx.Pipeline, 2);
        Assert.Equal(copy, first.Data.ToArray());
    }
}
