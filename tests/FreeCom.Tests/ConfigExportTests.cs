using System.Text;
using FreeCom.Core.Config;
using FreeCom.Core.Export;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

public class SettingsStoreTests
{
    private static string TempPath()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freecom-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void RoundTrip_AllFields()
    {
        var path = TempPath();
        var store = new SettingsStore(path);
        var settings = new AppSettings
        {
            TransportKind = "serial",
            TransportParams = new() { ["port"] = "COM7", ["baud"] = "921600" },
            ProtocolName = "EasyHex",
            ProtocolOptions = new() { ["type"] = "U8" },
            HexDisplay = true,
            HexSend = false,
            DisplayTimestamp = false,
            EncodingName = "gbk",
            Newline = "crlf",
            AutoScroll = false,
            AutoY = false,
            MaxPointsPerCurve = 123456,
            SendHistory = ["AT+RST", "AA 55"],
            CyclicSend = true,
            CyclicIntervalMs = 250,
            McpToken = "tok-123",
        };
        store.Save(settings);
        var loaded = new SettingsStore(path).Load();
        Assert.Equal("serial", loaded.TransportKind);
        Assert.Equal("COM7", loaded.TransportParams["port"]);
        Assert.Equal("921600", loaded.TransportParams["baud"]);
        Assert.Equal("EasyHex", loaded.ProtocolName);
        Assert.Equal("U8", loaded.ProtocolOptions["type"]);
        Assert.True(loaded.HexDisplay);
        Assert.False(loaded.DisplayTimestamp);
        Assert.Equal("gbk", loaded.EncodingName);
        Assert.Equal("crlf", loaded.Newline);
        Assert.False(loaded.AutoScroll);
        Assert.False(loaded.AutoY);
        Assert.Equal(123456, loaded.MaxPointsPerCurve);
        Assert.Equal(["AT+RST", "AA 55"], loaded.SendHistory);
        Assert.True(loaded.CyclicSend);
        Assert.Equal(250, loaded.CyclicIntervalMs);
        Assert.Equal("tok-123", loaded.McpToken);
        Assert.Equal(2, loaded.SchemaVersion);
    }

    [Fact]
    public void Migration_V1VirtualTransport_FallsBackToSerial()
    {
        // v0.1 的"虚拟回环"配置 → v0.1.1 迁移为串口
        var path = TempPath();
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "TransportKind": "virtual",
              "TransportParams": { "port": "loopback", "baud": "115200" },
              "ProtocolName": "TEXT"
            }
            """);
        var s = new SettingsStore(path).Load();
        Assert.Equal("serial", s.TransportKind);
        Assert.DoesNotContain("port", s.TransportParams.Keys);
        Assert.Equal(2, s.SchemaVersion);
    }

    [Fact]
    public void Migration_V1Serial_KeepsPort()
    {
        var path = TempPath();
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "TransportKind": "serial",
              "TransportParams": { "port": "COM3", "baud": "115200" }
            }
            """);
        var s = new SettingsStore(path).Load();
        Assert.Equal("serial", s.TransportKind);
        Assert.Equal("COM3", s.TransportParams["port"]);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var s = new SettingsStore(TempPath()).Load();
        Assert.Equal("TEXT", s.ProtocolName);
        Assert.Equal("serial", s.TransportKind);   // v2 默认：串口
        Assert.Equal(2, s.SchemaVersion);
        Assert.True(s.DisplayTimestamp); // 时间戳默认开启
    }

    [Fact]
    public void CorruptFile_DefaultsWithBackup()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not valid json !!!");
        var s = new SettingsStore(path).Load();
        Assert.Equal("TEXT", s.ProtocolName);
        Assert.True(File.Exists(path + ".corrupt"));
    }
}

public class ExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("freecom-export-").FullName;

    [Fact]
    public void ExportRaw_RxBytesOnly()
    {
        var log = new RawLog();
        log.Append(DataDirection.Rx, [0xAA, 0x55]);
        log.Append(DataDirection.Tx, [0xFF]);
        var path = System.IO.Path.Combine(_dir, "raw.dat");
        Exporters.ExportRaw(path, log);
        Assert.Equal([0xAA, 0x55], File.ReadAllBytes(path));
    }

    [Fact]
    public void ExportDisplay_TxtContainsTimestampAndDirection()
    {
        var sink = new DisplaySink();
        sink.Append(DataDirection.Rx, "hello"u8.ToArray());
        sink.Append(DataDirection.Tx, "cmd"u8.ToArray());
        var path = System.IO.Path.Combine(_dir, "display.txt");
        Exporters.ExportDisplay(path, sink, includeTimestamp: true);
        var text = File.ReadAllText(path);
        Assert.Contains("[", text);
        Assert.Contains("hello", text);
        Assert.Contains(">> cmd", text);
    }

    [Fact]
    public void CurvesCsv_LongFormat_Escaping()
    {
        var plots = new PlotService();
        plots.AddPlotFrame("v,a", [1.5, 2.5], null);
        var csv = Exporters.BuildCurvesCsv(plots, null);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("window,curve,x,y", lines[0].TrimEnd('\r'));
        Assert.StartsWith("\"v,a\",#1,0,1.5", lines[1]);
        Assert.StartsWith("\"v,a\",#2,0,2.5", lines[2]);
    }

    [Fact]
    public void ExportCurvesCsv_SingleWindow()
    {
        var plots = new PlotService();
        plots.AddPlotFrame("a", [1], null);
        plots.AddPlotFrame("b", [2], null);
        var path = System.IO.Path.Combine(_dir, "curves.csv");
        Exporters.ExportCurvesCsv(path, plots, "a");
        var csv = File.ReadAllText(path);
        Assert.StartsWith("a,#1", csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        Assert.DoesNotContain("b,#", csv);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
