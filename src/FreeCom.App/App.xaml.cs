using System.Windows;
using System.IO;

namespace FreeCom.App;

public partial class App : Application
{
    public App()
    {
        // Velopack 安装器/更新器钩子：普通直接运行时为无操作，仅在 vpk 安装包首次安装或更新时生效
        Velopack.VelopackApp.Build().Run();
    }

    public static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeCom", "freecom.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n");
            }
            catch { /* 日志失败不影响弹窗 */ }
            MessageBox.Show(args.Exception.Message, "FreeCom 异常",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }
}
