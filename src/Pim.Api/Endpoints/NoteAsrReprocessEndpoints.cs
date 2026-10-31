using Microsoft.AspNetCore.Mvc;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteAsrReprocessEndpoints
{
    public static void MapNoteAsrReprocessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/native-notes/asr-tasks")
            .RequireAuthorization().WithTags("Native note ASR");

        group.MapPost("/{sourceTaskId:guid}/reprocess",
            async (Guid sourceTaskId, ReprocessNoteAsrRequest request,
                [FromServices] NoteAsrReprocessService service, CancellationToken ct) =>
                Results.Ok(ApiResponse<NoteAsrReprocessResultDto>.Ok(
                    await service.ReprocessAsync(sourceTaskId, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .Produces<ApiResponse<NoteAsrReprocessResultDto>>()
            .Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);

        group.MapGet("/{sourceTaskId:guid}/reprocess-history",
            async (Guid sourceTaskId, [FromServices] NoteAsrReprocessService service,
                CancellationToken ct, Guid expectedUserId, int offset = 0, int limit = 20) =>
                Results.Ok(ApiResponse<NoteAsrReprocessHistoryDto>.Ok(
                    await service.HistoryAsync(sourceTaskId, expectedUserId, offset, limit, ct))))
            .Produces<ApiResponse<NoteAsrReprocessHistoryDto>>()
            .Produces(400).Produces(401).Produces(403).Produces(404);
    }
}
