using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FreeCom.Core.Protocols;

const int frameCount = 200_000;
var measurements = new List<object>();
foreach (string protocol in new[] { "TEXT", "CSV", "STAMP" })
{
    var input = new StringBuilder();
    for (int i = 0; i < frameCount; i++)
    {
        if (protocol == "STAMP") input.Append('<').Append(i.ToString(CultureInfo.InvariantCulture)).Append('>');
        if (protocol != "CSV") input.Append("{sensor}");
        input.Append("1.23456789012345,-2.5,3e-9,42,0,1.7976931348623157e308\r\n");
    }
    byte[] bytes = Encoding.UTF8.GetBytes(input.ToString());
    foreach (int chunkSize in new[] { 4096, 17 })
    {
        for (int iteration = -1; iteration < 3; iteration++)
        {
            using IProtocolParser parser = protocol switch
            {
                "TEXT" => new TextProtocol(), "CSV" => new CsvProtocol(), _ => new StampProtocol()
            };
            var output = new List<ProtocolFrame>(128);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            long parsed = 0;
            for (int offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                parser.Feed(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)), output);
                parsed += output.Count;
                output.Clear();
            }
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            if (parsed != frameCount || parser.ErrorCount != 0)
                throw new InvalidOperationException($"{protocol}: parsed={parsed}, errors={parser.ErrorCount}");
            if (iteration >= 0) measurements.Add(new
            {
                protocol, chunkSize, iteration, frameCount, parsed, errors = parser.ErrorCount,
                allocatedBytes = allocated, bytesPerFrame = (double)allocated / frameCount,
                elapsedMilliseconds = watch.Elapsed.TotalMilliseconds
            });
        }
    }
}
string json = JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), measurements },
    new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 0) File.WriteAllText(args[0], json);
Console.WriteLine(json);
