using System.Globalization;
using System.Text;
using FreeCom.Core.Export;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;
using Xunit;

namespace FreeCom.Tests;

public sealed class StreamingExportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("freecom-stream-export-").FullName;
    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Raw_ConcatenatesLargeRxEntriesAndTruncatesExistingFile()
    {
        var raw = new RawLog();
        var expected = new List<byte>();
        for (int i = 0; i < 33; i++)
        {
            var bytes = Enumerable.Range(0, 12_345).Select(j => (byte)(j + i)).ToArray();
            var dir = i % 3 == 0 ? DataDirection.Tx : DataDirection.Rx;
            raw.Append(dir, bytes);
            if (dir == DataDirection.Rx) expected.AddRange(bytes);
        }
        var path = PathFor("raw.dat");
        File.WriteAllBytes(path, new byte[1_000_000]);
        Exporters.ExportRaw(path, raw);
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
        raw.Clear();
        Exporters.ExportRaw(path, raw);
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Display_MatchesLegacyRenderingAcrossBuffersAndEntryBoundaries(bool hex, bool timestamp)
    {
        var sink = new DisplaySink();
        // Exercise supplementary characters on each side of the internal char boundary,
        // malformed UTF-8 and incomplete sequences at an entry boundary.
        foreach (int prefix in new[] { 3070, 3071, 3072, 6143 })
        {
            sink.Append(DataDirection.Rx, Encoding.UTF8.GetBytes(new string('a', prefix) + "😀汉字\r\n" + new string('z', 9001)));
            sink.Append(DataDirection.Tx, [0xFF, 0xED, 0xA0, 0x80, 0xF0, 0x9F]);
            sink.Append(DataDirection.Rx, [0x98, 0x80]);
            sink.Append(DataDirection.Tx, []);
        }
        var path = PathFor("display.txt");
        File.WriteAllBytes(path, new byte[1_000_000]);
        Exporters.ExportDisplay(path, sink, timestamp, hex);
        string expected = hex ? sink.RenderHex(timestamp) : sink.RenderText(timestamp);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path)); // exact content and no BOM
        sink.Clear();
        Exporters.ExportDisplay(path, sink, timestamp, hex);
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    public void Csv_PreservesRoundtripFormattingEscapingSamplingAndWindowSelection(int maxPoints)
    {
        var plots = new PlotService();
        var window = plots.GetOrCreate("测量,\"A\"\nB");
        double[] values = [double.MinValue, double.MaxValue, double.Epsilon, double.NaN,
            double.PositiveInfinity, double.NegativeInfinity, -0.0, 1.2345678901234567];
        for (int i = 0; i < values.Length; i++) window.Add([values[i], -values[i]], i * 0.125);
        window.Curves[0].Name = "电压,\"V\"\n通道";
        window.Curves[1].Name = "plain\rname"; // preserve existing escape behavior for bare CR
        plots.GetOrCreate("other").Add([12.5], 42);
        foreach (string? selected in new string?[] { null, window.Id, window.Title, "missing" })
        {
            var expected = LegacyCsv(plots, selected, maxPoints);
            Assert.Equal(expected, Exporters.BuildCurvesCsv(plots, selected, maxPoints));
            var path = PathFor("curves.csv");
            File.WriteAllBytes(path, new byte[12_000]);
            Assert.Equal(Path.GetFullPath(path), Exporters.ExportCurvesCsv(path, plots, selected, maxPoints));
            Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(expected)).ToArray(), File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Csv_DefaultLimitIs200000AndIncludesNewestPoint()
    {
        var plots = new PlotService();
        var window = plots.GetOrCreate("limit");
        double[] value = [0];
        for (int i = 0; i < 200_003; i++) window.Add(value, i);
        var path = PathFor("limit.csv");
        Exporters.ExportCurvesCsv(path, plots, null);
        var lines = File.ReadAllLines(path);
        Assert.Equal(200_001, lines.Length);
        Assert.Equal("window,curve,x,y", lines[0]);
        Assert.Equal("limit,#1,0,0", lines[1]);
        Assert.Equal("limit,#1,200002,0", lines[^1]);
    }

    private static string LegacyCsv(PlotService plots, string? selected, int maxPoints)
    {
        var text = new StringBuilder().AppendLine("window,curve,x,y");
        var windows = selected is null ? plots.SnapshotWindows() : plots.SnapshotWindows().Where(w => w == plots.Find(selected));
        foreach (var window in windows)
        foreach (var curve in window.Curves)
        {
            var snapshot = curve.Snapshot(maxPoints);
            for (int i = 0; i < snapshot.Xs.Length; i++)
                text.Append(Escape(window.Title)).Append(',').Append(Escape(curve.Name)).Append(',')
                    .Append(snapshot.Xs[i].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(snapshot.Ys[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }

    private static string Escape(string text) => text.Contains(',') || text.Contains('"') || text.Contains('\n')
        ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
