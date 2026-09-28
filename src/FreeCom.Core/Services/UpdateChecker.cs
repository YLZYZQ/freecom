using System.Net.Http;
using System.Text.Json;

namespace FreeCom.Core.Services;

/// <summary>GitHub Release 最新版本信息。</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string HtmlUrl, string Name, DateTime PublishedAtUtc);

/// <summary>
/// GitHub Releases 更新检查：GET /repos/{owner}/{repo}/releases/latest（公开仓库免鉴权，
/// 未认证限流 60 次/小时/IP，对本场景足够）。tag 约定 vX.Y.Z（v 前缀可省略）。
/// </summary>
public static class UpdateChecker
{
    public const string RepoOwner = "YLZYZQ";
    public const string RepoName = "freecom";

    /// <summary>测试/代理可覆盖的 API 基址（默认官方；空串回落官方）。</summary>
    public static string ApiBase { get; set; } = "https://api.github.com";

    /// <summary>获取最新 Release；仓库尚无 Release（404）返回 null；网络/服务错误抛异常。</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(HttpClient? http = null, CancellationToken ct = default)
    {
        var uri = $"{ApiBase.TrimEnd('/')}/repos/{RepoOwner}/{RepoName}/releases/latest";
        var client = http ?? new HttpClient();
        try
        {
            using var resp = await client.GetAsync(uri, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
            var published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                && DateTime.TryParse(p.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt : DateTime.MinValue;
            var ver = ParseTag(tag);
            if (ver is null || string.IsNullOrEmpty(url)) return null;
            return new ReleaseInfo(ver, tag!, url, name ?? "", published);
        }
        finally
        {
            if (http is null) client.Dispose();
        }
    }

    /// <summary>tag 形如 v0.2.0 / 0.2.0 / v1.2.3-beta；无法解析返回 null。</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static bool IsNewer(Version current, Version? remote) => remote is not null && remote > current;
}
