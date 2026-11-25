using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pim.Api.DTOs;
using Pim.Core.Common;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.RecycleBin;

public sealed class RecycleBinEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = AuthRegistrationTestHost.Create(
        AuthRegistrationTestHost.NewDbName("q03-http"));

    [Fact]
    public async Task UnifiedRoutes_RequireAuthenticationAndRejectUnsupportedDomain()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/recycle-bin")).StatusCode);

        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "q03-http-user");
        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);

        var listed = await client.GetAsync("/api/v1/recycle-bin?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = (await listed.Content.ReadFromJsonAsync<ApiResponse<RecycleBinPageDto>>())!.Data!;
        Assert.Empty(page.Items);
        Assert.Equal(4, page.Groups.Count);

        var invalid = await client.GetAsync("/api/v1/recycle-bin?domain=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(40001, (await invalid.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);

        var empty = await client.PostAsJsonAsync(
            "/api/v1/recycle-bin/restore",
            new RecycleBinBatchRequest([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(40001, (await empty.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);
    }

    public void Dispose() => _factory.Dispose();
}
