using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Api.Modules.ClientShell;
using Pim.Api.Services;
using Xunit;

public class ClientShellLatestTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ClientShellLatestTests(WebApplicationFactory<Program> f) => _factory = f.WithWebHostBuilder(b => b.UseSetting("ShellClient:WindowsVersion", "1.2.3").UseSetting("ShellClient:WindowsUrl", "https://example.com/win.zip").UseSetting("ShellClient:AndroidVersion", "1.2.4").UseSetting("ShellClient:AndroidUrl", "https://example.com/app.apk").UseSetting("DisableHangfire", "true").UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz"));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_fn(request));
    }

    [Fact]
    public async Task Latest_ReturnsConfiguredVersions()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/client/shell/latest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LatestDto>();
        Assert.Equal("1.2.3", body!.WindowsVersion);
        Assert.Equal("https://example.com/win.zip", body.WindowsUrl);
        Assert.Equal("1.2.4", body.AndroidVersion);
        Assert.Equal("https://example.com/app.apk", body.AndroidUrl);
    }

    [Fact]
    public async Task Latest_WithoutConfig_ReturnsEmptyVersions()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz"));
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/client/shell/latest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LatestDto>();
        Assert.Null(body!.WindowsVersion);
        Assert.Null(body.WindowsUrl);
        Assert.Null(body.AndroidVersion);
        Assert.Null(body.AndroidUrl);
    }

    [Fact]
    public async Task Latest_PrefersGitHubSnapshotOverConfig()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"v2026.08.212\",\"assets\":[{\"name\":\"pim-windows-v2026.08.212.zip\",\"browser_download_url\":\"https://github.com/2746267826/pim-platform/releases/download/v2026.08.212/pim-windows-v2026.08.212.zip\"},{\"name\":\"pim-android-v2026.08.212.apk\",\"browser_download_url\":\"https://github.com/2746267826/pim-platform/releases/download/v2026.08.212/pim-android-v2026.08.212.apk\"}]}")
        });
        var gh = new GitHubReleaseService(new HttpClient(handler), Options.Create(new GitHubReleaseOptions { Repo = "2746267826/pim-platform" }), NullLogger<GitHubReleaseService>.Instance);
        await gh.RefreshAsync(CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<ClientShellOptions>(o => { o.WindowsVersion = "0.0.1"; o.WindowsUrl = "https://example.com/old.zip"; });
        builder.Services.AddSingleton(gh);
        await using var app = builder.Build();
        app.MapClientShell();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var resp = await http.GetFromJsonAsync<LatestDto>("/api/client/shell/latest");
        Assert.Equal("2026.08.212", resp!.WindowsVersion);
        Assert.Contains("pim-windows", resp.WindowsUrl);
        Assert.Equal("2026.08.212", resp.AndroidVersion);
        Assert.Contains("pim-android", resp.AndroidUrl);
        Assert.NotNull(resp.CheckedAt);
    }

    [Fact]
    public async Task Latest_FallsBackToConfigWhenSnapshotEmpty()
    {
        var gh = new GitHubReleaseService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })), Options.Create(new GitHubReleaseOptions()), NullLogger<GitHubReleaseService>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<ClientShellOptions>(o => { o.WindowsVersion = "1.9.9"; o.WindowsUrl = "https://example.com/fallback.zip"; o.AndroidVersion = "1.9.8"; o.AndroidUrl = "https://example.com/fallback.apk"; });
        builder.Services.AddSingleton(gh);
        await using var app = builder.Build();
        app.MapClientShell();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var resp = await http.GetFromJsonAsync<LatestDto>("/api/client/shell/latest");
        Assert.Equal("1.9.9", resp!.WindowsVersion);
        Assert.Equal("https://example.com/fallback.zip", resp.WindowsUrl);
    }

    private record LatestDto(string? WindowsVersion, string? WindowsUrl, string? AndroidVersion, string? AndroidUrl, DateTimeOffset? CheckedAt, string? Error);
}
