using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;

namespace FreeCom.Core.ControlApi;

public sealed record AppInfoDto(
    string App,
    string Version,
    string TransportState,
    string TransportDescription,
    long RxBytes,
    long TxBytes,
    double RxRatePerSecond,
    long FramesParsed,
    long ParseErrors,
    int WindowCount);

public sealed record TransportStatusDto(string State, string Kind, string Description);

public sealed record OpenTransportRequest(string Transport, Dictionary<string, string>? Params);

public sealed record SendRequest(string Data, string Format, string? Encoding, string? Newline);

public sealed record ReceiveItemDto(long Seq, string Dir, string Time, string Data);

public sealed record ReceivePageDto(IReadOnlyList<ReceiveItemDto> Items, long NextSeq);

public sealed record ProtocolStateDto(string Name, Dictionary<string, string> Options);

public sealed record CurveDto(string Name, long Points);

public sealed record PlotWindowDto(string Id, string Title, bool AutoY, long TotalPoints, IReadOnlyList<CurveDto> Curves);

public sealed record CurveDataDto(string Name, double[] Xs, double[] Ys, long TotalPoints);

public sealed record PlotWindowDataDto(string Id, string Title, IReadOnlyList<CurveDataDto> Curves);

public sealed record EndpointDto(string Method, string Path, string Description);

public sealed record McpToolDto(string Name, string Description, string InputSchemaJson);

public sealed record CapabilitiesDto(
    string App,
    string Version,
    IReadOnlyList<EndpointDto> Endpoints,
    IReadOnlyList<McpToolDto> Tools,
    IReadOnlyList<ProtocolInfo> Protocols,
    IReadOnlyList<string> Transports);

/// <summary>Control API 对外操作面：App 与无头宿主都实现它，API 层不依赖具体宿主。</summary>
public interface IControlSurface
{
    AppInfoDto GetAppInfo();
    IReadOnlyList<PortDescriptor> ListPorts(string transportKind, bool probeBusy);
    TransportStatusDto GetTransportStatus();
    Task OpenTransportAsync(OpenTransportRequest request);
    Task CloseTransportAsync();
    Task SendAsync(SendRequest request);
    ReceivePageDto ReadReceive(long since, int limit, string format);
    ProtocolStateDto GetProtocol();
    Task SetProtocolAsync(string name, Dictionary<string, string>? options);
    IReadOnlyList<PlotWindowDto> GetPlotWindows();
    PlotWindowDataDto? GetPlotWindowData(string idOrTitle, int maxPoints);
    Task<string> ExportCurvesAsync(string? windowId, string? path);
}
