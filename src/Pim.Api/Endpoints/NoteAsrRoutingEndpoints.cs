using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.SwaggerGen;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteAsrRoutingEndpoints
{
    public static void MapNoteAsrRoutingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/native-notes/asr-routing")
            .RequireAuthorization().WithTags("Native note ASR routing");
        var nodes = group.MapGroup("/nodes").WithTags("Native note ASR nodes");
        nodes.MapPost("/register", async (RegisterNoteAsrNodeRequest request, [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrNodeDto>.Ok(await service.RegisterNodeAsync(request, ct))))
            .Produces<ApiResponse<NoteAsrNodeDto>>().Produces(400).Produces(401).Produces(403).Produces(404);
        nodes.MapPost("/{nodeId:guid}/heartbeat", async (Guid nodeId, NoteAsrNodeHeartbeatRequest request,
            [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrNodeDto>.Ok(await service.HeartbeatAsync(nodeId, request, ct))))
            .Produces<ApiResponse<NoteAsrNodeDto>>().Produces(400).Produces(401).Produces(403).Produces(404);
        nodes.MapGet("/", async ([FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteAsrNodeDto>>.Ok(await service.ListNodesAsync(ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteAsrNodeDto>>>().Produces(401).Produces(403);
        nodes.MapPost("/{nodeId:guid}/withdraw", async (Guid nodeId,
            [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrNodeDto>.Ok(await service.WithdrawNodeAsync(nodeId, ct))))
            .Produces<ApiResponse<NoteAsrNodeDto>>().Produces(401).Produces(403).Produces(404);
        var attempts = group.MapGroup("/attempts").WithTags("Native note ASR attempts");
        attempts.MapPost("/", async (CreateNoteAsrAttemptRequest request, [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrAttemptDetailDto>.Ok(await service.CreateAttemptAsync(request.TaskId,
                request.VersionId, request.ExpectedSegments, request.Language, request.Route, ct))))
            .Produces<ApiResponse<NoteAsrAttemptDetailDto>>().Produces(400).Produces(401).Produces(403).Produces(404).Produces(409);
        attempts.MapGet("/", async ([FromServices] NoteAsrRoutingService service, CancellationToken ct, Guid? taskId = null) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteAsrAttemptDetailDto>>.Ok(
                taskId is { } id ? await service.ListAttemptsAsync(id, ct) : await service.ListRecentAttemptsAsync(ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteAsrAttemptDetailDto>>>().Produces(401).Produces(403);
        attempts.MapGet("/{attemptId:guid}", async (Guid attemptId, [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrAttemptDetailDto>.Ok(await service.GetAttemptAsync(attemptId, ct))))
            .Produces<ApiResponse<NoteAsrAttemptDetailDto>>().Produces(401).Produces(403).Produces(404);
        attempts.MapPost("/{attemptId:guid}/stop", async (Guid attemptId,
            [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrAttemptDetailDto>.Ok(await service.StopAttemptAsync(attemptId, ct))))
            .Produces<ApiResponse<NoteAsrAttemptDetailDto>>().Produces(401).Produces(403).Produces(404).Produces(409);
        group.MapGet("/config", async ([FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrRouteConfigDto>.Ok(await service.GetConfigAsync(ct))))
            .Produces<ApiResponse<NoteAsrRouteConfigDto>>().Produces(401).Produces(403);
        group.MapPut("/config", async (UpdateNoteAsrRouteConfigRequest request, [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrRouteConfigDto>.Ok(await service.UpdateConfigAsync(request, ct))))
            .Produces<ApiResponse<NoteAsrRouteConfigDto>>().Produces(400).Produces(401).Produces(403).Produces(409);
        var leases = group.MapGroup("/leases").WithTags("Native note ASR leases");
        leases.MapPost("/claim", async (ClaimNoteAsrTaskRequest request, HttpRequest http,
            [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrClaimDto>.Ok(Bind(await service.ClaimAsync(request, ct), http))))
            .Produces<ApiResponse<NoteAsrClaimDto>>().Produces(400).Produces(401).Produces(403).Produces(404);
        leases.MapPost("/{leaseId:guid}/renew", async (Guid leaseId, NoteAsrLeaseHeartbeatRequest request, HttpRequest http,
            [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrLeaseDto>.Ok(Bind(await service.RenewLeaseAsync(leaseId, request, ct), http))))
            .Produces<ApiResponse<NoteAsrLeaseDto>>().Produces(400).Produces(401).Produces(403).Produces(404).Produces(409);
        leases.MapGet("/{leaseId:guid}/segments/{segmentIndex:int}/content", async (Guid leaseId, int segmentIndex,
            [FromHeader(Name = "X-Pim-Lease-Token")] Guid leaseToken,
            [FromHeader(Name = "X-Pim-Fencing-Token")] long fencingToken,
            HttpResponse response, [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
        {
            var content = await service.ReadSegmentInputAsync(leaseId, segmentIndex, leaseToken, fencingToken, ct);
            response.Headers.CacheControl = "private, no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Content-SHA256"] = content.Sha256;
            response.Headers["X-Pim-Source-SHA256"] = content.SourceSha256;
            response.Headers["X-Pim-Owner-Id"] = content.OwnerId.ToString("D");
            response.Headers["X-Pim-Node-Id"] = content.NodeId.ToString("D");
            response.Headers["X-Pim-Lease-Id"] = content.LeaseId.ToString("D");
            response.Headers["X-Pim-Attempt-Id"] = content.AttemptId.ToString("D");
            response.Headers["X-Pim-Version-Id"] = content.VersionId.ToString("D");
            response.Headers["X-Pim-Fencing-Token"] = content.FencingToken.ToString();
            response.Headers["X-Pim-Segment-Index"] = content.SegmentIndex.ToString();
            response.Headers["X-Pim-Source-File-Index"] = content.SourceFileIndex.ToString();
            response.Headers["X-Pim-Source-Offset-Samples"] = content.SourceOffsetSamples.ToString();
            response.Headers["X-Pim-Samples"] = content.Samples.ToString();
            return Results.File(content.Bytes, content.ContentType);
        }).Produces(200, contentType: "audio/wav").Produces(400).Produces(401).Produces(403).Produces(404).Produces(409);
        leases.MapPost("/{attemptId:guid}/ack", async (Guid attemptId, NoteAsrAckRequest request,
            [FromServices] NoteAsrRoutingService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrAckDto>.Ok(await service.AcknowledgeAsync(attemptId, request, ct))))
            .Produces<ApiResponse<NoteAsrAckDto>>().Produces(400).Produces(401).Produces(403).Produces(404).Produces(409);
        var batches = group.MapGroup("/batches").WithTags("Native note ASR batches");
        batches.MapPost("/", async ([FromForm] StartNoteAsrBatchForm form,
            [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
        {
            var request = new StartNoteAsrBatchRequest(form.TaskId, form.AttemptId, form.ModelId, form.ConsentRef,
                form.BatchScopeConfirmed,
                string.IsNullOrWhiteSpace(form.CompletionWindow) ? "24h" : form.CompletionWindow);
            await using var stream = form.File.OpenReadStream();
            return Results.Ok(ApiResponse<NoteAsrBatchJobDto>.Ok(await service.StartAsync(request, stream,
                form.File.Length, form.File.FileName, ct)));
        }).Accepts<StartNoteAsrBatchForm>("multipart/form-data")
          .WithMetadata(new RequestSizeLimitAttribute(132L * 1024 * 1024))
          .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 132L * 1024 * 1024 })
          .Produces<ApiResponse<NoteAsrBatchJobDto>>().Produces(400).Produces(401).Produces(403)
          .Produces(409).Produces(413).DisableAntiforgery();
        batches.MapGet("/", async ([FromServices] NoteAsrBatchService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteAsrBatchJobDto>>.Ok(await service.ListAsync(ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteAsrBatchJobDto>>>().Produces(401).Produces(403);
        batches.MapGet("/{batchJobId:guid}", async (Guid batchJobId, [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrBatchJobDto>.Ok(await service.GetAsync(batchJobId, ct))))
            .Produces<ApiResponse<NoteAsrBatchJobDto>>().Produces(401).Produces(403).Produces(404);
        batches.MapPost("/{batchJobId:guid}/poll", async (Guid batchJobId,
            [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrBatchJobDto>.Ok(await service.PollAsync(batchJobId, ct))))
            .Produces<ApiResponse<NoteAsrBatchJobDto>>().Produces(401).Produces(403).Produces(404).Produces(409);
        batches.MapPost("/{batchJobId:guid}/cancel", async (Guid batchJobId,
            [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteAsrBatchJobDto>.Ok(await service.CancelAsync(batchJobId, ct))))
            .Produces<ApiResponse<NoteAsrBatchJobDto>>().Produces(401).Produces(403).Produces(404).Produces(409);
        batches.MapDelete("/{batchJobId:guid}", async (Guid batchJobId,
            [FromServices] NoteAsrBatchService service, CancellationToken ct) =>
        {
            await service.DeleteTerminalAsync(batchJobId, ct);
            return Results.NoContent();
        }).Produces(204).Produces(401).Produces(403).Produces(404).Produces(409);
    }

    private static NoteAsrClaimDto Bind(NoteAsrClaimDto claim, HttpRequest http)
    {
        var origin = Origin(http);
        return claim with
        {
            Lease = claim.Lease is null ? null : claim.Lease with { ServerOrigin = origin },
            Execution = claim.Execution is null ? null : claim.Execution with { ServerOrigin = origin }
        };
    }

    private static NoteAsrLeaseDto Bind(NoteAsrLeaseDto lease, HttpRequest http) =>
        lease with { ServerOrigin = Origin(http) };

    private static string Origin(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
}

public sealed class StartNoteAsrBatchForm
{
    public Guid TaskId { get; set; }
    public Guid? AttemptId { get; set; }
    public Guid ModelId { get; set; }
    public string ConsentRef { get; set; } = "";
    public bool BatchScopeConfirmed { get; set; }
    public string? CompletionWindow { get; set; }
    public IFormFile File { get; set; } = default!;
}
