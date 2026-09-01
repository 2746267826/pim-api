using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mcp.DTOs;
using Pim.Module.Mcp.Services;

namespace Pim.Module.Mcp;

public class McpModule : IModule
{
    private static readonly VerifyThrottle _verifyThrottle = new();

    public string Name => "mcp";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<McpClientService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // WebUI management endpoints (JWT-protected).
        var mgmt = endpoints.MapGroup(McpEndpointPaths.Root).RequireAuthorization();

        mgmt.MapGet("/clients", async (
            [FromServices] McpClientService service,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } userId)
                return Results.Unauthorized();
            return Results.Ok(ApiResponse<List<McpClientDto>>.Ok(await service.ListAsync(userId, ct)));
        });

        mgmt.MapPost("/clients", async (
            [FromBody] McpCreateClientRequest request,
            [FromServices] McpClientService service,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } userId)
                return Results.Unauthorized();
            var result = await service.CreateAsync(request.Name ?? string.Empty, userId, ct);
            return Results.Created(McpEndpointPaths.Client(result.Client.Id.ToString()),
                ApiResponse<McpClientCreateResult>.Ok(result));
        });

        mgmt.MapPut("/clients/{id:guid}", async (
            Guid id,
            [FromBody] McpClientUpdateRequest request,
            [FromServices] McpClientService service,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } userId)
                return Results.Unauthorized();
            var dto = await service.UpdateAsync(id, request.Name, request.Permissions, userId, ct);
            return Results.Ok(ApiResponse<McpClientDto>.Ok(dto));
        });

        mgmt.MapPost("/clients/{id:guid}/revoke", async (
            Guid id,
            [FromServices] McpClientService service,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } userId)
                return Results.Unauthorized();
            var dto = await service.RevokeAsync(id, userId, ct);
            return Results.Ok(ApiResponse<McpClientDto>.Ok(dto));
        });

        mgmt.MapDelete("/clients/{id:guid}", async (
            Guid id,
            [FromServices] McpClientService service,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } userId)
                return Results.Unauthorized();
            await service.DeleteAsync(id, userId, ct);
            return Results.NoContent();
        });

        mgmt.MapGet("/catalog", (
            [FromServices] McpClientService service) =>
            Results.Ok(ApiResponse<McpCatalogDto>.Ok(service.Catalog())));

        // Internal verify endpoint used by the MCP server. NOT JWT-protected on purpose:
        // it authenticates via the mcp_clients token itself. It is expected to be reached
        // only from the MCP server inside the internal network.
        var verify = endpoints.MapGroup(McpEndpointPaths.Root);
        verify.MapPost("/verify", async (
            [FromBody] McpVerifyRequest request,
            [FromServices] McpClientService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_verifyThrottle.Allow(ip))
                return Results.Json(ApiResponse<string>.Error(42901, "too many attempts"), statusCode: StatusCodes.Status429TooManyRequests);

            var auth = httpContext.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth[7..].Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(token))
                return Results.Json(ApiResponse<string>.Error(40101, "missing bearer token"), statusCode: StatusCodes.Status401Unauthorized);

            var outcome = await service.VerifyAsync(token, request.Tool, request.ParamsSummary, ct);
            if (outcome.HttpStatus != 0)
                _verifyThrottle.RecordFailure(ip);
            return outcome.HttpStatus switch
            {
                400 => Results.Json(ApiResponse<string>.Error(400, outcome.Error ?? "bad request"), statusCode: StatusCodes.Status400BadRequest),
                401 => Results.Json(ApiResponse<string>.Error(40101, outcome.Error ?? "unauthorized"), statusCode: StatusCodes.Status401Unauthorized),
                403 => Results.Json(ApiResponse<string>.Error(40301, outcome.Error ?? "forbidden"), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Ok(ApiResponse<McpVerifyResult>.Ok(outcome.Result!)),
            };
        });
    }

    public Task InitializeAsync(IServiceProvider serviceProvider)
        => Task.CompletedTask;
}

public static class McpEndpointPaths
{
    public const string Root = "/api/v1/mcp";

    public static string Client(string id) => $"{Root}/clients/{id}";
}

/// <summary>
/// Sliding-window in-memory throttle for /verify failures (per remote IP), to blunt
/// credential-stuffing / DoS amplification. Process-local; resets on restart.
/// </summary>
internal sealed class VerifyThrottle
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();
    private readonly TimeSpan _window;
    private readonly int _maxFailures;

    public VerifyThrottle(int maxFailures = 20, TimeSpan? window = null)
    {
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(5);
    }

    public bool Allow(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_failures.TryGetValue(ip, out var list))
            return true;
        lock (list)
        {
            list.RemoveAll(t => now - t > _window);
            return list.Count < _maxFailures;
        }
    }

    public void RecordFailure(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        var list = _failures.GetOrAdd(ip, _ => new List<DateTimeOffset>());
        lock (list)
        {
            list.RemoveAll(t => now - t > _window);
            list.Add(now);
        }
    }
}
