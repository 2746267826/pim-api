using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;

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
            .WithMetadata(new RequestSizeLimitAttribute(AiAsrMaterialService.MaxMimoRequestJsonBytes))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413).Produces(503);
        group.MapPost("/asr-jobs", async (AiAsrCallRequest request, [FromServices] AiAsrJobService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiAsrJobResultDto>.Ok(await service.SubmitAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(AiAsrMaterialService.MaxMimoRequestJsonBytes))
            .Produces<ApiResponse<AiAsrJobResultDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413).Produces(503);
        group.MapGet("/asr-jobs/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiAsrJobService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiAsrJobResultDto>.Ok(await service.GetAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiAsrJobResultDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapDelete("/asr-jobs/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiAsrJobService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiAsrJobResultDto>.Ok(await service.CancelAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiAsrJobResultDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapPost("/asr-transport-observations", async (AiAsrTransportObservationRequest request,
            [FromServices] AiAsrTransportObservationService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiAsrTransportObservationDto>.Ok(await service.ReportAsync(request, ct))))
            .Produces<ApiResponse<AiAsrTransportObservationDto>>(200)
            .Produces(400).Produces(401).Produces(403).Produces(404);
        group.MapPost("/asr-url-calls", async (AiAsrUrlCallRequest request, [FromServices] AiAsrCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiAsrUrlCallResultDto>.Ok(await service.CallUrlAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
            .Produces<ApiResponse<AiAsrUrlCallResultDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413).Produces(503);
        group.MapGet("/asr-calls/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiAsrCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiTextCallResultDto>.Ok(await service.GetAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiTextCallResultDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapGet("/asr-url-calls/{clientRequestId:guid}", async (Guid clientRequestId, [FromServices] AiAsrCallService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiAsrUrlCallResultDto>.Ok(await service.GetUrlAsync(clientRequestId, ct))))
            .Produces<ApiResponse<AiAsrUrlCallResultDto>>(200).Produces(401).Produces(403).Produces(404);
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
        group.MapGet("/connections", async ([FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<AiProviderConnectionDto>>.Ok(await service.ListConnectionsAsync(ct))))
            .Produces<ApiResponse<IReadOnlyList<AiProviderConnectionDto>>>(200).Produces(401).Produces(403);
        group.MapPost("/connections", async (SaveAiProviderConnectionRequest request,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiProviderConnectionDto>.Ok(await service.CreateConnectionAsync(request.Name, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .Produces<ApiResponse<AiProviderConnectionDto>>(200).Produces(400).Produces(401).Produces(403).Produces(409).Produces(413);
        group.MapPut("/connections/{id:guid}", async (Guid id, SaveAiProviderConnectionRequest request,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiProviderConnectionDto>.Ok(await service.UpdateConnectionAsync(id, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .Produces<ApiResponse<AiProviderConnectionDto>>(200).Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);
        group.MapGet("/connections/{id:guid}/usage-limit", async (Guid id,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiUsageLimitRuleDto>.Ok(await service.GetRuleAsync(id, ct))))
            .Produces<ApiResponse<AiUsageLimitRuleDto>>(200).Produces(401).Produces(403).Produces(404);
        group.MapPut("/connections/{id:guid}/usage-limit", async (Guid id, SaveAiUsageLimitRuleRequest request,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiUsageLimitRuleDto>.Ok(await service.SaveRuleAsync(id, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .Produces<ApiResponse<AiUsageLimitRuleDto>>(200).Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);
        group.MapGet("/connections/{id:guid}/usage", async (Guid id, string? period, string? cycleKey,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiUsageLimitUsageDto>.Ok(await service.GetUsageAsync(id, period, cycleKey, ct))))
            .Produces<ApiResponse<AiUsageLimitUsageDto>>(200).Produces(400).Produces(401).Produces(403).Produces(404);
        group.MapGet("/usage-reminders", async (Guid? connectionId, bool? unreadOnly, int? limit,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<AiUsageReminderDto>>.Ok(
                await service.ListRemindersAsync(connectionId, unreadOnly ?? false, limit ?? 50, ct))))
            .Produces<ApiResponse<IReadOnlyList<AiUsageReminderDto>>>(200).Produces(400).Produces(401).Produces(403);
        group.MapPost("/usage-reminders/{eventId:guid}/read", async (Guid eventId,
            [FromServices] AiUsageLimitService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<bool>.Ok(await service.MarkReminderReadAsync(eventId, ct))))
            .Produces<ApiResponse<bool>>(200).Produces(401).Produces(403).Produces(404);
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
