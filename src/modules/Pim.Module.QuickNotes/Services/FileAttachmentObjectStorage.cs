using Pim.Core.Storage;

namespace Pim.Module.QuickNotes.Services;

/// <summary>
/// 把 QuickNotes 附件存到用户网盘的存储实现（工单 05 / F1）。
/// 依赖文件模块提供的 <see cref="IFileAttachmentStore"/>；未绑定网盘时抛出明确的领域错误。
///
/// 用户身份由调用方显式传入（<c>userId</c>），不从 objectKey 字符串反解 ——
/// 反解方案在 objectKey 变成裸远端条目 id 后必然失败。
/// 具体落到哪个账号由文件模块按「默认账号」决定，本类不关心 provider 是哪一个；
/// 返回的 <see cref="StoredObjectRef"/> 带上账号信息，供后续读/删定位。
/// </summary>
public sealed class FileAttachmentObjectStorage(IFileAttachmentStore store)
    : IQuickNoteObjectStorage, IQuickNoteDirectLinkStorage
{
    public async Task<StoredObjectRef> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default)
        => StoredObjectRef.From(await store.StoreAsync(userId, objectKey, content, contentType, sizeBytes, ct));

    public Task<Stream> OpenReadAsync(Guid userId, StoredObjectRef reference, CancellationToken ct = default)
        => store.OpenReadAsync(userId, reference.ToAttachmentRef(), ct);

    public async Task<string?> GetDirectLinkAsync(
        Guid userId,
        StoredObjectRef reference,
        CancellationToken ct = default)
        => await store.GetDirectLinkAsync(userId, reference.ToAttachmentRef(), ct);

    public Task DeleteAsync(Guid userId, StoredObjectRef reference, CancellationToken ct = default)
        => store.DeleteAsync(userId, reference.ToAttachmentRef(), ct);
}
