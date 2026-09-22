namespace FreeCom.Core.Protocols;

public enum FrameKind
{
    /// <summary>绘图帧：Window + Values（+可选 Stamp 作为 X）。</summary>
    Plot,
}

/// <summary>协议解析输出帧。</summary>
public sealed class ProtocolFrame
{
    public FrameKind Kind { get; init; }
    public string Window { get; init; } = "";
    public double[] Values { get; init; } = [];
    /// <summary>下位机提供的 X 值（STAMP）；null 表示由上位机自动编号。</summary>
    public double? Stamp { get; init; }
}

/// <summary>协议选项定义（用于 capabilities 与 UI 生成配置）。</summary>
public sealed record ProtocolOptionDef(string Key, string Description, string Default, string[]? EnumValues = null);

/// <summary>协议元信息。</summary>
public sealed record ProtocolInfo(string Name, string Description, ProtocolOptionDef[] Options);

/// <summary>
/// 协议解析器：输入原始字节流（可能任意分片），输出完整帧。
/// 实现要求：坏数据只能计数丢弃，绝不抛异常（NFR-4 解析容错）。
/// </summary>
public interface IProtocolParser : IDisposable
{
    string Name { get; }
    long ErrorCount { get; }
    /// <summary>输入一批字节，把解析出的完整帧追加到 output。跨批次半包必须由实现内部缓存。</summary>
    void Feed(ReadOnlySpan<byte> data, List<ProtocolFrame> output);
    /// <summary>清空内部状态（换协议/清空数据时调用）。</summary>
    void Reset();
}
