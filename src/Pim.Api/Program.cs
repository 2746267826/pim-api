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
using Pim.Api.Tiles;
using Pim.Core.Caching;
using Pim.Core.Common;
using Pim.Core.Today;
using Pim.Infrastructure.Extensions;
using Pim.Infrastructure.Metrics;
using Pim.Infrastructure.Operations;
using Pim.Module.Mcp.Services;
using Prometheus;
using Serilog;

// --mcp-stdio: dedicated local-process MCP stdio server. Stdout carries ONLY the MCP
// protocol, so console logs must go to stderr (serilog text writer sink) in that mode.
var isMcpStdio = args.Contains("--mcp-stdio", StringComparer.Ordinal);

var builder = WebApplication.CreateBuilder(args);

// 可选 Loki 日志聚合：设置 LOKI_URL（如 http://loki:3100）即启用，未设置时仅文件 + 控制台
var lokiUrl = Environment.GetEnvironmentVariable("LOKI_URL")
    ?? Environment.GetEnvironmentVariable("Loki__Url");

var retainedFiles = LoggingConfig.ResolveRetainedFileCount(
    Environment.GetEnvironmentVariable("PIM_LOG_RETAINED_FILES"));
var fileSizeLimitBytes = LoggingConfig.ResolveFileSizeLimitBytes(
    Environment.GetEnvironmentVariable("PIM_LOG_FILE_SIZE_LIMIT_BYTES"));
var rollOnFileSizeLimit = LoggingConfig.ResolveRollOnFileSizeLimit(
    Environment.GetEnvironmentVariable("PIM_LOG_ROLL_ON_FILE_SIZE_LIMIT"), fileSizeLimitBytes);

// 日志级别不再硬编码：PIM_LOG_LEVEL > Logging:LogLevel:Default > Information（默认生产可用档），
// EF 内务类别（ChangeTracking / Database.Command）默认抬到 Warning 且可配置覆盖。
// --mcp-stdio 分支与主分支共用这一套级别与接收器，只是把控制台输出换到 stderr。
var loggingLevels = LoggingConfig.ResolveLevels(builder.Configuration);

Log.Logger = LoggingConfig.CreateLoggerConfiguration(
        loggingLevels,
        new LoggingConfig.LoggingSinkOptions(
            LoggingConfig.DefaultLogFilePath,
            retainedFiles,
            fileSizeLimitBytes,
            rollOnFileSizeLimit,
            lokiUrl,
            ConsoleToStandardError: isMcpStdio))
    .CreateLogger();

builder.Host.UseSerilog();

Log.Information(
    "日志级别生效：MinimumLevel={MinimumLevel}（来源：{MinimumLevelSource}）；类别覆盖：{CategoryLevels}",
    loggingLevels.MinimumLevel,
    loggingLevels.MinimumLevelSource,
    string.Join(", ", loggingLevels.CategoryLevels.Select(pair => $"{pair.Key}={pair.Value}")));

foreach (var invalidEntry in loggingLevels.InvalidEntries)
{
    // 级别写错不能被静默吞掉：明确报出来，并按默认级别继续（不因为一条配置把服务卡死）
    Log.Warning("日志级别配置项无法解析，已按默认级别处理：{InvalidEntry}", invalidEntry);
}

// 客户端契约（OpenAPI）：文档由端点元数据生成，CI 启动 Test 环境实例导出到
// contract/openapi.json 供客户端仓生成类型。生产环境不映射该端点（见下方 UseSwagger）。
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // 项目里存在等价的尾斜杠路由（例如 GET /api/v1/pc/categories 与 .../categories/），
    // Swagger 要求 method+path 组合唯一；文档保留第一个即可，不影响运行时路由行为。
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
    // 契约批次2：非空引用类型属性进 required[]（C# 可空性即权威定义；仅影响文档元数据，
    // 不参与运行时绑定/校验，行为不变）。请求 DTO 上的 DataAnnotations（property target）
    // 同步渲染为 maxLength/minLength/format 等字段级约束。
    // 注：SupportNonNullableReferenceTypes 对 record 位置参数属性不生效（6.9 已知局限），
    // record 的 required 由 NullabilityRequiredSchemaFilter 推导；此开关兜底处理非 record 类型。
    c.SupportNonNullableReferenceTypes();
    c.SchemaFilter<Pim.Api.Infrastructure.OpenApi.NullabilityRequiredSchemaFilter>();
    // 契约批次2：服务端严格校验的取值集合 → enum（每条都有代码依据，见 filter 注释）。
    c.SchemaFilter<Pim.Api.Infrastructure.OpenApi.StrictValueSetsSchemaFilter>();
    // 契约批次3（工单《契约一致性与校验修正》/ issue #417）：整数枚举补 x-enum-varnames，
    // 让客户端能生成有意义的常量名；type/enum 不变，线上仍是整数。
    c.SchemaFilter<Pim.Api.Infrastructure.OpenApi.EnumMemberNamesSchemaFilter>();
    // 契约批次3：securitySchemes（bearer JWT + X-PIM-Ops-Key）与逐 operation 的 security，
    // 均按端点真实鉴权现状映射（见 SecurityMetadataOperationFilter 注释），不改变行为。
    c.OperationFilter<Pim.Api.Infrastructure.OpenApi.SecurityMetadataOperationFilter>();
    // 契约批次3（issue #417）：import-ics 的 handler 自己读表单，这里只补 requestBody 文档，
    // 不用 .Accepts（那会改变端点匹配、把 handler 的 400 分支变成死代码）。
    c.OperationFilter<Pim.Api.Infrastructure.OpenApi.IcsImportRequestBodyOperationFilter>();
    // 契约语义 §1（docs/client-contract-semantics.md）：按日端点在响应 schema 里声明业务日口径
    // （businessDate / businessTimeZone / businessDayStartHour + x-business-day）。
    // 运行时由各 DTO 的同名属性下发；过滤器负责「契约里一定有」，端点/schema 对不上就抛异常。
    c.DocumentFilter<Pim.Api.Infrastructure.OpenApi.BusinessDayFieldsDocumentFilter>();
    // 契约语义 §2：写操作的 Idempotency-Key 请求头（运行时见 IdempotencyMiddleware）。
    // 文档过滤器把参数收敛到「运行时真的生效」的端点，并补 409 与幂等重放标记。
    c.OperationFilter<Pim.Api.Infrastructure.OpenApi.IdempotencyKeyParameterOperationFilter>();
    c.DocumentFilter<Pim.Api.Infrastructure.OpenApi.IdempotencyKeyDocumentFilter>();
    c.DocumentFilter<Pim.Api.Infrastructure.OpenApi.SecuritySchemesDocumentFilter>();
});

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
                // 跨域 JS 只能读 safelisted 响应头；不 expose 的话客户端读不到重放标记（review P3-5）。
                .WithExposedHeaders(Pim.Api.Middleware.IdempotencyMiddleware.ReplayedHeader)
                .AllowCredentials();
        }
        else
        {
            // Fallback — still whitelist, never AllowAnyOrigin
            policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:5858", "http://127.0.0.1:5858")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders(Pim.Api.Middleware.IdempotencyMiddleware.ReplayedHeader)
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
builder.Services.AddTileServices(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.ValueCountLimit = 4096;
});
builder.Services.Configure<GitHubReleaseOptions>(o =>
{
    // 客户端更新检查：读各客户端仓发版时上传的 version.json（releases/latest/download 直链）
    o.WindowsRepo = builder.Configuration["GitHub:WindowsRepo"] ?? "2746267826/pim-windows";
    o.AndroidRepo = builder.Configuration["GitHub:AndroidRepo"] ?? "2746267826/pim-android";
    o.VersionFileName = builder.Configuration["GitHub:VersionFileName"] ?? "version.json";
    o.Token = builder.Configuration["GITHUB_TOKEN"] ?? builder.Configuration["GitHub:Token"];
    if (TimeSpan.TryParse(builder.Configuration["GitHub:PollInterval"], out var poll) && poll > TimeSpan.Zero)
    {
        o.PollInterval = poll;
    }
});
builder.Services.AddHttpClient("GitHubRelease");
builder.Services.AddSingleton<GitHubReleaseService>(sp => new GitHubReleaseService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubRelease"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubReleaseOptions>>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GitHubReleaseService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitHubReleaseService>());

// 本镜像内嵌的前端版本（wwwroot/web-version.json，由 pim-web 的产物镜像在构建期带进来）。
// 只读一次并缓存；文件缺失/不可读时返回 "unknown" 哨兵而非抛错，见 WebVersionProvider。
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new WebVersionProvider(
        env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebVersionProvider>>());
});

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownProxies = { System.Net.IPAddress.Parse("127.0.0.1"), System.Net.IPAddress.Parse("::1") },
    KnownNetworks = { new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("127.0.0.1"), 32), new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("::1"), 128) }
});
app.UseMiddleware<CorrelationIdMiddleware>();
// 指标中间件必须挂在 ExceptionMiddleware 外侧（此处）：它在请求结束时按响应上的
// StatusCode 打标签，而异常是在 ExceptionMiddleware 里才被转成 502/500 的。
// 顺序反了的话，异常请求在采样时状态码还是默认的 200 —— 5xx 永远不会进入
// http_requests_received_total，PimHttp5xxSpike 告警也就永远不会触发（issue #299）。
// 路由数据由 prometheus-net 的 CaptureRouteDataMiddleware 自行捕获，因此此处
// 早于 UseRouting 不影响 endpoint 标签。
app.UseHttpMetrics();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "{RemoteIpAddress} {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000} ms";
});
app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<OpsRateLimitMiddleware>();
app.UseMiddleware<OpsKeyMiddleware>();
app.UseMiddleware<McpScopedTokenMiddleware>();
app.UseAuthorization();
// 写操作幂等（docs/client-contract-semantics.md §2）：必须在 UseAuthorization 之后（需要当前用户），
// 且在端点执行之前（它要替换 Response.Body 截获成功响应）。
app.UseMiddleware<IdempotencyMiddleware>();
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
// 客户端契约端点：只在开发环境（以及 CI 用的 Test 环境）暴露。生产环境的契约以
// 构建期导出的 contract/openapi.json 为准，不开放运行时文档入口。
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Test"))
{
    app.UseSwagger();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Apply database migrations. Existing EnsureCreated databases are adopted before Migrate().
//
// 迁移失败默认必须显式失败退出（非零退出码），由编排层重启：
// 只记 Warning 继续启动会让进程以"半套 schema"对外服务（后续读写全部 500），
// 而且从启动日志里看不出根因是 schema；DB 慢启动的顾虑由有限次重试覆盖。
// 唯一的例外是无数据库的宿主测试（WebApplicationFactory）——它们用
// Database:Migrations:FailFast=false 显式声明"接受无迁移运行"，生产配置不设该项即为默认的失败退出。
var failFastOnMigrationFailure = app.Configuration.GetValue("Database:Migrations:FailFast", true);
try
{
    using (var scope = app.Services.CreateScope())
    {
        var adoption = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimMigrationAdoptionService>();
        var db = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimDbContext>();
        await Pim.Infrastructure.Data.PimDatabaseMigrationRetry.ExecuteAsync(
            async ct =>
            {
                await adoption.AdoptExistingSchemaAsync(ct);
                await db.Database.MigrateAsync(ct);
            },
            onRetry: (ex, attempt) =>
            {
                Log.Warning(
                    ex,
                    "数据库迁移第 {Attempt}/{MaxAttempts} 次失败，{DelaySeconds}s 后重试（等待 DB 就绪）。",
                    attempt,
                    Pim.Infrastructure.Data.PimDatabaseMigrationRetry.DefaultMaxAttempts,
                    Pim.Infrastructure.Data.PimDatabaseMigrationRetry.DefaultDelay.TotalSeconds);
                return Task.CompletedTask;
            });
    }
}
catch (Exception ex) when (failFastOnMigrationFailure)
{
    Log.Fatal(ex, "数据库迁移失败：进程退出，避免以未完成迁移的 schema 提供服务。");
    await Log.CloseAndFlushAsync();
    return 1;
}
catch (Exception ex)
{
    Log.Error(
        ex,
        "数据库迁移失败：Database:Migrations:FailFast=false，进程继续启动但 schema 可能不完整（仅用于无数据库的宿主测试）。");
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
app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy", DateTimeOffset.UtcNow)))
    .AllowAnonymous()
    .Produces<HealthResponse>(200)
    .Produces<ApiResponse<string>>(500);

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
app.MapDataReliabilityEndpoints();
app.MapTodayEndpoints();
app.MapAiEndpoints();
app.MapClientShell();
app.MapTileEndpoints();

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

        // 契约语义 §2：幂等记录 24 小时后不再有意义，且表里存着完整响应体，必须定期清理。
        RecurringJob.AddOrUpdate<IdempotencyCleanupJob>(
            "idempotency-cleanup",
            job => job.RunAsync(CancellationToken.None),
            Cron.Hourly);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to register Hangfire recurring jobs.");
    }
}

// 白屏修复 #2：未匹配的 /api/* 返回 JSON 404，避免落入 SPA fallback 返回 index.html 导致前端 Unexpected token '<'
// 注意顺序：此 catch-all 必须在 SPA fallback 之前，且勿在两者间插入其它 Fallback，否则未知 /api 路径会再次返回 HTML
app.MapMethods("/api/{*path}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD" }, (HttpContext ctx) =>
    Results.NotFound(ApiResponse<string>.Error(404, $"接口不存在: {ctx.Request.Path}")))
    .AllowAnonymous()
    .Produces<ApiResponse<string>>(404)
    .Produces<ApiResponse<string>>(500);

if (isMcpStdio)
{
    // Dedicated local-process MCP stdio server (Claude Code / Codex mcp.json).
    await McpServerBootstrap.RunStdioAsync(app);
    return 0;
}

// In-process MCP server: captures the pipeline, maps /mcp (bearer guard + 308 + MapMcp).
McpServerBootstrap.ConfigureHttp(app);

// SPA fallback: non-API routes serve index.html (React Router handles routing)
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// 迁移失败路径会 return 1（见上），因此这里显式返回 0。
return 0;

public partial class Program { }

// 契约注解用：/health 的响应形状（与原匿名对象 JSON 等价：camelCase 属性、同字段）
public record HealthResponse(string Status, DateTimeOffset Timestamp);
