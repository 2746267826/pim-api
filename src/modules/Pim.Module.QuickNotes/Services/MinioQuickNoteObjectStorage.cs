using Pim.Infrastructure.Storage;

namespace Pim.Module.QuickNotes.Services;

public sealed class MinioQuickNoteObjectStorage(MinioStorage storage) : IQuickNoteObjectStorage
{
    public async Task<string> StoreAsync(
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default)
    {
        await storage.EnsureBucketAsync(ct);
        return await storage.UploadAsync(objectKey, content, contentType, sizeBytes, ct);
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
        => storage.DownloadAsync(objectKey, ct);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        => storage.DeleteAsync(objectKey, ct);
}
