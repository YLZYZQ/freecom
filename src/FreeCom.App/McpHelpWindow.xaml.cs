using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FreeCom.Core.ControlApi;

namespace FreeCom.App;

/// <summary>MCP AI 自动化帮助：概览/开启服务/客户端接入/工具清单/使用示例/常见问题。
/// 工具清单从 McpToolCatalog 动态生成，与实际能力永远一致。主题样式与协议帮助窗口一致。</summary>
public partial class McpHelpWindow : Window
{
    private readonly List<(string Name, StackPanel Panel)> _sections = [];
    private StackPanel _current = null!;

    public McpHelpWindow()
    {
        InitializeComponent();

        Section("概览", () =>
        {
            Heading("MCP AI 自动化是什么");
            Body("MCP（Model Context Protocol）是 AI 客户端的工具接入标准。开启后，Cursor、Claude Desktop 等支持 MCP 的 AI 助手可以直接操作 FreeCom：打开串口、收发数据、等待应答、读曲线统计、控制设备模拟器等——用自然语言指挥 AI 完成串口调试。");
            Body("架构：FreeCom 内嵌本地 Control API（仅监听 127.0.0.1:17340，Bearer Token 鉴权），随软件提供 freecom-mcp.exe 桥接程序，把全部能力以 MCP 工具形式暴露给 AI 客户端。");
            Heading("能做什么");
            Body("・让 AI 自动做\"发 AT 命令并等 OK 应答\"类命令-应答测试，统计成功率");
            Body("・无硬件自测：AI 创建虚拟串口对 + 启动设备模拟器，验证整条收发与绘图链路");
            Body("・AI 直接读曲线统计（均值/最值）判断传感器数据是否正常");
            Body("・把接收数据导出成文件交给 AI 分析，或让 AI 下发固件文件到设备");
            Heading("安全说明");
            Body("服务仅监听本机回环地址（127.0.0.1），外部网络无法访问；所有请求需携带本机生成的 Token；不联网上报任何数据（仅\"检查更新\"访问 GitHub 一次）.");
        });

        Section("开启服务", () =>
        {
            Heading("三步开启");
            Body("1. 菜单 工具(T) → 勾选「启用 MCP 服务（本地 Control API）」");
            Body("2. 菜单 工具(T) → 「MCP 状态」查看连接地址与 Token（即 Device ID）");
            Body("3. 状态栏右侧「MCP」徽标显示服务是否运行");
            Sample("连接地址固定为 http://127.0.0.1:17340\nToken 示例：freecom-3f2a9c...（每台电脑首次启动时自动生成，保存在本机配置）");
            Heading("注意事项");
            Body("・同时只能有一个 FreeCom 实例开启 MCP（端口 17340 被占用时第二个实例会提示失败）");
            Body("・Token 等同于本机串口的操作权限，不要发给别人；怀疑泄露时可删除配置文件中的 McpToken 字段后重启，会生成新 Token");
            Body("・关闭软件或取消勾选即停止服务，AI 客户端将无法连接");
        });

        Section("客户端接入", () =>
        {
            Heading("Cursor 配置示例");
            Body("Cursor → Settings → MCP → Add new MCP Server，填入（路径按实际安装位置调整）：");
            Code("""
{
  \"mcpServers\": {
    \"freecom\": {
      \"command\": \"C:\\\\Program Files\\\\FreeCom\\\\FreeCom.Mcp.exe\",
      \"env\": {
        \"FREECOM_URL\": \"http://127.0.0.1:17340\",
        \"FREECOM_TOKEN\": \"你的 Token（软件 工具→MCP 状态 里查看）\"
      }
    }
  }
}
""");
            Body("Claude Desktop 等其他 MCP 客户端：在各自的 MCP 配置文件中加入相同的 command + env 结构即可。");
            Heading("路径说明");
            Body("・安装版：FreeCom.Mcp.exe 与主程序同目录（默认 C:\\Program Files\\FreeCom\\）");
            Body("・免安装版：解压目录内");
            Body("・接入前先启动 FreeCom 并开启 MCP 服务；AI 客户端里可调用 diag_connectivity 工具验证连通");
        });

        Section("工具清单", () =>
        {
            Heading($"全部 {McpToolCatalog.Tools.Length} 个工具（与当前版本实际能力一致）");
            Body("按用途分组；AI 会自行选择合适的工具，通常不需要人工指定。");
            foreach (var group in new (string Title, string[] Prefixes)[]
                     {
                         ("连接与诊断", new[] { "diag_", "serial_", "app_info" }),
                         ("发送", new[] { "device_send", "send_" }),
                         ("接收与等待", new[] { "receive_" }),
                         ("协议与绘图", new[] { "protocol_", "plot_", "curve_" }),
                         ("导出", new[] { "export_" }),
                         ("测试基建（无硬件）", new[] { "simulator_", "vcom_" }),
                     })
            {
                var tools = McpToolCatalog.Tools
                    .Where(t => group.Prefixes.Any(p => t.Name.StartsWith(p, StringComparison.Ordinal)))
                    .ToList();
                if (tools.Count == 0) continue;
                Heading(group.Title);
                foreach (var t in tools)
                    Body($"・{t.Name} —— {t.Description}");
            }
        });

        Section("使用示例", () =>
        {
            Heading("对 AI 说（示例）");
            Sample("・\"打开 COM24，波特率 115200，然后每 200ms 发送 AT+VER? 并等待 OK 应答，发 10 次统计成功率\"");
            Sample("・\"我没有任何硬件：创建一对虚拟串口，主程序连一头，用设备模拟器按 CSV 协议每 50ms 发三路正弦数据，1 分钟后告诉我三条曲线的均值和最值\"");
            Sample("・\"把刚才收到的全部原始数据导出到 D:\\\\capture.dat\"");
            Sample("・\"切到 ModbusRTU 协议，然后给我讲下这个协议的帧格式和下位机 C 例程\"");
            Heading("推荐的调试节奏");
            Body("1. 先让 AI 调用 protocol_help 拿协议规格，再生成/修改下位机代码；");
            Body("2. 用 send_expect 做命令-应答验证（比 receive_read 轮询更直接）；");
            Body("3. 数据异常时用 curve_stats 看统计、export_raw 留档分析；");
            Body("4. 无硬件阶段全部可用 simulator_* + vcom_* 在虚拟链路上验证。");
        });

        Section("常见问题", () =>
        {
            Heading("AI 报 401 / unauthorized");
            Body("FREECOM_TOKEN 与软件内 Token 不一致。在 工具 → MCP 状态 重新复制，更新客户端配置后重启 AI 客户端。");
            Heading("AI 连不上 / 无法连接 FreeCom Control API");
            Body("按顺序检查：① FreeCom 是否已启动并勾选启用 MCP；② 状态栏 MCP 徽标是否显示运行中；③ FREECOM_URL 是否为 http://127.0.0.1:17340；④ 是否有第二个 FreeCom 实例占了端口。");
            Heading("发送报\"串口写超时\"");
            Body("串口对端没有消费数据（对端未打开/固件不读）。属于链路问题而非软件故障，检查对端后重试。");
            Heading("修改配置后 AI 工具列表没变");
            Body("MCP 工具清单在 AI 客户端连接时读取一次，重启 AI 客户端（或重新连接 MCP 服务）即可。");
        });

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
        NavList.Items.Add(name);
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
