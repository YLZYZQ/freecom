namespace FreeCom.Core.Transports;

/// <summary>传输状态。</summary>
public enum TransportState
{
    Closed,
    Open,
    Error,
}

/// <summary>端口占用状态（对应 PRD F1.1 的 IDLE/BUSY/IGNORE）。</summary>
public enum PortStatus
{
    Idle,
    Busy,
    Ignore,
    Unknown,
}

/// <summary>传输打开参数：以字符串字典承载，具体传输自行解析。
/// 这样后续新增 TCP/UDP/HID 传输不需要改动管线与 UI 逻辑。</summary>
public sealed class TransportOptions
{
    public string Kind { get; init; } = "serial";
    public Dictionary<string, string> Parameters { get; init; } = new();

    public string? Get(string key) => Parameters.TryGetValue(key, out var v) ? v : null;
    public int GetInt(string key, int def) => int.TryParse(Get(key), out var v) ? v : def;
    public bool GetBool(string key, bool def) => bool.TryParse(Get(key), out var v) ? v : def;
}

/// <summary>端口描述（串口列表条目）。</summary>
public sealed record PortDescriptor(string Name, string Description, PortStatus Status, string Kind);

/// <summary>传输抽象：串口 / 虚拟回环（后续 TCP/UDP/HID）统一接口。</summary>
public interface ITransport : IDisposable
{
    string Kind { get; }
    TransportState State { get; }
    string Description { get; }

    /// <summary>收到设备侧数据（原始字节，只在线程外回调）。</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    void Open();
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    void Close();
}

/// <summary>传输工厂：负责枚举端口与创建实例，注册进 TransportRegistry。</summary>
public interface ITransportFactory
{
    string Kind { get; }
    IEnumerable<PortDescriptor> ListPorts(bool probeBusy = false);
    ITransport Create(TransportOptions options);
}

/// <summary>传输注册表：UI 与 Control API 按 Kind 取工厂，新增传输只需注册。</summary>
public static class TransportRegistry
{
    private static readonly Dictionary<string, ITransportFactory> s_factories = new();

    static TransportRegistry()
    {
        // 内置传输：串口。虚拟串口对（com0com）创建的 COM 口同样走串口传输，
        // 由 VirtualComManager 负责端口对管理，不作为独立传输类型。
        Register(new SerialTransportFactory());
    }

    public static void Register(ITransportFactory factory) => s_factories[factory.Kind] = factory;

    public static ITransportFactory? Find(string kind)
        => s_factories.TryGetValue(kind, out var f) ? f : null;

    public static IEnumerable<ITransportFactory> All => s_factories.Values;

    public static IEnumerable<PortDescriptor> ListPorts(string kind, bool probeBusy = false)
        => Find(kind)?.ListPorts(probeBusy) ?? Enumerable.Empty<PortDescriptor>();
}
