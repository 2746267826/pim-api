using Hangfire;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Pim.Api;
using Pim.Api.Endpoints;
using Pim.Api.Health;
using Pim.Api.Infrastructure;
using Pim.Api.Infrastructure.Ops;
using Pim.Api.Middleware;
using Pim.Api.Modules.ClientShell;
using Pim.Api.Services;
using Pim.Api.Search;
using Pim.Api.Today;
using Pim.Core.Caching;
using Pim.Core.Today;
using Pim.Infrastructure.Extensions;
using Pim.Infrastructure.Metrics;
using Pim.Infrastructure.Operations;
using Pim.Module.Mcp.Services;
using Prometheus;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.Grafana.Loki;

// --mcp-stdio: dedicated local-process MCP stdio server. Stdout carries ONLY the MCP
// protocol, so console logs must go to stderr (serilog text writer sink) in that mode.
var isMcpStdio = args.Contains("--mcp-stdio", StringComparer.Ordinal);

// 可选 Loki 日志聚合：设置 LOKI_URL（如 http://loki:3100）即启用，未设置时仅文件 + 控制台
var lokiUrl = Environment.GetEnvironmentVariable("LOKI_URL")
    ?? Environment.GetEnvironmentVariable("Loki__Url");

static Serilog.LoggerConfiguration WithLoki(Serilog.LoggerConfiguration cfg, string? url)
    => string.IsNullOrWhiteSpace(url)
        ? cfg
        : cfg.WriteTo.GrafanaLoki(url, [new LokiLabel { Key = "app", Value = "pim-api" }]);

Log.Logger = WithLoki(new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "pim-api")
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(new CompactJsonFormatter(), "/data/pim/logs/pim-api-.jsonl",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: LoggingConfig.ResolveRetainedFileCount(
            Environment.GetEnvironmentVariable("PIM_LOG_RETAINED_FILES"))), lokiUrl)
    .CreateLogger();

if (isMcpStdio)
{
    // stdio mode: stdout carries ONLY the MCP protocol. Swap the console sink to stderr
    // (the file sink above remains for ops forensics).
    Log.CloseAndFlush();
    Log.Logger = WithLoki(new LoggerConfiguration()
        .MinimumLevel.Debug()
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "pim-api")
        .WriteTo.TextWriter(new CompactJsonFormatter(), Console.Error)
        .WriteTo.File(new CompactJsonFormatter(), "/data/pim/logs/pim-api-.jsonl",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: LoggingConfig.ResolveRetainedFileCount(
                Environment.GetEnvironmentVariable("PIM_LOG_RETAINED_FILES"))), lokiUrl)
        .CreateLogger();
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Observability: health checks + metrics refresh
builder.Services.AddPimHealthChecks(builder.Configuration);
builder.Services.AddHostedService<Pim.Infrastructure.Metrics.MetricsRefreshService>();

// Infrastructure
builder.Services.AddPimInfrastructure(builder.Configuration);
builder.Services.AddPimAuth();
builder.Services.AddAggregateResultCaching();
builder.Services.Configure<OpsOptions>(o =>
{
    o.OpsKey = builder.Configuration["PIM_OPS_KEY"] ?? builder.Configuration["Ops:Key"];
    o.RoConnectionString = builder.Configuration["PIM_OPS_RO_CONNECTION"] ?? builder.Configuration.GetConnectionString("OpsRo");
});
builder.Services.AddOptions<OpsOptions>()
    .Validate(o =>
    {
        try
        {
            _ = new OpsKeyValidator(o.OpsKey);
            return true;
        }
        catch (Microsoft.Extensions.Options.OptionsValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Microsoft.Extensions.Options.OptionsValidationException(nameof(OpsOptions), typeof(OpsOptions), new[] { ex.Message });
        }
    })
    .ValidateOnStart();

// HTTP (AddHttpContextAccessor is already called in AddPimInfrastructure)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // PIM-002: whitelist-based CORS — do not use AllowAnyOrigin with credentials
        var raw = builder.Configuration["Cors:AllowedOrigins"]
            ?? Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")
            ?? Environment.GetEnvironmentVariable("PIM_CORS_ORIGINS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "http://localhost:5173,http://127.0.0.1:5173,http://localhost:5858,http://127.0.0.1:5858";
        }

        var allowedOrigins = raw
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(o => o.Trim())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .ToArray();

        if (allowedOrigins.Length > 0)
        {
            // WithOrigins already validates Origin; no need for redundant SetIsOriginAllowed.
            // Use OrdinalIgnoreCase via normalized comparison if custom check is ever needed.
            policy.WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
        else
        {
            // Fallback — still whitelist, never AllowAnyOrigin
            policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:5858", "http://127.0.0.1:5858")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
    });
});

// Module discovery
var moduleRegistry = new ModuleRegistry();
try
{
    moduleRegistry.DiscoverModules(builder.Services, builder.Configuration);
}
catch (Exception ex)
{
    Log.Warning(ex, "Module discovery failed; continuing with discovered modules only.");
}

builder.Services.AddScoped<TodaySectionService>();
builder.Services.AddScoped<ITodaySectionProvider, CalendarScheduleTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, CalendarTasksTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, CalendarHabitsTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, CalendarAvailabilityTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, CalendarAiPlaceholdersTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, OperationsConfirmationsTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, OutlookSyncTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, RemindersQueueTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, ReportsAvailableTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, EndpointsStatusTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, PcActivityTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, PcQualityTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, OperationsHealthTodaySectionProvider>();
builder.Services.AddScoped<ITodaySectionProvider, ClassificationSuggestionsTodaySectionProvider>();
builder.Services.AddSingleton<OpsLogsService>();
builder.Services.AddSingleton<SqlAstValidator>();
builder.Services.AddScoped<OpsDbService>();
builder.Services.AddSingleton<OpsRateLimiter>();
builder.Services.AddClientShell(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.ValueCountLimit = 4096;
});
builder.Services.Configure<GitHubReleaseOptions>(o =>
{
    o.Repo = builder.Configuration["GitHub:Repo"] ?? "2746267826/pim-platform";
    o.Token = builder.Configuration["GITHUB_TOKEN"] ?? builder.Configuration["GitHub:Token"];
});
builder.Services.AddHttpClient("GitHubRelease");
builder.Services.AddSingleton<GitHubReleaseService>(sp => new GitHubReleaseService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubRelease"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubReleaseOptions>>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GitHubReleaseService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitHubReleaseService>());

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownProxies = { System.Net.IPAddress.Parse("127.0.0.1"), System.Net.IPAddress.Parse("::1") },
    KnownNetworks = { new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("127.0.0.1"), 32), new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("::1"), 128) }
});
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "{RemoteIpAddress} {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000} ms";
});
app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseHttpMetrics();
app.UseAuthentication();
app.UseMiddleware<OpsRateLimitMiddleware>();
app.UseMiddleware<OpsKeyMiddleware>();
app.UseMiddleware<McpScopedTokenMiddleware>();
app.UseAuthorization();
try
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter() }
    });
}
catch (Exception ex)
{
    Log.Warning(ex, "Hangfire dashboard disabled: storage not configured");
}

// Serve React SPA static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// Apply database migrations. Existing EnsureCreated databases are adopted before Migrate().
try
{
    using (var scope = app.Services.CreateScope())
    {
        var adoption = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimMigrationAdoptionService>();
        await adoption.AdoptExistingSchemaAsync();

        var db = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimDbContext>();
        await db.Database.MigrateAsync();
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "Database migration failed; the API will start but database-dependent endpoints may not work.");
}

// Admin bootstrap: 已部署系统升级后若无任何管理员，自动将最早注册的有效用户提升为管理员（幂等）
try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimDbContext>();
        var promoted = await Pim.Infrastructure.Auth.AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None);
        if (promoted is not null)
            Log.Warning("系统中没有管理员账号：已自动将最早注册的有效用户 {UserId} 提升为管理员（admin）", promoted);
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "Admin bootstrap failed; the API will start but admin-only endpoints may be unreachable.");
}

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();

// Liveness（进程存活）与 Readiness（依赖可用；可选依赖仅 Degraded）
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = Pim.Api.Health.PimHealthChecks.WriteReadyResponse
}).AllowAnonymous();

// Prometheus 指标端点：admin JWT 或 OpsKey（X-PIM-Ops-Key / Bearer）鉴权，不公开
app.MapMetrics("/metrics").AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    if (http.User.IsInRole("admin"))
        return await next(context);

    var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
    var validator = new OpsKeyValidator(cfg["PIM_OPS_KEY"] ?? cfg["Ops:Key"]);
    var key = http.Request.Headers["X-PIM-Ops-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(key))
    {
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            key = auth["Bearer ".Length..];
    }
    if (validator.HasKeys && validator.IsValid(key))
        return await next(context);

    return Results.Text("{\"code\":40101,\"message\":\"MetricsAuthRequired\"}", "application/json", statusCode: 401);
});

// Ops endpoints
app.MapOpsLogsEndpoints();
app.MapOpsDbEndpoints();
app.MapOpsHealthEndpoints();

// Version endpoint — reads AssemblyInformationalVersion at runtime
app.MapVersionEndpoints();

// Auth endpoints (before modules so they're not auth-protected)
app.MapAuthEndpoints();
app.MapAdminEndpoints();

// Search endpoint (uses ISearchProvider from modules)
app.MapSearchEndpoints();
app.MapStatusEndpoints();
app.MapDaemonEndpoints();
app.MapEndpointEndpoints();
app.MapOperationsEndpoints();
app.MapTodayEndpoints();
app.MapAiEndpoints();
app.MapClientShell();

// Module endpoints
moduleRegistry.MapAllEndpoints(app);

// Init modules
try
{
    await moduleRegistry.InitializeAllAsync(app.Services);
}
catch (Exception ex)
{
    Log.Warning(ex, "Module initialization failed; the API will start but module endpoints may not work.");
}
var hangfireDisabled = bool.TryParse(builder.Configuration["DisableHangfire"], out var hd) && hd;
var dbConn = builder.Configuration.GetConnectionString("DefaultConnection");
var hangfireEnabled = !hangfireDisabled && !string.IsNullOrWhiteSpace(dbConn);

if (!hangfireEnabled)
{
    Log.Warning("Hangfire background jobs are unconfigured/disabled; running in degraded mode without background scheduling.");
}
else
{
    try
    {
        RecurringJob.AddOrUpdate<Stage0DiagnosticJob>(
            "stage0-diagnostic",
            job => job.RunAsync(CancellationToken.None),
            Cron.Hourly);

        RecurringJob.AddOrUpdate<HeartbeatStaleInspectionJob>(
            "heartbeat-stale-inspection",
            job => job.RunAsync(CancellationToken.None),
            "*/5 * * * *");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to register Hangfire recurring jobs.");
    }
}

// 白屏修复 #2：未匹配的 /api/* 返回 JSON 404，避免落入 SPA fallback 返回 index.html 导致前端 Unexpected token '<'
// 注意顺序：此 catch-all 必须在 SPA fallback 之前，且勿在两者间插入其它 Fallback，否则未知 /api 路径会再次返回 HTML
app.MapMethods("/api/{*path}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" }, (HttpContext ctx) =>
    Results.NotFound(new { code = 404, message = $"接口不存在: {ctx.Request.Path}", data = (object?)null, timestamp = DateTimeOffset.UtcNow }))
    .AllowAnonymous();

if (isMcpStdio)
{
    // Dedicated local-process MCP stdio server (Claude Code / Codex mcp.json).
    await McpServerBootstrap.RunStdioAsync(app);
    return;
}

// In-process MCP server: captures the pipeline, maps /mcp (bearer guard + 308 + MapMcp).
McpServerBootstrap.ConfigureHttp(app);

// SPA fallback: non-API routes serve index.html (React Router handles routing)
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
