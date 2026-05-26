using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Module.QuickNotes;
using Xunit;

namespace Pim.UnitTests.QuickNotes;

public class QuickNoteEndpointPathTests
{
    [Fact]
    public void QuickNoteEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/quick-notes", QuickNoteEndpointPaths.Root);
        Assert.Equal("/api/v1/quick-notes/11111111-1111-1111-1111-111111111111", QuickNoteEndpointPaths.Note("11111111-1111-1111-1111-111111111111"));
        Assert.Equal("/api/v1/quick-notes/attachments", QuickNoteEndpointPaths.Attachments);
        Assert.Equal("/api/v1/quick-notes/attachments/22222222-2222-2222-2222-222222222222/download", QuickNoteEndpointPaths.AttachmentDownload("22222222-2222-2222-2222-222222222222"));
    }

    [Fact]
    public async Task MapEndpoints_RegistersExpectedAuthorizedRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        new QuickNotesModule().MapEndpoints(app);
        await app.StartAsync();

        var routeEndpoints = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToLookup(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty));

        var expectedRoutes = new[]
        {
            "/api/v1/quick-notes",
            "/api/v1/quick-notes/{id:guid}",
            "/api/v1/quick-notes/{id:guid}/process",
            "/api/v1/quick-notes/{id:guid}/archive",
            "/api/v1/quick-notes/{id:guid}/restore",
            "/api/v1/quick-notes/attachments",
            "/api/v1/quick-notes/attachments/{id:guid}/download",
            "/api/v1/quick-notes/attachments/{id:guid}"
        };

        foreach (var expectedRoute in expectedRoutes)
        {
            var endpoints = routeEndpoints[expectedRoute].ToList();
            Assert.True(endpoints.Count > 0, $"Missing route: {expectedRoute}. Found: {string.Join(", ", routeEndpoints.Select(group => group.Key))}");
            Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        }
    }

    private static string NormalizeRoute(string route)
        => route.Length > 1 ? route.TrimEnd('/') : route;
}
