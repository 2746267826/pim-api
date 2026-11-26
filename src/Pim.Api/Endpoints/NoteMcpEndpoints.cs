using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Security.Cryptography;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Notes;
using Pim.Module.NoteTaskSuggestions.DTOs;
using Pim.Module.NoteTaskSuggestions.Services;

namespace Pim.Api.Endpoints;

/// <summary>
/// REST surface for the native-note MCP tools. Every route is owner-scoped through the
/// MCP-verified JWT and only exposes the current account's notes; the MCP write-token scope
/// additionally binds each tool to exactly one of these mutations.
/// </summary>
public static class NoteMcpEndpoints
{
    private const string GroupRoute = "/api/v1/native-notes/mcp";
    private const long MaxPackageRequestBytes = 96L * 1024 * 1024;

    public static IEndpointRouteBuilder MapNoteMcpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(GroupRoute).RequireAuthorization()
            .WithTags("Native notes MCP");

        group.MapGet("/notes", async (string? status, int? offset, int? limit,
            NativeNoteMcpService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpPageDto>.Ok(
                await service.ListAsync(status, offset ?? 0, limit ?? 20, ct))))
            .Produces<ApiResponse<NativeNoteMcpPageDto>>().Produces(400).Produces(401);

        group.MapGet("/notes/{noteId:guid}", async (Guid noteId, bool? includeResults,
            int? versionLimit, NativeNoteMcpService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpDetailDto>.Ok(
                await service.GetAsync(noteId, includeResults ?? true, versionLimit ?? 20, ct))))
            .Produces<ApiResponse<NativeNoteMcpDetailDto>>().Produces(400).Produces(401)
            .Produces<ApiResponse<string>>(404);

        group.MapGet("/notes/{noteId:guid}/versions", async (Guid noteId, int? limit,
            NativeNoteMcpService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpVersionsDto>.Ok(
                await service.GetVersionsAsync(noteId, limit ?? 50, ct))))
            .Produces<ApiResponse<NativeNoteMcpVersionsDto>>().Produces(400).Produces(401)
            .Produces<ApiResponse<string>>(404);

        group.MapGet("/notes/{noteId:guid}/results", async (Guid noteId,
            NativeNoteMcpService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpResultsDto>.Ok(
                await service.GetResultsAsync(noteId, ct))))
            .Produces<ApiResponse<NativeNoteMcpResultsDto>>().Produces(401)
            .Produces<ApiResponse<string>>(404);

        group.MapGet("/notes/{noteId:guid}/processing", async (Guid noteId, Guid? versionId,
            NativeNoteMcpService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>.Ok(
                await service.GetProcessingAsync(noteId, versionId, ct))))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>>()
            .Produces(401).Produces<ApiResponse<string>>(404);

        group.MapGet("/notes/{noteId:guid}/suggestions", async (Guid noteId,
            NoteTaskSuggestionStatusDto? status, NativeNoteMcpService service,
            NoteTaskSuggestionService suggestions, CancellationToken ct) =>
        {
            var snapshot = await service.GetVersionsAsync(noteId, 1, ct);
            var list = await suggestions.ListAsync(noteId, status, ct);
            return Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionListDto>>.Ok(
                new NativeNoteMcpOperationResultDto<NoteTaskSuggestionListDto>(
                    service.ReadReceipt("get_native_note_suggestions", noteId, snapshot.Revision,
                        "已读取笔记任务与日程建议"),
                    list)));
        })
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionListDto>>>()
            .Produces(400).Produces(401).Produces<ApiResponse<string>>(404);

        group.MapGet("/notes/{noteId:guid}/package", async (Guid noteId, Guid? versionId,
            HttpContext context, NativeNoteMcpService service, CancellationToken ct) =>
        {
            var resolved = await service.ResolvePackageAsync(noteId, versionId, ct);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["X-PIM-Note-Id"] = resolved.NoteId.ToString("D");
            context.Response.Headers["X-PIM-Version-Id"] = resolved.VersionId.ToString("D");
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"pim-native-note-{resolved.NoteId:D}-{Guid.NewGuid():N}.zip");
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan |
                          FileOptions.DeleteOnClose,
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            var stream = new FileStream(tempPath, streamOptions);
            try
            {
                // ZipArchive writes its central directory synchronously when disposed. Build the
                // ZIP on a seekable temp file first so this works in in-process MCP requests
                // where IHttpBodyControlFeature is unavailable, then stream the completed file.
                await service.WritePackageAsync(noteId, resolved.VersionId, stream, ct);
                await stream.FlushAsync(ct);
                var packageSha256 = await HashStreamAsync(stream, ct);
                context.Response.Headers["X-PIM-Package-Sha256"] = packageSha256;
                context.Response.Headers["X-PIM-Package-Byte-Length"] =
                    stream.Length.ToString(CultureInfo.InvariantCulture);
                stream.Position = 0;
                return Results.File(stream, "application/zip",
                    $"{noteId:D}.pimnote.zip", enableRangeProcessing: true);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        })
            .Produces<Stream>(200, "application/zip").Produces(401)
            .Produces<ApiResponse<string>>(404).Produces<ApiResponse<string>>(409);

        group.MapPost("/notes/{noteId:guid}/package", async (Guid noteId,
            NativeNoteMcpPackageRequest request, NativeNoteMcpService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpPackageUploadDto>>.Ok(
                await service.UploadPackageAsync(noteId, request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(MaxPackageRequestBytes))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpPackageUploadDto>>>()
            .Produces(400).Produces(401).Produces(409).Produces(413);

        group.MapPost("/notes/{noteId:guid}/promote", async (Guid noteId,
            NativeNoteMcpPromoteRequest request, NativeNoteMcpService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpVersionMutationDto>>.Ok(
                await service.PromoteAsync(noteId, request, ct))))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpVersionMutationDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409);

        group.MapPost("/notes/{noteId:guid}/cancel-upload", async (Guid noteId,
            NativeNoteMcpCancelUploadRequest request, NativeNoteMcpService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpCancelUploadDto>>.Ok(
                await service.CancelUploadAsync(noteId, request, ct))))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpCancelUploadDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409);

        foreach (var action in new[] { "archive", "restore", "delete" })
        {
            group.MapPost($"/notes/{{noteId:guid}}/lifecycle/{action}", async (Guid noteId,
                NativeNoteMcpLifecycleRequest request, NativeNoteMcpService service,
                CancellationToken ct) =>
                Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpLifecycleDto>>.Ok(
                    await service.LifecycleAsync(noteId, action, request, ct))))
                .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpLifecycleDto>>>()
                .Produces(400).Produces(401).Produces(404).Produces(409);
        }

        group.MapPost("/notes/{noteId:guid}/processing/start", async (Guid noteId,
            NativeNoteMcpProcessingStartRequest request, NativeNoteMcpService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>.Ok(
                await service.StartProcessingAsync(noteId, request, ct))))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpProcessingDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409).Produces(503);

        group.MapPost("/notes/{noteId:guid}/processing/retry", async (Guid noteId,
            NativeNoteMcpProcessingRetryRequest request, NativeNoteMcpService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpRetryDto>>.Ok(
                await service.RetryProcessingAsync(noteId, request, ct))))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpRetryDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409);

        group.MapPost("/notes/{noteId:guid}/results/{artifactId:guid}/adopt", async (
            Guid noteId, Guid artifactId, NativeNoteMcpAdoptRequest request,
            NativeNoteMcpService service, CancellationToken ct) =>
            Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpAdoptDto>>.Ok(
                await service.AdoptResultAsync(noteId, artifactId, request, ct))))
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NativeNoteMcpAdoptDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409);

        group.MapPost("/notes/{noteId:guid}/suggestions/extractions", async (Guid noteId,
            NativeNoteMcpSuggestionExtractionRequest request, ICurrentUserService user,
            NativeNoteMcpService service, NoteTaskSuggestionService suggestions,
            NoteMcpOperationStore operations, CancellationToken ct) =>
        {
            var owner = UserId(user);
            var result = await operations.RunAsync(
                owner,
                request.RequestId,
                "extract_native_note_suggestions",
                new
                {
                    noteId,
                    request.ArtifactId,
                    request.VersionId,
                    request.ExpectedContentSha256,
                    request.AcknowledgePossibleCharge,
                    Consent = request.Consent,
                },
                async lockedCt =>
                {
                    var note = await service.GetAsync(noteId, includeResults: false,
                        versionLimit: 1, ct: lockedCt);
                    if (note.Status != "Active")
                        throw new DomainException(40901, "笔记不在可用状态，请先恢复");
                    if (note.CurrentVersionId is null)
                        throw new DomainException(40901, "当前笔记没有完整版本，不能提取建议");
                    var extraction = await suggestions.StartExtractionAsync(
                        new StartNoteTaskSuggestionExtractionRequest(
                            request.RequestId,
                            owner,
                            noteId,
                            request.ArtifactId,
                            request.VersionId,
                            request.ExpectedContentSha256,
                            request.Consent,
                            request.AcknowledgePossibleCharge),
                        lockedCt);
                    return new NativeNoteMcpOperationResultDto<NoteTaskSuggestionExtractionDto>(
                        new NativeNoteMcpReceiptDto(
                            request.RequestId,
                            "extract_native_note_suggestions",
                            noteId,
                            note.Revision,
                            "applied",
                            false,
                            DateTimeOffset.UtcNow,
                            "建议提取已按固定结果版本入队；完成后可读取并采用任务或日程建议"),
                        extraction);
                },
                ct);
            return Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionExtractionDto>>.Ok(result));
        })
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionExtractionDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409).Produces(503);

        group.MapPost("/notes/{noteId:guid}/suggestions/{suggestionId:guid}/confirm", async (
            Guid noteId, Guid suggestionId, NativeNoteMcpSuggestionConfirmRequest request,
            ICurrentUserService user, NativeNoteMcpService service,
            NoteTaskSuggestionService suggestions, NoteMcpOperationStore operations,
            CancellationToken ct) =>
        {
            var owner = UserId(user);
            var result = await operations.RunAsync(
                owner,
                request.RequestId,
                "confirm_native_note_suggestion",
                new { noteId, suggestionId, request.ExpectedRevision },
                async lockedCt =>
                {
                    var note = await service.GetAsync(noteId, includeResults: false,
                        versionLimit: 1, ct: lockedCt);
                    if (note.Status != "Active")
                        throw new DomainException(40901, "笔记不在可用状态，请先恢复");
                    var existing = await suggestions.GetAsync(suggestionId, lockedCt);
                    if (existing.NoteId != noteId)
                        throw new DomainException(40401, "建议不存在或不属于当前笔记");
                    var confirmed = await suggestions.ConfirmAsync(suggestionId,
                        new ConfirmNoteTaskSuggestionRequest(request.RequestId, request.ExpectedRevision),
                        lockedCt);
                    return new NativeNoteMcpOperationResultDto<NoteTaskSuggestionDto>(
                        new NativeNoteMcpReceiptDto(
                            request.RequestId,
                            "confirm_native_note_suggestion",
                            noteId,
                            note.Revision,
                            "applied",
                            false,
                            DateTimeOffset.UtcNow,
                            "建议已采用并生成对应的任务或日程；相同 requestId 会返回同一结果"),
                        confirmed);
                },
                ct);
            return Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionDto>>.Ok(result));
        })
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409);

        group.MapPost("/notes/{noteId:guid}/suggestions/{suggestionId:guid}/dismiss", async (
            Guid noteId, Guid suggestionId, NativeNoteMcpSuggestionDismissRequest request,
            ICurrentUserService user, NativeNoteMcpService service,
            NoteTaskSuggestionService suggestions, NoteMcpOperationStore operations,
            CancellationToken ct) =>
        {
            var owner = UserId(user);
            var result = await operations.RunAsync(
                owner,
                request.RequestId,
                "dismiss_native_note_suggestion",
                new { noteId, suggestionId, request.ExpectedRevision },
                async lockedCt =>
                {
                    var note = await service.GetAsync(noteId, includeResults: false,
                        versionLimit: 1, ct: lockedCt);
                    if (note.Status != "Active")
                        throw new DomainException(40901, "笔记不在可用状态，请先恢复");
                    var existing = await suggestions.GetAsync(suggestionId, lockedCt);
                    if (existing.NoteId != noteId)
                        throw new DomainException(40401, "建议不存在或不属于当前笔记");
                    var dismissed = await suggestions.DismissAsync(suggestionId,
                        new DismissNoteTaskSuggestionRequest(request.ExpectedRevision), lockedCt);
                    return new NativeNoteMcpOperationResultDto<NoteTaskSuggestionDto>(
                        new NativeNoteMcpReceiptDto(
                            request.RequestId,
                            "dismiss_native_note_suggestion",
                            noteId,
                            note.Revision,
                            "applied",
                            false,
                            DateTimeOffset.UtcNow,
                            "建议已忽略；相同 requestId 会返回同一结果"),
                        dismissed);
                },
                ct);
            return Results.Ok(ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionDto>>.Ok(result));
        })
            .Produces<ApiResponse<NativeNoteMcpOperationResultDto<NoteTaskSuggestionDto>>>()
            .Produces(400).Produces(401).Produces(404).Produces(409);

        return endpoints;
    }

    private static Guid UserId(ICurrentUserService user)
        => user.UserId ?? throw new DomainException(40101, "请先登录");

    private static async Task<string> HashStreamAsync(FileStream stream, CancellationToken ct)
    {
        var position = stream.Position;
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        stream.Position = position;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
