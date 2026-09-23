using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;
using Xunit;

namespace FreeCom.Tests;

public class PlotServiceTests
{
    [Fact]
    public void WindowReusedByTitle()
    {
        var plots = new PlotService();
        plots.AddPlotFrame("voltage", [1, 2], null);
        plots.AddPlotFrame("voltage", [3, 4], null);
        var windows = plots.SnapshotWindows();
        Assert.Single(windows);
        Assert.Equal(2, windows[0].Curves.Count);
        Assert.Equal(4, windows[0].TotalPoints);
    }

    [Fact]
    public void LastPoint_TracksRingHead()
    {
        var plots = new PlotService();
        var win = plots.GetOrCreate("w");
        win.Add([1], null);
        win.Add([2], null);
        Assert.Equal((1, 2), win.Curves[0].LastPoint());
        // 环形淘汰后仍取最新（X 为自动索引：第 3 帧 → 2）
        win.Curves[0].MaxPoints = 2;
        win.Add([3], null);
        var last = win.Curves[0].LastPoint();
        Assert.NotNull(last);
        Assert.Equal(2, last!.Value.X);
        Assert.Equal(3, last!.Value.Y);
        // 清空后无点
        win.ClearData();
        Assert.Null(win.Curves[0].LastPoint());
    }

    [Fact]
    public void XFromEnd_TracksRingWindow()
    {
        var plots = new PlotService();
        var win = plots.GetOrCreate("w");
        for (int i = 0; i < 5; i++) win.Add([i], null);
        var curve = win.Curves[0];
        Assert.Equal(4, curve.XFromEnd(0));   // back=0 即最新点 X
        Assert.Equal(2, curve.XFromEnd(2));   // 往前数第 2 个点
        Assert.Equal(0, curve.XFromEnd(999)); // 窗口比数据大 → 回退到最老点（曲线从左向右生长）
        // 环形淘汰后仍正确
        curve.MaxPoints = 3;
        win.Add([100], null);                 // 存活 X=3,4,5
        Assert.Equal(5, curve.XFromEnd(0));
        Assert.Equal(3, curve.XFromEnd(2));
        Assert.Equal(3, curve.XFromEnd(999)); // 越界回退到最老存活点
        win.ClearData();
        Assert.Null(curve.XFromEnd(0));
    }

    [Fact]
    public void SnapshotTailInto_TakesNewestExactly()
    {
        var plots = new PlotService();
        var win = plots.GetOrCreate("w");
        for (int i = 0; i < 100; i++) win.Add([i], null);
        var curve = win.Curves[0];
        var xs = new double[5]; var ys = new double[5];
        // 滚动窗口渲染：取末尾 N 点（对比 SnapshotInto 的全曲线均匀抽稀）
        var take = curve.SnapshotTailInto(xs, ys, 5);
        Assert.Equal(5, take);
        Assert.Equal([95.0, 96, 97, 98, 99], xs);
        Assert.Equal([95.0, 96, 97, 98, 99], ys);
        // 请求超过现有点数 → 全量
        var big = new double[200]; var bigY = new double[200];
        Assert.Equal(100, curve.SnapshotTailInto(big, bigY, 200));
        Assert.Equal(99.0, big[99]);
        // 环形淘汰后仍取最新（MaxPoints=10 保留 X=90..99，再 Add X=100 → 存活 91..100）
        curve.MaxPoints = 10;
        win.Add([100], null);
        Assert.Equal(5, curve.SnapshotTailInto(xs, ys, 5));
        Assert.Equal([96.0, 97, 98, 99, 100], ys);
    }

    [Fact]
    public void AutoX_IncrementsPerFrame()
    {
        var plots = new PlotService();
        plots.AddPlotFrame("w", [10, 20], null);
        plots.AddPlotFrame("w", [11, 21], null);
        var curves = plots.SnapshotWindows()[0].Curves;
        Assert.Equal([0, 1], curves[0].Snapshot().Xs);
        Assert.Equal([0, 1], curves[1].Snapshot().Xs);
    }

    [Fact]
    public void StampBecomesX()
    {
        var plots = new PlotService();
        plots.AddPlotFrame("w", [10], 0.5);
        plots.AddPlotFrame("w", [11], 1.5);
        Assert.Equal([0.5, 1.5], plots.SnapshotWindows()[0].Curves[0].Snapshot().Xs);
    }

    [Fact]
    public void NewWindowInheritsTemplateAutoY()
    {
        var plots = new PlotService();
        var first = plots.GetOrCreate("template");
        first.AutoY = false;                       // 修改默认（模板）窗口
        var second = plots.GetOrCreate("another");
        Assert.False(second.AutoY);                // 新窗口继承
    }

    [Fact]
    public void DefaultAutoY_WhenNoTemplate()
    {
        var plots = new PlotService { DefaultAutoY = false };
        Assert.False(plots.GetOrCreate("first").AutoY);
    }

    [Fact]
    public void MaxSixteenCurves()
    {
        var plots = new PlotService();
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        plots.AddPlotFrame("w", values, null);
        Assert.Equal(PlotWindow.MaxCurves, plots.SnapshotWindows()[0].Curves.Count);
    }

    [Fact]
    public void CurvePointCap_RingEviction()
    {
        var plots = new PlotService { MaxPointsPerCurve = 100 };
        var win = plots.GetOrCreate("w");
        win.MaxPointsPerCurve = 100;
        for (int i = 0; i < 5000; i++) win.Add([i], null);
        Assert.Equal(100, win.Curves[0].Count); // 严格环形上限
        Assert.Equal(5000, win.TotalPoints);
    }

    [Fact]
    public void Snapshot_Downsampling()
    {
        var plots = new PlotService();
        var win = plots.GetOrCreate("w");
        for (int i = 0; i < 1000; i++) win.Add([i], null);
        var snap = win.Curves[0].Snapshot(maxPoints: 100);
        Assert.Equal(100, snap.Xs.Length);
        Assert.Equal(0, snap.Xs[0]);    // 均匀抽稀保留首尾
        Assert.Equal(999, snap.Xs[^1]); // 实时绘图包含最新点
        Assert.Equal(1000, snap.TotalPoints);
    }

    [Fact]
    public void Find_ByIdOrTitle_CaseInsensitive()
    {
        var plots = new PlotService();
        var w = plots.GetOrCreate("Voltage");
        Assert.Same(w, plots.Find("voltage"));
        Assert.Same(w, plots.Find(w.Id));
        Assert.Null(plots.Find("nope"));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var plots = new PlotService();
        plots.AddPlotFrame("a", [1], null);
        plots.AddPlotFrame("b", [2], null);
        Assert.True(plots.Remove(plots.Find("a")!.Id));
        Assert.Single(plots.SnapshotWindows());
        plots.ClearData();
        Assert.Equal(0, plots.SnapshotWindows()[0].TotalPoints);
    }
}

public class DisplaySinkTests
{
    [Fact]
    public void RingCapacity_Enforced()
    {
        var sink = new DisplaySink(capacity: 100);
        for (int i = 0; i < 500; i++) sink.Append(DataDirection.Rx, [1]);
        Assert.Equal(100, sink.Snapshot().Count);
        Assert.Equal(401, sink.Snapshot()[0].Seq); // 最旧被淘汰
    }

    [Fact]
    public void SincePaging()
    {
        var sink = new DisplaySink();
        for (int i = 0; i < 10; i++) sink.Append(DataDirection.Rx, [1]);
        var page = sink.Snapshot(sinceSeq: 7, limit: 2);
        Assert.Equal([8, 9], page.Select(e => e.Seq));
    }

    [Fact]
    public void RenderText_DirectionMarkers()
    {
        var sink = new DisplaySink();
        sink.Append(DataDirection.Rx, "hello"u8.ToArray());
        sink.Append(DataDirection.Tx, "AT+RST"u8.ToArray());
        var text = sink.RenderText(includeTimestamp: false);
        Assert.StartsWith("<< hello", text);        // RX 双向标记
        Assert.Contains(">> AT+RST", text);         // TX 带 >> 前缀
    }

    [Fact]
    public void RenderText_TimestampEnabled()
    {
        var sink = new DisplaySink();
        sink.Append(DataDirection.Rx, "data"u8.ToArray());
        var text = sink.RenderText(includeTimestamp: true);
        Assert.StartsWith("[", text);               // [HH:mm:ss.fff] << data
        Assert.Contains("] << data", text);
    }

    [Fact]
    public void RenderHex_Spaced()
    {
        var sink = new DisplaySink();
        sink.Append(DataDirection.Rx, [0xAA, 0x55]);
        Assert.Contains("AA 55", sink.RenderHex(includeTimestamp: false));
    }
}

public class RawLogTests
{
    [Fact]
    public void AppendSnapshot_WithDirectionFilter()
    {
        var log = new RawLog();
        log.Append(DataDirection.Rx, [1]);
        log.Append(DataDirection.Tx, [2]);
        log.Append(DataDirection.Rx, [3]);
        Assert.Equal(2, log.Snapshot(dir: DataDirection.Rx).Count);
        Assert.Single(log.Snapshot(dir: DataDirection.Tx));
    }

    [Fact]
    public void CollectBytes_RxOnly_InOrder()
    {
        var log = new RawLog();
        log.Append(DataDirection.Rx, [1, 2]);
        log.Append(DataDirection.Tx, [9]);
        log.Append(DataDirection.Rx, [3]);
        Assert.Equal([1, 2, 3], log.CollectBytes(DataDirection.Rx));
    }

    [Fact]
    public void CapacityRing()
    {
        var log = new RawLog(capacity: 100);
        for (int i = 0; i < 500; i++) log.Append(DataDirection.Rx, [1]);
        Assert.Equal(100, log.Snapshot().Count);
    }
}

public class CountersTests
{
    [Fact]
    public void CountersAccumulate()
    {
        var c = new Counters();
        c.AddRx(10);
        c.AddRx(5);
        c.AddTx(3);
        c.AddFrames(2);
        c.SetParseErrors(7);
        Assert.Equal(15, c.RxBytes);
        Assert.Equal(3, c.TxBytes);
        Assert.Equal(2, c.FramesParsed);
        Assert.Equal(7, c.ParseErrors);
        Assert.True(c.RxRatePerSecond > 0);
    }
}
