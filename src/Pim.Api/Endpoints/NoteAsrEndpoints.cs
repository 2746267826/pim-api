using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteAsrEndpoints
{
    public static void MapNoteAsrEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Matches current model/call permissions. Normal-account product integration remains a separate slice.
        var group = endpoints.MapGroup("/api/v1/native-notes/asr-tasks")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" }).WithTags("Native note ASR");
        group.MapPost("/", async (StartNoteAsrRequest request, [FromServices] NoteAsrService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrTaskDto>.Ok(await service.StartAsync(request, ct))))
            .Produces<ApiResponse<NoteAsrTaskDto>>().Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);
        group.MapGet("/", async ([FromServices] NoteAsrService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteAsrTaskDto>>.Ok(await service.ListAsync(ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteAsrTaskDto>>>().Produces(401).Produces(403);
        group.MapGet("/sources", async ([FromServices] NoteAsrService service, CancellationToken ct, int offset = 0, int limit = 20) =>
            Results.Ok(ApiResponse<NoteAsrSourcesDto>.Ok(await service.SourcesAsync(offset, limit, ct))))
            .Produces<ApiResponse<NoteAsrSourcesDto>>().Produces(400).Produces(401).Produces(403);
        group.MapGet("/{taskId:guid}", async (Guid taskId, [FromServices] NoteAsrService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrTaskDto>.Ok(await service.GetAsync(taskId, ct))))
            .Produces<ApiResponse<NoteAsrTaskDto>>().Produces(401).Produces(403).Produces(404);
        group.MapGet("/{taskId:guid}/segments", async (Guid taskId, [FromServices] NoteAsrService service,
            CancellationToken ct, int offset = 0, int limit = 10) =>
            Results.Ok(ApiResponse<NoteAsrSegmentsDto>.Ok(await service.SegmentsAsync(taskId, offset, limit, ct))))
            .Produces<ApiResponse<NoteAsrSegmentsDto>>().Produces(400).Produces(401).Produces(403).Produces(404);
        group.MapPost("/{taskId:guid}/stop", async (Guid taskId, [FromServices] NoteAsrService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrTaskDto>.Ok(await service.StopAsync(taskId, ct))))
            .Produces<ApiResponse<NoteAsrTaskDto>>().Produces(401).Produces(403).Produces(404);
        group.MapPost("/{taskId:guid}/retry", async (Guid taskId, RetryNoteAsrRequest request,
            [FromServices] NoteAsrService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrTaskDto>.Ok(await service.RetryAsync(taskId, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(128 * 1024))
            .Produces<ApiResponse<NoteAsrTaskDto>>().Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);
    }
}
