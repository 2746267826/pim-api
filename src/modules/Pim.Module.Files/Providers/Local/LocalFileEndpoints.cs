using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Storage;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;

namespace Pim.Module.Files.Providers.Local;

public static class LocalFileEndpoints
{
    public static IEndpointRouteBuilder MapLocalFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/files/local/content", async ([FromQuery] string ticket, [FromServices] LocalFileGrants grants,
            [FromServices] LocalFileStore store, [FromServices] PimDbContext db, [FromServices] SensitivePathPolicy policy, HttpContext context, CancellationToken ct) =>
        {
            var grant = grants.ReadTicket(ticket);
            await EnsureAccount(db, grant.UserId, grant.ProviderId, ct);
            return await store.TransactionAsync<IResult>(grant.UserId, grant.ProviderId, entries =>
            {
                var item = LocalFileStore.Live(entries, grant.ItemId);
                var path = LocalFileStore.PathOf(entries, item);
                if (item.IsFolder || item.Etag != grant.Etag || path != grant.Path || policy.IsProtected(path))
                    throw new DomainException(40301, "文件已变化或受到保护，请重新获取授权");
                if (grant.Preview && !LocalFileGrants.CanPreview(item.ContentType)) throw new DomainException(40301, "该文件不支持内嵌预览");
                var content = File.OpenRead(store.Binaries.ObjectPath(grant.UserId, ManagedBinaryStore.Files, grant.ProviderId, item.BlobKey!));
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Cache-Control"] = "private, no-store";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
                var mime = LocalFileGrants.CanPreview(item.ContentType) ? item.ContentType! : "application/octet-stream";
                return Task.FromResult<IResult>(Results.File(content, mime, grant.Preview ? null : item.Name, enableRangeProcessing: true));
            }, false, ct);
        }).AllowAnonymous().Produces(200, contentType: "application/octet-stream").Produces(206).Produces(403).Produces(404);

        endpoints.MapPut("/api/v1/files/local/uploads/{id:guid}", async (Guid id, [FromQuery] string ticket,
            [FromServices] LocalFileGrants grants, [FromServices] PimDbContext db, HttpRequest request, CancellationToken ct) =>
        {
            var grant = grants.UploadTicket(id, ticket);
            await EnsureAccount(db, grant.UserId, grant.ProviderId, ct);
            var result = await grants.UploadAsync(grant, request.Headers.ContentRange.ToString(), request.Body, ct);
            return Results.Json(result, statusCode: result.Id is null ? 202 : 200);
        }).AllowAnonymous().Accepts<Stream>("application/octet-stream")
            .WithMetadata(new RequestSizeLimitAttribute(LocalFileGrants.MaxPartBytes))
            .Produces<LocalUploadReply>(200).Produces<LocalUploadReply>(202).Produces(400).Produces(403).Produces(404).Produces(409).Produces(413);
        return endpoints;
    }

    private static async Task EnsureAccount(PimDbContext db, Guid userId, Guid providerId, CancellationToken ct)
    {
        if (!await db.Set<FileProviderEntity>().AnyAsync(row => row.UserId == userId && row.Id == providerId && row.Provider == LocalFileProvider.Key && row.Status == "connected", ct))
            throw new DomainException(40301, "文件授权对应的来源不可用");
    }
}
