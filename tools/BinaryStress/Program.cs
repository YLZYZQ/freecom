using System.Diagnostics;
using System.Text.Json;
using FreeCom.Core.Protocols;

var invalid = new byte[65536];
var dense = new byte[65536];
for (int i = 0; i < dense.Length; i += 4)
{
    dense[i] = 1;
    dense[i + 2] = dense[i + 3] = 255;
}
Measure("modbus-invalid-64KiB", () => new ModbusRtuProtocol(), invalid);
Measure("easyhex-dense-64KiB", () => new EasyHexProtocol(), dense);

static void Measure(string scenario, Func<IProtocolParser> factory, byte[] input)
{
    using (var warm = factory()) warm.Feed(input.AsSpan(0, 256), new());
    for (int run = 1; run <= 3; run++)
    {
        using var parser = factory();
        var frames = new List<ProtocolFrame>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        parser.Feed(input, frames);
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine(JsonSerializer.Serialize(new { scenario, run, inputBytes = input.Length,
            allocatedBytes, elapsedMs, frames = frames.Count, errors = parser.ErrorCount }));
    }
}
