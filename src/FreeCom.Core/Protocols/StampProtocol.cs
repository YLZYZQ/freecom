namespace FreeCom.Core.Protocols;

/// <summary>
/// STAMP 协议（PRD 附录 A.2）：&lt;stamp&gt;{title}values\n
/// stamp 由下位机给出（同一窗口必须递增），作为曲线 X 轴。回滚帧丢弃并计错。
/// </summary>
public sealed class StampProtocol : IProtocolParser
{
    private readonly LineAssembler _assembler = new();
    private readonly Dictionary<string, double> _lastStamp = new();
    private string? _lastTitle;

    public string Name => "STAMP";
    public long ErrorCount { get; private set; }

    public static readonly ProtocolInfo Meta = new(
        "STAMP",
        "带时间戳文本协议 <stamp>{title}1,2,3\\n，下位机指定 X 轴",
        Array.Empty<ProtocolOptionDef>());

    public void Feed(ReadOnlySpan<byte> data, List<ProtocolFrame> output)
    {
        _assembler.FeedUtf8(data, (Parser: this, Output: output), static (line, state) =>
        {
            var parser = state.Parser;
            // 非 STAMP 行（不以 < 开头）静默跳过：日志混发不算错误
            if (line.IsEmpty || line[0] != '<') return;
            int close = line.IndexOf('>');
            if (close <= 0 || close == line.Length - 1) { parser.ErrorCount++; return; }
            if (!double.TryParse(line[1..close], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var stamp)) { parser.ErrorCount++; return; }

            if (!TextProtocol.TryParse(line[(close + 1)..], ref parser._lastTitle, out var title, out var values))
            {
                parser.ErrorCount++;
                return;
            }

            if (parser._lastStamp.TryGetValue(title, out var last) && stamp <= last)
            {
                parser.ErrorCount++; // 时间戳回滚：丢弃（PRD 要求清空曲线，MVP 以丢帧+计数处理）
                return;
            }
            parser._lastStamp[title] = stamp;
            state.Output.Add(new ProtocolFrame
            {
                Kind = FrameKind.Plot,
                Window = title,
                Values = values,
                Stamp = stamp,
            });
        });
    }

    public void Reset()
    {
        _assembler.Reset();
        _lastStamp.Clear();
        _lastTitle = null;
        ErrorCount = 0;
    }

    public void Dispose() { }
}
