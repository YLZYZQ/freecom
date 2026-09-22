using FreeCom.Core.Pipeline;
using Xunit;

namespace FreeCom.Tests;

public class DisplayRecentTests
{
    [Fact]
    public void FilterIsAppliedBeforeTheRecentByteBudget()
    {
        var display = new DisplaySink();
        display.Append(DataDirection.Tx, new byte[1]);
        for (int i = 0; i < 100; i++) display.Append(DataDirection.Rx, new byte[1024]);
        var match = Assert.Single(display.SnapshotRecent(0, byteBudget: 1024,
            predicate: e => e.Dir == DataDirection.Tx));
        Assert.Equal(1, match.Seq);
    }

    [Fact]
    public void RecentSnapshotHasByteAndEntryLimitsAndKeepsNewest()
    {
        var display = new DisplaySink();
        for (int i = 0; i < 100; i++) display.Append(DataDirection.Rx, new byte[1024]);
        var recent = display.SnapshotRecent(0, byteBudget: 4096, limit: 3);
        Assert.Equal(new long[] { 98, 99, 100 }, recent.Select(e => e.Seq));
        Assert.Equal(100, display.Snapshot().Count); // view limits do not discard export data
        Assert.Equal(new long[] { 99, 100 }, display.SnapshotRecent(98).Select(e => e.Seq));
        Assert.Empty(display.SnapshotRecent(100));
    }

    [Fact]
    public void OversizedNewestPacketIsAvailableAndCursorDoesNotSkipLaterArrival()
    {
        var display = new DisplaySink();
        display.Append(DataDirection.Rx, new byte[8192]);
        var snapshot = display.SnapshotRecent(0, byteBudget: 1024);
        Assert.Single(snapshot);
        display.Append(DataDirection.Tx, new byte[1]);
        Assert.Equal(2, Assert.Single(display.SnapshotRecent(snapshot[^1].Seq)).Seq);
        Assert.Empty(display.SnapshotRecent(0, limit: 0));
        Assert.Empty(display.SnapshotRecent(0, byteBudget: 0));
    }
}
