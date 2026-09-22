using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FreeCom.Core.Export;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;

if (args.Length != 1) throw new ArgumentException("Usage: ExportStress <report.json>");
var raw = new RawLog();
var display = new DisplaySink();
var block = new byte[4096];
for (int i = 0; i < block.Length; i++) block[i] = (byte)(' ' + i % 95);
for (int i = 0; i < 1024; i++)
{
    raw.Append(DataDirection.Rx, block);
    display.Append(DataDirection.Rx, block);
}
var plots = new PlotService();
var window = plots.GetOrCreate("export,stress");
for (int i = 0; i < 200_000; i++) window.Add([i * 0.125, -i * 0.25, Math.Sin(i * 0.01)], i);
var scratch = Directory.CreateTempSubdirectory("freecom-export-stress-");
var results = new List<object>();
try
{
    Measure("raw-4MiB", path => Exporters.ExportRaw(path, raw));
    Measure("text-4MiB", path => Exporters.ExportDisplay(path, display, includeTimestamp: false));
    Measure("hex-4MiB", path => Exporters.ExportDisplay(path, display, includeTimestamp: false, hex: true));
    Measure("curves-3x200000", path => Exporters.ExportCurvesCsv(path, plots, null));
    File.WriteAllText(args[0], JsonSerializer.Serialize(new
    {
        Utc = DateTime.UtcNow,
        Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        CoreAssembly = typeof(Exporters).Assembly.Location,
        Results = results
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { scratch.Delete(recursive: true); }

void Measure(string name, Action<string> export)
{
    var path = Path.Combine(scratch.FullName, name);
    export(path); // JIT and file-system warmup, excluded from measurements.
    var samples = new List<object>();
    for (int sample = 0; sample < 3; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var clock = new Stopwatch();
        long start = GC.GetAllocatedBytesForCurrentThread();
        clock.Start();
        export(path);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        samples.Add(new { AllocatedBytes = allocated, ElapsedMs = clock.Elapsed.TotalMilliseconds });
    }
    using var file = File.OpenRead(path);
    var result = new { Name = name, OutputBytes = file.Length, Sha256 = Convert.ToHexString(SHA256.HashData(file)), Samples = samples };
    results.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
