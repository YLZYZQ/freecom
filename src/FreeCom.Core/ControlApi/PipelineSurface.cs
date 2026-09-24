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
        // 参数校验先行：无效请求（如缺 port）直接 400，不破坏现有连接
        // （此前"先关旧再开新"会让失败的 open 请求把已打开的串口连接杀掉）
        if (string.Equals(kind, "serial", StringComparison.OrdinalIgnoreCase))
        {
            var p = request.Params ?? [];
            var port = p.TryGetValue("port", out var v) ? v : null;
            if (string.IsNullOrWhiteSpace(port))
                throw new ArgumentException("串口参数缺少 port（可用 /v1/serial/ports 查询）");
        }
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

    public async Task<long> SendFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("必须提供 path（文件完整路径）");
        return await Pipeline.SendFileAsync(path, ct).ConfigureAwait(false);
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

    // ---------------- MCP v0.2 新增（P0/P1） ----------------

    private readonly SimulatorEngine _simulator = new();

    /// <summary>阻塞等待匹配的接收行：contains/direction 过滤，超时上限 10s。</summary>
    public async Task<WaitResultDto> WaitReceiveAsync(WaitRequest r, CancellationToken ct)
    {
        var timeoutMs = Math.Clamp(r.TimeoutMs <= 0 ? 3000 : r.TimeoutMs, 100, 10_000);
        var dir = (r.Direction ?? "any").ToLowerInvariant() is "rx" or "tx" ? (r.Direction ?? "any").ToLowerInvariant() : "any";
        var limit = Math.Clamp(r.Limit <= 0 ? 50 : r.Limit, 1, 500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cursor = r.Since;
        DataDirection? dirEnum = dir switch { "rx" => DataDirection.Rx, "tx" => DataDirection.Tx, _ => null };
        var useHex = (r.Format ?? "text").Equals("hex", StringComparison.OrdinalIgnoreCase);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var entries = Pipeline.Raw.Snapshot(cursor, limit, dirEnum);
            var hits = entries
                .Select(e => new ReceiveItemDto(
                    e.Seq,
                    e.Dir == DataDirection.Tx ? "tx" : "rx",
                    e.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
                    useHex ? HexParse.ToHexSpaced(e.Data.Span) : Encoding.UTF8.GetString(e.Data.Span)))
                .Where(i => dir == "any" || i.Dir == dir)
                .Where(i => string.IsNullOrEmpty(r.Contains) || i.Data.Contains(r.Contains, StringComparison.Ordinal))
                .ToList();
            var next = entries.Count > 0 ? entries[^1].Seq : cursor;
            if (hits.Count > 0)
                return new WaitResultDto(true, sw.ElapsedMilliseconds, next, hits);
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new WaitResultDto(false, sw.ElapsedMilliseconds, next, []);
            cursor = next;
            try { await Task.Delay(60, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return new WaitResultDto(false, sw.ElapsedMilliseconds, cursor, []); }
        }
    }

    /// <summary>发送并等待应答：以发送前的接收游标为起点，只匹配发送之后到达的数据。</summary>
    public async Task<ExpectResultDto> SendExpectAsync(ExpectRequest r, CancellationToken ct)
    {
        var since = Pipeline.Raw.LastSeq;
        await SendAsync(r.Send).ConfigureAwait(false);
        var wait = await WaitReceiveAsync(new WaitRequest(since, r.Contains, r.Direction, r.TimeoutMs, 50, r.Format), ct).ConfigureAwait(false);
        return new ExpectResultDto(Pipeline.Counters.TxBytes, wait);
    }

    public IReadOnlyList<ProtocolHelp> GetProtocolHelp(string? name) => ProtocolDocs.Get(name);

    public PlotWindowStatsDto? GetPlotWindowStats(string idOrTitle)
    {
        var w = Pipeline.Plots.Find(idOrTitle);
        if (w is null) return null;
        var stats = new List<CurveStatsDto>();
        foreach (var c in w.Curves)
        {
            var s = c.Snapshot(100_000);
            if (s.Ys.Length == 0) { stats.Add(new CurveStatsDto(c.Name, 0, 0, 0, 0, 0, 0)); continue; }
            double min = s.Ys[0], max = s.Ys[0], sum = 0;
            foreach (var v in s.Ys)
            {
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            stats.Add(new CurveStatsDto(c.Name, s.TotalPoints, min, max, sum / s.Ys.Length, s.Ys[0], s.Ys[^1]));
        }
        return new PlotWindowStatsDto(w.Id, w.Title, stats);
    }

    public IReadOnlyList<SendRecordDto> GetSendHistory(int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);
        return Pipeline.GetSendHistory(limit)
            .Select(a => new SendRecordDto(a.Seq, a.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff"), a.Bytes, a.Text, a.Hex))
            .ToList();
    }

    public Task<string> ExportRawAsync(string? path, string? direction)
    {
        DataDirection? dir = direction?.ToLowerInvariant() switch
        {
            "rx" => DataDirection.Rx,
            "tx" => DataDirection.Tx,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path))
        {
            var dirPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FreeCom");
            Directory.CreateDirectory(dirPath);
            path = System.IO.Path.Combine(dirPath, $"raw-{DateTime.Now:yyyyMMdd-HHmmss}.dat");
        }
        File.WriteAllBytes(path, Pipeline.Raw.CollectBytes(dir));
        return Task.FromResult(System.IO.Path.GetFullPath(path));
    }

    public Task<string> ExportDisplayAsync(string? path, bool timestamp, bool hex)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dirPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FreeCom");
            Directory.CreateDirectory(dirPath);
            path = System.IO.Path.Combine(dirPath, $"display-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        }
        var text = hex
            ? Pipeline.Display.RenderHex(timestamp)
            : Pipeline.Display.RenderText(timestamp);
        File.WriteAllText(path, text, Encoding.UTF8);
        return Task.FromResult(System.IO.Path.GetFullPath(path));
    }

    // ---------------- 虚拟串口对 / 设备模拟器 ----------------

    private readonly VirtualComManager _vcom = new();

    public IReadOnlyList<VcomPairDto> ListVcomPairs() =>
        // API/MCP 无人值守：只走免提权路径（PnP 设备名解析 → setupc 直查），绝不弹 UAC
        _vcom.ListPairs(allowElevate: false)
            .Select(p => new VcomPairDto(
                int.TryParse(new string(p.IdA.Where(char.IsDigit).ToArray()), out var n) ? n : 0,
                p.PortA, p.PortB))
            .ToList();

    public Task<VcomPairDto> CreateVcomPairAsync(string? portA, string? portB)
    {
        if (!_vcom.DriverInstalled)
            throw new InvalidOperationException("com0com 驱动未安装（虚拟串口管理器 → 下载与安装说明）");
        if (string.IsNullOrWhiteSpace(portA) || string.IsNullOrWhiteSpace(portB))
        {
            var (a, b) = VirtualComManager.SuggestFreePorts(System.IO.Ports.SerialPort.GetPortNames());
            portA ??= a;
            portB ??= b;
        }
        _vcom.CreatePair(portA, portB); // 需管理员：UAC 取消抛 ElevationCancelledException
        var created = ListVcomPairs().FirstOrDefault(p => p.PortA == portA && p.PortB == portB)
            ?? new VcomPairDto(0, portA, portB);
        return Task.FromResult(created);
    }

    public Task RemoveVcomPairAsync(string pairNumber)
    {
        if (!_vcom.DriverInstalled)
            throw new InvalidOperationException("com0com 驱动未安装");
        if (!int.TryParse(pairNumber.Trim(), out var n) || n < 0)
            throw new ArgumentException($"配对编号必须是数字，收到: {pairNumber}");
        _vcom.RemovePair(pairNumber.Trim()); // 需管理员
        return Task.CompletedTask;
    }

    public SimulatorStatusDto StartSimulator(SimulatorStartRequest request)
    {
        if (!ProtocolRegistry.Exists(string.IsNullOrWhiteSpace(request.Protocol) ? "TEXT" : request.Protocol))
            throw new ArgumentException($"未知协议: {request.Protocol}（可用: {string.Join(", ", ProtocolRegistry.Names)}）");
        var st = _simulator.Start(request.Port, request.Protocol, request.IntervalMs);
        return new SimulatorStatusDto(st.Running, st.Port, st.Protocol, st.IntervalMs, st.SentFrames, st.SentBytes, st.LastError);
    }

    public SimulatorStatusDto StopSimulator()
    {
        var st = _simulator.Stop();
        return new SimulatorStatusDto(st.Running, st.Port, st.Protocol, st.IntervalMs, st.SentFrames, st.SentBytes, st.LastError);
    }

    public SimulatorStatusDto GetSimulatorStatus()
    {
        var st = _simulator.Status;
        return new SimulatorStatusDto(st.Running, st.Port, st.Protocol, st.IntervalMs, st.SentFrames, st.SentBytes, st.LastError);
    }
}
