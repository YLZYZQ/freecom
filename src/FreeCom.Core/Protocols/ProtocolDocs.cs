namespace FreeCom.Core.Protocols;

public sealed record ProtocolHelp(string Name, string Format, string Example, string CSnippet, string Notes);

/// <summary>五协议速查文档（Control API / MCP protocol_help 工具的数据源）。
/// 内容与"协议格式说明"窗口一致，浓缩为 AI 可直接使用的规格文本。</summary>
public static class ProtocolDocs
{
    public static readonly IReadOnlyList<ProtocolHelp> All =
    [
        new("TEXT",
            "{窗口名}数值1,数值2,...\\n（行以换行符 0x0A 结尾）",
            "{plotter}1,2,3\\n{plotter}4,5,6\\n{voltage}3.30,5.00",
            """
            printf("{motor}%ld,%.1f\r\n", speed_rpm, current_a);
            // 或分片构造：snprintf 后任意发送函数，必须带换行符 0x0A
            """,
            "最常用。窗口名决定绘图窗口（自动创建/分窗，仅字母/数字/下划线/横线）；逗号分隔多值为多条曲线（≤16）；X 为电脑接收序号。非数字行（如 {log}boot ok）不绘图仅显示。"),
        new("CSV",
            "数值1,数值2,...\\n　每列一条曲线，单窗口",
            "1,2,3\\n4,5,6",
            """printf("%f,%f,%f\n", adc0, adc1, vbus);""",
            "最简单，无窗口名（全部进入默认窗口）。"),
        new("STAMP",
            "<时间戳>{窗口名}数值1,数值2,...\\n",
            "<0.20>{plotter}1,2,3\\n<0.40>{plotter}4,5,6",
            """printf("<%lu>{motor}%ld,%ld\n", HAL_GetTick(), rpm, ma);""",
            "下位机指定 X 轴（同一窗口时间戳必须严格递增）；时间戳回滚的帧丢弃并计错——此时清空后继续。适合数据与时间严格对齐的场合。"),
        new("EasyHex",
            "原始数据字节 + 固定帧尾（类型决定帧尾；字节序小端/大端可选，默认 U16 小端）",
            "U16 尾 FF FF；I16 尾 00 80。示例：I16 小端 300,500 → 2C 01 F4 01 00 80",
            """
            int16_t v[2] = {300, 500};
            static const uint8_t tail[2] = {0x00, 0x80};   // I16 帧尾
            uart_send((uint8_t*)v, sizeof(v));
            uart_send(tail, 2);
            """,
            "无校验，线路误码会产生异常点（可靠场景用 ModbusRTU）；帧尾值不能出现在数据中。类型在协议切换时经 options 指定（type/order）。"),
        new("ModbusRTU",
            "解析 03 功能码应答帧：地址码 功能码(03) 字节数 数据… CRC低 CRC高（CRC16-MODBUS）",
            "01 03 04 01 2C 01 F4 3A 11（地址01 功能03 数据=300,500）",
            """
            uint16_t crc16_modbus(const uint8_t *p, uint16_t n) {
                uint16_t crc = 0xFFFF;
                while (n--) { crc ^= *p++;
                    for (int i = 0; i < 8; i++) crc = (crc & 1) ? (crc >> 1) ^ 0xA001 : (crc >> 1); }
                return crc;
            }
            // body: addr,0x03,字节数,寄存器值(默认 I16 大端)... 尾部 CRC 低字节在前
            """,
            "校验失败整帧丢弃并自动重同步；按“地址码+功能码”自动分窗。仅解析应答帧（请求帧用发送区发）。数据类型与字节序在协议切换时经 options 指定（默认 I16 大端）。"),
    ];

    public static IReadOnlyList<ProtocolHelp> Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return All;
        var hit = All.Where(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0)
            throw new ArgumentException($"未知协议: {name}（可用: {string.Join(", ", ProtocolRegistry.Names)}）");
        return hit;
    }
}
