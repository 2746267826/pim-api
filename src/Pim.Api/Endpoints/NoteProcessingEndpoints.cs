using Microsoft.AspNetCore.Mvc;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteProcessingEndpoints
{
    public static void MapNoteProcessingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/native-notes/processing")
            .RequireAuthorization().WithTags("Native note processing");
        group.MapPost("/start", async (StartNoteProcessingRequest request,
            [FromServices] NoteProcessingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteProcessingStatusDto>.Ok(
                await service.StartAsync(request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .Produces<ApiResponse<NoteProcessingStatusDto>>().Produces(400).Produces(401)
            .Produces(403).Produces(404).Produces(409).Produces(413).Produces(503);
        group.MapGet("/", async ([FromQuery] Guid noteId, [FromQuery] Guid versionId,
            [FromServices] NoteProcessingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteProcessingStatusDto>.Ok(
                await service.GetAsync(noteId, versionId, ct))))
            .Produces<ApiResponse<NoteProcessingStatusDto>>().Produces(400).Produces(401)
            .Produces(403).Produces(404);
        group.MapGet("/{processingId:guid}", async (Guid processingId,
            [FromServices] NoteProcessingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteProcessingStatusDto>.Ok(
                await service.GetByProcessingIdAsync(processingId, ct))))
            .Produces<ApiResponse<NoteProcessingStatusDto>>().Produces(400).Produces(401)
            .Produces(403).Produces(404);
    }
}
