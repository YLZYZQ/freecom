using System.Text;
using FreeCom.Core.Export;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;

namespace FreeCom.Core.ControlApi;

/// <summary>基于 DataPipeline 的标准 Control Surface：App 与无头宿主共用。</summary>
public sealed class PipelineSurface : IControlSurface
{
    public const string AppVersion = "0.1.0";

    private readonly object _transportLock = new();
    private ITransport? _transport;
    private string _transportKind = "";
    private Dictionary<string, string> _protocolOptions = [];

    public DataPipeline Pipeline { get; }

    public PipelineSurface(DataPipeline pipeline) => Pipeline = pipeline;

    public AppInfoDto GetAppInfo() => new(
        "FreeCom",
        AppVersion,
        Pipeline.TransportState.ToString(),
        Pipeline.TransportDescription,
        Pipeline.Counters.RxBytes,
        Pipeline.Counters.TxBytes,
        Pipeline.Counters.RxRatePerSecond,
        Pipeline.Counters.FramesParsed,
        Pipeline.Counters.ParseErrors,
        Pipeline.Plots.SnapshotWindows().Count);

    public IReadOnlyList<PortDescriptor> ListPorts(string transportKind, bool probeBusy)
    {
        var factory = TransportRegistry.Find(string.IsNullOrWhiteSpace(transportKind) ? "serial" : transportKind)
            ?? throw new ArgumentException($"未知传输类型: {transportKind}");
        return factory.ListPorts(probeBusy).ToList();
    }

    public TransportStatusDto GetTransportStatus()
    {
        lock (_transportLock)
        {
            return new TransportStatusDto(
                Pipeline.TransportState.ToString(),
                _transportKind,
                Pipeline.TransportDescription);
        }
    }

    public Task OpenTransportAsync(OpenTransportRequest request)
    {
        var kind = string.IsNullOrWhiteSpace(request.Transport) ? "serial" : request.Transport;
        var factory = TransportRegistry.Find(kind)
            ?? throw new ArgumentException($"未知传输类型: {kind}（可用: {string.Join(", ", TransportRegistry.All.Select(f => f.Kind))}）");
        // 先释放旧连接再开新连接：真实串口不允许同端口被两个句柄同时占用
        lock (_transportLock)
        {
            Pipeline.DetachTransport();
            _transport?.Dispose();
            _transport = null;
            _transportKind = "";
        }

        var transport = factory.Create(new TransportOptions { Kind = kind, Parameters = request.Params ?? [] });
        try
        {
            transport.Open();
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        lock (_transportLock)
        {
            _transport = transport;
            _transportKind = kind;
            Pipeline.AttachTransport(transport);
        }
        return Task.CompletedTask;
    }

    public Task CloseTransportAsync()
    {
        lock (_transportLock)
        {
            Pipeline.DetachTransport();
            _transport?.Dispose();
            _transport = null;
            _transportKind = "";
        }
        return Task.CompletedTask;
    }

    public async Task SendAsync(SendRequest request)
    {
        var format = (request.Format ?? "text").ToLowerInvariant();
        if (format is "hex")
            await Pipeline.SendHexAsync(request.Data, request.Newline).ConfigureAwait(false);
        else if (format is "text" or "ascii")
            await Pipeline.SendTextAsync(request.Data, request.Encoding, request.Newline).ConfigureAwait(false);
        else
            throw new ArgumentException($"format 必须为 text 或 hex，收到: {request.Format}");
    }

    public ReceivePageDto ReadReceive(long since, int limit, string format)
    {
        limit = Math.Clamp(limit == 0 ? 100 : limit, 1, 10_000);
        var entries = Pipeline.Raw.Snapshot(since, limit, DataDirection.Rx);
        var useHex = (format ?? "text").Equals("hex", StringComparison.OrdinalIgnoreCase);
        var items = entries.Select(e => new ReceiveItemDto(
            e.Seq,
            "rx",
            e.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
            useHex ? HexParse.ToHexSpaced(e.Data.Span) : Encoding.UTF8.GetString(e.Data.Span))).ToList();
        return new ReceivePageDto(items, entries.Count > 0 ? entries[^1].Seq : since);
    }

    public ProtocolStateDto GetProtocol() => new(Pipeline.ProtocolName, _protocolOptions);

    public Task SetProtocolAsync(string name, Dictionary<string, string>? options)
    {
        if (!ProtocolRegistry.Exists(name))
            throw new ArgumentException($"未知协议: {name}（可用: {string.Join(", ", ProtocolRegistry.Names)}）");
        var parser = ProtocolRegistry.Create(name, options);
        Pipeline.SetParser(parser);
        _protocolOptions = options ?? [];
        return Task.CompletedTask;
    }

    public IReadOnlyList<PlotWindowDto> GetPlotWindows() =>
        Pipeline.Plots.SnapshotWindows()
            .Select(w => new PlotWindowDto(
                w.Id, w.Title, w.AutoY, w.TotalPoints,
                w.Curves.Select(c => new CurveDto(c.Name, c.Count)).ToList()))
            .ToList();

    public PlotWindowDataDto? GetPlotWindowData(string idOrTitle, int maxPoints)
    {
        maxPoints = Math.Clamp(maxPoints == 0 ? 2000 : maxPoints, 10, 2_000_000);
        var w = Pipeline.Plots.Find(idOrTitle);
        if (w is null) return null;
        return new PlotWindowDataDto(
            w.Id,
            w.Title,
            w.Curves.Select(c =>
            {
                var s = c.Snapshot(maxPoints);
                return new CurveDataDto(s.Name, s.Xs, s.Ys, s.TotalPoints);
            }).ToList());
    }

    public Task<string> ExportCurvesAsync(string? windowId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FreeCom");
            Directory.CreateDirectory(dir);
            path = System.IO.Path.Combine(dir, $"curves-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        }
        var fullPath = Exporters.ExportCurvesCsv(path, Pipeline.Plots, windowId);
        return Task.FromResult(fullPath);
    }
}
