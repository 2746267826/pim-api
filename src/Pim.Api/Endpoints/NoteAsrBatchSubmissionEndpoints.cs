using Microsoft.AspNetCore.Mvc;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteAsrBatchSubmissionEndpoints
{
    public static void MapNoteAsrBatchSubmissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/native-notes/asr-tasks")
            .RequireAuthorization().WithTags("Native note ASR");

        group.MapPost("/{sourceTaskId:guid}/batch-from-task",
            async (Guid sourceTaskId, StartNoteAsrBatchFromTaskRequest request,
                [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
            {
                if (request.SourceTaskId != sourceTaskId)
                    throw new DomainException(40001, "路径与请求体中的原任务标识不一致");
                return Results.Ok(ApiResponse<NoteAsrBatchSubmissionResultDto>.Ok(
                    await service.StartFromTaskAsync(request, ct)));
            })
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
            .Produces<ApiResponse<NoteAsrBatchSubmissionResultDto>>()
            .Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);

        group.MapPost("/{sourceJobId:guid}/batch-retry-failed",
            async (Guid sourceJobId, RetryFailedNoteAsrBatchRequest request,
                [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
            {
                if (request.SourceJobId != sourceJobId)
                    throw new DomainException(40001, "路径与请求体中的原 Batch 标识不一致");
                return Results.Ok(ApiResponse<NoteAsrBatchSubmissionResultDto>.Ok(
                    await service.RetryFailedItemsAsync(request, ct)));
            })
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
            .Produces<ApiResponse<NoteAsrBatchSubmissionResultDto>>()
            .Produces(400).Produces(401).Produces(403).Produces(404).Produces(409).Produces(413);
    }
}
