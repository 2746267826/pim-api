namespace Pim.Core.Storage;

/// <summary>
/// 用户级 OneDrive 附件存储抽象（文件模块 v2，设计文档 §10）：
/// QuickNotes 等模块通过它把附件存入**用户自己的** OneDrive。
///
/// 所有方法都显式接收 <paramref name="userId"/>（定位该用户的 OneDrive 绑定），
/// <paramref name="objectKey"/> 是存储实现返回的句柄（OneDrive 实现中即 driveItem id）。
/// 实现由文件模块提供并注册进 DI。
/// </summary>
public interface IOneDriveAttachmentStore
{
    /// <summary>上传附件（≤4MB），返回可用于后续读/删的 objectKey。</summary>
    Task<string> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    /// <summary>读取附件内容（瞬态流，≤4MB）。</summary>
    Task<Stream> OpenReadAsync(Guid userId, string objectKey, CancellationToken ct = default);

    /// <summary>预授权下载直链（短时效）；无直链时为 null。</summary>
    Task<string?> GetDirectLinkAsync(Guid userId, string objectKey, CancellationToken ct = default);

    /// <summary>删除附件（进 OneDrive 回收站）；远端已删除时不报错。</summary>
    Task DeleteAsync(Guid userId, string objectKey, CancellationToken ct = default);
}
