namespace Pim.Module.Files.Providers;

public sealed class NextcloudFileProviderAdapter : IFileProviderAdapter
{
    public NextcloudFileProviderAdapter(HttpClient httpClient)
    {
    }

    public Task<FileProviderTestResult> TestConnectionAsync(
        FileProviderConnection connection,
        CancellationToken ct = default)
        => Task.FromResult(new FileProviderTestResult(
            false,
            "not_implemented",
            "Nextcloud connection testing is not implemented yet"));
}
