namespace FreeCom.Core.Protocols;

/// <summary>CSV 协议（PRD 附录 A.3）：每行逗号分隔数字，每列一条曲线，单窗口。</summary>
public sealed class CsvProtocol : IProtocolParser
{
    private readonly LineAssembler _assembler = new();

    public string Name => "CSV";
    public long ErrorCount { get; private set; }

    public static readonly ProtocolInfo Meta = new(
        "CSV",
        "CSV 文本协议，每列一条曲线，不支持多窗口",
        [new ProtocolOptionDef("window", "窗口名", "csv")]);

    private readonly string _window;

    public CsvProtocol(string window = "csv") => _window = string.IsNullOrWhiteSpace(window) ? "csv" : window;

    public void Feed(ReadOnlySpan<byte> data, List<ProtocolFrame> output)
    {
        _assembler.FeedUtf8(data, (Parser: this, Output: output), static (rawLine, state) =>
        {
            var line = rawLine.Trim();
            if (line.Length == 0) return;
            if (TextProtocol.TryParseNumbers(line, out var values))
            {
                state.Output.Add(new ProtocolFrame { Kind = FrameKind.Plot, Window = state.Parser._window, Values = values });
            }
            else
            {
                state.Parser.ErrorCount++;
            }
        });
    }

    public void Reset()
    {
        _assembler.Reset();
        ErrorCount = 0;
    }

    public void Dispose() { }
}
