using FreeCom.Core.Pipeline;
using Xunit;

namespace FreeCom.Tests;

/// <summary>静默间隔组帧器：分片拼接/静默切帧/尾帧冲刷/直通模式。</summary>
public class FrameAssemblerTests
{
    private static byte[] B(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    [Fact]
    public void FastFragments_MergedIntoOneFrame()
    {
        var frames = new List<byte[]>();
        using (var fa = new FrameAssembler(40, f => frames.Add(f)))
        {
            fa.Feed(B("DIST"));
            Thread.Sleep(5);
            fa.Feed(B("ANCE:43, "));
            Thread.Sleep(5);
            fa.Feed(B("ANGLE:110.77\r\n"));
            Thread.Sleep(5);
            Assert.Empty(frames); // 静默未到：不成帧
        } // Dispose 冲刷尾帧
        Assert.Single(frames);
        Assert.Equal("DISTANCE:43, ANGLE:110.77\r\n", System.Text.Encoding.ASCII.GetString(frames[0]));
    }

    [Fact]
    public void Silence_SplitsFrames()
    {
        var frames = new List<byte[]>();
        using (var fa = new FrameAssembler(30, f => frames.Add(f)))
        {
            fa.Feed(B("line1\n"));
            Thread.Sleep(80); // 超过静默间隔 → 第一帧交付
            fa.Feed(B("line2\n"));
            Thread.Sleep(80);
        }
        Assert.Equal(2, frames.Count);
        Assert.Equal("line1\n", System.Text.Encoding.ASCII.GetString(frames[0]));
        Assert.Equal("line2\n", System.Text.Encoding.ASCII.GetString(frames[1]));
    }

    [Fact]
    public void Dispose_FlushesPendingTail()
    {
        var frames = new List<byte[]>();
        var fa = new FrameAssembler(500, f => frames.Add(f)); // 长 gap：不等到期
        fa.Feed(B("tail"));
        Assert.Empty(frames);
        fa.Dispose(); // 关闭即冲刷
        Assert.Single(frames);
        Assert.Equal("tail", System.Text.Encoding.ASCII.GetString(frames[0]));
    }

    [Fact]
    public void GapZero_PassthroughPerChunk()
    {
        var frames = new List<byte[]>();
        using (var fa = new FrameAssembler(0, f => frames.Add(f)))
        {
            fa.Feed(B("a"));
            fa.Feed(B("b"));
        }
        Assert.Equal(2, frames.Count); // 直通模式：一片一条（旧行为）
    }
}

/// <summary>空行修复：条目自带尾换行吸收为条目分隔（RenderText/导出不产生空行）。</summary>
public class EntryNewlineTests
{
    [Fact]
    public void CrlfTail_Absorbed()
    {
        Assert.Equal("abc\n", FreeCom.Core.Pipeline.DisplaySink.WithEntryNewline("abc\r\n"));
        Assert.Equal("abc\n", FreeCom.Core.Pipeline.DisplaySink.WithEntryNewline("abc\n"));
    }

    [Fact]
    public void NoTail_GetsNewline()
    {
        Assert.Equal("abc\n", FreeCom.Core.Pipeline.DisplaySink.WithEntryNewline("abc"));
    }

    [Fact]
    public void RenderText_NoBlankLinesBetweenEntries()
    {
        var sink = new FreeCom.Core.Pipeline.DisplaySink();
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("DISTANCE:43\r\n"));
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("DISTANCE:44\r\n"));
        var text = sink.RenderText(includeTimestamp: false);
        Assert.Equal("<< DISTANCE:43\n<< DISTANCE:44\n", text);
    }
}

/// <summary>偶发空行场景：行尾换行独立成帧（设备行数据与 \r\n 间隔超组帧间隔）、帧头换行。</summary>
public class BlankEntryTests
{
    [Fact]
    public void StandaloneNewlineFrame_Skipped()
    {
        var sink = new FreeCom.Core.Pipeline.DisplaySink();
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("DISTANCE:43\r\n"));
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("\r\n")); // 行尾独立成帧
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("DISTANCE:44\r\n"));
        Assert.Equal("<< DISTANCE:43\n<< DISTANCE:44\n", sink.RenderText(includeTimestamp: false));
    }

    [Fact]
    public void LeadingNewlineFrame_Stripped()
    {
        var sink = new FreeCom.Core.Pipeline.DisplaySink();
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("DISTANCE:43"));   // 无行尾（切在行尾前）
        sink.Append(FreeCom.Core.Pipeline.DataDirection.Rx, System.Text.Encoding.ASCII.GetBytes("\r\nDISTANCE:44\r\n")); // 帧头带换行
        Assert.Equal("<< DISTANCE:43\n<< DISTANCE:44\n", sink.RenderText(includeTimestamp: false));
    }

    [Fact]
    public void BlankOnlyEntry_ReturnsEmpty()
    {
        Assert.Equal("", FreeCom.Core.Pipeline.DisplaySink.NormalizeEntryText("\r\n"));
        Assert.Equal("", FreeCom.Core.Pipeline.DisplaySink.NormalizeEntryText("\n"));
        Assert.Equal("", FreeCom.Core.Pipeline.DisplaySink.NormalizeEntryText(""));
    }
}
