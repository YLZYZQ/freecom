using System.Diagnostics;
using System.Text.Json;
using FreeCom.Core;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Plots;

var results = new List<object>();
var plots = new PlotService { MaxPointsPerCurve = 16 };
double[] values = [1, 2, 3];
for (int i = 0; i < 100; i++) plots.AddPlotFrame("scope", values, null);
long routeBefore = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 100000; i++) plots.AddPlotFrame("scope", values, null);
results.Add(new { scenario = "route-100000-frames", allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - routeBefore });
using (var pipeline = new DataPipeline(new TextProtocol()))
{
    byte[] payload = new byte[65536];
    pipeline.SendBytesAsync(payload).GetAwaiter().GetResult();
    pipeline.Raw.Clear(); pipeline.Display.Clear();
    long liveBefore = GC.GetTotalMemory(true);
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    for (int i = 0; i < 500; i++) pipeline.SendBytesAsync(payload).GetAwaiter().GetResult();
    watch.Stop();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    long liveAfter = GC.GetTotalMemory(true);
    results.Add(new { scenario = "pipeline-log-payloads", inputBytes = 500L * payload.Length,
        allocatedBytes = allocated, retainedHeapDeltaBytes = liveAfter - liveBefore, elapsedMs = watch.Elapsed.TotalMilliseconds,
        rawBytes = pipeline.Raw.Snapshot().Sum(x => x.Bytes.Length), displayBytes = pipeline.Display.Snapshot().Sum(x => x.Bytes.Length) });
}
byte[] hex = new byte[65536];
HexParse.ToHexSpaced(hex.AsSpan(0, 32));
for (int run = 0; run < 3; run++)
{
    long before = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    var text = HexParse.ToHexSpaced(hex);
    watch.Stop();
    results.Add(new { scenario = "hex-format-64KiB", run, inputBytes = hex.Length, outputChars = text.Length,
        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before, elapsedMs = watch.Elapsed.TotalMilliseconds });
}
File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results));
