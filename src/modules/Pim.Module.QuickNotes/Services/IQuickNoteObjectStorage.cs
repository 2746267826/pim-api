namespace Pim.Module.QuickNotes.Services;

public interface IQuickNoteObjectStorage
{
    Task<string> StoreAsync(
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default);

    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}
