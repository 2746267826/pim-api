using Pim.Api;
using Pim.Api.Endpoints;
using Pim.Api.Middleware;
using Pim.Api.Search;
using Pim.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

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
app.UseAuthentication();
app.UseAuthorization();
app.UseCors();

// Auto-create database schema
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimDbContext>();
    db.Database.EnsureCreated();
}

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();

// Auth endpoints (before modules so they're not auth-protected)
app.MapAuthEndpoints();

// Search endpoint (uses ISearchProvider from modules)
app.MapSearchEndpoints();

// Module endpoints
moduleRegistry.MapAllEndpoints(app);

// Init modules
await moduleRegistry.InitializeAllAsync(app.Services);

app.Run();
