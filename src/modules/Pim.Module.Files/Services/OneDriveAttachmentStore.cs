using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

/// <summary>
/// <see cref="IOneDriveAttachmentStore"/> 的文件模块实现（设计文档 §10）：
/// 附件按约定目录 <c>/PIM/{objectKey}</c> 存入**用户自己的** OneDrive（≤4MB 简单上传），
/// 返回 driveItem id 作为后续读/删的句柄；内容读取瞬态经过，不落盘。
/// </summary>
public sealed class OneDriveAttachmentStore(
    PimDbContext db,
    IOneDriveGraphClient client,
    OneDriveTokenService tokens,
    ILogger<OneDriveAttachmentStore>? logger = null) : IOneDriveAttachmentStore
{
    /// <summary>附件在 OneDrive 中的根目录（与用户文件树区分开）。</summary>
    public const string AttachmentRoot = "/PIM";

    public const string ProviderName = "onedrive";

    public async Task<string> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default)
    {
        var provider = await LoadConnectedProviderAsync(userId, ct);
        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);

        // 边读边计数：不能先全量读进内存再判上限，否则超大请求会在检查前吃光内存。
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > OneDriveContentService.MaxSaveBytes)
            {
                throw new DomainException(
                    5331, $"附件超过 {OneDriveContentService.MaxSaveBytes / 1024 / 1024}MB，请使用 OneDrive 客户端");
            }

            buffer.Write(chunk, 0, read);
        }

        var drivePath = OneDriveAttachmentPath(objectKey);
        var driveItemId = await client.PutNewFileByPathAsync(token, drivePath, buffer.ToArray(), contentType, ct);
        logger?.LogInformation(
            "OneDrive attachment stored: user {UserId}, bytes {Bytes}", userId, buffer.Length);
        return driveItemId;
    }

    public async Task<Stream> OpenReadAsync(Guid userId, string objectKey, CancellationToken ct = default)
    {
        var provider = await LoadConnectedProviderAsync(userId, ct);
        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
        var downloaded = await client.DownloadSmallAsync(token, objectKey, OneDriveContentService.MaxSaveBytes, ct)
            ?? throw new DomainException(4006, "附件不存在");
        return new MemoryStream(downloaded.Bytes);
    }

    public async Task<string?> GetDirectLinkAsync(Guid userId, string objectKey, CancellationToken ct = default)
    {
        var provider = await LoadConnectedProviderAsync(userId, ct);
        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
        return await client.GetDownloadUrlAsync(token, objectKey, ct);
    }

    public async Task DeleteAsync(Guid userId, string objectKey, CancellationToken ct = default)
    {
        var provider = await LoadConnectedProviderAsync(userId, ct);
        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
        // 客户端对 404 视为已删除，不报错
        await client.DeleteItemAsync(token, objectKey, ct);
        logger?.LogInformation("OneDrive attachment deleted: user {UserId}", userId);
    }

    /// <summary>objectKey（<c>quick-notes/{userId}/{id}/{name}</c>）映射为 OneDrive 路径 <c>/PIM/{objectKey}</c>。</summary>
    public static string OneDriveAttachmentPath(string objectKey)
        => AttachmentRoot + "/" + objectKey.TrimStart('/');

    private async Task<FileProviderEntity> LoadConnectedProviderAsync(Guid userId, CancellationToken ct)
    {
        var provider = await db.Set<FileProviderEntity>()
            .SingleOrDefaultAsync(row => row.UserId == userId && row.Provider == ProviderName, ct)
            ?? throw new DomainException(5320, "尚未绑定 OneDrive，附件功能不可用");
        if (provider.Status != "connected" || provider.RefreshTokenEncrypted is not { Length: > 0 })
        {
            throw new DomainException(5321, "OneDrive 尚未完成绑定，附件功能不可用");
        }

        return provider;
    }
}
