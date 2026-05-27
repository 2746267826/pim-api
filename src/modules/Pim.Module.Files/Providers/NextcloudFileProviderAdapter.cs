using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Pim.Module.Files.Providers;

public sealed class NextcloudFileProviderAdapter : IFileProviderAdapter
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod Move = new("MOVE");
    private readonly HttpClient _httpClient;

    public NextcloudFileProviderAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<FileProviderTestResult> TestConnectionAsync(
        FileProviderConnection connection,
        CancellationToken ct = default)
    {
        try
        {
            await GetMetadataAsync(connection, "/", ct);
            return new FileProviderTestResult(true, "connected", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FileProviderTestResult(false, "error", ex.Message);
        }
    }

    public async Task<IReadOnlyList<ProviderFileItem>> ListFolderAsync(
        FileProviderConnection connection,
        string path,
        CancellationToken ct = default)
    {
        using var request = CreatePropFindRequest(connection, FileUrl(connection, path), 1);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var xml = await response.Content.ReadAsStringAsync(ct);
        return NextcloudDavXmlParser.ParseItems(xml, FilesHrefPrefix(connection), path);
    }

    public async Task<ProviderFileItem> GetMetadataAsync(
        FileProviderConnection connection,
        string path,
        CancellationToken ct = default)
    {
        using var request = CreatePropFindRequest(connection, FileUrl(connection, path), 0);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var xml = await response.Content.ReadAsStringAsync(ct);
        return NextcloudDavXmlParser.ParseItems(xml, FilesHrefPrefix(connection), path)
            .First();
    }

    public async Task<ProviderFileItem> UploadAsync(
        FileProviderConnection connection,
        string destinationPath,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, FileUrl(connection, destinationPath), connection);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        return await GetMetadataAsync(connection, destinationPath, ct);
    }

    public async Task<ProviderDownload> DownloadAsync(
        FileProviderConnection connection,
        string path,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, FileUrl(connection, path), connection);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new ProviderDownload(
            stream,
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            FileNameFromResponse(response, path));
    }

    public async Task<ProviderFileItem> MoveAsync(
        FileProviderConnection connection,
        string sourcePath,
        string destinationPath,
        CancellationToken ct = default)
    {
        using var request = CreateMoveRequest(
            connection,
            FileUrl(connection, sourcePath),
            FileUrl(connection, destinationPath));
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        return await GetMetadataAsync(connection, destinationPath, ct);
    }

    public Task<ProviderFileItem> RenameAsync(
        FileProviderConnection connection,
        string sourcePath,
        string name,
        CancellationToken ct = default)
    {
        var destinationPath = CombinePath(ParentPath(sourcePath), name);
        return MoveAsync(connection, sourcePath, destinationPath, ct);
    }

    public async Task DeleteToTrashAsync(
        FileProviderConnection connection,
        string path,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, FileUrl(connection, path), connection);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(
        FileProviderConnection connection,
        CancellationToken ct = default)
    {
        var trashUrl = $"{TrashRoot(connection)}/trash";
        using var request = CreatePropFindRequest(connection, trashUrl, 1);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var xml = await response.Content.ReadAsStringAsync(ct);
        return NextcloudDavXmlParser.ParseTrashItems(xml, TrashHrefPrefix(connection));
    }

    public async Task RestoreTrashAsync(
        FileProviderConnection connection,
        string trashId,
        CancellationToken ct = default)
    {
        var sourceUrl = $"{TrashRoot(connection)}/trash{EscapePath(trashId)}";
        var destinationUrl = $"{TrashRoot(connection)}/restore";
        using var request = CreateMoveRequest(connection, sourceUrl, destinationUrl);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<IReadOnlyList<ProviderFileVersion>> ListVersionsAsync(
        FileProviderConnection connection,
        string externalFileId,
        CancellationToken ct = default)
    {
        var versionsUrl = VersionsFolderUrl(connection, externalFileId);
        using var request = CreatePropFindRequest(connection, versionsUrl, 1);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var xml = await response.Content.ReadAsStringAsync(ct);
        return NextcloudDavXmlParser.ParseVersions(xml, VersionsHrefPrefix(connection, externalFileId));
    }

    public async Task<ProviderDownload> DownloadVersionAsync(
        FileProviderConnection connection,
        string externalFileId,
        string externalVersionId,
        string fileName,
        CancellationToken ct = default)
    {
        var versionUrl = $"{VersionsFolderUrl(connection, externalFileId)}{EscapePath(externalVersionId)}";
        using var request = CreateRequest(HttpMethod.Get, versionUrl, connection);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        return new ProviderDownload(
            await response.Content.ReadAsStreamAsync(ct),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            fileName);
    }

    public async Task RestoreVersionAsync(
        FileProviderConnection connection,
        string externalFileId,
        string externalVersionId,
        CancellationToken ct = default)
    {
        var versionUrl = $"{VersionsFolderUrl(connection, externalFileId)}{EscapePath(externalVersionId)}";
        var destinationUrl = $"{VersionsRoot(connection)}/restore";
        using var request = CreateMoveRequest(connection, versionUrl, destinationUrl);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public ProviderOpenLink BuildOpenLink(
        FileProviderConnection connection,
        string path,
        string mode)
    {
        var parentPath = ParentPath(path);
        var url = $"{connection.BaseUrl.TrimEnd('/')}/apps/files/files?dir={Uri.EscapeDataString(parentPath)}&mode={Uri.EscapeDataString(mode)}";
        return new ProviderOpenLink(url, mode);
    }

    private static HttpRequestMessage CreatePropFindRequest(
        FileProviderConnection connection,
        string url,
        int depth)
    {
        var request = CreateRequest(PropFind, url, connection);
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new StringContent(PropfindBody(), Encoding.UTF8, "application/xml");
        return request;
    }

    private static HttpRequestMessage CreateMoveRequest(
        FileProviderConnection connection,
        string sourceUrl,
        string destinationUrl)
    {
        var request = CreateRequest(Move, sourceUrl, connection);
        request.Headers.TryAddWithoutValidation("Destination", destinationUrl);
        request.Headers.TryAddWithoutValidation("Overwrite", "F");
        return request;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        FileProviderConnection connection)
    {
        var request = new HttpRequestMessage(method, url);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.AppPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MultiStatus)
            return;

        var message = response.Content is null
            ? response.ReasonPhrase
            : await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(message) ? $"Nextcloud request failed with {(int)response.StatusCode}" : message,
            null,
            response.StatusCode);
    }

    private static string DavRoot(FileProviderConnection connection)
        => $"{(connection.InternalBaseUrl ?? connection.BaseUrl).TrimEnd('/')}/remote.php/dav";

    private static string FilesRoot(FileProviderConnection connection)
        => $"{DavRoot(connection)}/files/{Uri.EscapeDataString(connection.Username)}";

    private static string FileUrl(FileProviderConnection connection, string path)
        => $"{FilesRoot(connection)}{EscapePath(path)}";

    private static string TrashRoot(FileProviderConnection connection)
        => $"{DavRoot(connection)}/trashbin/{Uri.EscapeDataString(connection.Username)}";

    private static string VersionsRoot(FileProviderConnection connection)
        => $"{DavRoot(connection)}/versions/{Uri.EscapeDataString(connection.Username)}";

    private static string VersionsFolderUrl(FileProviderConnection connection, string externalFileId)
        => $"{VersionsRoot(connection)}/versions/{Uri.EscapeDataString(externalFileId)}";

    private static string FilesHrefPrefix(FileProviderConnection connection)
        => $"/remote.php/dav/files/{connection.Username}";

    private static string TrashHrefPrefix(FileProviderConnection connection)
        => $"/remote.php/dav/trashbin/{connection.Username}/trash";

    private static string VersionsHrefPrefix(FileProviderConnection connection, string externalFileId)
        => $"/remote.php/dav/versions/{connection.Username}/versions/{externalFileId}";

    private static string EscapePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return string.Empty;

        var trimmed = path.Trim('/');
        return "/" + string.Join(
            "/",
            trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string ParentPath(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == "/")
            return "/";

        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex <= 0 ? "/" : normalized[..slashIndex];
    }

    private static string CombinePath(string parentPath, string name)
    {
        var safeName = name.Trim('/');
        return parentPath == "/" ? $"/{safeName}" : $"{parentPath}/{safeName}";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return "/";

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = $"/{normalized}";

        return normalized.TrimEnd('/');
    }

    private static string FileNameFromResponse(HttpResponseMessage response, string path)
        => response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? Path.GetFileName(path.TrimEnd('/'))
            ?? "download";

    private static string PropfindBody()
        => """
        <?xml version="1.0"?>
        <d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
          <d:prop>
            <d:resourcetype />
            <oc:fileid />
            <oc:permissions />
            <d:getetag />
            <d:getcontentlength />
            <d:getcontenttype />
            <d:getlastmodified />
            <oc:trashbin-filename />
            <oc:trashbin-original-location />
            <oc:trashbin-deletion-time />
            <nc:trashbin-filename />
            <nc:trashbin-original-location />
            <nc:trashbin-deletion-time />
          </d:prop>
        </d:propfind>
        """;
}
