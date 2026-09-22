using FreeCom.Core;
using Xunit;

namespace FreeCom.Tests;

public class HexAllocationTests
{
    [Fact]
    public void EveryByteFormatsExactlyWithoutTrailingSpace()
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
        Assert.Equal(string.Join(" ", bytes.Select(x => x.ToString("X2"))), HexParse.ToHexSpaced(bytes));
        Assert.Equal("", HexParse.ToHexSpaced([]));
        Assert.Equal("00", HexParse.ToHexSpaced([0]));
    }

    [Fact]
    public void HexFormattingDoesNotAllocateAStringPerByte()
    {
        byte[] bytes = new byte[65536];
        HexParse.ToHexSpaced(bytes.AsSpan(0, 32));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var text = HexParse.ToHexSpaced(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(3 * bytes.Length - 1, text.Length);
        Assert.InRange(allocated, 0, bytes.Length * 14);
    }
}
