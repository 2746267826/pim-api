namespace Pim.Module.Files.Providers;

public sealed record FileProviderConnection(Guid ProviderId, string BaseUrl, string? InternalBaseUrl, string Username, string AppPassword);
public sealed record FileProviderTestResult(bool Success, string Status, string? ErrorMessage);
public sealed record ProviderFileItem(string ExternalFileId, string? ParentExternalFileId, string Path, string Name, string ItemType, string? MimeType, long? Size, string? Etag, string? Permissions, DateTimeOffset ModifiedAt);
public sealed record ProviderFileVersion(string ExternalVersionId, string? Etag, long? Size, DateTimeOffset ModifiedAt, string Source, bool IsCurrent);
public sealed record ProviderTrashItem(string TrashId, string OriginalLocation, string Name, string ItemType, long? Size, DateTimeOffset DeletedAt);
public sealed record ProviderOpenLink(string Url, string Mode);
public sealed record ProviderDownload(Stream Content, string ContentType, string FileName);

public interface IFileProviderAdapter
{
    Task<FileProviderTestResult> TestConnectionAsync(FileProviderConnection connection, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderFileItem>> ListFolderAsync(FileProviderConnection connection, string path, CancellationToken ct = default);
    Task<ProviderFileItem> GetMetadataAsync(FileProviderConnection connection, string path, CancellationToken ct = default);
    Task<ProviderFileItem> UploadAsync(FileProviderConnection connection, string destinationPath, Stream content, string contentType, CancellationToken ct = default);
    Task<ProviderDownload> DownloadAsync(FileProviderConnection connection, string path, CancellationToken ct = default);
    Task<ProviderFileItem> MoveAsync(FileProviderConnection connection, string sourcePath, string destinationPath, CancellationToken ct = default);
    Task<ProviderFileItem> RenameAsync(FileProviderConnection connection, string sourcePath, string name, CancellationToken ct = default);
    Task DeleteToTrashAsync(FileProviderConnection connection, string path, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(FileProviderConnection connection, CancellationToken ct = default);
    Task RestoreTrashAsync(FileProviderConnection connection, string trashId, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderFileVersion>> ListVersionsAsync(FileProviderConnection connection, string externalFileId, CancellationToken ct = default);
    Task<ProviderDownload> DownloadVersionAsync(FileProviderConnection connection, string externalFileId, string externalVersionId, string fileName, CancellationToken ct = default);
    Task RestoreVersionAsync(FileProviderConnection connection, string externalFileId, string externalVersionId, CancellationToken ct = default);
    ProviderOpenLink BuildOpenLink(FileProviderConnection connection, string path, string mode);
}
