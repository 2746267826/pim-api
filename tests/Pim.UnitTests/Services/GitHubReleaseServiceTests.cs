using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Api.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public sealed class GitHubReleaseServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_fn(request));
    }

    [Fact]
    public async Task FetchAsync_ParsesTagAndAssetUrls()
    {
        var handler = new FakeHandler(req =>
        {
            Assert.Contains("api.github.com", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v2026.08.212\",\"assets\":[{\"name\":\"pim-windows-v2026.08.212.zip\",\"browser_download_url\":\"https://github.com/2746267826/pim-platform/releases/download/v2026.08.212/pim-windows-v2026.08.212.zip\"},{\"name\":\"pim-android-v2026.08.212.apk\",\"browser_download_url\":\"https://github.com/2746267826/pim-platform/releases/download/v2026.08.212/pim-android-v2026.08.212.apk\"}]}"),
                Headers = { ETag = new EntityTagHeaderValue("\"abc\"") }
            };
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions { Repo = "2746267826/pim-platform" }), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
        var result = await svc.RefreshAsync(CancellationToken.None);
        Assert.Equal("2026.08.212", result.LatestVersion);
        Assert.Contains("pim-windows", result.WindowsUrl);
        Assert.Contains("pim-android", result.AndroidUrl);
        Assert.NotNull(result.CheckedAt);
        Assert.Null(result.Error);
        Assert.Equal("\"abc\"", result.ETag);
    }

    [Fact]
    public async Task FetchAsync_SetsErrorOnFailure_AndEndpointExposesError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);
        Assert.NotNull(r.Error);
        Assert.NotNull(r.CheckedAt);
    }

    [Fact]
    public async Task FetchAsync_FiltersNonWhitelistedUrls()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"v2026.08.213\",\"assets\":[{\"name\":\"pim-windows-v2026.08.213.zip\",\"browser_download_url\":\"https://evil.com/pim-windows-v2026.08.213.zip\"},{\"name\":\"pim-android-v2026.08.213.apk\",\"browser_download_url\":\"https://github.com/2746267826/pim-platform/releases/download/v2026.08.213/pim-android-v2026.08.213.apk\"}]}")
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);
        Assert.Null(r.WindowsUrl);
        Assert.Contains("pim-android", r.AndroidUrl);
    }

    [Fact]
    public async Task FetchAsync_HandlesNotModified()
    {
        var call = 0;
        var handler = new FakeHandler(req =>
        {
            call++;
            if (call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tag_name\":\"v2026.08.212\",\"assets\":[]}"),
                    Headers = { ETag = new EntityTagHeaderValue("\"etag1\"") }
                };
            }
            Assert.True(req.Headers.IfNoneMatch.Count > 0);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
        var first = await svc.RefreshAsync(CancellationToken.None);
        var firstChecked = first.CheckedAt;
        await Task.Delay(10);
        var second = await svc.RefreshAsync(CancellationToken.None);
        Assert.Equal("2026.08.212", second.LatestVersion);
        Assert.NotEqual(firstChecked, second.CheckedAt);
        Assert.Null(second.Error);
    }

    [Fact]
    public async Task FetchAsync_TrimsVPrefix()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"2026.08.214\",\"assets\":[]}")
        });
        var svc = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
        var r = await svc.RefreshAsync(CancellationToken.None);
        Assert.Equal("2026.08.214", r.LatestVersion);
    }
}
