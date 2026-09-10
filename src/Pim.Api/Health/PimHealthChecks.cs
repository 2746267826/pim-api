using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Prometheus;

namespace Pim.Api.Health;

/// <summary>主数据库（PostgreSQL）连通性：核心依赖，失败即 Unhealthy。</summary>
public sealed class PimDatabaseHealthCheck(PimDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? HealthCheckResult.Healthy("database reachable")
                : HealthCheckResult.Unhealthy("cannot connect to database");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database check failed", ex);
        }
    }
}

/// <summary>Hangfire 存储：未配置时为 Degraded（后台任务降级运行），配置后失败为 Unhealthy。</summary>
public sealed class HangfireStorageHealthCheck(IConfiguration cfg, IHangfireMonitoringClient monitoring) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var disabled = bool.TryParse(cfg["DisableHangfire"], out var d) && d;
        var conn = cfg.GetConnectionString("DefaultConnection");
        if (disabled || string.IsNullOrWhiteSpace(conn))
            return Task.FromResult(HealthCheckResult.Degraded("hangfire disabled"));

        try
        {
            var snapshot = monitoring.GetSnapshot();
            return Task.FromResult(HealthCheckResult.Healthy(
                $"enqueued={snapshot.Enqueued} processing={snapshot.Processing} failed={snapshot.Failed}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("hangfire storage check failed", ex));
        }
    }
}

/// <summary>可选外部依赖的通用 HTTP 探测：未配置 → Degraded(disabled)；配置后不可达 → Degraded。</summary>
public abstract class OptionalHttpHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration cfg) : IHealthCheck
{
    protected abstract string Name { get; }
    protected abstract string? ResolveUrl(IConfiguration cfg);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var url = ResolveUrl(cfg);
        if (string.IsNullOrWhiteSpace(url))
            return HealthCheckResult.Degraded($"{Name} not configured");

        try
        {
            using var client = httpClientFactory.CreateClient($"healthcheck-{Name}");
            client.Timeout = TimeSpan.FromSeconds(5);
            using var resp = await client.GetAsync(url, ct);
            return resp.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"{Name} reachable")
                : HealthCheckResult.Degraded($"{Name} returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"{Name} unreachable", ex);
        }
    }
}

public sealed class MinioHealthCheck(IHttpClientFactory f, IConfiguration cfg) : OptionalHttpHealthCheck(f, cfg)
{
    protected override string Name => "minio";
    protected override string? ResolveUrl(IConfiguration cfg)
        => cfg["Minio:Endpoint"] is { Length: > 0 } e ? $"{e.TrimEnd('/')}/minio/health/live" : null;
}

public sealed class TikaHealthCheck(IHttpClientFactory f, IConfiguration cfg) : OptionalHttpHealthCheck(f, cfg)
{
    protected override string Name => "tika";
    protected override string? ResolveUrl(IConfiguration cfg)
        => cfg["Tika:BaseUrl"] is { Length: > 0 } e ? $"{e.TrimEnd('/')}/version" : null;
}

public sealed class QdrantHealthCheck(IHttpClientFactory f, IConfiguration cfg) : OptionalHttpHealthCheck(f, cfg)
{
    protected override string Name => "qdrant";
    protected override string? ResolveUrl(IConfiguration cfg)
        => cfg["Qdrant:BaseUrl"] is { Length: > 0 } e ? $"{e.TrimEnd('/')}/collections" : null;
}

public sealed class LiteLlmHealthCheck(IHttpClientFactory f, IConfiguration cfg) : OptionalHttpHealthCheck(f, cfg)
{
    protected override string Name => "litellm";
    protected override string? ResolveUrl(IConfiguration cfg)
    {
        var enabled = bool.TryParse(cfg["AI_ENABLED"] ?? cfg["Ai:Enabled"], out var en) && en;
        if (!enabled) return null;
        var baseUrl = cfg["AI_BASE_URL"] ?? cfg["Ai:BaseUrl"];
        return baseUrl is { Length: > 0 } ? $"{baseUrl.TrimEnd('/')}/health/liveliness" : null;
    }
}

public static class PimHealthChecks
{
    /// <summary>注册 PIM 健康检查：database 为核心依赖，其余可选依赖失败仅 Degraded；同时导出为 Prometheus 指标。</summary>
    public static IHealthChecksBuilder AddPimHealthChecks(this IServiceCollection services, IConfiguration cfg)
    {
        return services.AddHealthChecks()
            .ForwardToPrometheus()
            .AddCheck<PimDatabaseHealthCheck>("database", failureStatus: HealthStatus.Unhealthy, tags: ["ready"])
            .AddCheck<HangfireStorageHealthCheck>("hangfire", failureStatus: HealthStatus.Degraded, tags: ["ready"])
            .AddCheck<MinioHealthCheck>("minio", failureStatus: HealthStatus.Degraded, tags: ["ready"])
            .AddCheck<TikaHealthCheck>("tika", failureStatus: HealthStatus.Degraded, tags: ["ready"])
            .AddCheck<QdrantHealthCheck>("qdrant", failureStatus: HealthStatus.Degraded, tags: ["ready"])
            .AddCheck<LiteLlmHealthCheck>("litellm", failureStatus: HealthStatus.Degraded, tags: ["ready"]);
    }

    /// <summary>/health/ready 的 JSON 响应：整体状态 + 各检查明细。</summary>
    public static async Task WriteReadyResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1)
            }),
            timestamp = DateTimeOffset.UtcNow
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
