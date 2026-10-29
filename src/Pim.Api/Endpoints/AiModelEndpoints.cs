using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;

namespace Pim.Api.Endpoints;

public static class AiModelEndpoints
{
    public static void MapAiModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/ai")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" })
            .RequireCurrentActiveAdmin();
        group.MapGet("/asr-presets", ([FromServices] AiAsrPresetRegistry presets) =>
            Results.Ok(ApiResponse<IReadOnlyList<AiAsrPresetDto>>.Ok(presets.List())))
            .Produces<ApiResponse<IReadOnlyList<AiAsrPresetDto>>>(200).Produces(401).Produces(403);
        group.MapPost("/asr-calls", async (AiAsrCallRequest request, [FromServices] AiAsrCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.CallAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(7 * 1024 * 1024))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413).Produces(503);
        group.MapGet("/asr-calls/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiAsrCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.GetAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapGet("/requests/{logId:guid}/audio", async (Guid logId, [FromServices] AiAsrMaterialService audio, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiCallAudioDto?>.Ok(await audio.DescribeAsync(logId, ct))))
            .Produces<ApiResponse<AiCallAudioDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapGet("/requests/{logId:guid}/audio/content", async (Guid logId, HttpResponse response,
            [FromServices] AiAsrMaterialService audio, CancellationToken ct) =>
        {
            var content = await audio.ReadAsync(logId, ct);
            response.Headers.CacheControl = "private, no-store"; response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content.Bytes, content.ContentType);
        }).Produces(200, contentType: "audio/wav", additionalContentTypes: ["audio/mpeg"]).Produces(401).Produces(403).Produces(404).Produces(409);
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
        group.MapPost("/vision-calls", async (AiVisionCallRequest request, [FromServices] AiTextCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.CallVisionAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(13 * 1024 * 1024))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413).Produces(503);
        group.MapGet("/vision-calls/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiTextCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.GetAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapGet("/requests/{logId:guid}/images", async (Guid logId, [FromServices] AiVisionMaterialService images, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<AiCallImageDto>>.Ok(await images.ListAsync(logId, ct))))
            .Produces<ApiResponse<IReadOnlyList<AiCallImageDto>>>(200).Produces(401).Produces(403).Produces(404);
        group.MapGet("/requests/{logId:guid}/images/{imageId:guid}", async (Guid logId, Guid imageId, HttpResponse response,
            [FromServices] AiVisionMaterialService images, CancellationToken ct) =>
        {
            // ReadAsync verifies current-user ownership before accessing the original file provider.
            var content = await images.ReadAsync(logId, imageId, ct);
            response.Headers.CacheControl = "private, no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content.Bytes, content.ContentType);
        }).Produces(200, contentType: "image/png", additionalContentTypes: ["image/jpeg"]).Produces(401).Produces(403).Produces(404).Produces(409);
    }
}
