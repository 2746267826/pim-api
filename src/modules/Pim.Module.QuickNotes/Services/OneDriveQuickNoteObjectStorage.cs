using Pim.Core.Storage;

namespace Pim.Module.QuickNotes.Services;

/// <summary>
/// 附件存 OneDrive 的 QuickNotes 存储实现（设计文档 §10）：
/// 依赖文件模块提供的 <see cref="IOneDriveAttachmentStore"/>，
/// objectKey 即 OneDrive driveItem id；未绑定 OneDrive 时抛出明确的领域错误。
///
/// 用户身份由调用方显式传入（<c>userId</c>），不再从 objectKey 字符串反解——
/// 反解方案在 objectKey 变成裸 driveItem id 后必然失败（交接文档资产里的已知缺陷）。
/// </summary>
public sealed class OneDriveQuickNoteObjectStorage(IOneDriveAttachmentStore store)
    : IQuickNoteObjectStorage, IQuickNoteDirectLinkStorage
{
    public const string ProviderName = "onedrive";

    public Task<string> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default)
        => store.StoreAsync(userId, objectKey, content, contentType, sizeBytes, ct);

    public Task<Stream> OpenReadAsync(Guid userId, string objectKey, CancellationToken ct = default)
        => store.OpenReadAsync(userId, objectKey, ct);

    public Task<string?> GetDirectLinkAsync(Guid userId, string objectKey, CancellationToken ct = default)
        => store.GetDirectLinkAsync(userId, objectKey, ct);

    public Task DeleteAsync(Guid userId, string objectKey, CancellationToken ct = default)
        => store.DeleteAsync(userId, objectKey, ct);
}
