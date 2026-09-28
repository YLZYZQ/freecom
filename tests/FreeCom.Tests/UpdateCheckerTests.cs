using System.Net;
using System.Net.Http;
using FreeCom.Core.Services;
using Xunit;

namespace FreeCom.Tests;

/// <summary>更新提醒：tag 解析/版本比较/GitHub API 响应解析/404 语义（全部离线，mock HttpMessageHandler）。</summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData(" v0.10.0 ", "0.10.0")]
    public void ParseTag_Valid(string tag, string expected)
        => Assert.Equal(new Version(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("vX.Y.Z")]
    [InlineData("release-1")]
    [InlineData("v1.2.3-beta")] // 预发布后缀：.NET Version 不支持，按"忽略提醒"处理（本项目只发纯数字正式 tag）
    public void ParseTag_Invalid(string? tag)
        => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void IsNewer_Comparison()
    {
        var cur = new Version("0.2.0");
        Assert.True(UpdateChecker.IsNewer(cur, new Version("0.2.1")));
        Assert.True(UpdateChecker.IsNewer(cur, new Version("1.0.0")));
        Assert.True(UpdateChecker.IsNewer(cur, new Version("0.10.0")));  // 0.10 > 0.2 语义正确
        Assert.False(UpdateChecker.IsNewer(cur, new Version("0.2.0")));
        Assert.False(UpdateChecker.IsNewer(cur, new Version("0.1.9")));
        Assert.False(UpdateChecker.IsNewer(cur, null));
    }

    private sealed class MockHttp : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public MockHttp(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    [Fact]
    public async Task FetchLatest_ParsesReleaseJson()
    {
        var oldBase = UpdateChecker.ApiBase;
        UpdateChecker.ApiBase = "http://localhost:1"; // 不实际访问：handler mock 拦截
        try
        {
            var json = """
                {"tag_name":"v0.3.0","name":"v0.3.0 发布","html_url":"https://github.com/YLZYZQ/freecom/releases/tag/v0.3.0",
                 "published_at":"2026-10-01T08:00:00Z","assets":[]}
                """;
            using var http = new HttpClient(new MockHttp(HttpStatusCode.OK, json));
            var r = await UpdateChecker.FetchLatestAsync(http);
            Assert.NotNull(r);
            Assert.Equal(new Version("0.3.0"), r!.Version);
            Assert.Equal("v0.3.0", r.Tag);
            Assert.Equal("https://github.com/YLZYZQ/freecom/releases/tag/v0.3.0", r.HtmlUrl);
            Assert.Equal("v0.3.0 发布", r.Name);
            Assert.True(UpdateChecker.IsNewer(new Version("0.2.0"), r.Version));
        }
        finally { UpdateChecker.ApiBase = oldBase; }
    }

    [Fact]
    public async Task FetchLatest_NoRelease_ReturnsNull()
    {
        var oldBase = UpdateChecker.ApiBase;
        UpdateChecker.ApiBase = "http://localhost:1";
        try
        {
            using var http = new HttpClient(new MockHttp(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));
            var r = await UpdateChecker.FetchLatestAsync(http);
            Assert.Null(r); // 仓库尚无 Release：不是错误，是"无版本"
        }
        finally { UpdateChecker.ApiBase = oldBase; }
    }

    [Fact]
    public async Task FetchLatest_BadTag_ReturnsNull()
    {
        var oldBase = UpdateChecker.ApiBase;
        UpdateChecker.ApiBase = "http://localhost:1";
        try
        {
            var json = """{"tag_name":"latest","html_url":"https://github.com/x/y"}""";
            using var http = new HttpClient(new MockHttp(HttpStatusCode.OK, json));
            Assert.Null(await UpdateChecker.FetchLatestAsync(http));
        }
        finally { UpdateChecker.ApiBase = oldBase; }
    }
}
