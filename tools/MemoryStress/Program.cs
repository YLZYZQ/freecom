using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using FreeCom.App;
using FreeCom.Core.Config;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using FreeCom.Core.Transports;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(MainWindow w, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(w, value);
    static void Call(MainWindow w, string name) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(w, null);
    [STAThread]
    static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "memory-run");
        var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "60");
        var mode = args.ElementAtOrDefault(2) ?? "receive";
        var appPort = args.ElementAtOrDefault(3) ?? "COM20";
        var devPort = args.ElementAtOrDefault(4) ?? "COM21";
        Directory.CreateDirectory(output);
        var app = new Application();
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas")));
        app.Resources["Mono"] = style;
        var window = new MainWindow();
        // Save shutdown settings to the run folder, without copying the user's token.
        Set(window, "_settingsStore", new SettingsStore(Path.Combine(output, "settings.json")));
        Set(window, "_settings", new AppSettings { McpToken = "memory-stress-disabled" });
        ((CheckBox)window.FindName("CkCyclic")).IsChecked = false;
        ((CheckBox)window.FindName("CkHexView")).IsChecked = mode == "hex";
        ((CheckBox)window.FindName("CkTimestamp")).IsChecked = true;
        ((CheckBox)window.FindName("CkAutoScroll")).IsChecked = true;
        ((ComboBox)window.FindName("CbFilterDir")).SelectedIndex = 0;
        ((TextBox)window.FindName("TbFilterText")).Text = "";
        var tabs = (TabControl)window.FindName("MainTabs");
        var receive = (RichTextBox)window.FindName("TbReceive");
        using var pipeline = new DataPipeline(ProtocolRegistry.Create("TEXT"));
        using var transport = new SerialTransport(new TransportOptions { Parameters = new() { ["port"] = appPort } });
        using var device = new SerialPort(devPort, 115200) { WriteTimeout = 5000 };
        Set(window, "_pipeline", pipeline);
        Set(window, "_transport", transport);
        pipeline.Plots.WindowsChanged += () => window.Dispatcher.BeginInvoke(() => Call(window, "RebuildPlotTabs"));
        var samples = new List<object>();
        var phase = "warmup";
        long sentBytes = 0, sentFrames = 0;
        var watch = Stopwatch.StartNew();
        var sampleTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(1) };
        using var stopMonitor = new CancellationTokenSource();
        var monitoring = Task.Run(async () =>
        {
            using var log = new StreamWriter(Path.Combine(output, "process.jsonl")) { AutoFlush = true };
            while (!stopMonitor.IsCancellationRequested)
            {
                using var p = Process.GetCurrentProcess();
                await log.WriteLineAsync(JsonSerializer.Serialize(new { seconds = watch.Elapsed.TotalSeconds, phase,
                    workingSetMiB = p.WorkingSet64 / 1048576.0, privateMiB = p.PrivateMemorySize64 / 1048576.0,
                    managedMiB = GC.GetTotalMemory(false) / 1048576.0, cpuSeconds = p.TotalProcessorTime.TotalSeconds,
                    sentBytes, sentFrames, rxBytes = pipeline.Counters.RxBytes, frames = pipeline.Counters.FramesParsed }));
                if (watch.Elapsed.TotalSeconds > seconds * 2 + 45)
                {
                    File.WriteAllText(Path.Combine(output, "timeout.txt"), "UI or serial run exceeded watchdog deadline; see process.jsonl");
                    Process.GetCurrentProcess().Kill();
                }
                await Task.Delay(1000);
            }
        });
        var lastTick = watch.Elapsed.TotalSeconds;
        sampleTimer.Tick += (_, _) =>
        {
            using var process = Process.GetCurrentProcess();
            var now = watch.Elapsed.TotalSeconds;
            var chars = receive.Document.Blocks.OfType<Paragraph>().Sum(p => p.Inlines.OfType<Run>().Sum(r => r.Text.Length));
            samples.Add(new { seconds = now, phase, workingSetMiB = process.WorkingSet64 / 1048576.0,
                privateMiB = process.PrivateMemorySize64 / 1048576.0, managedMiB = GC.GetTotalMemory(false) / 1048576.0,
                allocatedMiB = GC.GetTotalAllocatedBytes() / 1048576.0, cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                uiTickGapSeconds = now - lastTick, blocks = receive.Document.Blocks.Count, chars,
                rxBytes = pipeline.Counters.RxBytes, frames = pipeline.Counters.FramesParsed });
            lastTick = now;
            File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples));
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                transport.Open(); pipeline.AttachTransport(transport); device.Open();
                sampleTimer.Start();
                await Task.Delay(2000);
                phase = "flood";
                var batch = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 500).Select(i => $"{{stress}}{i % 97},{i % 89},{i % 73}\n")));
                // Fixed amount and minimum interval; software COM pairs do not emulate baud rate.
                var sender = Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < seconds * 50; i++)
                    {
                        device.Write(batch, 0, batch.Length);
                        sentBytes += batch.Length; sentFrames += 500;
                        Thread.Sleep(20);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await Task.Delay(500);
                if (mode != "plot") tabs.SelectedIndex = 0;
                await sender;
                phase = "drain";
                var drain = Stopwatch.StartNew();
                while (pipeline.Counters.FramesParsed < sentFrames && drain.Elapsed.TotalSeconds < 10) await Task.Delay(100);
                phase = "idle";
                await Task.Delay(10000);
                bool passed = pipeline.Counters.RxBytes == sentBytes && pipeline.Counters.FramesParsed == sentFrames && pipeline.Counters.ParseErrors == 0;
                var summary = new { mode, seconds, appPort, devPort, sentBytes, sentFrames,
                    rxBytes = pipeline.Counters.RxBytes, parsedFrames = pipeline.Counters.FramesParsed,
                    errors = pipeline.Counters.ParseErrors, passed, elapsedSeconds = watch.Elapsed.TotalSeconds };
                File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(summary));
                if (args.Contains("--verify"))
                {
                    phase = "verify";
                    VerifyUi(window, pipeline, output);
                }
                phase = "clear"; Call(window, "ClearDisplay"); pipeline.Plots.ClearData();
                await Task.Delay(3000);
                Environment.ExitCode = passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
                Console.Error.WriteLine(ex); Environment.ExitCode = 1;
            }
            finally
            {
                sampleTimer.Stop();
                stopMonitor.Cancel();
                File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }
        };
        app.Run(window);
        return Environment.ExitCode;
    }

    static void VerifyUi(MainWindow window, DataPipeline pipeline, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("UI regression: " + name);
            checks.Add(name);
        }
        var tabs = (TabControl)window.FindName("MainTabs");
        var receive = (RichTextBox)window.FindName("TbReceive");
        string Text() => new TextRange(receive.Document.ContentStart, receive.Document.ContentEnd).Text;
        tabs.SelectedIndex = 0;
        Call(window, "OnUiTick");
        Check(receive.Document.Blocks.OfType<Paragraph>().Sum(p => p.Inlines.OfType<Run>().Sum(r => r.Text.Length)) <= 128 * 1024,
            "receive character budget");
        ((CheckBox)window.FindName("CkHexView")).IsChecked = false;
        Call(window, "ClearDisplay");
        pipeline.Display.Append(DataDirection.Rx, Encoding.UTF8.GetBytes("RX_PROBE"));
        pipeline.Display.Append(DataDirection.Tx, Encoding.UTF8.GetBytes("TX_PROBE"));
        Call(window, "OnUiTick");
        Check(Text().Contains("RX_PROBE") && Text().Contains("TX_PROBE"), "both directions after clear");
        ((ComboBox)window.FindName("CbFilterDir")).SelectedIndex = 1;
        Call(window, "OnUiTick");
        Check(Text().Contains("RX_PROBE") && !Text().Contains("TX_PROBE"), "receive direction filter");
        ((ComboBox)window.FindName("CbFilterDir")).SelectedIndex = 0;
        ((TextBox)window.FindName("TbFilterText")).Text = "TX_PROBE";
        Call(window, "OnUiTick");
        Check(!Text().Contains("RX_PROBE") && Text().Contains("TX_PROBE"), "keyword filter");
        ((TextBox)window.FindName("TbFilterText")).Text = "";
        ((CheckBox)window.FindName("CkHexView")).IsChecked = true;
        Call(window, "OnUiTick");
        Check(Text().Contains("52 58 5F 50 52 4F 42 45"), "hex view");
        ((CheckBox)window.FindName("CkHexView")).IsChecked = false;
        Call(window, "OnUiTick");
        Check(Text().Contains("RX_PROBE"), "text view restored");
        tabs.SelectedIndex = 1;
        var before = Text();
        pipeline.Display.Append(DataDirection.Rx, Encoding.UTF8.GetBytes("HIDDEN_PROBE"));
        Call(window, "OnUiTick");
        Check(Text() == before, "hidden receive document stays unchanged");
        tabs.SelectedIndex = 0;
        Call(window, "OnUiTick");
        Check(Text().Contains("HIDDEN_PROBE"), "tab return catches up");
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "ui-check.png"))) png.Save(file);
        File.WriteAllText(Path.Combine(output, "ui-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
