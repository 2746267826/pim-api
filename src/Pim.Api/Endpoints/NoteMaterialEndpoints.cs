using Microsoft.AspNetCore.Mvc;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Notes;

namespace Pim.Api.Endpoints;

public static class NoteMaterialEndpoints
{
    public static IEndpointRouteBuilder MapNoteMaterialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/native-notes").RequireAuthorization().WithTags("Native notes");
        group.MapGet("/upload-limits", (NoteMaterialUploadStore store) => Results.Ok(ApiResponse<NoteMaterialUploadLimitsDto>.Ok(store.Limits)))
            .Produces<ApiResponse<NoteMaterialUploadLimitsDto>>().Produces(401);
        group.MapPost("/versions", async (BeginNoteMaterialUploadRequest request, NoteMaterialUploadStore store,
            ICurrentUserService user, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteMaterialUploadDto>.Ok(await store.BeginAsync(UserId(user), request, ct))))
            .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024))
            .Produces<ApiResponse<NoteMaterialUploadDto>>().Produces(401)
            .Produces<ApiResponse<string>>(400).Produces<ApiResponse<string>>(409).Produces<ApiResponse<string>>(413);
        group.MapGet("/versions/{versionId:guid}", async (Guid versionId, NoteMaterialUploadStore store,
            ICurrentUserService user, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteMaterialVersionDto>.Ok(await store.GetAsync(UserId(user), versionId, ct))))
            .Produces<ApiResponse<NoteMaterialVersionDto>>().Produces(401).Produces<ApiResponse<string>>(404);
        group.MapGet("/{noteId:guid}/versions", async (Guid noteId, NoteMaterialUploadStore store,
            ICurrentUserService user, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NoteMaterialUploadDto>>.Ok(await store.ListAsync(UserId(user), noteId, ct))))
            .Produces<ApiResponse<IReadOnlyList<NoteMaterialUploadDto>>>().Produces(401);
        group.MapPut("/versions/{versionId:guid}/files/{fileIndex:int}/parts/{partIndex:int}", async (
            Guid versionId, int fileIndex, int partIndex, [FromQuery] string sha256, HttpRequest request,
            NoteMaterialUploadStore store, ICurrentUserService user, CancellationToken ct) =>
        {
            await store.UploadPartAsync(UserId(user), versionId, fileIndex, partIndex, sha256, request.Body, ct);
            return Results.Ok(ApiResponse<string>.Ok("分块已保存"));
        }).Accepts<Stream>("application/octet-stream")
            .WithMetadata(new RequestSizeLimitAttribute(NoteMaterialUploadOptions.PartSizeBytes))
            .Produces<ApiResponse<string>>().Produces(401).Produces<ApiResponse<string>>(400)
            .Produces<ApiResponse<string>>(404).Produces<ApiResponse<string>>(409).Produces<ApiResponse<string>>(413);
        group.MapPost("/versions/{versionId:guid}/complete", async (Guid versionId, NoteMaterialUploadStore store,
            ICurrentUserService user, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteMaterialUploadDto>.Ok(await store.CompleteAsync(UserId(user), versionId, ct))))
            .Produces<ApiResponse<NoteMaterialUploadDto>>().Produces(401).Produces<ApiResponse<string>>(400)
            .Produces<ApiResponse<string>>(404).Produces<ApiResponse<string>>(409);
        group.MapDelete("/versions/{versionId:guid}/files/{fileIndex:int}/parts/{partIndex:int}", async (
            Guid versionId, int fileIndex, int partIndex, NoteMaterialUploadStore store, ICurrentUserService user, CancellationToken ct) =>
        {
            await store.ResetPartAsync(UserId(user), versionId, fileIndex, partIndex, ct);
            return Results.Ok(ApiResponse<string>.Ok("未完成版本的此分块已清除，可以重新上传"));
        }).Produces<ApiResponse<string>>().Produces(401).Produces<ApiResponse<string>>(400)
            .Produces<ApiResponse<string>>(404).Produces<ApiResponse<string>>(409);
        group.MapPost("/versions/{versionId:guid}/promote", async (Guid versionId, PromoteNoteMaterialVersionRequest request,
            NoteMaterialUploadStore store, ICurrentUserService user, CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteMaterialUploadDto>.Ok(await store.PromoteAsync(UserId(user), versionId, request.ExpectedCurrentVersionId, ct))))
            .Produces<ApiResponse<NoteMaterialUploadDto>>().Produces(401).Produces<ApiResponse<string>>(400)
            .Produces<ApiResponse<string>>(404).Produces<ApiResponse<string>>(409);
        group.MapGet("/versions/{versionId:guid}/files/{fileIndex:int}", async (Guid versionId, int fileIndex,
            HttpContext context, NoteMaterialUploadStore store, ICurrentUserService user, CancellationToken ct) =>
        {
            var userId = UserId(user);
            var version = await store.GetAsync(userId, versionId, ct);
            if (version.Upload.Phase == "Uploading") throw new DomainException(40901, "材料尚未提交完整");
            if (fileIndex < 0 || fileIndex >= version.Upload.Files.Count) throw new DomainException(40401, "材料文件不存在");
            var file = version.Upload.Files[fileIndex];
            context.Response.Headers["X-Content-SHA256"] = file.Sha256;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Stream(output => store.ReadFileAsync(userId, versionId, fileIndex, output, ct),
                "application/octet-stream", fileDownloadName: Path.GetFileName(file.Path));
        }).Produces(200, contentType: "application/octet-stream").Produces(401)
            .Produces<ApiResponse<string>>(404).Produces<ApiResponse<string>>(409);
        group.MapDelete("/versions/{versionId:guid}", async (Guid versionId, NoteMaterialUploadStore store,
            ICurrentUserService user, CancellationToken ct) =>
        {
            await store.CancelAsync(UserId(user), versionId, ct);
            return Results.Ok(ApiResponse<string>.Ok("未完成上传已取消，本机原材料不受影响"));
        }).Produces<ApiResponse<string>>().Produces(401).Produces<ApiResponse<string>>(409);
        group.MapDelete("/{noteId:guid}", async (Guid noteId, NoteMaterialUploadStore store,
            ICurrentUserService user, CancellationToken ct) =>
        {
            await store.DeleteNoteAsync(UserId(user), noteId, ct);
            return Results.Ok(ApiResponse<string>.Ok("本篇笔记的服务器材料已删除，本机原材料不受影响"));
        }).Produces<ApiResponse<string>>().Produces(401);
        return endpoints;
    }

    private static Guid UserId(ICurrentUserService user) => user.UserId ?? throw new DomainException(40101, "请先登录");
}
