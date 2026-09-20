namespace Pim.Module.QuickNotes.Services;

/// <summary>
/// QuickNotes 附件的对象存储抽象（文件模块 v2，设计文档 §10）。
///
/// 所有方法都显式接收 <paramref name="userId"/>：附件存放在**用户自己的** OneDrive 下，
/// 存储实现需要它来定位该用户的 OneDrive 绑定。历史上这里靠 objectKey 的字符串约定
/// （<c>quick-notes/{userId:N}/…</c>）反解用户，但 OneDrive 实现把 objectKey 收窄成了
/// 裸 driveItem id，反解必然失败——因此改为由调用方显式传入。
/// </summary>
public interface IQuickNoteObjectStorage
{
    /// <summary>存入附件，返回持久化到 <c>ObjectKey</c> 的句柄。</summary>
    Task<string> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    Task<Stream> OpenReadAsync(Guid userId, string objectKey, CancellationToken ct = default);

    Task DeleteAsync(Guid userId, string objectKey, CancellationToken ct = default);
}

/// <summary>
/// 可选能力：存储后端能给出预授权直链时，内容端点可以 302 直连，
/// 避免服务器代理附件流量（设计文档 §10）。
/// </summary>
public interface IQuickNoteDirectLinkStorage
{
    Task<string?> GetDirectLinkAsync(Guid userId, string objectKey, CancellationToken ct = default);
}
