using Microsoft.EntityFrameworkCore;
using Pim.Api;
using Pim.Api.Endpoints;
using Pim.Api.Middleware;
using Pim.Api.Search;
using Pim.Infrastructure.Extensions;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("/data/pim/logs/pim-api-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{Level}] {Message:lj}{NewLine}{Exception}")
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

app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "{RemoteIpAddress} {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000} ms";
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

// SPA fallback: non-API routes serve index.html (React Router handles routing)
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
