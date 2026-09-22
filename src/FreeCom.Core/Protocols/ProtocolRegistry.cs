namespace FreeCom.Core.Protocols;

/// <summary>协议注册表：按名称创建解析器，新增协议只需注册工厂。</summary>
public static class ProtocolRegistry
{
    private static readonly Dictionary<string, Func<Dictionary<string, string>, IProtocolParser>> s_factories = new(StringComparer.OrdinalIgnoreCase);

    static ProtocolRegistry()
    {
        Register("TEXT", _ => new TextProtocol());
        Register("CSV", o => new CsvProtocol(o.TryGetValue("window", out var w) ? w : "csv"));
        Register("STAMP", _ => new StampProtocol());
        Register("EasyHex", o => new EasyHexProtocol(
            ParseType(o, "U16"),
            ParseOrder(o, "LE")));
        Register("ModbusRTU", o => new ModbusRtuProtocol(
            ParseType(o, "I16"),
            ParseOrder(o, "BE")));
    }

    public static void Register(string name, Func<Dictionary<string, string>, IProtocolParser> factory)
        => s_factories[name] = factory;

    public static IEnumerable<string> Names => s_factories.Keys;

    public static bool Exists(string name) => s_factories.ContainsKey(name);

    public static IProtocolParser Create(string name, Dictionary<string, string>? options = null)
        => s_factories.TryGetValue(name, out var f)
            ? f(options ?? [])
            : throw new ArgumentException($"未知协议: {name}（可用: {string.Join(", ", s_factories.Keys)}）");

    public static IReadOnlyList<ProtocolInfo> Infos() =>
    [
        TextProtocol.Info,
        CsvProtocol.Meta,
        StampProtocol.Meta,
        EasyHexProtocol.Meta,
        ModbusRtuProtocol.Meta,
    ];

    private static EasyHexDataType ParseType(Dictionary<string, string> options, string def)
        => options.TryGetValue("type", out var t) && Enum.TryParse<EasyHexDataType>(t, true, out var v) ? v
           : Enum.TryParse<EasyHexDataType>(def, true, out var d) ? d : EasyHexDataType.U16;

    private static ByteOrder ParseOrder(Dictionary<string, string> options, string def)
        => options.TryGetValue("order", out var o) && o.Equals("BE", StringComparison.OrdinalIgnoreCase)
            ? ByteOrder.BigEndian : ByteOrder.LittleEndian;
}
