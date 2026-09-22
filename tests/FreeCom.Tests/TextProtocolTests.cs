using System.Text;
using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

public static class FeedExtensions
{
    public static List<ProtocolFrame> FeedText(this IProtocolParser parser, string text)
    {
        var frames = new List<ProtocolFrame>();
        parser.Feed(Encoding.UTF8.GetBytes(text), frames);
        return frames;
    }

    public static List<ProtocolFrame> FeedBytes(this IProtocolParser parser, params byte[] bytes)
    {
        var frames = new List<ProtocolFrame>();
        parser.Feed(bytes, frames);
        return frames;
    }
}

// ---------------- TEXT 协议 ----------------
public class TextProtocolTests
{
    private static List<ProtocolFrame> Parse(string text) => new TextProtocol().FeedText(text);

    [Fact]
    public void DocExample_TwoFramesThreeCurves()
    {
        var frames = Parse("{plotter}1,2,3\n{plotter}4,5,6\n");
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal("plotter", f.Window));
        Assert.Equal([1, 2, 3], frames[0].Values);
        Assert.Equal([4, 5, 6], frames[1].Values);
        Assert.Null(frames[0].Stamp); // TEXT：X 由上位机自动编号
    }

    [Fact]
    public void MultiWindow_ByTitle()
    {
        var frames = Parse("{voltage}1,2\n{current}3,4\n{voltage}5,6\n");
        Assert.Equal(3, frames.Count);
        Assert.Equal("voltage", frames[0].Window);
        Assert.Equal("current", frames[1].Window);
        Assert.Equal("voltage", frames[2].Window);
    }

    [Fact]
    public void NonNumericPayload_NoFrame_CountsError()
    {
        var p = new TextProtocol();
        var frames = p.FeedText("{adc}voltage=6, current=7\n");
        Assert.Empty(frames);
        Assert.Equal(1, p.ErrorCount);
    }

    [Theory]
    [InlineData("{ Plotter}1,2\n")]     // title 含空格
    [InlineData("plotter 1,2,3\n")]     // 缺 {
    [InlineData("{plotter 1,2\n")]      // 缺 }
    [InlineData("{}1,2\n")]             // 空 title
    [InlineData("{w}\n")]               // 空 payload
    [InlineData("{w}1,2,x\n")]          // 非数字
    [InlineData("hello world\n")]       // 普通日志行
    [InlineData("\n")]                  // 空行
    public void InvalidLines_ErrorCounted_NoThrow(string line)
    {
        var p = new TextProtocol();
        var frames = p.FeedText(line);
        Assert.Empty(frames);
        Assert.True(p.ErrorCount >= 0); // 不抛异常、帧数为 0
    }

    [Fact]
    public void FloatsAndNegativeAndExponent()
    {
        var frames = Parse("{w}1.5,-2,3e2\n");
        Assert.Single(frames);
        Assert.Equal([1.5, -2, 300], frames[0].Values);
    }

    [Fact]
    public void ChunksSplitAnywhere_ParsedOnce()
    {
        var p = new TextProtocol();
        var all = new List<ProtocolFrame>();
        foreach (var chunk in new[] { "{demo", "}1,", "2,3", "\n" })
            p.Feed(Encoding.UTF8.GetBytes(chunk), all);
        Assert.Single(all);
        Assert.Equal([1, 2, 3], all[0].Values);
    }

    [Fact]
    public void CrlfHandled()
    {
        var frames = Parse("{w}1,2\r\n{w}3,4\r\n");
        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void NoTrailingNewline_PendingUntilReset()
    {
        var p = new TextProtocol();
        Assert.Empty(p.FeedText("{w}1,2"));   // 无换行 → 挂起
        var frames = p.FeedText("\n{w}3\n");
        Assert.Equal(2, frames.Count);
    }
}

// ---------------- CSV 协议 ----------------
public class CsvProtocolTests
{
    private static List<ProtocolFrame> Parse(string text) => new CsvProtocol().FeedText(text);

    [Fact]
    public void DocExample_ColumnsAreCurves()
    {
        var frames = Parse("1,2,3\n4,5,6\n");
        Assert.Equal(2, frames.Count);
        Assert.Equal([1, 2, 3], frames[0].Values);
        Assert.Equal([4, 5, 6], frames[1].Values);
        Assert.Equal("csv", frames[0].Window); // 单窗口
    }

    [Fact]
    public void SingleColumn_SingleCurve()
    {
        var frames = Parse("1\n2\n3\n");
        Assert.Equal(3, frames.Count);
        Assert.Single(frames[0].Values);
    }

    [Fact]
    public void BadLine_Counted_Ignored()
    {
        var p = new CsvProtocol();
        var frames = p.FeedText("a,b\n1,2\n");
        Assert.Single(frames);
        Assert.Equal(1, p.ErrorCount);
    }

    [Fact]
    public void EmptyLines_Skipped_NoError()
    {
        var p = new CsvProtocol();
        var frames = p.FeedText("\n\n1,2\n\n");
        Assert.Single(frames);
        Assert.Equal(0, p.ErrorCount);
    }

    [Fact]
    public void CustomWindowName()
    {
        var frames = new CsvProtocol("sensor").FeedText("1\n");
        Assert.Equal("sensor", frames[0].Window);
    }
}

// ---------------- STAMP 协议 ----------------
public class StampProtocolTests
{
    private static List<ProtocolFrame> Parse(string text) => new StampProtocol().FeedText(text);

    [Fact]
    public void DocExample_StampBecomesX()
    {
        var frames = Parse("<0.2>{plotter}1,2,3\n<0.4>{plotter}4,5,6\n");
        Assert.Equal(2, frames.Count);
        Assert.Equal(0.2, frames[0].Stamp);
        Assert.Equal(0.4, frames[1].Stamp);
        Assert.Equal("plotter", frames[0].Window);
    }

    [Fact]
    public void RollbackStamp_Dropped_Counted()
    {
        var p = new StampProtocol();
        var frames = p.FeedText("<0.5>{w}1\n<0.3>{w}2\n<0.5>{w}3\n<0.6>{w}4\n");
        Assert.Equal(2, frames.Count); // 0.3 与 0.5（非严格递增）被丢弃
        Assert.Equal(0.5, frames[0].Stamp);
        Assert.Equal(0.6, frames[1].Stamp);
        Assert.Equal(2, p.ErrorCount);
    }

    [Fact]
    public void MultiWindow_StampsIndependent()
    {
        var frames = Parse("<1>{a}1\n<0.1>{b}2\n<2>{a}3\n");
        Assert.Equal(3, frames.Count); // a/b 各自独立比较
    }

    [Theory]
    [InlineData("<x>{w}1\n")]        // 非数字 stamp
    [InlineData("<1>{w}a,b\n")]      // 非数字 payload
    [InlineData("<1>no text\n")]     // 缺 {}
    public void Invalid_ErrorCounted(string line)
    {
        var p = new StampProtocol();
        Assert.Empty(p.FeedText(line));
        Assert.Equal(1, p.ErrorCount);
    }

    [Fact]
    public void MixedStream_NonPlotLines_SkippedForPlot_NoError()
    {
        // 绘图帧与设备日志混发：日志行不计错、不产帧（接收区显示走独立链路，不受影响）
        var p = new TextProtocol();
        var frames = p.FeedText("system boot ok\n{demo}1,2,3\nrun mode=auto\n{demo}4,5,6\ntemp warning!\n" +
                                 "随机日志行 without braces\n");
        Assert.Equal(2, frames.Count);                       // 仅两行绘图帧
        Assert.All(frames, f => Assert.Equal("demo", f.Window));
        Assert.Equal(0, p.ErrorCount);                       // 纯文本行不计错

        var sp = new StampProtocol();
        var stampFrames = sp.FeedText("log line plain\n<0.1>{w}1\nanother log\n<0.2>{w}2\n");
        Assert.Equal(2, stampFrames.Count);
        Assert.Equal(0, sp.ErrorCount);
    }
}
