using Hangfire;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Pim.Api;
using Pim.Api.Endpoints;
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
using Pim.Infrastructure.Operations;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "pim-api")
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(new CompactJsonFormatter(), "/data/pim/logs/pim-api-.jsonl",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: LoggingConfig.ResolveRetainedFileCount(
            Environment.GetEnvironmentVariable("PIM_LOG_RETAINED_FILES")))
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

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
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
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
builder.Services.Configure<GitHubReleaseOptions>(o =>
{
    o.Repo = builder.Configuration["GitHub:Repo"] ?? "2746267826/pim-platform";
    o.Token = builder.Configuration["GITHUB_TOKEN"] ?? builder.Configuration["GitHub:Token"];
});
builder.Services.AddHttpClient<GitHubReleaseService>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<GitHubReleaseService>());
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
app.UseAuthentication();
app.UseMiddleware<OpsRateLimitMiddleware>();
app.UseMiddleware<OpsKeyMiddleware>();
app.UseAuthorization();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter() }
});

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

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();

// Ops endpoints
app.MapOpsLogsEndpoints();
app.MapOpsDbEndpoints();
app.MapOpsHealthEndpoints();

// Version endpoint — reads AssemblyInformationalVersion at runtime
app.MapVersionEndpoints();

// Auth endpoints (before modules so they're not auth-protected)
app.MapAuthEndpoints();

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
try
{
    RecurringJob.AddOrUpdate<Stage0DiagnosticJob>(
        "stage0-diagnostic",
        job => job.RunAsync(),
        Cron.Hourly);
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to register Hangfire diagnostic recurring job.");
}

// SPA fallback: non-API routes serve index.html (React Router handles routing)
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
