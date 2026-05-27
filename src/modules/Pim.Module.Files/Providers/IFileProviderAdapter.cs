namespace Pim.Module.Files.Providers;

public sealed record FileProviderConnection(
    Guid ProviderId,
    string BaseUrl,
    string? InternalBaseUrl,
    string Username,
    string AppPassword);

public sealed record FileProviderTestResult(bool Success, string Status, string? ErrorMessage);

public interface IFileProviderAdapter
{
    Task<FileProviderTestResult> TestConnectionAsync(
        FileProviderConnection connection,
        CancellationToken ct = default);
}
