using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ClientShellLatestTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ClientShellLatestTests(WebApplicationFactory<Program> f) => _factory = f.WithWebHostBuilder(b => b.UseSetting("ShellClient:WindowsVersion", "1.2.3").UseSetting("ShellClient:WindowsUrl", "https://example.com/win.zip").UseSetting("ShellClient:AndroidVersion", "1.2.4").UseSetting("ShellClient:AndroidUrl", "https://example.com/app.apk").UseSetting("DisableHangfire", "true"));

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
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(_ => {});
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/client/shell/latest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LatestDto>();
        Assert.Null(body!.WindowsVersion);
        Assert.Null(body.WindowsUrl);
        Assert.Null(body.AndroidVersion);
        Assert.Null(body.AndroidUrl);
    }

    private record LatestDto(string? WindowsVersion, string? WindowsUrl, string? AndroidVersion, string? AndroidUrl);
}
