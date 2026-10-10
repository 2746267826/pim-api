using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Api.Endpoints;
using Pim.Api.Services;
using Xunit;

namespace Pim.UnitTests.Api;

public sealed class VersionEndpointTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_fn(request));
    }

    [Fact]
    public void PhaseOneCapabilitiesAdvertiseItemResultsAndAndroidEmbed()
    {
        Assert.Contains(VersionEndpoints.MobileItemResultsV1, VersionEndpoints.Capabilities);
        Assert.Contains("androidEmbedV1", VersionEndpoints.Capabilities);
    }

    [Fact]
    public async Task MapVersionEndpoints_ReturnsTypedJsonContract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new GitHubReleaseService(new HttpClient(new FakeHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") })), Options.Create(new GitHubReleaseOptions()), NullLogger<GitHubReleaseService>.Instance));
        await using var app = builder.Build();
        app.MapVersionEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var response = await client.GetFromJsonAsync<ApiVersionResponse>("/api/version");

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
        Assert.Contains(VersionEndpoints.MobileItemResultsV1, response.Capabilities);
        Assert.Contains("androidEmbedV1", response.Capabilities);
    }

    [Fact]
    public async Task MapVersionEndpoints_ExposesLatestAndCheckedAt()
    {
        // latestVersion 语义为「本服务自身的最新版本」，取程序集版本，不再依赖网络查 release
        var handler = new FakeHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var gh = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), NullLogger<GitHubReleaseService>.Instance);
        await gh.RefreshAsync(CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(gh);
        await using var app = builder.Build();
        app.MapVersionEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var resp = await client.GetFromJsonAsync<ApiVersionResponse>("/api/version");
        Assert.NotNull(resp!.LatestVersion);
        Assert.Equal(resp.Version, resp.LatestVersion);   // 与自身版本一致
        Assert.NotNull(resp.CheckedAt);
    }

    [Fact]
    public async Task MapVersionEndpoints_ExposesClientComponentVersionsFromVersionFiles()
    {
        var handler = new FakeHandler(req =>
        {
            var isWin = req.RequestUri!.ToString().Contains("pim-windows");
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(isWin
                    ? "{\"version\":\"2026.10.810\",\"assets\":{\"windows\":{\"version\":\"2026.10.810\",\"url\":\"https://github.com/2746267826/pim-windows/releases/download/v2026.10.810/pim-windows-v2026.10.810.zip\"}}}"
                    : "{\"version\":\"2026.10.811\",\"assets\":{\"android\":{\"version\":\"2026.10.811\",\"url\":\"https://github.com/2746267826/pim-android/releases/download/v2026.10.811/pim-android-v2026.10.811.apk\"}}}")
            };
        });
        var gh = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), NullLogger<GitHubReleaseService>.Instance);
        await gh.RefreshAsync(CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(gh);
        await using var app = builder.Build();
        app.MapVersionEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var resp = await client.GetFromJsonAsync<ApiVersionResponse>("/api/version");
        Assert.Equal("2026.10.810", resp!.WindowsVersion);
        Assert.Equal("2026.10.811", resp.AndroidVersion);
        // /api/version 只暴露各组件版本号；下载地址由 /api/client/shell/latest 提供
        Assert.Null(resp.ShellWindowsVersion);   // 该仓的 version.json 里没有 shellWindows
        Assert.Null(resp.ShellAndroidVersion);
    }

    [Fact]
    public async Task MapVersionEndpoints_ExposesErrorWhenFetchFailed()
    {
        var handler = new FakeHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        var gh = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), NullLogger<GitHubReleaseService>.Instance);
        await gh.RefreshAsync(CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(gh);
        await using var app = builder.Build();
        app.MapVersionEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var resp = await client.GetFromJsonAsync<ApiVersionResponse>("/api/version");
        Assert.NotNull(resp!.Error);
        Assert.NotNull(resp.CheckedAt);
    }
}
