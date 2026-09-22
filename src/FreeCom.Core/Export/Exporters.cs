using System.Globalization;
using System.Text;
using FreeCom.Core.Pipeline;
using FreeCom.Core.Plots;

namespace FreeCom.Core.Export;

/// <summary>数据导出（PRD F8.2 MVP 子集）：原始 DAT / 显示 TXT / 曲线 CSV。</summary>
public static class Exporters
{
    /// <summary>原始数据（仅 RX 方向，未处理字节）→ DAT。</summary>
    public static void ExportRaw(string path, RawLog raw)
    {
        var entries = raw.Snapshot(dir: DataDirection.Rx);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024);
        foreach (var entry in entries) file.Write(entry.Data.Span);
    }

    /// <summary>显示数据（含时间戳/方向）→ TXT。</summary>
    public static void ExportDisplay(string path, DisplaySink display, bool includeTimestamp = true, bool hex = false)
    {
        var entries = display.Snapshot();
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false), bufferSize: 16 * 1024);
        Span<char> chars = stackalloc char[3072];
        var decoder = hex ? null : Encoding.UTF8.GetDecoder();
        foreach (var entry in entries)
        {
            if (includeTimestamp)
            {
                writer.Write('[');
                writer.Write(entry.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff"));
                writer.Write("] ");
            }
            writer.Write(entry.Dir == DataDirection.Tx ? ">> " : "<< ");
            var bytes = entry.Data.Span;
            if (hex)
            {
                const string digits = "0123456789ABCDEF";
                bool first = true;
                while (!bytes.IsEmpty)
                {
                    int take = Math.Min(bytes.Length, chars.Length / 3);
                    int written = 0;
                    foreach (byte value in bytes[..take])
                    {
                        if (!first) chars[written++] = ' ';
                        chars[written++] = digits[value >> 4];
                        chars[written++] = digits[value & 15];
                        first = false;
                    }
                    writer.Write(chars[..written]);
                    bytes = bytes[take..];
                }
            }
            else
            {
                // Match GetString per entry: preserve UTF-8 sequences inside a large
                // entry, but flush incomplete sequences at the original entry boundary.
                decoder!.Reset();
                bool completed;
                do
                {
                    decoder.Convert(bytes, chars, flush: true, out int used, out int written, out completed);
                    writer.Write(chars[..written]);
                    bytes = bytes[used..];
                } while (!completed);
            }
            writer.WriteLine();
        }
    }

    /// <summary>曲线数据 → CSV（长表：window,curve,x,y；windowId 为空导出全部窗口）。</summary>
    public static string BuildCurvesCsv(PlotService plots, string? windowId, int maxPoints = 200_000)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCurvesCsv(writer, plots, windowId, maxPoints);
        return writer.ToString();
    }

    public static string ExportCurvesCsv(string path, PlotService plots, string? windowId, int maxPoints = 200_000)
    {
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(true), bufferSize: 16 * 1024))
            WriteCurvesCsv(writer, plots, windowId, maxPoints);
        return System.IO.Path.GetFullPath(path);
    }

    private static void WriteCurvesCsv(TextWriter writer, PlotService plots, string? windowId, int maxPoints)
    {
        writer.WriteLine("window,curve,x,y");
        IReadOnlyList<PlotWindow> windows;
        if (windowId is null)
        {
            windows = plots.SnapshotWindows();
        }
        else
        {
            var w = plots.Find(windowId);
            windows = w is null ? [] : [w];
        }
        Span<char> number = stackalloc char[32];
        foreach (var w in windows)
        {
            foreach (var c in w.Curves)
            {
                var snap = c.Snapshot(maxPoints);
                var winName = Escape(w.Title);
                var curveName = Escape(c.Name);
                for (int i = 0; i < snap.Xs.Length; i++)
                {
                    writer.Write(winName);
                    writer.Write(',');
                    writer.Write(curveName);
                    writer.Write(',');
                    snap.Xs[i].TryFormat(number, out int xLength, "R", CultureInfo.InvariantCulture);
                    writer.Write(number[..xLength]);
                    writer.Write(',');
                    snap.Ys[i].TryFormat(number, out int yLength, "R", CultureInfo.InvariantCulture);
                    writer.Write(number[..yLength]);
                    writer.Write('\n');
                }
            }
        }
    }

    private static string Escape(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
