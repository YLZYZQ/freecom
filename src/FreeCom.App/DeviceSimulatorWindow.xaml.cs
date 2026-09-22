using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FreeCom.Core;
using FreeCom.Core.Protocols;

namespace FreeCom.App;

/// <summary>设备模拟器：占用虚拟串口对的对端，扮演下位机（协议流量 + 手动发送）。</summary>
public partial class DeviceSimulatorWindow : Window
{
    private SerialPort? _port;
    private DispatcherTimer? _timer;
    private long _frameIndex;

    public DeviceSimulatorWindow(string? excludePort)
    {
        InitializeComponent();
        foreach (var name in ProtocolRegistry.Names)
            CbProtocol.Items.Add(name);
        CbProtocol.SelectedItem = "TEXT";
        ReloadPorts(excludePort);
    }

    private void ReloadPorts_OnClick(object sender, RoutedEventArgs e) => ReloadPorts(null);

    private void ReloadPorts(string? exclude)
    {
        var selected = CbPort.SelectedItem as string;
        CbPort.Items.Clear();
        foreach (var p in SerialPort.GetPortNames().Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (p == exclude) continue; // 避开主界面已连接的端口
            CbPort.Items.Add(p);
        }
        if (selected is not null && CbPort.Items.Contains(selected)) CbPort.SelectedItem = selected;
        else if (CbPort.Items.Count > 0) CbPort.SelectedIndex = 0;
    }

    private bool EnsureOpen()
    {
        if (_port is { IsOpen: true }) return true;
        var name = CbPort.SelectedItem as string ?? CbPort.Text;
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            _port = new SerialPort(name, 115200) { WriteTimeout = 3000 };
            _port.Open();
            return true;
        }
        catch (Exception ex)
        {
            TbState.Text = $"状态: 打开 {name} 失败（{ex.Message}）";
            return false;
        }
    }

    private void Start_OnClick(object sender, RoutedEventArgs e)
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer = null;
            BtnStart.Content = "开始发送";
            TbState.Text = "状态: 已停止";
            return;
        }
        if (!EnsureOpen())
        {
            MessageBox.Show("请先选择可用的对端端口（虚拟串口对的另一头）。", "FreeCom");
            return;
        }
        if (!int.TryParse(TbInterval.Text, out var interval) || interval < 10) interval = 100;
        var protocol = CbProtocol.SelectedItem as string ?? "TEXT";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interval) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                var frame = TrafficFrames.FrameFor(protocol, ++_frameIndex);
                _port!.Write(frame, 0, frame.Length);
                if (_frameIndex % 20 == 1)
                    TbLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {protocol} #{_frameIndex} 已发送\n");
            }
            catch (Exception ex)
            {
                TbState.Text = $"状态: 发送失败（{ex.Message}）";
                if (_timer is not null) { _timer.Stop(); _timer = null; BtnStart.Content = "开始发送"; }
            }
        };
        _timer.Start();
        BtnStart.Content = "停止";
        TbState.Text = $"状态: 每 {interval}ms 发送 {protocol} → {_port!.PortName}";
    }

    private void Manual_OnClick(object sender, RoutedEventArgs e)
    {
        var text = TbManual.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (!EnsureOpen()) return;
        try
        {
            byte[] bytes = RbHex.IsChecked == true
                ? HexParse.ParseLoose(text)
                : System.Text.Encoding.UTF8.GetBytes(text);
            _port!.BaseStream.Write(bytes);
            _port.BaseStream.Flush();
            TbLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] 手动 {bytes.Length}B 已发送\n");
            TbLog.ScrollToEnd();
        }
        catch (Exception ex)
        {
            TbState.Text = $"状态: 发送失败（{ex.Message}）";
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _timer?.Stop();
        try { if (_port is { IsOpen: true }) _port.Close(); _port?.Dispose(); } catch { }
        base.OnClosing(e);
    }
}
