using Pim.Core.Storage;

namespace Pim.Module.QuickNotes.Services;

/// <summary>
/// QuickNotes 附件的对象存储抽象（工单 05 / F1）。
///
/// 所有方法都显式接收 <paramref name="userId"/>：附件存放在**用户自己的**网盘下，
/// 存储实现需要它来定位该用户的账号。写入返回的句柄里带上账号信息 ——
/// 多账号并存后只有这样才读得回来（老数据句柄的账号为空，由实现回退查找）。
///
/// 本接口不出现任何 provider 名（原先的类型名里写死了 OneDrive）。
/// </summary>
public interface IQuickNoteObjectStorage
{
    /// <summary>存入附件，返回可用于后续读/删的句柄。</summary>
    Task<StoredObjectRef> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    Task<Stream> OpenReadAsync(Guid userId, StoredObjectRef reference, CancellationToken ct = default);

    Task DeleteAsync(Guid userId, StoredObjectRef reference, CancellationToken ct = default);
}

/// <summary>
/// 已存对象的定位信息。等价于文件模块的 <see cref="FileAttachmentRef"/>，
/// 保留独立类型是为了让 QuickNotes 不必依赖文件模块的命名空间。
/// </summary>
public sealed record StoredObjectRef(string ObjectKey, string StorageProvider, Guid? StorageAccountId)
{
    public static StoredObjectRef From(FileAttachmentRef reference)
        => new(reference.ObjectKey, reference.ProviderKey, reference.ProviderId);

    public FileAttachmentRef ToAttachmentRef()
        => new(ObjectKey, StorageProvider, StorageAccountId);
}

/// <summary>
/// 可选能力：存储后端能给出预授权直链时，内容端点可以 302 直连，
/// 避免服务器代理附件流量。
/// </summary>
public interface IQuickNoteDirectLinkStorage
{
    Task<string?> GetDirectLinkAsync(Guid userId, StoredObjectRef reference, CancellationToken ct = default);
}
