using System.Text;
using System.Threading.Channels;
using FreeCom.Core.Plots;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;

namespace FreeCom.Core.Pipeline;

public sealed class PipelineOptions
{
    public int DisplayCapacity { get; set; } = 20_000;
    public int RawLogCapacity { get; set; } = 50_000;
    public int MaxPointsPerCurve { get; set; } = 500_000;
}

/// <summary>
/// 数据管线（技术方案 §3.2）：
/// 传输回调 → 原始日志/显示/计数 → Channel → 解析线程 → 绘图服务。
/// 发送路径：SendText/SendHex → 计数/日志/显示 → 传输写入。
/// 解析器坏帧只计数不抛异常（NFR-4）。
/// </summary>
public sealed class DataPipeline : IDisposable
{
    private readonly Channel<byte[]> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private readonly object _parserLock = new();
    private readonly object _transportLock = new();

    private IProtocolParser _parser;
    private ITransport? _transport;

    public Counters Counters { get; } = new();
    public DisplaySink Display { get; }
    public RawLog Raw { get; }
    public PlotService Plots { get; }

    // ---------------- 发送审计（MCP send_history 数据源：UI 与 API 的发送都经此处） ----------------
    public sealed record SendAudit(long Seq, DateTime TimeUtc, long Bytes, string Text, string Hex);

    private readonly object _auditLock = new();
    private readonly Queue<SendAudit> _sendAudits = new();
    private const int MaxAudits = 50;

    /// <summary>最近发送记录（新→旧）：UI 与 API 的全部发送都在此审计。</summary>
    public IReadOnlyList<SendAudit> GetSendHistory(int limit = 20)
    {
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, MaxAudits);
        lock (_auditLock) return _sendAudits.Reverse().Take(limit).ToList();
    }

    private void RecordSend(byte[] bytes)
    {
        // 末 64B 预览：不可打印字节以 '.' 占位，控制字符转义
        var text = new StringBuilder();
        foreach (var b in bytes.AsSpan(Math.Max(0, bytes.Length - 64)))
        {
            if (b is 0x0A) text.Append("\\n");
            else if (b is 0x0D) text.Append("\\r");
            else if (b is 0x09) text.Append("\\t");
            else if (b is >= 0x20 and < 0x7F) text.Append((char)b);
            else text.Append('.');
        }
        var hex = HexParse.ToHexSpaced(bytes.AsSpan(Math.Max(0, bytes.Length - 64)));
        lock (_auditLock)
        {
            _sendAudits.Enqueue(new SendAudit(_sendAudits.Count + 1, DateTime.UtcNow, bytes.Length, text.ToString(), hex));
            while (_sendAudits.Count > MaxAudits) _sendAudits.Dequeue();
        }
    }

    public DataPipeline(IProtocolParser parser, PipelineOptions? options = null)
    {
        options ??= new PipelineOptions();
        _parser = parser;
        Display = new DisplaySink(options.DisplayCapacity);
        Raw = new RawLog(options.RawLogCapacity);
        Plots = new PlotService { MaxPointsPerCurve = options.MaxPointsPerCurve };
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest, // 显示层丢最旧保实时（方案 §3.2 背压策略）
        });
        _consumer = Task.Run(ConsumeLoopAsync);
    }

    /// <summary>当前协议名（线程安全读取）。</summary>
    public string ProtocolName { get { lock (_parserLock) return _parser.Name; } }

    /// <summary>诊断：管线消费任务（观察异常/状态）。</summary>
    public Task ConsumerTask => _consumer;

    /// <summary>切换协议解析器（内部先排空通道，保证顺序一致）。</summary>
    public void SetParser(IProtocolParser parser)
    {
        lock (_parserLock)
        {
            var old = _parser;
            _parser = parser;
            old.Dispose();
            Counters.SetParseErrors(0);
        }
    }

    public void AttachTransport(ITransport transport)
    {
        lock (_transportLock)
        {
            DetachTransportLocked();
            _transport = transport;
            _transport.DataReceived += OnTransportData;
        }
    }

    public void DetachTransport()
    {
        lock (_transportLock) DetachTransportLocked();
    }

    private void DetachTransportLocked()
    {
        if (_transport == null) return;
        _transport.DataReceived -= OnTransportData;
        _transport = null;
    }

    public TransportState TransportState
    {
        get { lock (_transportLock) return _transport?.State ?? TransportState.Closed; }
    }

    public string TransportDescription
    {
        get { lock (_transportLock) return _transport?.Description ?? ""; }
    }

    public bool IsTransportOpen
    {
        get { lock (_transportLock) return _transport is { State: TransportState.Open }; }
    }

    private void OnTransportData(ReadOnlyMemory<byte> data)
    {
        var arr = data.ToArray();
        Raw.AppendOwned(DataDirection.Rx, arr);
        Counters.AddRx(arr.Length);
        Display.AppendOwned(DataDirection.Rx, arr);
        _channel.Writer.TryWrite(arr); // 有界丢最旧，不阻塞 IO 线程
    }

    private async Task ConsumeLoopAsync()
    {
        var frames = new List<ProtocolFrame>(16);
        var reader = _channel.Reader;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var data = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                frames.Clear();
                IProtocolParser parser;
                lock (_parserLock) parser = _parser;
                try
                {
                    parser.Feed(data, frames);
                }
                catch (Exception)
                {
                    // 解析器实现缺陷兜底：不允许异常中断管线
                    Counters.SetParseErrors(parser.ErrorCount + 1);
                }
                foreach (var f in frames)
                {
                    if (f.Kind == FrameKind.Plot && f.Values.Length > 0)
                        Plots.AddPlotFrame(f.Window, f.Values, f.Stamp);
                }
                if (frames.Count > 0) Counters.AddFrames(frames.Count);
                Counters.SetParseErrors(parser.ErrorCount);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
    }

    // ---------------- 发送路径 ----------------

    public string DefaultEncoding { get; set; } = "utf-8";
    public string DefaultNewline { get; set; } = "none"; // none / crlf / lf / cr

    public async Task SendTextAsync(string text, string? encoding = null, string? newline = null)
    {
        var enc = EncodingHelper.Get(encoding ?? DefaultEncoding);
        var nl = newline ?? DefaultNewline;
        var suffix = nl switch
        {
            "crlf" or @"\r\n" => "\r\n",
            "lf" or @"\n" => "\n",
            "cr" or @"\r" => "\r",
            _ => "",
        };
        await SendBytesAsync(enc.GetBytes(text + suffix)).ConfigureAwait(false);
    }

    public async Task SendHexAsync(string hex, string? newline = null)
    {
        var bytes = HexParse.ParseLoose(hex);
        var nl = newline ?? DefaultNewline;
        if (nl is "crlf") bytes = [.. bytes, 0x0D, 0x0A];
        else if (nl is "lf") bytes = [.. bytes, 0x0A];
        else if (nl is "cr") bytes = [.. bytes, 0x0D];
        await SendBytesAsync(bytes).ConfigureAwait(false);
    }

    public async Task SendBytesAsync(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        // 隔离调用方可能复用的数组，再由原始/显示日志共享这一份只读数据。
        bytes = bytes.ToArray();
        RecordSend(bytes);
        Counters.AddTx(bytes.Length);
        Raw.AppendOwned(DataDirection.Tx, bytes);
        Display.AppendOwned(DataDirection.Tx, bytes);

        ITransport? t;
        lock (_transportLock) t = _transport;
        if (t is { State: TransportState.Open })
            await t.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _consumer.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        lock (_parserLock) _parser.Dispose();
        DetachTransport();
    }
}
