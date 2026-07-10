using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Pim.Api.Endpoints;
using Xunit;

namespace Pim.UnitTests.Api;

public sealed class VersionEndpointTests
{
    [Fact]
    public void PhaseOneCapabilitiesAdvertiseItemResultsOnly()
    {
        Assert.Contains(VersionEndpoints.MobileItemResultsV1, VersionEndpoints.Capabilities);
        Assert.DoesNotContain("androidEmbedV1", VersionEndpoints.Capabilities);
    }

    [Fact]
    public async Task MapVersionEndpoints_ReturnsTypedJsonContract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapVersionEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var response = await client.GetFromJsonAsync<ApiVersionResponse>("/api/version");

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
        Assert.Equal([VersionEndpoints.MobileItemResultsV1], response.Capabilities);
    }
}
