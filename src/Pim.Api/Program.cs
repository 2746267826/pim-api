using Hangfire;
using Microsoft.EntityFrameworkCore;
using Pim.Api;
using Pim.Api.Endpoints;
using Pim.Api.Infrastructure;
using Pim.Api.Middleware;
using Pim.Api.Search;
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
        retainedFileCountLimit: 30)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Infrastructure
builder.Services.AddPimInfrastructure(builder.Configuration);
builder.Services.AddPimAuth();

// HTTP (AddHttpContextAccessor is already called in AddPimInfrastructure)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Module discovery
var moduleRegistry = new ModuleRegistry();
moduleRegistry.DiscoverModules(builder.Services, builder.Configuration);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "{RemoteIpAddress} {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000} ms";
});
app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter() }
});

// Serve React SPA static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// Apply database migrations. Existing EnsureCreated databases are adopted before Migrate().
using (var scope = app.Services.CreateScope())
{
    var adoption = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimMigrationAdoptionService>();
    await adoption.AdoptExistingSchemaAsync();

    var db = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimDbContext>();
    await db.Database.MigrateAsync();
}

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();

// Auth endpoints (before modules so they're not auth-protected)
app.MapAuthEndpoints();

// Search endpoint (uses ISearchProvider from modules)
app.MapSearchEndpoints();
app.MapStatusEndpoints();
app.MapDaemonEndpoints();
app.MapOperationsEndpoints();

// Module endpoints
moduleRegistry.MapAllEndpoints(app);

// Init modules
await moduleRegistry.InitializeAllAsync(app.Services);
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
