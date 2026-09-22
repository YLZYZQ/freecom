using System.Globalization;
using System.Text;
using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

public class TextAllocationTests
{
    private static IProtocolParser Create(string name) => name switch
    {
        "TEXT" => new TextProtocol(), "CSV" => new CsvProtocol(), _ => new StampProtocol()
    };

    [Theory]
    [InlineData("TEXT")]
    [InlineData("CSV")]
    [InlineData("STAMP")]
    public void EveryByteSplit_PreservesUtf8NumbersErrorsAndStampOrder(string name)
    {
        const string numbers = "-0,4.9406564584124654e-324,1.7976931348623157e308,NaN,Infinity,-Infinity,1.2345678901234567";
        string source = name switch
        {
            "TEXT" => $"设备日志😀\r\n{{sensor}}{numbers}\r\n{{bad title}}1\n{{sensor}}1,,3\n{{sensor}}9,10\n",
            "CSV" => $"\u2003\u00a0\r\n\u2003{numbers}\u2003\r\n错误😀\n1,,3\n9,10\n",
            _ => $"设备日志😀\r\n<1>{{sensor}}{numbers}\r\n<1>{{sensor}}2\n<2>{{bad title}}1\n<2>{{sensor}}9,10\n"
        };
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        for (int split = 0; split <= bytes.Length; split++)
        {
            using var parser = Create(name);
            var frames = new List<ProtocolFrame>();
            parser.Feed(bytes.AsSpan(0, split), frames);
            parser.Feed(bytes.AsSpan(split), frames);
            Assert.Equal(2, parser.ErrorCount);
            Assert.Equal(2, frames.Count);
            double[] expected = [-0d, double.Epsilon, double.MaxValue, double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, 1.2345678901234567];
            Assert.Equal(expected.Length, frames[0].Values.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i]), BitConverter.DoubleToInt64Bits(frames[0].Values[i]));
            Assert.Equal(new double[] { 9, 10 }, frames[1].Values);
            Assert.Equal(name == "CSV" ? "csv" : "sensor", frames[0].Window);
            Assert.Equal(name == "STAMP" ? 1d : (double?)null, frames[0].Stamp);
            Assert.Equal(name == "STAMP" ? 2d : (double?)null, frames[1].Stamp);
        }
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("CSV")]
    [InlineData("STAMP")]
    public void InvalidUtf8_ReplacedAndCountedWithoutThrowing(string name)
    {
        using var parser = Create(name);
        string prefix = name switch { "TEXT" => "{w}", "STAMP" => "<1>{w}", _ => "" };
        byte[] bytes = [.. Encoding.UTF8.GetBytes(prefix), 0xc3, 0x28, (byte)'\n'];
        var frames = new List<ProtocolFrame>();
        foreach (byte b in bytes) parser.Feed(new[] { b }, frames);
        Assert.Empty(frames);
        Assert.Equal(1, parser.ErrorCount);
        parser.Reset();
        Assert.Equal(0, parser.ErrorCount);
        Assert.Single(parser.FeedText(prefix + "1\n"));
    }

    [Fact]
    public void LineAssembler_SplitCrlf_StripsExactlyOneCrAndOwnsReturnedBytes()
    {
        const string source = "first\r\n\r\nthird\r\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var parser = new LineAssembler();
            var lines = parser.Feed(bytes.AsSpan(0, split));
            lines.AddRange(parser.Feed(bytes.AsSpan(split)));
            parser.Feed(Encoding.UTF8.GetBytes("overwrite\n"));
            Assert.Equal(new[] { "first", "", "third\r" }, lines.Select(Encoding.UTF8.GetString));
        }
    }

    [Fact]
    public void PendingLimit_DropsOversizedTailAndResetDropsPartialUtf8()
    {
        var parser = new LineAssembler { MaxLineLength = 8 };
        Assert.Empty(parser.Feed(Encoding.UTF8.GetBytes("12345678")));
        Assert.Empty(parser.Feed(Encoding.UTF8.GetBytes("9")));
        Assert.Equal("ok", Encoding.UTF8.GetString(Assert.Single(parser.Feed(Encoding.UTF8.GetBytes("ok\n")))));
        Assert.Empty(parser.Feed(new byte[] { 0xe4 }));
        parser.Reset();
        Assert.Equal("new", Encoding.UTF8.GetString(Assert.Single(parser.Feed(Encoding.UTF8.GetBytes("new\n")))));
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("CSV")]
    [InlineData("STAMP")]
    public void LongLine_PooledDecodeBuffer_DoesNotEscapeIntoFrames(string name)
    {
        using var parser = Create(name);
        string prefix = name switch { "TEXT" => "{w}", "STAMP" => "<1>{w}", _ => "" };
        string payload = string.Join(',', Enumerable.Repeat("1.2345678901234567", 100));
        var first = Assert.Single(parser.FeedText(prefix + payload + "\n"));
        var second = Assert.Single(parser.FeedText(prefix.Replace("<1>", "<2>") + payload.Replace("1.2345678901234567", "9") + "\n"));
        Assert.All(first.Values, value => Assert.Equal(1.2345678901234567, value));
        Assert.All(second.Values, value => Assert.Equal(9, value));
        Assert.NotSame(first.Values, second.Values);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("CSV")]
    [InlineData("STAMP")]
    public void SteadyState_AllocatesOnlyOwnedFrameAndValues(string name)
    {
        const int count = 10_000;
        using var parser = Create(name);
        var input = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (name == "STAMP") input.Append('<').Append(i.ToString(CultureInfo.InvariantCulture)).Append('>');
            if (name != "CSV") input.Append("{sensor}");
            input.Append("1,2,3,4,5,6\n");
        }
        byte[] bytes = Encoding.UTF8.GetBytes(input.ToString());
        var output = new List<ProtocolFrame>(1024);
        parser.Feed(bytes.AsSpan(0, Math.Min(4096, bytes.Length)), output);
        parser.Reset();
        output.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int parsed = 0;
        for (int offset = 0; offset < bytes.Length; offset += 17)
        {
            parser.Feed(bytes.AsSpan(offset, Math.Min(17, bytes.Length - offset)), output);
            parsed += output.Count;
            output.Clear();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count, parsed);
        Assert.Equal(0, parser.ErrorCount);
        Assert.InRange(allocated, 1L, count * 160L);
    }
}
