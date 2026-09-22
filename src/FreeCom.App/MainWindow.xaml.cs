using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using FreeCom.Core;
using FreeCom.Core.Config;
using FreeCom.Core.ControlApi;
using FreeCom.Core.Export;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;
using ScottPlot.WPF;

namespace FreeCom.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings = new();
    private DataPipeline? _pipeline;
    private ITransport? _transport;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _plotTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _cyclicTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ControlApiServer? _apiServer;
    private PipelineSurface? _surface;

    /// <summary>绘图页状态：含复用渲染缓冲区与持久化 Scatter（零分配渲染，防大对象堆碎片化）。</summary>
    private sealed class PlotTabState
    {
        public TabItem Tab = null!;
        public WpfPlot Plot = null!;
        public long Version;
        public double RenderMs;
        public DateTime LastRenderUtc;
        public double[][] XBuf = [];
        public double[][] YBuf = [];
        public ScottPlot.Plottables.Scatter?[] Scatters = [];
    }

    private const int MaxRenderPointsPerCurve = 4_000;   // 32KB/数组：低于 LOH 阈值
    private static readonly TimeSpan RenderMinInterval = TimeSpan.FromMilliseconds(200); // 渲染节流 5fps
    private readonly Dictionary<string, PlotTabState> _plotTabs = new();

    public MainWindow()
    {
        InitializeComponent();
        EncodingHelper.Get("utf-8"); // 触发编码注册（GBK）

        _settingsStore = new SettingsStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreeCom", "settings.json"));
        _settings = _settingsStore.Load();

        foreach (var name in ProtocolRegistry.Names)
            CbProtocol.Items.Add(name);
        CbProtocol.SelectedItem = ProtocolRegistry.Exists(_settings.ProtocolName) ? _settings.ProtocolName : "TEXT";

        _uiTimer.Tick += (_, _) => OnUiTick();
        _plotTimer.Tick += (_, _) => OnPlotTick();
        // 切换选项卡瞬间：若切到绘图页，立即重绘（避免等待下个定时周期出现空白）
        MainTabs.SelectionChanged += (_, _) =>
        {
            foreach (var kv in _plotTabs)
            {
                if (!ReferenceEquals(MainTabs.SelectedItem, kv.Value.Tab)) continue;
                if (_pipeline is not null && _pipeline.Plots.Find(kv.Key) is { } w)
                {
                    if (kv.Value.Version != w.Version) RenderPlotWindow(w, kv.Value);
                    else kv.Value.Plot.Refresh();
                }
                break;
            }
        };
        _cyclicTimer.Tick += (_, _) =>
        {
            if (_pipeline is { IsTransportOpen: true }) DoSend();
        };
        CkCyclic.Checked += (_, _) => StartCyclicSend();
        CkCyclic.Unchecked += (_, _) => _cyclicTimer.Stop();
        _uiTimer.Start();
        _plotTimer.Start();

        ApplySettings();
        RefreshPorts();
        if (_settings.McpToken is null)
        {
            _settings.McpToken = $"freecom-{Guid.NewGuid():N}";
            _settingsStore.Save(_settings);
        }
        UpdateMcpMenu();

        // 启动参数：--connect <COM口> --protocol <名称> 自动连接（便于演示/自动化验证）
        var cmd = Environment.GetCommandLineArgs();
        string? ArgValue(string name)
        {
            var i = Array.IndexOf(cmd, name);
            return i >= 0 && i + 1 < cmd.Length ? cmd[i + 1] : null;
        }
        var autoConnect = ArgValue("--connect");
        var autoProtocol = ArgValue("--protocol");
        if (autoProtocol is not null && ProtocolRegistry.Exists(autoProtocol))
            CbProtocol.SelectedItem = autoProtocol;
        if (autoConnect is not null)
            Loaded += (_, _) =>
            {
                CbPort.Text = autoConnect;
                OpenClose_OnClick(this, new RoutedEventArgs());
            };

        // 命令行 --protocol-help：启动即打开协议说明（便于文档/验证）
        if (cmd.Contains("--protocol-help"))
            Loaded += (_, _) => ProtocolHelp_OnClick(this, new RoutedEventArgs());
    }

    // ---------------- 串口连接 ----------------

    private void RefreshPorts_OnClick(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        if (CbPort is null || CbBaud is null) return;
        CbPort.Items.Clear();
        try
        {
            foreach (var p in TransportRegistry.ListPorts("serial"))
                CbPort.Items.Add(p.Name);
        }
        catch (Exception ex)
        {
            TbSendHint.Text = $"枚举端口失败: {ex.Message}";
        }
        if (CbPort.Items.Count > 0)
        {
            var preferred = _settings.TransportParams.GetValueOrDefault("port");
            CbPort.SelectedItem = CbPort.Items.Contains(preferred) ? preferred : CbPort.Items[0];
        }
    }

    private string NewlineSelection() => (CbNewline.SelectedItem as ComboBoxItem)?.Content.ToString() switch
    {
        @"\r\n" => "crlf",
        @"\n" => "lf",
        @"\r" => "cr",
        _ => "none",
    };

    private void OpenClose_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pipeline is { IsTransportOpen: true })
        {
            CloseTransport();
            return;
        }

        var parameters = new Dictionary<string, string>
        {
            ["port"] = CbPort.Text,
            ["baud"] = CbBaud.Text,
            ["dataBits"] = ((ComboBoxItem)CbDataBits.SelectedItem).Content.ToString() ?? "8",
            ["parity"] = ((ComboBoxItem)CbParity.SelectedItem).Content.ToString() switch
            {
                "奇" => "odd",
                "偶" => "even",
                _ => "none",
            },
            ["stop"] = ((ComboBoxItem)CbStopBits.SelectedItem).Content.ToString() ?? "1",
            ["flow"] = ((ComboBoxItem)CbFlow.SelectedItem).Content.ToString() switch
            {
                "硬件RTS/CTS" => "rtscts",
                "软件XON/XOFF" => "xon",
                _ => "none",
            },
            ["dtr"] = (CkDtr.IsChecked == true).ToString(),
            ["rts"] = (CkRts.IsChecked == true).ToString(),
        };

        try
        {
            var factory = TransportRegistry.Find("serial") ?? throw new InvalidOperationException("串口传输未注册");
            var transport = factory.Create(new TransportOptions { Kind = "serial", Parameters = parameters });
            transport.Open();

            _pipeline ??= new DataPipeline(
                ProtocolRegistry.Create(_settings.ProtocolName, _settings.ProtocolOptions),
                new PipelineOptions { MaxPointsPerCurve = _settings.MaxPointsPerCurve });
            _pipeline.DefaultEncoding = (CbEncoding.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "utf-8";
            _pipeline.DefaultNewline = NewlineSelection();
            _pipeline.AttachTransport(transport);
            _transport = transport;
            _pipeline.Plots.WindowsChanged += OnPlotsChanged;

            BtnOpen.Content = "关闭";
            TbState.Text = $"状态: 已连接 {_pipeline.TransportDescription}";
            TbSendHint.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开失败：{ex.Message}\n请检查端口是否被占用、参数是否正确。",
                "FreeCom", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseTransport()
    {
        if (_pipeline != null) _pipeline.Plots.WindowsChanged -= OnPlotsChanged;
        _pipeline?.DetachTransport();
        _transport?.Dispose();
        _transport = null;
        BtnOpen.Content = "打开";
        TbState.Text = "状态: 已断开";
    }

    // ---------------- 发送 ----------------

    private void Send_OnClick(object sender, RoutedEventArgs e) => DoSend();

    private void TbSend_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            DoSend();
            e.Handled = true;
        }
    }

    private async void DoSend()
    {
        if (_pipeline is not { IsTransportOpen: true })
        {
            TbSendHint.Text = "端口未打开";
            return;
        }
        var text = TbSend.Text;
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            if (RbHex.IsChecked == true)
            {
                await _pipeline.SendHexAsync(text, NewlineSelection());
            }
            else
            {
                await _pipeline.SendTextAsync(text,
                    (CbEncoding.SelectedItem as ComboBoxItem)?.Content.ToString(),
                    NewlineSelection());
            }
            TbSendHint.Text = "";
            AddHistory(text);
        }
        catch (FormatException ex)
        {
            TbSendHint.Text = ex.Message;
        }
    }

    private const string HistoryPlaceholder = "发送历史";

    private void StartCyclicSend()
    {
        if (!int.TryParse(TbInterval.Text, out var ms)) ms = 1000;
        _cyclicTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 20, 3_600_000));
        _cyclicTimer.Start();
    }

    private void AddHistory(string text)
    {
        CbHistory.Items.Remove(HistoryPlaceholder);
        _settings.SendHistory.Remove(text);
        _settings.SendHistory.Insert(0, text);
        if (_settings.SendHistory.Count > 50) _settings.SendHistory.RemoveAt(_settings.SendHistory.Count);
        CbHistory.Items.Clear();
        foreach (var h in _settings.SendHistory) CbHistory.Items.Add(h);
        if (CbHistory.Items.Count > 0) CbHistory.SelectedIndex = 0;
    }

    private void History_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbHistory.SelectedItem is string s && s != HistoryPlaceholder) TbSend.Text = s;
    }

    // ---------------- UI 刷新（接收区：方向双色 + 视图级筛选） ----------------

    private static readonly SolidColorBrush TxBrush = new(Color.FromRgb(0xF5, 0xA6, 0x23)); // 发送：橙
    private static readonly SolidColorBrush RxBrush = new(Color.FromRgb(0x9C, 0xDC, 0xFE)); // 接收：蓝
    private const int MaxDisplayBlocks = 128;
    private const int MaxDisplayCharacters = 128 * 1024;
    private const int MaxBlockCharacters = 4096;
    private const int MaxEntryBytes = 16 * 1024;
    private readonly Queue<int> _displayBlockLengths = new();
    private int _displayCharacters;
    private string _displaySignature = "";
    private long _renderedSeq;

    private void ResetDisplayDocument()
    {
        TbReceive.Document.Blocks.Clear();
        _displayBlockLengths.Clear();
        _displayCharacters = 0;
    }

    private string DisplaySignature() =>
        $"{CkTimestamp?.IsChecked == true}|{CkHexView?.IsChecked == true}|{(CbFilterDir?.SelectedItem as ComboBoxItem)?.Content}|{TbFilterText?.Text ?? ""}";

    private void OnUiTick()
    {
        if (_pipeline is null) return;
        var c = _pipeline.Counters;
        TbCounters.Text = $"RX: {c.RxBytes} B   TX: {c.TxBytes} B   速率: {c.RxRatePerSecond:F0} B/s   " +
                          $"解析: {c.FramesParsed} 帧   错误: {c.ParseErrors}   " +
                          $"托管内存: {GC.GetTotalMemory(false) / 1048576.0:F0}MB";
        // 隐藏的接收区不构建 WPF 文本；切回时从有界显示日志补最新数据。
        if (MainTabs.SelectedIndex != 0) return;
        var sink = _pipeline.Display;

        var signature = DisplaySignature();
        var rebuild = signature != _displaySignature;
        if (!rebuild && sink.LastSeq <= _renderedSeq) return;

        var hex = CkHexView.IsChecked == true;
        var showTs = CkTimestamp?.IsChecked != false;
        var dirText = (CbFilterDir?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        DataDirection? dir = dirText switch
        {
            "仅接收" => DataDirection.Rx,
            "仅发送" => DataDirection.Tx,
            _ => null,
        };
        var contains = TbFilterText?.Text ?? "";

        var since = rebuild ? 0 : _renderedSeq;
        if (rebuild) ResetDisplayDocument();
        var doc = TbReceive.Document;

        // 按方向分组合批渲染（高速数据时把每行一个段落合并为最多 80 行一个段落，
        // 大幅降低 FlowDocument 对象数与 UI 线程开销；同段同色）
        var pending = new StringBuilder(MaxBlockCharacters);
        var pendingDir = DataDirection.Rx;
        var pendingCount = 0;

        void FlushPending()
        {
            if (pending.Length == 0) return;
            // 添加前淘汰，避免一次刷新先创建巨型文档再删减。
            while (_displayBlockLengths.Count > 0 &&
                   (_displayCharacters + pending.Length > MaxDisplayCharacters || doc.Blocks.Count >= MaxDisplayBlocks))
            {
                _displayCharacters -= _displayBlockLengths.Dequeue();
                doc.Blocks.Remove(doc.Blocks.FirstBlock);
            }
            doc.Blocks.Add(new Paragraph(new Run(pending.ToString())
            {
                Foreground = pendingDir == DataDirection.Tx ? TxBrush : RxBrush,
            })
            { Margin = new Thickness(0) });
            _displayBlockLengths.Enqueue(pending.Length);
            _displayCharacters += pending.Length;
            pending.Clear();
            pendingCount = 0;
        }

        void AppendBounded(string text)
        {
            int offset = 0;
            while (offset < text.Length)
            {
                int take = Math.Min(MaxBlockCharacters - pending.Length, text.Length - offset);
                pending.Append(text, offset, take);
                offset += take;
                if (pending.Length == MaxBlockCharacters) FlushPending();
            }
        }

        bool Matches(DisplayEntry entry)
        {
            if (dir is not null && entry.Dir != dir) return false;
            if (contains.Length == 0) return true;
            return Encoding.UTF8.GetString(entry.Data.Span).Contains(contains, StringComparison.OrdinalIgnoreCase) ||
                   (hex && HexParse.ToHexSpaced(entry.Data.Span).Contains(contains, StringComparison.OrdinalIgnoreCase));
        }
        // 筛选先于视图预算，确保能找到缓存中较早的匹配项。
        // 先记录水位，即使没有匹配项也推进游标；并发到达的数据留给下一次刷新。
        var snapshotSeq = sink.LastSeq;
        var entries = sink.SnapshotRecent(since, predicate: Matches);
        TbReceive.BeginChange();
        try
        {
            if (dir is null && contains.Length == 0 && entries.Count > 0 && entries[0].Seq > since + 1)
                AppendBounded("[接收视图已跳至最新数据；历史数据可在缓存范围内导出]\n");
            foreach (var e in entries)
            {
                if (e.Dir != pendingDir)
                {
                    FlushPending();
                    pendingDir = e.Dir;
                }
                AppendBounded(showTs ? $"[{e.TimeUtc.ToLocalTime():HH:mm:ss.fff}] " : "");
                AppendBounded(e.Dir == DataDirection.Tx ? ">> " : "<< ");
                if (e.Data.Length > MaxEntryBytes) AppendBounded("[大数据块仅显示末尾] ");
                var tail = e.Data.Span[Math.Max(0, e.Data.Length - MaxEntryBytes)..];
                AppendBounded(hex ? HexParse.ToHexSpaced(tail) : Encoding.UTF8.GetString(tail));
                AppendBounded("\n");
                if (++pendingCount >= 80) FlushPending();
            }
            FlushPending();
        }
        finally { TbReceive.EndChange(); }
        // 只推进到本次快照末尾，避免跳过刷新过程中刚收到的包。
        _renderedSeq = entries.Count > 0 ? Math.Max(snapshotSeq, entries[^1].Seq) : snapshotSeq;
        _displaySignature = signature;

        if (CkAutoScroll.IsChecked == true) TbReceive.ScrollToEnd();

    }

    // ---------------- 绘图窗口 ----------------

    private void OnPlotsChanged()
        => Dispatcher.BeginInvoke(RebuildPlotTabs);

    private void RebuildPlotTabs()
    {
        if (_pipeline is null) return;
        var windows = _pipeline.Plots.SnapshotWindows();

        foreach (var w in windows)
            if (!_plotTabs.ContainsKey(w.Id)) AddPlotTab(w);

        var alive = windows.Select(w => w.Id).ToHashSet();
        foreach (var kv in _plotTabs.ToList())
        {
            if (!alive.Contains(kv.Key))
            {
                MainTabs.Items.Remove(kv.Value.Tab);
                _plotTabs.Remove(kv.Key);
            }
        }
    }

    private void AddPlotTab(PlotWindow window)
    {
        var plot = new WpfPlot();
        ApplyDarkTheme(plot.Plot);
        var tab = new TabItem { Header = $"{window.Title}  (绘图)", Content = plot };
        MainTabs.Items.Add(tab);
        MainTabs.SelectedItem = tab; // 新窗口出现时自动切换展示

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名曲线..." };
        rename.Click += (_, _) => RenameCurve(window);
        var autoScale = new MenuItem { Header = "自动缩放" };
        autoScale.Click += (_, _) => { plot.Plot.Axes.AutoScale(); plot.Refresh(); };
        var clear = new MenuItem { Header = "清空本窗口数据" };
        clear.Click += (_, _) => window.ClearData();
        var close = new MenuItem { Header = "关闭本窗口" };
        close.Click += (_, _) =>
        {
            MainTabs.Items.Remove(tab);
            _plotTabs.Remove(window.Id);
            _pipeline?.Plots.Remove(window.Id);
        };
        menu.Items.Add(rename);
        menu.Items.Add(autoScale);
        menu.Items.Add(clear);
        menu.Items.Add(close);
        plot.ContextMenu = menu;

        _plotTabs[window.Id] = new PlotTabState { Tab = tab, Plot = plot };
    }

    private void RenameCurve(PlotWindow window)
    {
        var curves = window.Curves;
        if (curves.Count == 0) return;
        var dialog = new Window
        {
            Title = "重命名曲线",
            Width = 340,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };
        var combo = new ComboBox { Margin = new Thickness(12) };
        foreach (var c in curves) combo.Items.Add(c.Name);
        combo.SelectedIndex = 0;
        var input = new TextBox { Margin = new Thickness(12, 4, 12, 12) };
        input.TextChanged += (_, _) => { };
        combo.SelectionChanged += (_, _) =>
        {
            var idx = combo.SelectedIndex;
            if (idx >= 0 && idx < curves.Count) input.Text = curves[idx].Name;
        };
        if (curves.Count > 0) input.Text = curves[0].Name;
        var ok = new Button { Content = "确定", Width = 70, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        dialog.Content = new StackPanel { Children = { new Label { Content = "选择曲线：" }, combo, input, ok } };
        ok.Click += (_, _) =>
        {
            var idx = combo.SelectedIndex;
            if (idx >= 0 && idx < curves.Count && input.Text.Trim().Length > 0)
                curves[idx].Name = input.Text.Trim();
            dialog.Close();
        };
        dialog.ShowDialog();
    }

    private void OnPlotTick()
    {
        if (_pipeline is null || _plotTabs.Count == 0) return;
        foreach (var w in _pipeline.Plots.SnapshotWindows())
        {
            if (!_plotTabs.TryGetValue(w.Id, out var entry)) continue;
            // 只渲染当前选中的绘图页；未选中的页在切换瞬间由 SelectionChanged 渲染。
            // 数据未变化不重绘（空闲零渲染）；渲染节流（高速数据下 5fps 足够，同时降低分配压力）。
            if (!ReferenceEquals(MainTabs.SelectedItem, entry.Tab)) continue;
            if (entry.Version == w.Version) continue;
            if (DateTime.UtcNow - entry.LastRenderUtc < RenderMinInterval) continue;
            RenderPlotWindow(w, entry);
        }
    }

    /// <summary>把 PlotWindow 数据更新到 ScottPlot 并刷新（复用缓冲区与 Scatter，零稳态分配）。</summary>
    private void RenderPlotWindow(PlotWindow w, PlotTabState entry)
    {
        var sw = Stopwatch.StartNew();
        var plot = entry.Plot.Plot;
        var curves = w.Curves;

        // 曲线数量变化时才重建 Scatter；否则复用（缓冲区原地覆写 + MaxRenderIndex 限长）
        if (entry.XBuf.Length != curves.Count)
        {
            plot.Clear();
            entry.XBuf = new double[curves.Count][];
            entry.YBuf = new double[curves.Count][];
            entry.Scatters = new ScottPlot.Plottables.Scatter?[curves.Count];
            for (int i = 0; i < curves.Count; i++)
            {
                entry.XBuf[i] = new double[MaxRenderPointsPerCurve];
                entry.YBuf[i] = new double[MaxRenderPointsPerCurve];
            }
        }

        for (int i = 0; i < curves.Count; i++)
        {
            var take = curves[i].SnapshotInto(entry.XBuf[i], entry.YBuf[i], MaxRenderPointsPerCurve);
            if (entry.Scatters[i] is null)
            {
                entry.Scatters[i] = plot.Add.Scatter(entry.XBuf[i], entry.YBuf[i]);
                entry.Scatters[i]!.LineWidth = 1.2f;
                plot.ShowLegend();
            }
            entry.Scatters[i]!.LegendText = curves[i].Name;
            entry.Scatters[i]!.Data.MaxRenderIndex = Math.Max(-1, take - 1);
        }
        plot.Axes.AutoScale();
        entry.Plot.Refresh();
        sw.Stop();

        entry.RenderMs = entry.RenderMs * 0.7 + sw.Elapsed.TotalMilliseconds * 0.3;
        entry.Tab.Header = $"{w.Title}  ({entry.RenderMs:F0}ms)";
        entry.Version = w.Version;
        entry.LastRenderUtc = DateTime.UtcNow;
    }

    private static void ApplyDarkTheme(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#14171C");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101317");
        plot.Axes.Color(ScottPlot.Color.FromHex("#9AB0C0"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#1F2933");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1A1E24");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#D6E2EE");
        plot.XLabel("points");
        plot.YLabel("value");
    }

    // ---------------- 协议切换 ----------------

    private void Protocol_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbProtocol.SelectedItem is not string name || !IsLoaded) return;
        _settings.ProtocolName = name;
        _settings.ProtocolOptions = [];
        try
        {
            _pipeline?.SetParser(ProtocolRegistry.Create(name));
        }
        catch (Exception ex)
        {
            TbSendHint.Text = $"协议切换失败: {ex.Message}";
        }
    }

    // ---------------- 清空 / 导出 ----------------

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        _pipeline?.Plots.ClearData();
        ClearDisplay();
    }

    private void ClearDisplay_OnClick(object sender, RoutedEventArgs e) => ClearDisplay();

    private void ClearDisplay()
    {
        _pipeline?.Display.Clear();
        _pipeline?.Raw.Clear();
        _renderedSeq = _pipeline?.Display.LastSeq ?? 0;
        ResetDisplayDocument();
    }

    private void ExportRaw_OnClick(object sender, RoutedEventArgs e)
        => ExportWithDialog("原始数据|*.dat", path =>
        {
            if (_pipeline is null) return;
            Exporters.ExportRaw(path, _pipeline.Raw);
        });

    private void ExportDisplay_OnClick(object sender, RoutedEventArgs e)
        => ExportWithDialog("显示数据|*.txt", path =>
        {
            if (_pipeline is null) return;
            Exporters.ExportDisplay(path, _pipeline.Display, true, CkHexView.IsChecked == true);        });

    private void ExportCurves_OnClick(object sender, RoutedEventArgs e)
        => ExportWithDialog("曲线数据|*.csv", path =>
        {
            if (_pipeline is null) return;
            Exporters.ExportCurvesCsv(path, _pipeline.Plots, null);
        });

    private void ExportWithDialog(string filter, Action<string> save)
    {
        if (_pipeline is null)
        {
            MessageBox.Show("没有可导出的数据。", "FreeCom");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                save(dlg.FileName);
                MessageBox.Show($"已导出：{dlg.FileName}", "FreeCom");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "FreeCom");
            }
        }
    }

    // ---------------- MCP / Control API ----------------

    private async void McpToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (McpToggle.IsChecked == true)
        {
            _pipeline ??= new DataPipeline(ProtocolRegistry.Create(_settings.ProtocolName));
            _surface ??= new PipelineSurface(_pipeline);
            _apiServer = new ControlApiServer(_surface, new ControlApiOptions
            {
                Port = 17340,
                Token = _settings.McpToken!,
            });
            try
            {
                await _apiServer.StartAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"MCP 服务启动失败：{ex.Message}", "FreeCom");
                McpToggle.IsChecked = false;
                _apiServer = null;
                return;
            }
        }
        else
        {
            if (_apiServer != null) await _apiServer.StopAsync();
            _apiServer = null;
        }
        UpdateMcpMenu();
    }

    private void UpdateMcpMenu()
    {
        var on = _apiServer != null;
        McpInfo.Header = on
            ? $"MCP：http://127.0.0.1:17340  Token: {_settings.McpToken}"
            : "MCP 状态：未启用";
    }

    private void McpInfo_OnClick(object sender, RoutedEventArgs e)
    {
        if (_apiServer == null) return;
        var usage =
            "AI 客户端（Cursor 等）MCP 配置：\n\n" +
            "命令: freecom-mcp.exe\n" +
            "环境变量:\n" +
            $"  FREECOM_URL=http://127.0.0.1:17340\n" +
            $"  FREECOM_TOKEN={_settings.McpToken}\n\n" +
            "接口仅监听本机（127.0.0.1）。";
        MessageBox.Show(usage, "FreeCom MCP 接入", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- 虚拟串口管理 / 设备模拟 ----------------

    private VcomManagerWindow? _vcomWindow;
    private DeviceSimulatorWindow? _simWindow;

    private void VcomManager_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vcomWindow is { IsLoaded: true }) { _vcomWindow.Activate(); return; }
        _vcomWindow = new VcomManagerWindow(() => RefreshPorts()) { Owner = this };
        _vcomWindow.Show();
    }

    private void DeviceSimulator_OnClick(object sender, RoutedEventArgs e)
    {
        if (_simWindow is { IsLoaded: true }) { _simWindow.Activate(); return; }
        var currentPort = _pipeline is { IsTransportOpen: true } ? CbPort.Text : null;
        _simWindow = new DeviceSimulatorWindow(currentPort) { Owner = this };
        _simWindow.Show();
    }

    // ---------------- 其它 ----------------

    private ProtocolHelpWindow? _helpWindow;

    private void ProtocolHelp_OnClick(object sender, RoutedEventArgs e)
    {
        if (_helpWindow is { IsLoaded: true }) { _helpWindow.Activate(); return; }
        _helpWindow = new ProtocolHelpWindow { Owner = this };
        _helpWindow.Show();
    }

    private void About_OnClick(object sender, RoutedEventArgs e)
        => MessageBox.Show("FreeCom 免费串口调试助手 v0.1\n全功能免费 · 无账户 · 无授权\n\nMVP：串口/虚拟回环 + 5 协议绘图 + MCP AI 自动化",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Exit_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ApplySettings()
    {
        if (int.TryParse(_settings.TransportParams.GetValueOrDefault("baud"), out var baud))
            CbBaud.Text = baud.ToString();
        CkHexView.IsChecked = _settings.HexDisplay;
        RbHex.IsChecked = _settings.HexSend;
        CkAutoScroll.IsChecked = _settings.AutoScroll;
        CkTimestamp.IsChecked = _settings.DisplayTimestamp;
        CkCyclic.IsChecked = _settings.CyclicSend;
        // 串口参数还原
        foreach (ComboBoxItem item in CbDataBits.Items)
            if ((string)item.Content == _settings.TransportParams.GetValueOrDefault("dataBits", "8"))
                CbDataBits.SelectedItem = item;
        foreach (ComboBoxItem item in CbStopBits.Items)
            if ((string)item.Content == _settings.TransportParams.GetValueOrDefault("stop", "1"))
                CbStopBits.SelectedItem = item;
        var parity = _settings.TransportParams.GetValueOrDefault("parity", "none") switch { "odd" => "奇", "even" => "偶", _ => "无" };
        foreach (ComboBoxItem item in CbParity.Items)
            if ((string)item.Content == parity) CbParity.SelectedItem = item;
        var flow = _settings.TransportParams.GetValueOrDefault("flow", "none") switch { "rtscts" => "硬件RTS/CTS", "xon" => "软件XON/XOFF", _ => "无" };
        foreach (ComboBoxItem item in CbFlow.Items)
            if ((string)item.Content == flow) CbFlow.SelectedItem = item;
        CkDtr.IsChecked = bool.TryParse(_settings.TransportParams.GetValueOrDefault("dtr"), out var dtr) && dtr;
        CkRts.IsChecked = bool.TryParse(_settings.TransportParams.GetValueOrDefault("rts"), out var rts) && rts;
        // 接收区筛选还原
        foreach (ComboBoxItem item in CbFilterDir.Items)
            if ((string)item.Content == (_settings.DisplayFilterDir is null ? "全部" : _settings.DisplayFilterDir))
                CbFilterDir.SelectedItem = item;
        TbFilterText.Text = _settings.DisplayFilterText ?? "";
        TbInterval.Text = _settings.CyclicIntervalMs.ToString();
        foreach (ComboBoxItem item in CbEncoding.Items)
            if ((string)item.Content == _settings.EncodingName) CbEncoding.SelectedItem = item;
        foreach (var h in _settings.SendHistory) CbHistory.Items.Add(h);
        if (CbHistory.Items.Count == 0)
        {
            CbHistory.Items.Add(HistoryPlaceholder);
            CbHistory.SelectedItem = HistoryPlaceholder; // 收起态显示提示而非空白
        }
    }

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.TransportKind = "serial"; // v0.1.1：唯一传输类型为串口（配置迁移兜底）
        _settings.TransportParams["port"] = CbPort.Text;
        if (int.TryParse(CbBaud.Text, out var baud)) _settings.TransportParams["baud"] = baud.ToString();
        _settings.TransportParams["dataBits"] = ((ComboBoxItem)CbDataBits.SelectedItem).Content.ToString() ?? "8";
        _settings.TransportParams["parity"] = ((ComboBoxItem)CbParity.SelectedItem).Content.ToString() switch
        {
            "奇" => "odd",
            "偶" => "even",
            _ => "none",
        };
        _settings.TransportParams["stop"] = ((ComboBoxItem)CbStopBits.SelectedItem).Content.ToString() ?? "1";
        _settings.TransportParams["flow"] = ((ComboBoxItem)CbFlow.SelectedItem).Content.ToString() switch
        {
            "硬件RTS/CTS" => "rtscts",
            "软件XON/XOFF" => "xon",
            _ => "none",
        };
        _settings.TransportParams["dtr"] = (CkDtr.IsChecked == true).ToString();
        _settings.TransportParams["rts"] = (CkRts.IsChecked == true).ToString();
        _settings.DisplayFilterDir = ((ComboBoxItem)CbFilterDir.SelectedItem)?.Content?.ToString() ?? "全部";
        _settings.DisplayFilterText = TbFilterText.Text;
        _settings.HexDisplay = CkHexView.IsChecked == true;
        _settings.HexSend = RbHex.IsChecked == true;
        _settings.AutoScroll = CkAutoScroll.IsChecked == true;
        _settings.DisplayTimestamp = CkTimestamp.IsChecked == true;
        _settings.EncodingName = (CbEncoding.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "utf-8";
        _settings.CyclicSend = CkCyclic.IsChecked == true;
        if (int.TryParse(TbInterval.Text, out var ms)) _settings.CyclicIntervalMs = Math.Max(20, ms);
        _settingsStore.Save(_settings);

        _uiTimer.Stop();
        _plotTimer.Stop();
        _cyclicTimer.Stop();
        CloseTransport();
        _pipeline?.Dispose();
        _apiServer?.StopAsync().GetAwaiter().GetResult();
    }
}
