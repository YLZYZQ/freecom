using System.Windows;

namespace FreeCom.App;

/// <summary>坐标轴显示窗口设置（示波器式）：
/// X：自动 / 滚动窗口（最近 N 点，新点右入、最老点左出）/ 固定范围；
/// Y：自动 / 固定窗口（超出窗口的数据被裁剪出画面）。
/// 子输入区随所在单选项启停（未选中的模式输入置灰）。</summary>
public partial class SetAxisRangeWindow : Window
{
    /// <summary>滚动窗口点数上限：与渲染缓冲一致，超过会导致窗口左侧空白。</summary>
    public const int MaxWindowPoints = 4_000;

    public bool XAuto { get; private set; } = true;
    public int XWindowPoints { get; private set; } = 500;
    public double XMin { get; private set; }
    public double XMax { get; private set; }
    public bool YAuto { get; private set; } = true;
    public double YMin { get; private set; }
    public double YMax { get; private set; }

    public SetAxisRangeWindow(bool xAuto, int xWindowPoints, double xMin, double xMax,
                              bool yAuto, double yMin, double yMax)
    {
        InitializeComponent();
        XAuto = xAuto;
        XWindowPoints = xWindowPoints > 0 ? xWindowPoints : 500;
        XMin = xMin; XMax = xMax;
        YAuto = yAuto; YMin = yMin; YMax = yMax;

        // 子输入区跟随单选项启停：未选中的模式置灰，当前模式可直接输入
        WireMode(RbXWindow, PanelXWindow);
        WireMode(RbXFixed, PanelXFixed);
        WireMode(RbYFixed, PanelYFixed);

        RbXAuto.IsChecked = XAuto;
        RbXWindow.IsChecked = !XAuto && xWindowPoints > 0;
        RbXFixed.IsChecked = !XAuto && xWindowPoints == 0;
        TbXPoints.Text = XWindowPoints.ToString();
        TbXMin.Text = XMin.ToString("G6");
        TbXMax.Text = XMax.ToString("G6");
        RbYAuto.IsChecked = YAuto;
        RbYFixed.IsChecked = !YAuto;
        TbYMin.Text = YMin.ToString("G6");
        TbYMax.Text = YMax.ToString("G6");
    }

    private static void WireMode(System.Windows.Controls.Primitives.ToggleButton radio, FrameworkElement panel)
    {
        void Apply(bool on)
        {
            panel.IsEnabled = on;
            panel.Opacity = on ? 1.0 : 0.45;
        }
        radio.Checked += (_, _) => Apply(true);
        radio.Unchecked += (_, _) => Apply(false);
        Apply(radio.IsChecked == true);
    }

    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        XAuto = RbXAuto.IsChecked == true;
        YAuto = RbYAuto.IsChecked == true;

        if (RbXWindow.IsChecked == true)
        {
            if (!int.TryParse(TbXPoints.Text, out var n) || n < 2)
            {
                TbHint.Text = "滚动窗口点数必须是 ≥ 2 的整数";
                return;
            }
            if (n > MaxWindowPoints)
            {
                TbHint.Text = $"滚动窗口最多 {MaxWindowPoints} 个点（当前渲染上限）";
                return;
            }
            XWindowPoints = n;
        }
        else if (!XAuto)
        {
            if (!double.TryParse(TbXMin.Text, out var xMin) ||
                !double.TryParse(TbXMax.Text, out var xMax))
            {
                TbHint.Text = "请输入合法数值";
                return;
            }
            if (xMax <= xMin)
            {
                TbHint.Text = "X 最大值必须大于最小值";
                return;
            }
            XMin = xMin;
            XMax = xMax;
        }

        if (!YAuto)
        {
            if (!double.TryParse(TbYMin.Text, out var yMin) ||
                !double.TryParse(TbYMax.Text, out var yMax))
            {
                TbHint.Text = "请输入合法数值";
                return;
            }
            if (yMax <= yMin)
            {
                TbHint.Text = "Y 最大值必须大于最小值";
                return;
            }
            YMin = yMin;
            YMax = yMax;
        }

        DialogResult = true;
    }
}
