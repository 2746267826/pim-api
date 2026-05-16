using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Data;
using Pim.Module.Stats.DTOs;
using Pim.Module.Stats.Services;

namespace Pim.Module.Stats;

public class StatsModule : IModule
{
    public string Name => "stats";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<StatsService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/stats")
            .RequireAuthorization();

        group.MapPost("/upload", async (
            [FromBody] UploadBatch batch,
            [FromServices] StatsService svc,
            CancellationToken ct) =>
        {
            if (batch.Entries.Count == 0)
                return Results.Ok(ApiResponse<int>.Ok(0));

            var count = await svc.IngestBatchAsync(batch, ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        await Task.CompletedTask;
    }
}
