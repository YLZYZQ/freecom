using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeCom.Core.Config;

/// <summary>应用配置（PRD F10 子集）：schema 版本化，字段全部开放可编辑。</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;

    public string TransportKind { get; set; } = "serial";
    public Dictionary<string, string> TransportParams { get; set; } = new()
    {
        ["baud"] = "115200",
    };

    public string ProtocolName { get; set; } = "TEXT";
    public Dictionary<string, string> ProtocolOptions { get; set; } = [];

    public bool HexDisplay { get; set; }
    public bool HexSend { get; set; }
    public bool DisplayTimestamp { get; set; } = true;
    public string EncodingName { get; set; } = "utf-8";
    public string Newline { get; set; } = "none";
    public bool AutoScroll { get; set; } = true;
    public string DisplayFilterDir { get; set; } = "全部";
    public string DisplayFilterText { get; set; } = "";

    public bool AutoY { get; set; } = true;
    public int MaxPointsPerCurve { get; set; } = 500_000;

    public List<string> SendHistory { get; set; } = [];
    public bool CyclicSend { get; set; }
    public int CyclicIntervalMs { get; set; } = 1000;

    public string? McpToken { get; set; }

    /// <summary>界面主题：dark / light（v0.2 双主题）。缺省 dark，旧配置无此字段自动回落。</summary>
    public string Theme { get; set; } = "dark";
}

/// <summary>配置存取：JSON 文件、原子写入、坏文件降级为默认值并备份。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Path { get; }

    public SettingsStore(string path) => Path = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return Migrate(new AppSettings());
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), s_json) ?? new AppSettings();
            return Migrate(loaded);
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(Path)) File.Copy(Path, Path + ".corrupt", overwrite: true);
            }
            catch { /* 备份失败忽略 */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = 2;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, s_json));
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>schema 迁移：v1→v2 移除"虚拟回环"传输（v0.1.1），回落到串口。</summary>
    private static AppSettings Migrate(AppSettings s)
    {
        if (s.SchemaVersion < 2)
        {
            if (string.Equals(s.TransportKind, "virtual", StringComparison.OrdinalIgnoreCase))
            {
                s.TransportKind = "serial";
                s.TransportParams.Remove("port");
            }
            s.SchemaVersion = 2;
        }
        return s;
    }
}
