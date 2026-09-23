using System.Windows;
using System.Windows.Input;

namespace FreeCom.App;

/// <summary>关于对话框（设计稿 v2）：Logo / 版本 / 开源徽章 / 仓库链接。</summary>
public partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/YLZYZQ/freecom";

    public AboutWindow()
    {
        InitializeComponent();
        TbVersion.Text = $"免费串口调试助手 · v{typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1"}";
        TbRepo.ToolTip = RepoUrl;
    }

    private void Repo_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
        catch { /* 无默认浏览器时忽略 */ }
    }
}
