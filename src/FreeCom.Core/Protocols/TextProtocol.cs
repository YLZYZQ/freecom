namespace FreeCom.Core.Protocols;

/// <summary>
/// TEXT 协议（PRD 附录 A.1）：{title}values\n
/// title 为英文标识；values 为逗号分隔数字时输出绘图帧，X 由上位机自动编号。
/// 非数字内容行（如 {adc}voltage=6）不产出绘图帧（分窗显示属 v0.2）。
/// </summary>
public sealed class TextProtocol : IProtocolParser
{
    private readonly LineAssembler _assembler = new();
    private string? _lastTitle;
    public string Name => "TEXT";
    public long ErrorCount { get; private set; }

    public static readonly ProtocolInfo Info = new(
        "TEXT",
        "文本协议 {title}1,2,3\\n，多窗口，PC 自动打 X 戳",
        [new ProtocolOptionDef("window", "默认窗口名", "plotter")]);

    public void Feed(ReadOnlySpan<byte> data, List<ProtocolFrame> output)
    {
        _assembler.FeedUtf8(data, (Parser: this, Output: output), static (line, state) =>
        {
            if (!TryParse(line, ref state.Parser._lastTitle, out var title, out var values))
            {
                // 纯文本行（不以 { 开头）不是错误：设备日志与绘图数据混发是正常场景，跳过即可；
                // 以 { 开头但格式错的行视为协议错误（下位机本想发绘图帧但写错了）
                if (line.Length > 0 && line[0] == '{') state.Parser.ErrorCount++;
                return;
            }
            state.Output.Add(new ProtocolFrame { Kind = FrameKind.Plot, Window = title, Values = values });
        });
    }

    internal static bool TryParse(string line, out ProtocolFrame? frame)
    {
        frame = null;
        string? lastTitle = null;
        if (!TryParse(line.AsSpan(), ref lastTitle, out var title, out var values)) return false;
        frame = new ProtocolFrame { Kind = FrameKind.Plot, Window = title, Values = values };
        return true;
    }

    internal static bool TryParse(ReadOnlySpan<char> line, ref string? lastTitle,
        out string title, out double[] values)
    {
        title = "";
        values = [];
        if (line.IsEmpty || line[0] != '{') return false;
        int close = line.IndexOf('}');
        if (close <= 1 || close == line.Length - 1) return false;
        var titleSpan = line[1..close];
        foreach (var ch in titleSpan)
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
                return false;
        var payload = line[(close + 1)..];
        if (!TryParseNumbers(payload, out values)) return false;
        // One cached title avoids per-frame strings without unbounded interning.
        if (lastTitle == null || !titleSpan.SequenceEqual(lastTitle.AsSpan())) lastTitle = titleSpan.ToString();
        title = lastTitle;
        return true;
    }

    internal static bool TryParseNumbers(string text, out double[] values)
        => TryParseNumbers(text.AsSpan(), out values);

    internal static bool TryParseNumbers(ReadOnlySpan<char> text, out double[] values)
    {
        values = [];
        if (text.IsEmpty) return false;
        int count = 1;
        foreach (char ch in text) if (ch == ',') count++;
        var result = new double[count];
        for (int i = 0; i < result.Length; i++)
        {
            int end = text.IndexOf(',');
            if (end < 0) end = text.Length;
            if (!double.TryParse(text[..end], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return false;
            result[i] = v;
            if (end < text.Length) text = text[(end + 1)..];
        }
        values = result;
        return true;
    }

    public void Reset()
    {
        _assembler.Reset();
        _lastTitle = null;
        ErrorCount = 0;
    }

    public void Dispose() { }
}
