using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Endpoints;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;
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
    public void TryParseStatus_AcceptsPublicStatusValues(string value, AiRequestStatus expected)
    {
        var parsed = AiEndpoints.TryParseStatus(value, out var status);

        Assert.True(parsed);
        Assert.Equal(expected, status);
    }

    [Fact]
    public void TryParseStatus_RejectsInvalidStatus()
    {
        var parsed = AiEndpoints.TryParseStatus("nope", out var status);

        Assert.False(parsed);
        Assert.Null(status);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("999")]
    [InlineData("Failed, Blocked")]
    public void TryParseStatus_RejectsNumericAndCombinedStatusValues(string value)
    {
        var parsed = AiEndpoints.TryParseStatus(value, out var status);

        Assert.False(parsed);
        Assert.Null(status);
    }

    [Fact]
    public async Task MapAiEndpoints_RegistersExpectedAuthorizedRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAiGateway, FakeAiGateway>();
        builder.Services.AddSingleton<IAiUsageService, FakeAiUsageService>();
        builder.Services.AddSingleton<IAiProviderHealthService, FakeAiProviderHealthService>();
        using var app = builder.Build();

        app.MapAiEndpoints();
        await app.StartAsync();

        var routes = app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToLookup(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty));

        foreach (var expected in new[]
        {
            "/api/v1/ai/status",
            "/api/v1/ai/test",
            "/api/v1/ai/requests",
            "/api/v1/ai/requests/{id:guid}",
            "/api/v1/ai/usage/summary",
            "/api/v1/ai/health-check"
        })
        {
            var endpoints = routes[expected].ToList();
            Assert.True(endpoints.Count > 0, $"Missing route: {expected}");
            Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        }
    }

    private static string NormalizeRoute(string route) => route.Length > 1 ? route.TrimEnd('/') : route;

    private sealed class FakeAiGateway : IAiGateway
    {
        public Task<AiResult> CompleteAsync(AiGatewayRequest request, CancellationToken ct = default)
            => Task.FromResult(new AiResult(AiRequestStatus.Succeeded, "ok", null, [], new AiTokenUsage(1, 1, 2, null, null), Guid.NewGuid(), null));
    }

    private sealed class FakeAiUsageService : IAiUsageService
    {
        public Task<AiStatusDto> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new AiStatusDto(true, "litellm", "http://litellm:4000", "pim-default", null, null, null));

        public Task<PagedResult<AiRequestLogListItemDto>> ListRequestsAsync(AiRequestLogFilter filter, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<AiRequestLogListItemDto>([], 1, 50, 0, 0));

        public Task<AiRequestLogDetailDto?> GetRequestDetailAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<AiRequestLogDetailDto?>(null);

        public Task<AiUsageSummaryDto> GetUsageSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
            => Task.FromResult(new AiUsageSummaryDto(0, 0, 0, 0, 0, 0, 0, [], [], [], []));
    }

    private sealed class FakeAiProviderHealthService : IAiProviderHealthService
    {
        public Task CheckAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
