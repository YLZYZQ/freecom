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

// ---------------- MCP v0.2 新增（P0/P1） ----------------

public sealed record WaitRequest(long Since, string? Contains, string? Direction, int TimeoutMs, int Limit, string? Format);

public sealed record WaitResultDto(bool Matched, long ElapsedMs, long NextSeq, IReadOnlyList<ReceiveItemDto> Items);

public sealed record ExpectRequest(SendRequest Send, string? Contains, string? Direction, int TimeoutMs, string? Format);

public sealed record ExpectResultDto(long SentBytes, WaitResultDto Wait);

public sealed record VcomPairDto(int PairNumber, string PortA, string PortB);

public sealed record SimulatorStartRequest(string Port, string Protocol, int IntervalMs);

public sealed record SimulatorStatusDto(
    bool Running, string? Port, string? Protocol, int IntervalMs,
    long SentFrames, long SentBytes, string? LastError);

public sealed record CurveStatsDto(string Curve, long Count, double Min, double Max, double Mean, double First, double Last);

public sealed record PlotWindowStatsDto(string Id, string Title, IReadOnlyList<CurveStatsDto> Curves);

public sealed record SendRecordDto(long Seq, string Time, long Bytes, string Text, string Hex);

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
    Task<long> SendFileAsync(string path, CancellationToken ct);
    ReceivePageDto ReadReceive(long since, int limit, string format);
    ProtocolStateDto GetProtocol();
    Task SetProtocolAsync(string name, Dictionary<string, string>? options);
    IReadOnlyList<PlotWindowDto> GetPlotWindows();
    PlotWindowDataDto? GetPlotWindowData(string idOrTitle, int maxPoints);
    Task<string> ExportCurvesAsync(string? windowId, string? path);

    // ---------------- MCP v0.2 新增（P0/P1） ----------------
    Task<WaitResultDto> WaitReceiveAsync(WaitRequest request, CancellationToken ct);
    Task<ExpectResultDto> SendExpectAsync(ExpectRequest request, CancellationToken ct);
    IReadOnlyList<ProtocolHelp> GetProtocolHelp(string? name);
    PlotWindowStatsDto? GetPlotWindowStats(string idOrTitle);
    IReadOnlyList<SendRecordDto> GetSendHistory(int limit);
    Task<string> ExportRawAsync(string? path, string? direction);
    Task<string> ExportDisplayAsync(string? path, bool timestamp, bool hex);

    // 虚拟串口对（com0com）与设备模拟器：宿主级能力
    IReadOnlyList<VcomPairDto> ListVcomPairs();
    Task<VcomPairDto> CreateVcomPairAsync(string? portA, string? portB);
    Task RemoveVcomPairAsync(string pairNumber);
    SimulatorStatusDto StartSimulator(SimulatorStartRequest request);
    SimulatorStatusDto StopSimulator();
    SimulatorStatusDto GetSimulatorStatus();
}
