using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Api.Services;
using Xunit;

namespace Pim.UnitTests.Services;

/// <summary>
/// 客户端版本检查：改造后数据源为各客户端仓 release 里的 version.json
/// （releases/latest/download/version.json 直链），不再解析 releases API 的资产名。
/// </summary>
public sealed class GitHubReleaseServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public int Calls { get; private set; }
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_fn(request));
        }
    }

    private const string WindowsRepo = "2746267826/pim-windows";
    private const string AndroidRepo = "2746267826/pim-android";

    private static GitHubReleaseOptions Opts(string? token = null) => new()
    {
        WindowsRepo = WindowsRepo,
        AndroidRepo = AndroidRepo,
        Token = token
    };

    private static string VersionJson(string version, string repo, string asset, string ext) =>
        $$"""
        {
          "version": "{{version}}",
          "releasedAt": "2026-10-10T00:00:00Z",
          "assets": {
            "{{asset}}": {
              "version": "{{version}}",
              "url": "https://github.com/{{repo}}/releases/download/v{{version}}/pim-{{asset}}-v{{version}}.{{ext}}"
            }
          }
        }
        """;

    private static HttpResponseMessage Ok(string body, string? etag = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (etag != null) resp.Headers.ETag = new EntityTagHeaderValue(etag);
        return resp;
    }

    [Fact]
    public async Task Refresh_ParsesBothRepositoriesVersionFiles()
    {
        var handler = new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains(WindowsRepo))
            {
                return Ok("""
                {
                  "version": "2026.10.810",
                  "assets": {
                    "windows":      { "version": "2026.10.810", "url": "https://github.com/2746267826/pim-windows/releases/download/windows-v26.10.810/pim-windows-v2026.10.810.zip" },
                    "shellWindows": { "version": "2026.10.810", "url": "https://github.com/2746267826/pim-windows/releases/download/windows-v26.10.810/pim-shell-windows-v2026.10.810.zip" }
                  }
                }
                """);
            }
            Assert.Contains(AndroidRepo, url);
            return Ok("""
            {
              "version": "2026.10.810",
              "assets": {
                "android":      { "version": "2026.10.810", "url": "https://github.com/2746267826/pim-android/releases/download/android-v26.10.810/pim-android-v2026.10.810.apk" },
                "shellAndroid": { "version": "2026.10.810", "url": "https://github.com/2746267826/pim-android/releases/download/android-v26.10.810/pim-shell-android-v2026.10.810.apk" }
              }
            }
            """);
        });

        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);

        Assert.Equal("2026.10.810", r.WindowsVersion);
        Assert.Contains("pim-windows-v2026.10.810.zip", r.WindowsUrl);
        Assert.Equal("2026.10.810", r.ShellWindowsVersion);
        Assert.Contains("pim-shell-windows", r.ShellWindowsUrl);
        Assert.Equal("2026.10.810", r.AndroidVersion);
        Assert.Contains("pim-android-v2026.10.810.apk", r.AndroidUrl);
        Assert.Equal("2026.10.810", r.ShellAndroidVersion);
        Assert.Contains("pim-shell-android", r.ShellAndroidUrl);
        Assert.Null(r.Error);
        Assert.NotNull(r.CheckedAt);
    }

    [Fact]
    public async Task Refresh_UsesStableReleaseDownloadUrl()
    {
        var handler = new FakeHandler(req =>
        {
            // 必须是 releases/latest/download 直链；不查 releases API
            Assert.DoesNotContain("api.github.com", req.RequestUri!.ToString());
            Assert.Contains("/releases/latest/download/version.json", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);
        await svc.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, handler.Calls); // 两个客户端仓各一次
    }

    [Fact]
    public async Task Refresh_TreatsMissingReleaseAsNotYetPublished_NotAnError()
    {
        // 仓库还没有 release → 404 → 视为「未发版」，不产生 error
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);

        Assert.Null(r.WindowsVersion);
        Assert.Null(r.AndroidVersion);
        Assert.Null(r.WindowsUrl);
        Assert.Null(r.AndroidUrl);
        // 404 属于「尚未发版」，不是故障：首次装机时仓库本来就还没 release
        Assert.Null(r.Error);
        Assert.NotNull(r.CheckedAt);
    }

    [Fact]
    public async Task Refresh_OneRepoFailing_DoesNotBlankTheOther()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains(WindowsRepo))
            {
                return Ok("""
                { "version": "2026.10.810", "assets": {
                    "windows": { "version": "2026.10.810", "url": "https://github.com/2746267826/pim-windows/releases/download/windows-v26.10.810/pim-windows-v2026.10.810.zip" } } }
                """);
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);

        Assert.Equal("2026.10.810", r.WindowsVersion);   // 成功的一侧保留
        Assert.Null(r.AndroidVersion);                    // 失败的一侧为空
        Assert.NotNull(r.Error);                          // 并如实报错
    }

    [Fact]
    public async Task Refresh_IgnoresUrlsPointingOutsideTheRepository()
    {
        // 版本文件里塞外站链接 → 必须丢弃，避免更新检查被带偏
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains(WindowsRepo))
            {
                return Ok("""
                {
                  "version": "2026.10.810",
                  "assets": {
                    "windows": { "version": "2026.10.810", "url": "https://evil.com/pim-windows-v2026.10.810.zip" }
                  }
                }
                """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);

        Assert.Null(r.WindowsUrl);                       // 外站链接被拒
        Assert.Null(r.WindowsVersion);                   // 组件被整条丢弃后才不会出现「有版本无地址」的假更新提示
    }

    [Fact]
    public async Task Refresh_SendsConditionalRequestAfterEtag()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            return Ok(VersionJson("2026.10.810", WindowsRepo, "windows", "zip"), "\"etag1\"");
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);

        var first = await svc.RefreshAsync(CancellationToken.None);
        Assert.Equal("2026.10.810", first.WindowsVersion);
        Assert.NotNull(first.ETag);
        var firstChecked = first.CheckedAt;

        await Task.Delay(10);
        var second = await svc.RefreshAsync(CancellationToken.None);
        Assert.Equal("2026.10.810", second.WindowsVersion);   // 304 时沿用上次结果
        Assert.NotEqual(firstChecked, second.CheckedAt);       // 但仍然更新 checkedAt
    }

    [Fact]
    public async Task Refresh_SendsAuthHeaderOnlyWhenTokenConfigured()
    {
        var withToken = new FakeHandler(req =>
        {
            Assert.NotNull(req.Headers.Authorization);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc1 = new GitHubReleaseService(new HttpClient(withToken), Options.Create(Opts("tok")), NullLogger<GitHubReleaseService>.Instance);
        await svc1.RefreshAsync(CancellationToken.None);

        var noToken = new FakeHandler(req =>
        {
            Assert.Null(req.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc2 = new GitHubReleaseService(new HttpClient(noToken), Options.Create(Opts()), NullLogger<GitHubReleaseService>.Instance);
        await svc2.RefreshAsync(CancellationToken.None);
    }
}
