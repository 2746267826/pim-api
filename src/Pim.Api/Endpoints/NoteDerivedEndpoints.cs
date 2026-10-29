using Microsoft.AspNetCore.Mvc;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteDerivedEndpoints
{
    public static void MapNoteDerivedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tasks = endpoints.MapGroup("/api/v1/native-notes/derived-tasks")
            .RequireAuthorization().WithTags("Native note derived artifacts");
        tasks.MapGet("/limits", (NoteDerivedService service) =>
            Results.Ok(ApiResponse<NoteDerivedLimitsDto>.Ok(service.Limits)))
            .Produces<ApiResponse<NoteDerivedLimitsDto>>().Produces(401);
        tasks.MapGet("/destinations", async (NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedDestinationsDto>.Ok(await service.DestinationsAsync(ct))))
            .Produces<ApiResponse<NoteDerivedDestinationsDto>>().Produces(401).Produces(403);
        tasks.MapGet("/sources", async (NoteDerivedService service, CancellationToken ct,
            int offset = 0, int limit = 20) =>
            Results.Ok(ApiResponse<NoteAsrSourcesDto>.Ok(await service.SourcesAsync(offset, limit, ct))))
            .Produces<ApiResponse<NoteAsrSourcesDto>>().Produces(400).Produces(401).Produces(403);
        tasks.MapPost("/", async (StartNoteDerivedTaskRequest request, NoteDerivedService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedTaskDto>.Ok(await service.StartAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024))
            .Produces<ApiResponse<NoteDerivedTaskDto>>().Produces(400).Produces(401).Produces(403)
            .Produces(404).Produces(409).Produces(413).Produces(503);
        tasks.MapGet("/", async ([FromQuery] Guid? noteId, NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteDerivedTaskDto>>.Ok(await service.ListAsync(noteId, ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteDerivedTaskDto>>>().Produces(401).Produces(403);
        tasks.MapGet("/{taskId:guid}", async (Guid taskId, NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedTaskDto>.Ok(await service.GetAsync(taskId, ct))))
            .Produces<ApiResponse<NoteDerivedTaskDto>>().Produces(401).Produces(403).Produces(404);
        tasks.MapGet("/{taskId:guid}/steps", async (Guid taskId, NoteDerivedService service,
            CancellationToken ct, int offset = 0, int limit = 10) =>
            Results.Ok(ApiResponse<NoteDerivedStepsDto>.Ok(await service.StepsAsync(taskId, offset, limit, ct))))
            .Produces<ApiResponse<NoteDerivedStepsDto>>().Produces(400).Produces(401).Produces(403).Produces(404);
        tasks.MapPost("/{taskId:guid}/stop", async (Guid taskId, NoteDerivedService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedTaskDto>.Ok(await service.StopAsync(taskId, ct))))
            .Produces<ApiResponse<NoteDerivedTaskDto>>().Produces(401).Produces(403).Produces(404);
        tasks.MapPost("/{taskId:guid}/retry", async (Guid taskId, RetryNoteDerivedTaskRequest request,
            NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedTaskDto>.Ok(await service.RetryAsync(taskId, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(128 * 1024))
            .Produces<ApiResponse<NoteDerivedTaskDto>>().Produces(400).Produces(401).Produces(403)
            .Produces(404).Produces(409).Produces(413);

        var artifacts = endpoints.MapGroup("/api/v1/native-notes/derived-artifacts")
            .RequireAuthorization().WithTags("Native note derived artifacts");
        artifacts.MapGet("/", async ([FromQuery] Guid noteId, NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteDerivedArtifactDto>>.Ok(
                await service.ListArtifactsAsync(noteId, ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteDerivedArtifactDto>>>().Produces(400)
            .Produces(401).Produces(403);
        artifacts.MapGet("/{artifactId:guid}", async (Guid artifactId, NoteDerivedService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedArtifactDto>.Ok(await service.GetArtifactAsync(artifactId, ct))))
            .Produces<ApiResponse<NoteDerivedArtifactDto>>().Produces(401).Produces(403).Produces(404);
        artifacts.MapGet("/{artifactId:guid}/versions/{versionId:guid}", async (Guid artifactId, Guid versionId,
            NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedVersionDto>.Ok(
                await service.GetVersionAsync(artifactId, versionId, ct))))
            .Produces<ApiResponse<NoteDerivedVersionDto>>().Produces(401).Produces(403).Produces(404);
        artifacts.MapPost("/{artifactId:guid}/manual-revisions", async (Guid artifactId,
            SaveNoteDerivedManualRevisionRequest request, NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedMutationDto>.Ok(
                await service.SaveManualRevisionAsync(artifactId, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(5 * 1024 * 1024))
            .Produces<ApiResponse<NoteDerivedMutationDto>>().Produces(400).Produces(401).Produces(403)
            .Produces(404).Produces(409).Produces(413);
        artifacts.MapPost("/{artifactId:guid}/select", async (Guid artifactId,
            SelectNoteDerivedVersionRequest request, NoteDerivedService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteDerivedArtifactDto>.Ok(
                await service.SelectAsync(artifactId, request, ct))))
            .Produces<ApiResponse<NoteDerivedArtifactDto>>().Produces(400).Produces(401).Produces(403)
            .Produces(404).Produces(409);
    }
}
