using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FreeCom.App;

/// <summary>协议帮助（设计稿 v2）：左侧协议导航 + 右侧该协议的格式定义/示例/C 例程。
/// 颜色经主题资源解析，跟随暗/亮主题。代码块两种主题下都保持深底（可读性优先）。</summary>
public partial class ProtocolHelpWindow : Window
{
    private readonly List<(string Name, StackPanel Panel)> _sections = [];
    private StackPanel _current = null!;

    public ProtocolHelpWindow()
    {
        InitializeComponent();

        Section("通用", () =>
        {
            Heading("通用说明");
            Body("协议与通信方式无关（串口/虚拟串口对均可用）；换行符指 0x0A。");
            Body("选型建议：优先文本协议（可读、易排查）；需要严格时间对齐用 STAMP；二进制场景用 EasyHex；已有 Modbus 设备用 ModbusRTU。调试疑难时切换 HEX 显示核对原始字节。");
            Body("五协议可随时在主窗口左侧“协议”下拉切换；以下 X 指 X 轴取值，Y 指曲线数值。");
            Heading("快速自验");
            Body("无需下位机：工具 → 设备模拟器，选对端端口 + 对应协议 → 开始发送，即可在绘图窗口看到每种协议的实际效果。");
        });

        Section("TEXT", () =>
        {
            Heading("TEXT 协议（最常用）");
            Body("格式：{窗口名}数值1,数值2,...\\n（行以换行符结尾）");
            Body("一行一帧。窗口名决定数据进入哪个绘图窗口（自动创建/分窗）；逗号分隔的数字依次作为第 1~16 条曲线的 Y 值；X 由电脑收到时刻自动编号。窗口名仅限英文字母/数字/下划线/横线。");
            Sample("发送（下位机输出）：\n{plotter}1,2,3\n{plotter}4,5,6\n{voltage}3.30,5.00\n效果：自动创建窗口 plotter（3 条曲线，各 2 个点）与窗口 voltage（2 条曲线）");
            Code("""
// 下位机 C 例程（printf 走串口重定向即可）
printf("{motor}%ld,%.1f\\r\\n", speed_rpm, current_a);

// 也可分片构造（任意 TCP/串口发送函数）
char line[64];
int n = snprintf(line, sizeof(line), "{imu}%.2f,%.2f,%.2f\\n", gx, gy, gz);
uart_send((uint8_t*)line, n);   // 必须带换行符 0x0A
""");
            Body("注意：非数字内容的行（如 {log}boot ok）不会绘图，仅留在接收区；乱码时先查波特率与编码。");
        });

        Section("STAMP", () =>
        {
            Heading("STAMP 协议（下位机指定 X 轴）");
            Body("格式：<时间戳>{窗口名}数值1,数值2,...\\n");
            Body("与 TEXT 相同，但 X 由下位机给出（同一窗口的时间戳必须严格递增），适合数据与时间严格对齐的场合。");
            Sample("发送：\n<0.20>{plotter}1,2,3\n<0.40>{plotter}4,5,6\n效果：plotter 窗口三点曲线，X 分别为 0.20、0.40");
            Code("""
// 下位机 C 例程：ms 计数或秒计数皆可，保证递增
printf("<%lu>{motor}%ld,%ld\\n", HAL_GetTick(), rpm, ma);
""");
            Body("注意：时间戳回滚（系统重启计数清零）时软件丢弃该帧并计入解析错误——此时请点主界面“清空”后继续。");
        });

        Section("CSV", () =>
        {
            Heading("CSV 协议（最简单）");
            Body("格式：数值1,数值2,...\\n　每列一条曲线，单窗口。");
            Sample("发送：\n1,2,3\n4,5,6\n效果：3 条曲线，曲线1 的点为 1、4，曲线2 为 2、5……");
            Code("""
printf("%f,%f,%f\\n", adc0, adc1, vbus);
""");
        });

        Section("EasyHex", () =>
        {
            Heading("EasyHex 协议（二进制·简单帧尾）");
            Body("格式：原始数据字节 + 固定帧尾。帧尾由数据类型决定，字节序可选小端/大端：");
            Sample("类型   帧尾(小端流中出现顺序)\nU8     FF\nI8     80\nU16    FF FF\nI16    00 80\nU32    FF FF FF FF\nI32    00 00 00 80\nF32    FF FF FF FF\n示例：I16 小端发送 300、500 → 2C 01 F4 01 00 80");
            Code("""
// 下位机 C 例程：I16 小端双通道
int16_t v[2] = {300, 500};
static const uint8_t tail[2] = {0x00, 0x80};   // I16 帧尾
uart_send((uint8_t*)v, sizeof(v));              // 4 字节数据
uart_send(tail, 2);                             // 帧尾

// F32 多通道
float f[3] = {1.0f, -2.5f, 3.14f};
static const uint8_t tail32[4] = {0xFF, 0xFF, 0xFF, 0xFF};
uart_send((uint8_t*)f, sizeof(f));
uart_send(tail32, 4);
""");
            Body("注意：① 帧尾值被占用（如 I16 的 -32768 即 0x8000 不能出现，数据含 0x0080 序列会提前分帧）；② 无校验，线路误码会产生异常点——可靠场景请用 ModbusRTU。");
        });

        Section("ModbusRTU", () =>
        {
            Heading("ModbusRTU 协议（二进制·CRC 校验）");
            Body("格式：解析 03 功能码应答帧：地址码 功能码(03) 字节数 数据… CRC低 CRC高。CRC16-MODBUS 校验失败整帧丢弃并自动重同步；按“地址码+功能码”自动分窗。数据类型与字节序在协议切换时设定（默认 I16 大端）。");
            Sample("帧：01 03 04 01 2C 01 F4 3A 11\n含义：地址01 功能03 共4字节 数据=0x012C(300)、0x01F4(500) CRC=0x113A\n效果：窗口 ADDR:01 FUNC:03 画出 300、500\n分窗：换地址码（如 02）即自动开新窗口");
            Code("""
// 下位机 C 例程：构造应答帧
uint16_t crc16_modbus(const uint8_t *p, uint16_t n) {
    uint16_t crc = 0xFFFF;
    while (n--) {
        crc ^= *p++;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) ? (crc >> 1) ^ 0xA001 : (crc >> 1);
    }
    return crc;
}

void send_modbus_frame(uint8_t addr, const int16_t *val, uint8_t count) {
    uint8_t body[3 + 246];
    body[0] = addr; body[1] = 0x03; body[2] = (uint8_t)(count * 2);
    for (uint8_t i = 0; i < count; i++) {          // 大端
        body[3 + i*2]     = (uint8_t)(val[i] >> 8);
        body[3 + i*2 + 1] = (uint8_t)(val[i] & 0xFF);
    }
    uint16_t crc = crc16_modbus(body, 3 + count * 2);
    uint8_t tail[2] = { (uint8_t)(crc & 0xFF), (uint8_t)(crc >> 8) }; // 低字节在前
    uart_send(body, 3 + count * 2);
    uart_send(tail, 2);
}
""");
            Body("注意：仅解析应答帧（请求帧可用发送区发送）；同一帧内数据类型必须一致。");
        });

        foreach (var (name, _) in _sections) NavList.Items.Add(name);
        NavList.SelectedIndex = 0;
    }

    private void Section(string name, Action build)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        _current = panel;
        build();
        panel.Visibility = Visibility.Collapsed;
        _sections.Add((name, panel));
        ContentHost.Children.Add(panel);
    }

    private void Nav_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = NavList.SelectedIndex;
        if (idx < 0 || idx >= _sections.Count) return;
        for (int i = 0; i < _sections.Count; i++)
            _sections[i].Panel.Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
    }

    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    private void Heading(string text)
        => _current.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = ThemeBrush("TextPrimary"),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 4),
        });

    private void Body(string text)
        => _current.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = ThemeBrush("TextSecondary"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        });

    private void Sample(string text) => AddBlock(text, "RxBrush");

    private void Code(string text) => AddBlock(text, "CodeTextBrush");

    /// <summary>代码/示例块：深底 + 等宽，可选中复制。</summary>
    private void AddBlock(string text, string foregroundKey)
        => _current.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 12,
            Background = ThemeBrush("CodeBrush"),
            Foreground = ThemeBrush(foregroundKey),
            BorderBrush = ThemeBrush("BorderBrush"),
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(8),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
}
