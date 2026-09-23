using System.Windows;
using System.Windows.Controls;
using FreeCom.Core.Transports;

namespace FreeCom.App;

/// <summary>虚拟串口管理器：列出/创建/删除 com0com 端口对（操作需 UAC 提权）。</summary>
public partial class VcomManagerWindow : Window
{
    private readonly VirtualComManager _manager = new();
    private readonly Action _onPortsChanged;

    public VcomManagerWindow(Action onPortsChanged)
    {
        InitializeComponent();
        _onPortsChanged = onPortsChanged;
        Reload();
    }

    private void Reload_OnClick(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        TbDriver.Text = _manager.DriverInstalled
            ? $"驱动已安装 · {_manager.InstallDir}"
            : "驱动未安装（点击右侧“下载与安装说明”）";
        DotDriver.Fill = (System.Windows.Media.Brush)FindResource(
            _manager.DriverInstalled ? "SuccessBrush" : "DangerBrush");
        LbPairs.Items.Clear();
        if (!_manager.DriverInstalled) return;
        try
        {
            foreach (var pair in _manager.ListPairs(allowElevate: true))
                LbPairs.Items.Add($"{pair.PortA}  ↔  {pair.PortB}    (配对 #{pair.IdA.Replace("CNCA", "")})");
        }
        catch (ElevationCancelledException)
        {
            // 用户在 UAC 弹窗点了"否"：不是错误，给出可重试提示
            TbDriver.Text = "已取消管理员授权：端口对列表未刷新（点“刷新”重试）";
            DotDriver.Fill = (System.Windows.Media.Brush)FindResource("WarnBrush");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取端口对失败：{ex.Message}", "FreeCom",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_manager.DriverInstalled)
        {
            MessageBox.Show("请先安装 com0com 驱动。", "FreeCom");
            return;
        }
        try
        {
            // 虚拟对的端口也会出现在 GetPortNames 中，据此挑选空闲号
            var (a, b) = VirtualComManager.SuggestFreePorts(
                System.IO.Ports.SerialPort.GetPortNames());
            _manager.CreatePair(a, b);
            MessageBox.Show($"已创建端口对：{a} ↔ {b}\n\n在主界面选择 {a} 打开即可；对端 {b} 可接设备模拟器或第三方串口工具。",
                "FreeCom", MessageBoxButton.OK, MessageBoxImage.Information);
            Reload();
            _onPortsChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is ElevationCancelledException ? "已取消管理员授权，未创建端口对。" : $"创建失败：{ex.Message}",
                "FreeCom", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        if (LbPairs.SelectedItem is not string item) return;
        // 条目格式 "COM20  ↔  COM21    (配对 #3)"：取括号内数字作为配对编号
        var start = item.LastIndexOf('(');
        if (start < 0) return;
        var inner = item[(start + 1)..].TrimEnd(')');
        var number = new string(inner.Where(char.IsDigit).ToArray());
        if (number.Length == 0) return;
        try
        {
            _manager.RemovePair(number);
            Reload();
            _onPortsChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is ElevationCancelledException ? "已取消管理员授权，未执行删除。" : $"删除失败：{ex.Message}",
                "FreeCom", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Help_OnClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "虚拟串口基于开源驱动 com0com（免费，不随 FreeCom 分发）：\n\n" +
            "1. 下载签名安装包 com0com-3.0.0.0-i386-and-x64-signed.zip\n" +
            $"   下载页：{VirtualComManager.DownloadUrl}\n" +
            $"2. 校验 SHA256：\n   {VirtualComManager.InstallerSha256}\n" +
            "3. 运行安装程序（需要管理员/UAC）\n" +
            "4. 回到本窗口即可创建端口对\n\n" +
            "提示：创建/删除端口对同样需要管理员权限（UAC）。",
            "驱动下载与安装说明", MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = VirtualComManager.DownloadUrl,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
