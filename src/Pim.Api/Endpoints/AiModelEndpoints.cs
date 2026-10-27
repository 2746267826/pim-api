using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;

namespace Pim.Api.Endpoints;

public static class AiModelEndpoints
{
    public static void MapAiModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/ai").RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });
        group.MapGet("/models", async ([FromServices] AiModelSettingsService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<AiModelDto>>.Ok(await service.ListAsync(ct))))
            .Produces<ApiResponse<IReadOnlyList<AiModelDto>>>(200).Produces(401).Produces(403);
        group.MapPost("/models", async (SaveAiModelRequest request, [FromServices] AiModelSettingsService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiModelDto>.Ok(await service.SaveAsync(null, request, ct))))
            .Produces<ApiResponse<AiModelDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409);
        group.MapPut("/models/{id:guid}", async (Guid id, SaveAiModelRequest request, [FromServices] AiModelSettingsService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiModelDto>.Ok(await service.SaveAsync(id, request, ct))))
            .Produces<ApiResponse<AiModelDto>>(200).Produces(400).Produces(401).Produces(403).Produces(404).Produces(409);
        group.MapDelete("/models/{id:guid}", async (Guid id, [FromQuery] Guid revision, [FromServices] AiModelSettingsService service, CancellationToken ct) =>
        { await service.DeleteAsync(id, revision, ct); return Results.Ok(ApiResponse<bool>.Ok(true)); })
            .Produces<ApiResponse<bool>>(200).Produces(401).Produces(403).Produces(404).Produces(409);
        group.MapGet("/model-settings", async ([FromServices] AiModelSettingsService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiModelSettingsDto>.Ok(await service.GetSettingsAsync(ct))))
            .Produces<ApiResponse<AiModelSettingsDto>>(200).Produces(401).Produces(403);
        group.MapPut("/model-settings", async (SaveAiModelSettingsRequest request, [FromServices] AiModelSettingsService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiModelSettingsDto>.Ok(await service.SaveSettingsAsync(request, ct))))
            .Produces<ApiResponse<AiModelSettingsDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409);
        group.MapPost("/text-calls", async (AiTextCallRequest request, [FromServices] AiTextCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.CallAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(1024 * 1024))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413).Produces(503);
        group.MapGet("/text-calls/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiTextCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.GetAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(401).Produces(403).Produces(404);
    }
}
