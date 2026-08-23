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
        builder.Services.AddSingleton(new GitHubReleaseService(new HttpClient(new FakeHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") })), Options.Create(new GitHubReleaseOptions()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance));
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
        var handler = new FakeHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"v2026.08.212\",\"assets\":[{\"name\":\"pim-windows-v2026.08.212.zip\",\"browser_download_url\":\"https://github.com/2746267826/pim-platform/releases/download/v2026.08.212/pim-windows-v2026.08.212.zip\"}]}"),
            Headers = { ETag = new EntityTagHeaderValue("\"abc\"") }
        });
        var gh = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions { Repo = "2746267826/pim-platform" }), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
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
        Assert.Equal("2026.08.212", resp.LatestVersion);
        Assert.NotNull(resp.CheckedAt);
    }

    [Fact]
    public async Task MapVersionEndpoints_ExposesErrorWhenFetchFailed()
    {
        var handler = new FakeHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        var gh = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubReleaseService>.Instance);
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
