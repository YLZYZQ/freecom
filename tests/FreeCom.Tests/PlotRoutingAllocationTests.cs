using FreeCom.Core.Plots;
using Xunit;

namespace FreeCom.Tests;

public class PlotRoutingAllocationTests
{
    [Fact]
    public void RepeatedFramesDoNotAllocateRoutingClosures()
    {
        var plots = new PlotService { MaxPointsPerCurve = 16 };
        double[] values = [1, 2, 3];
        for (int i = 0; i < 100; i++) plots.AddPlotFrame("scope", values, null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) plots.AddPlotFrame("scope", values, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1024);
        Assert.Equal(30300, plots.Find("scope")!.TotalPoints);
    }

    [Fact]
    public void RoutingRespectsRenamedAndRemovedWindows()
    {
        var plots = new PlotService();
        var first = plots.GetOrCreate("a");
        first.Title = "Renamed";
        Assert.Same(first, plots.GetOrCreate("renamed"));
        var second = plots.GetOrCreate("a");
        Assert.NotSame(first, second);
        Assert.True(plots.Remove(first.Id));
        Assert.NotSame(first, plots.GetOrCreate("Renamed"));
    }
}
