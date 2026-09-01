using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Module.Mcp;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpModuleEndpointTests
{
    private static IEnumerable<RouteEndpoint> McpEndpoints(WebApplication app)
    {
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/mcp", StringComparison.Ordinal) == true)
            .ToList();
        return endpoints;
    }
    [Fact]
    public void McpEndpoints_AreMappedUnderApiV1Mcp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        new McpModule().MapEndpoints(app);

        var endpoints = McpEndpoints(app).ToList();

        var paths = endpoints.Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet();
        Assert.Contains("/api/v1/mcp/clients", paths);
        Assert.Contains("/api/v1/mcp/clients/{id:guid}", paths);
        Assert.Contains("/api/v1/mcp/clients/{id:guid}/revoke", paths);
        Assert.Contains("/api/v1/mcp/verify", paths);
        Assert.Contains("/api/v1/mcp/catalog", paths);
    }

    [Fact]
    public void ManagementEndpoints_RequireAuthorization_ButVerifyDoesNot()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        new McpModule().MapEndpoints(app);

        var endpoints = McpEndpoints(app).ToList();

        foreach (var endpoint in endpoints)
        {
            var hasAuth = endpoint.Metadata.Any(metadata => metadata is IAuthorizeData);
            if (endpoint.RoutePattern.RawText == "/api/v1/mcp/verify")
                Assert.False(hasAuth, "verify must stay anonymous (token-based auth)");
            else
                Assert.True(hasAuth, $"{endpoint.RoutePattern.RawText} must require authorization");
        }
    }

    [Fact]
    public void McpServices_AreRegisteredScoped()
    {
        var services = new ServiceCollection();
        new McpModule().RegisterServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(McpClientService)
            && descriptor.Lifetime == ServiceLifetime.Scoped);
    }
}