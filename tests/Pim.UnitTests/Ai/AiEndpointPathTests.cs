using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Endpoints;
using Pim.Core.Ai;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiEndpointPathTests
{
    [Theory]
    [InlineData("FailedValidation", AiRequestStatus.FailedValidation)]
    [InlineData("failed_validation", AiRequestStatus.FailedValidation)]
    [InlineData("TimedOut", AiRequestStatus.TimedOut)]
    [InlineData("timed_out", AiRequestStatus.TimedOut)]
    [InlineData("succeeded", AiRequestStatus.Succeeded)]
    [InlineData("running", AiRequestStatus.Running)]
    [InlineData("cancelled", AiRequestStatus.Cancelled)]
    [InlineData("incomplete", AiRequestStatus.Incomplete)]
    public void TryParseStatus_AcceptsPublicStatusValues(string value, AiRequestStatus expected)
    {
        var parsed = AiEndpoints.TryParseStatus(value, out var status);

        Assert.True(parsed);
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("1")]
    [InlineData("999")]
    [InlineData("Failed, Blocked")]
    public void TryParseStatus_RejectsInvalidStatus(string value)
    {
        var parsed = AiEndpoints.TryParseStatus(value, out var status);

        Assert.False(parsed);
        Assert.Null(status);
    }

    [Fact]
    public async Task MapAiEndpoints_RegistersAdminRoutesAndRemovedLegacyGatewayRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        app.MapAiEndpoints();
        await app.StartAsync();

        var routes = app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToLookup(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty));

        foreach (var expected in new[]
        {
            "/api/v1/ai/requests",
            "/api/v1/ai/requests/{id:guid}",
            "/api/v1/ai/usage/summary",
            "/api/v1/ai/models",
            "/api/v1/ai/models/{id:guid}",
            "/api/v1/ai/models/{id:guid}/connection-test",
            "/api/v1/ai/models/connection-tests/latest",
            "/api/v1/ai/models/{id:guid}/connection-tests/latest",
            "/api/v1/ai/model-settings",
            "/api/v1/ai/text-calls",
            "/api/v1/ai/text-calls/{clientRequestId:guid}",
            "/api/v1/ai/vision-calls",
            "/api/v1/ai/vision-calls/{clientRequestId:guid}",
            "/api/v1/ai/requests/{logId:guid}/images",
            "/api/v1/ai/requests/{logId:guid}/images/{imageId:guid}"
        })
        {
            var endpoints = routes[expected].ToList();
            Assert.True(endpoints.Count > 0, $"Missing route: {expected}");
            Assert.All(endpoints, endpoint =>
            {
                var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
                Assert.NotEmpty(authorizeData);
                Assert.Contains(authorizeData, RequiresAdminRole);
            });
        }

        foreach (var removed in new[] { "/api/v1/ai/status", "/api/v1/ai/test", "/api/v1/ai/health-check" })
            Assert.Empty(routes[removed]);
    }

    private static string NormalizeRoute(string route) => route.Length > 1 ? route.TrimEnd('/') : route;

    private static bool RequiresAdminRole(IAuthorizeData authorizeData) =>
        authorizeData.Roles?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(role => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)) == true;
}
