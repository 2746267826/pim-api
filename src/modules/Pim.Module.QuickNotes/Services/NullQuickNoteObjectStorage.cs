namespace Pim.Module.QuickNotes.Services;

public sealed class NullQuickNoteObjectStorage : IQuickNoteObjectStorage
{
    private const string NotConfiguredMessage = "MinIO 未配置，附件功能不可用";

    public Task<string> StoreAsync(string objectKey, Stream content, string contentType, long sizeBytes, CancellationToken ct = default)
        => throw new InvalidOperationException(NotConfiguredMessage);

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
        => throw new InvalidOperationException(NotConfiguredMessage);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        => Task.CompletedTask;
}
