using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pim.Api.Health;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 健康检查降级语义：可选依赖未配置或不可达仅 Degraded；Hangfire 未配置 Degraded；数据库 InMemory 连通 Healthy。
/// </summary>
public class PimHealthChecksTests
{
    private static IConfiguration NewCfg(params (string Key, string? Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHttpClientFactory NewHttpFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    [Fact]
    public async Task Tika_NotConfigured_IsDegraded()
    {
        var check = new TikaHealthCheck(NewHttpFactory(), NewCfg());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("not configured", result.Description);
    }

    [Fact]
    public async Task Tika_ConfiguredButUnreachable_IsDegraded()
    {
        var check = new TikaHealthCheck(NewHttpFactory(),
            NewCfg(("Tika:BaseUrl", "http://127.0.0.1:1")));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task LiteLlm_AiDisabled_IsDegraded()
    {
        var check = new LiteLlmHealthCheck(NewHttpFactory(),
            NewCfg(("AI_ENABLED", "false")));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Minio_NotConfigured_IsDegraded()
    {
        var check = new MinioHealthCheck(NewHttpFactory(), NewCfg());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Qdrant_NotConfigured_IsDegraded()
    {
        var check = new QdrantHealthCheck(NewHttpFactory(), NewCfg());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Hangfire_Disabled_IsDegraded()
    {
        var check = new HangfireStorageHealthCheck(
            NewCfg(("DisableHangfire", "true")),
            new PimHealthChecksTestsFakeMonitoring());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("disabled", result.Description);
    }

    [Fact]
    public async Task Hangfire_ConfiguredAndReachable_IsHealthy()
    {
        var check = new HangfireStorageHealthCheck(
            NewCfg(("ConnectionStrings:DefaultConnection", "Host=postgres;Database=pim")),
            new PimHealthChecksTestsFakeMonitoring());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Database_InMemory_IsHealthy()
    {
        await using var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"health-{Guid.NewGuid()}").Options);
        var check = new PimDatabaseHealthCheck(db);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class PimHealthChecksTestsFakeMonitoring : IHangfireMonitoringClient
    {
        public HangfireMonitoringSnapshot GetSnapshot() => new(1, 2, 3, 4);
    }
}
