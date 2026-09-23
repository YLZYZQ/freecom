using System.Diagnostics;
using System.Security.Principal;

namespace FreeCom.Core.Transports;

/// <summary>
/// 提权执行辅助：以 UAC（runas）运行 PowerShell 脚本，stdout 经临时文件回传。
/// 用于 com0com 端口对管理（setupc 需要管理员且必须在其安装目录下运行）。
/// </summary>
public static class ElevatedRunner
{
    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>提权运行脚本；返回 (退出码, 输出)。脚本需自行把内容写到 $env:FREECOM_ELEV_OUT。</summary>
    public static (int ExitCode, string Output) RunScript(string scriptBody, int timeoutMs = 60_000)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FreeCom-elev-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "run.ps1");
        var outputPath = Path.Combine(dir, "out.txt");
        File.WriteAllText(scriptPath, scriptBody, System.Text.Encoding.UTF8);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process is null) return (-1, "无法启动提权进程");
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                return (-1, "提权进程超时");
            }
            var output = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (ex.NativeErrorCode, $"提权被拒绝或失败: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>一对虚拟串口。</summary>
public sealed record VirtualComPair(string IdA, string IdB, string PortA, string PortB);

/// <summary>用户在 UAC 弹窗取消了提权（Win32 1223）：调用方应以友好提示替代错误框。</summary>
public sealed class ElevationCancelledException : InvalidOperationException
{
    public ElevationCancelledException() : base("已取消管理员授权（UAC）") { }
}

/// <summary>
/// com0com 虚拟串口管理（PRD v0.1.1）：
/// 检测驱动、列出/创建/删除端口对。全部操作经 setupc.exe（需管理员）。
/// 注意：setupc 必须在 com0com 安装目录下运行（其 INF 按工作目录解析）。
/// </summary>
public sealed class VirtualComManager
{
    private static readonly string[] s_installDirs =
    [
        @"C:\Program Files (x86)\com0com",
        @"C:\Program Files\com0com",
    ];

    public string SetupcPath { get; }
    public string InstallDir => Path.GetDirectoryName(SetupcPath)!;
    public bool DriverInstalled => SetupcPath is not null && File.Exists(SetupcPath);

    /// <summary>默认驱动包下载页（按需下载策略，不随包分发）。</summary>
    public const string DownloadUrl = "https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/";
    public const string InstallerSha256 = "26486B28604B49A9008C54FEB11B9ECE0008A8287EE5CAF0BCF2A62F4317128F";

    public VirtualComManager()
    {
        SetupcPath = s_installDirs
            .Select(d => Path.Combine(d, "setupc.exe"))
            .FirstOrDefault(File.Exists) ?? "";
    }

    // ---------------- 纯逻辑：输出解析（可单测） ----------------

    /// <summary>解析 `setupc list` 输出为端口对列表。</summary>
    public static List<VirtualComPair> ParseListOutput(string listOutput)
    {
        var pairs = new Dictionary<string, (string? A, string? B)>();
        foreach (var rawLine in listOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            // 形如 "CNCA0 PortName=COM20" 或 "CNCB1 PortName=COM23"（无 PortName 的行跳过）
            var parts = line.Split(' ', 2);
            if (parts.Length != 2) continue;
            var id = parts[0].Trim();
            var rest = parts[1].Trim();
            const string prefix = "PortName=";
            if (!rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var port = rest[prefix.Length..].Trim();
            if (port.Length == 0 || port == "-") continue; // 未命名的对不产出
            if (id.Length < 5 || !id.StartsWith("CNC", StringComparison.OrdinalIgnoreCase)) continue;

            var bus = id[..4];      // CNCA / CNCB
            var num = id[4..];      // 0,1,2...（配对编号：CNCA0 与 CNCB0 同属一对）
            var (a, b) = pairs.TryGetValue(num, out var v) ? v : (null, null);
            if (bus.Equals("CNCA", StringComparison.OrdinalIgnoreCase)) a = port; else b = port;
            pairs[num] = (a, b);
        }

        return pairs
            .Where(kv => kv.Value.A is not null && kv.Value.B is not null)
            .Select(kv => new VirtualComPair(
                "CNCA" + kv.Key, "CNCB" + kv.Key, kv.Value.A!, kv.Value.B!))
            .OrderBy(p => p.PortA, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- 提权操作 ----------------

    private string RunSetupc(params string[] args)
    {
        if (!DriverInstalled)
            throw new InvalidOperationException("com0com 驱动未安装（工具 → 虚拟串口管理器 → 安装引导）");
        var argLine = string.Join(" ", args.Select(a => $"'{a.Replace("'", "''")}'"));
        var script = $"""
            $ErrorActionPreference = 'Continue'
            Set-Location '{InstallDir.Replace("'", "''")}'
            $out = & .\setupc.exe {argLine} 2>&1 | Out-String
            $out += ('EXITCODE=' + $LASTEXITCODE)
            $out | Out-File -FilePath '{Path.Combine(Path.GetTempPath(), "FreeCom-setupc-out.txt").Replace("'", "''")}' -Encoding UTF8
            """;
        var outPath = Path.Combine(Path.GetTempPath(), "FreeCom-setupc-out.txt");
        try { File.Delete(outPath); } catch { }
        var (code, output) = ElevatedRunner.RunScript(script);
        if (code == 1223) throw new ElevationCancelledException(); // 用户在 UAC 弹窗点了"否"
        if (code != 0) throw new InvalidOperationException($"setupc 提权执行失败（code={code}）: {output}");
        var fileOutput = File.Exists(outPath) ? File.ReadAllText(outPath) : output;

        // setupc 自身退出码（脚本尾附 EXITCODE=n）：非 0 时把真实输出抛给调用方
        var marker = fileOutput.LastIndexOf("EXITCODE=", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var tail = fileOutput[(marker + "EXITCODE=".Length)..].Trim();
            var body = fileOutput[..marker].TrimEnd();
            if (int.TryParse(tail, out var setupcExit) && setupcExit != 0)
                throw new InvalidOperationException(
                    $"setupc 操作失败（exit={setupcExit}）: {body}");
            return body;
        }
        return fileOutput;
    }

    /// <summary>列出当前端口对：只读查询优先免提权直接执行（不弹 UAC），失败再走提权。</summary>
    public List<VirtualComPair> ListPairs()
    {
        var direct = TryRunSetupcDirect("list");
        if (direct is not null) return ParseListOutput(direct);
        return ParseListOutput(RunSetupc("list"));
    }

    /// <summary>免提权直接运行 setupc（只用于查询类命令）；启动失败/超时/非零退出返回 null。</summary>
    private string? TryRunSetupcDirect(params string[] args)
    {
        try
        {
            if (!DriverInstalled) return null;
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(InstallDir, "setupc.exe"),
                Arguments = string.Join(" ", args),
                WorkingDirectory = InstallDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000) || p.ExitCode != 0) return null;
            return output;
        }
        catch { return null; }
    }

    /// <summary>创建端口对（提权）。返回 setupc 原始输出。</summary>
    public string CreatePair(string portA, string portB)
        => RunSetupc("install", $"PortName={portA}", $"PortName={portB}");

    /// <summary>删除端口对（提权）。id 接受 CNCA3/CNCB3 或编号 3——setupc 实际按配对编号删除。</summary>
    public string RemovePair(string id)
    {
        var trimmed = (id ?? "").Trim();
        if (trimmed.StartsWith("CNC", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
            trimmed = trimmed[4..];              // CNCA3 / CNCB3 → 3
        if (!int.TryParse(trimmed, out _))
            throw new ArgumentException($"无效的端口对标识: {id}（应为 CNCA 编号，如 CNCA3）");
        return RunSetupc("remove", trimmed);
    }

    /// <summary>建议下一个空闲端口号（从 20 起避开常见物理口，向上探测）。</summary>
    public static (string A, string B) SuggestFreePorts(IReadOnlyCollection<string> existingPorts)
    {
        var used = existingPorts.Select(p => p.TrimStart('C', 'O', 'M'))
            .Where(n => int.TryParse(n, out _))
            .Select(int.Parse)
            .ToHashSet();
        int next = 20;
        string Take()
        {
            while (used.Contains(next)) next++;
            used.Add(next);
            return $"COM{next}";
        }
        var a = Take();
        var b = Take();
        return (a, b);
    }
}
