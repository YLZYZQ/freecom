using System.Reflection;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;
using Xunit;

namespace FreeCom.Tests;

public class MemoryBoundsTests
{
    [Fact]
    public void Curve_WrapsAtExactLimit_AndPreservesLatestOrderedPairs()
    {
        var curve = new Curve(0) { MaxPoints = 3 };
        for (int i = 0; i < 20; i++)
        {
            curve.AddPoint(i, -i);
            Assert.Equal(Math.Min(i + 1, 3), curve.Count);
        }

        var snapshot = curve.Snapshot();
        Assert.Equal(new double[] { 17, 18, 19 }, snapshot.Xs);
        Assert.Equal(new double[] { -17, -18, -19 }, snapshot.Ys);
        Assert.Equal(3, snapshot.TotalPoints);
    }

    [Fact]
    public void Curve_GrowsOnDemand_WithoutExceedingStorageBudget_AndClearReleasesStorage()
    {
        var curve = new Curve(0);
        Assert.Equal(500_000, curve.MaxPoints);
        Assert.Equal(0, StorageCapacity(curve, "_xs"));
        curve.MaxPoints = 1000;
        curve.AddPoint(0, 0);
        Assert.InRange(StorageCapacity(curve, "_xs"), 1, 999);
        for (int i = 1; i < 2000; i++) curve.AddPoint(i, i);
        Assert.Equal(1000, StorageCapacity(curve, "_xs"));
        Assert.Equal(1000, StorageCapacity(curve, "_ys"));
        Assert.Equal(1000, curve.Count);

        curve.Clear();
        Assert.Equal(0, curve.Count);
        Assert.Equal(0, StorageCapacity(curve, "_xs"));
        Assert.Equal(0, StorageCapacity(curve, "_ys"));
        Assert.Empty(curve.Snapshot().Xs);
        curve.AddPoint(7, 9);
        Assert.Equal(new double[] { 7 }, curve.Snapshot().Xs);
        Assert.Equal(new double[] { 9 }, curve.Snapshot().Ys);
    }

    [Fact]
    public void Curve_ShrinkingWrappedStorageImmediatelyRetainsNewestPoints()
    {
        var curve = new Curve(0) { MaxPoints = 7 };
        for (int i = 0; i < 20; i++) curve.AddPoint(i, i * 2);
        curve.MaxPoints = 3;
        Assert.Equal(3, curve.Count);
        Assert.Equal(3, StorageCapacity(curve, "_xs"));
        Assert.Equal(new double[] { 17, 18, 19 }, curve.Snapshot().Xs);

        curve.MaxPoints = 6;
        curve.AddPoint(20, 40);
        Assert.Equal(new double[] { 17, 18, 19, 20 }, curve.Snapshot().Xs);
        Assert.Equal(new double[] { 34, 36, 38, 40 }, curve.Snapshot().Ys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Curve_NonpositiveCapacityDiscardsPointsAndReleasesStorage(int capacity)
    {
        var curve = new Curve(0);
        curve.AddPoint(1, 2);
        curve.MaxPoints = capacity;
        curve.AddPoint(3, 4);
        Assert.Equal(0, curve.Count);
        Assert.Equal(0, StorageCapacity(curve, "_xs"));
        Assert.Empty(curve.Snapshot().Xs);
    }

    [Fact]
    public void Curve_DecimatedSnapshotsAgreeAndIncludeLatestPointAfterWrap()
    {
        var curve = new Curve(0) { MaxPoints = 10 };
        for (int i = 0; i < 15; i++) curve.AddPoint(i, -i);
        var snapshot = curve.Snapshot(4);
        Assert.Equal(new double[] { 5, 8, 11, 14 }, snapshot.Xs);
        Assert.Equal(10, snapshot.TotalPoints);

        var xs = new double[6];
        var ys = new double[4];
        Assert.Equal(4, curve.SnapshotInto(xs, ys, 100));
        Assert.Equal(snapshot.Xs, xs.Take(4));
        Assert.Equal(snapshot.Ys, ys);
        Assert.Equal(new double[] { 14 }, curve.Snapshot(1).Xs);
        Assert.Equal(1, curve.SnapshotInto(xs, ys, 1));
        Assert.Equal(14, xs[0]);
        Assert.Equal(-14, ys[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Curve_ZeroSnapshotBudgetIsSafeAndDoesNotWriteBuffers(int cap)
    {
        var curve = new Curve(0);
        curve.AddPoint(1, 2);
        Assert.Empty(curve.Snapshot(cap).Xs);
        Assert.Equal(1, curve.Snapshot(cap).TotalPoints);
        var xs = new double[] { 42 };
        var ys = new double[] { 43 };
        Assert.Equal(0, curve.SnapshotInto(xs, ys, cap));
        Assert.Equal(42, xs[0]);
        Assert.Equal(43, ys[0]);
        Assert.Equal(0, curve.SnapshotInto([], ys, 10));
        Assert.Equal(0, curve.SnapshotInto(xs, [], 10));
    }

    [Fact]
    public void Curve_EmptySnapshotIntoReturnsZero()
    {
        var curve = new Curve(0);
        Assert.Equal(0, curve.SnapshotInto(new double[1], new double[1], 1));
    }

    [Fact]
    public void RawLog_ClearRestoresFullByteBudgetAndPreservesSequence()
    {
        var log = new RawLog(byteBudget: 8);
        log.Append(DataDirection.Rx, new byte[8]);
        log.Clear();
        Assert.Empty(log.Snapshot());
        log.Append(DataDirection.Rx, new byte[4]);
        log.Append(DataDirection.Tx, new byte[4]);
        Assert.Equal(new long[] { 2, 3 }, log.Snapshot().Select(e => e.Seq));
        log.Append(DataDirection.Rx, new byte[4]);
        Assert.Equal(new long[] { 3, 4 }, log.Snapshot().Select(e => e.Seq));
        Assert.Equal(8, log.CollectBytes().Length);
    }

    [Fact]
    public void DisplaySink_ClearRestoresFullByteBudgetAndPreservesSequence()
    {
        var sink = new DisplaySink(byteBudget: 8);
        sink.Append(DataDirection.Rx, new byte[8]);
        sink.Clear();
        Assert.Empty(sink.Snapshot());
        sink.Append(DataDirection.Rx, new byte[4]);
        sink.Append(DataDirection.Tx, new byte[4]);
        Assert.Equal(new long[] { 2, 3 }, sink.Snapshot().Select(e => e.Seq));
        sink.Append(DataDirection.Rx, new byte[4]);
        Assert.Equal(new long[] { 3, 4 }, sink.Snapshot().Select(e => e.Seq));
        Assert.Equal(8, sink.Snapshot().Sum(e => e.Bytes.Length));
    }

    private static int StorageCapacity(Curve curve, string field)
        => ((double[])typeof(Curve).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(curve)!).Length;
}
