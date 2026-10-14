namespace Pim.Core.Storage;

/// <summary>
/// 附件在存储后端中的定位句柄（工单 05 / F1、F2）。
///
/// <paramref name="ObjectKey"/> 是后端返回的条目句柄（网盘实现中即远端条目 id）。
/// <paramref name="ProviderId"/> 是**账号行 id** —— 多账号并存后必须有它才能定位附件
/// 落在哪个账号的网盘里；老数据（本列引入之前写的行）为 null，由实现在该用户的
/// 其余已连接账号里按句柄回退查找。
/// </summary>
public sealed record FileAttachmentRef(string ObjectKey, string ProviderKey, Guid? ProviderId);

/// <summary>
/// 通用附件存储抽象（工单 05 / F1）：QuickNotes 等模块通过它把附件存入
/// **用户自己的**网盘，而不关心是 OneDrive 还是 Google Drive。
///
/// 所有方法都显式接收 <paramref name="userId"/>（定位该用户的账号）。
/// 实现由文件模块提供并注册进 DI；落点账号是该用户的<em>默认账号</em>。
/// </summary>
public interface IFileAttachmentStore
{
    /// <summary>上传附件（≤4MB），返回可用于后续读/删的句柄。</summary>
    Task<FileAttachmentRef> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    /// <summary>读取附件内容（瞬态流，≤4MB）。</summary>
    Task<Stream> OpenReadAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default);

    /// <summary>预授权下载直链（短时效）；无直链时为 null。</summary>
    Task<string?> GetDirectLinkAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default);

    /// <summary>删除附件（进网盘回收站）；远端已删除时不报错。</summary>
    Task DeleteAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default);
}
