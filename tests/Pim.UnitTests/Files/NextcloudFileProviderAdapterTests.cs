using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Pim.Core.Exceptions;
using Pim.Module.Files.Providers;
using Xunit;

namespace Pim.UnitTests.Files;

public class NextcloudFileProviderAdapterTests
{
    private static readonly Guid ProviderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task ListFolderAsync_SendsPropfindWithDepthOneAndBasicAuth()
    {
        var handler = new CapturingHandler(MultistatusForReports());
        var adapter = CreateAdapter(handler);
        var connection = CreateConnection();

        await adapter.ListFolderAsync(connection, "/Reports");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/files/alice/Reports", request.Url);
        Assert.Equal("1", request.Headers["Depth"].Single());
        AssertBasicAuth(request.Authorization, "alice", "app-password");
        Assert.Contains("<d:propfind", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFolderAsync_ReturnsChildrenWithoutRequestedFolderSelfEntry()
    {
        var handler = new CapturingHandler(MultistatusForReports());
        var adapter = CreateAdapter(handler);

        var item = Assert.Single(await adapter.ListFolderAsync(CreateConnection(), "/Reports"));

        Assert.Equal("/Reports/report.docx", item.Path);
        Assert.Equal("10", item.ParentExternalFileId);
    }

    [Fact]
    public async Task DownloadAsync_SendsGetToFileUrlAndReturnsResponseContent()
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("document"));
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content.ToArray())
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = "report.docx"
            };
            return response;
        });
        var adapter = CreateAdapter(handler);

        var download = await adapter.DownloadAsync(CreateConnection(), "/Reports/report.docx");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/files/alice/Reports/report.docx", request.Url);
        AssertBasicAuth(request.Authorization, "alice", "app-password");
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", download.ContentType);
        Assert.Equal("report.docx", download.FileName);
        using var reader = new StreamReader(download.Content);
        Assert.Equal("document", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task DownloadAsync_DisposingReturnedStreamDisposesResponseContent()
    {
        var content = new TrackingContent(new TrackingStream(Encoding.UTF8.GetBytes("document")));
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        var adapter = CreateAdapter(handler);

        var download = await adapter.DownloadAsync(CreateConnection(), "/Reports/report.docx");

        Assert.False(content.Disposed);
        await download.Content.DisposeAsync();
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task UploadAsync_SendsPutWithContentTypeThenFetchesMetadata()
    {
        var handler = new CapturingHandler(request =>
            request.Method.Method == "PUT"
                ? new HttpResponseMessage(HttpStatusCode.Created)
                : XmlResponse(MultistatusForReportFile()));
        var adapter = CreateAdapter(handler);

        await adapter.UploadAsync(
            CreateConnection(),
            "/Reports/report.docx",
            new MemoryStream(Encoding.UTF8.GetBytes("document")),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.Equal(2, handler.Requests.Count);
        var put = handler.Requests[0];
        Assert.Equal("PUT", put.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/files/alice/Reports/report.docx", put.Url);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", put.ContentType);
        Assert.Equal("document", put.Body);
        AssertBasicAuth(put.Authorization, "alice", "app-password");

        var metadata = handler.Requests[1];
        Assert.Equal("PROPFIND", metadata.Method);
        Assert.Equal("0", metadata.Headers["Depth"].Single());
    }

    [Fact]
    public async Task MoveAsync_SendsMoveWithDestinationAndOverwriteFalseThenFetchesMetadata()
    {
        var handler = new CapturingHandler(request =>
            request.Method.Method == "MOVE"
                ? new HttpResponseMessage(HttpStatusCode.Created)
                : XmlResponse(MultistatusForReportFile("/Archive/report.docx")));
        var adapter = CreateAdapter(handler);

        await adapter.MoveAsync(CreateConnection(), "/Reports/report.docx", "/Archive/report.docx");

        Assert.Equal(2, handler.Requests.Count);
        var move = handler.Requests[0];
        Assert.Equal("MOVE", move.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/files/alice/Reports/report.docx", move.Url);
        Assert.Equal("http://nextcloud/remote.php/dav/files/alice/Archive/report.docx", move.Headers["Destination"].Single());
        Assert.Equal("F", move.Headers["Overwrite"].Single());
        AssertBasicAuth(move.Authorization, "alice", "app-password");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("/Reports/../secret.txt")]
    [InlineData("/Reports/./report.docx")]
    [InlineData("Reports\\..\\secret.txt")]
    public async Task FilePathOperations_RejectDangerousPathsWithoutSendingRequests(string path)
    {
        var handler = new CapturingHandler(string.Empty);
        var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => adapter.DownloadAsync(CreateConnection(), path));

        Assert.Equal(5202, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nested/report.docx")]
    [InlineData("nested\\report.docx")]
    public async Task RenameAsync_RejectsUnsafeNamesWithoutSendingRequests(string name)
    {
        var handler = new CapturingHandler(string.Empty);
        var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => adapter.RenameAsync(CreateConnection(), "/Reports/report.docx", name));

        Assert.Equal(5202, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RestoreTrashAsync_RejectsDangerousTrashIdsWithoutSendingRequests()
    {
        var handler = new CapturingHandler(string.Empty);
        var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => adapter.RestoreTrashAsync(CreateConnection(), "../report.docx.d1684580000"));

        Assert.Equal(5202, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RestoreTrashAsync_RejectsEmptyTrashIdsWithoutSendingRequests()
    {
        var handler = new CapturingHandler(string.Empty);
        var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => adapter.RestoreTrashAsync(CreateConnection(), " "));

        Assert.Equal(5202, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RestoreVersionAsync_RejectsDangerousVersionIdsWithoutSendingRequests()
    {
        var handler = new CapturingHandler(string.Empty);
        var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => adapter.RestoreVersionAsync(CreateConnection(), "11", "../1684580000"));

        Assert.Equal(5202, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListVersionsAsync_RejectsDangerousExternalFileIdsWithoutSendingRequests()
    {
        var handler = new CapturingHandler(string.Empty);
        var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => adapter.ListVersionsAsync(CreateConnection(), "../11"));

        Assert.Equal(5202, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DeleteToTrashAsync_SendsDeleteToSourceUrl()
    {
        var handler = new CapturingHandler(string.Empty, HttpStatusCode.NoContent);
        var adapter = CreateAdapter(handler);

        await adapter.DeleteToTrashAsync(CreateConnection(), "/Reports/report.docx");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/files/alice/Reports/report.docx", request.Url);
        AssertBasicAuth(request.Authorization, "alice", "app-password");
    }

    [Fact]
    public async Task ListTrashAsync_SendsPropfindToTrashFolder()
    {
        var handler = new CapturingHandler(TrashMultistatus());
        var adapter = CreateAdapter(handler);

        await adapter.ListTrashAsync(CreateConnection());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/trashbin/alice/trash", request.Url);
        Assert.Equal("1", request.Headers["Depth"].Single());
        AssertBasicAuth(request.Authorization, "alice", "app-password");
    }

    [Fact]
    public async Task RestoreTrashAsync_SendsMoveToTrashRestoreEndpoint()
    {
        var handler = new CapturingHandler(string.Empty, HttpStatusCode.Created);
        var adapter = CreateAdapter(handler);

        await adapter.RestoreTrashAsync(CreateConnection(), "report.docx.d1684580000");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("MOVE", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/trashbin/alice/trash/report.docx.d1684580000", request.Url);
        Assert.Equal("http://nextcloud/remote.php/dav/trashbin/alice/restore", request.Headers["Destination"].Single());
        Assert.Equal("F", request.Headers["Overwrite"].Single());
        AssertBasicAuth(request.Authorization, "alice", "app-password");
    }

    [Fact]
    public async Task ListVersionsAsync_SendsPropfindToVersionsFolder()
    {
        var handler = new CapturingHandler(VersionsMultistatus());
        var adapter = CreateAdapter(handler);

        await adapter.ListVersionsAsync(CreateConnection(), "11");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/versions/alice/versions/11", request.Url);
        Assert.Equal("1", request.Headers["Depth"].Single());
        AssertBasicAuth(request.Authorization, "alice", "app-password");
    }

    [Fact]
    public async Task RestoreVersionAsync_SendsMoveToVersionsRestoreEndpoint()
    {
        var handler = new CapturingHandler(string.Empty, HttpStatusCode.Created);
        var adapter = CreateAdapter(handler);

        await adapter.RestoreVersionAsync(CreateConnection(), "11", "1684580000");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("MOVE", request.Method);
        Assert.Equal("http://nextcloud/remote.php/dav/versions/alice/versions/11/1684580000", request.Url);
        Assert.Equal("http://nextcloud/remote.php/dav/versions/alice/restore", request.Headers["Destination"].Single());
        Assert.Equal("F", request.Headers["Overwrite"].Single());
        AssertBasicAuth(request.Authorization, "alice", "app-password");
    }

    [Fact]
    public void BuildOpenLink_UsesPublicBaseUrlAndParentDirectory()
    {
        var adapter = CreateAdapter(new CapturingHandler(string.Empty));

        var link = adapter.BuildOpenLink(CreateConnection(), "/Reports/report.docx", "edit");

        Assert.Equal("edit", link.Mode);
        Assert.Equal("https://cloud.example.test/apps/files/files?dir=%2FReports&mode=edit", link.Url);
    }

    [Fact]
    public void BuildOpenLink_IncludesOpenFileWhenExternalFileIdIsProvided()
    {
        var adapter = CreateAdapter(new CapturingHandler(string.Empty));

        var link = adapter.BuildOpenLink(CreateConnection(), "/Reports/report.docx", "view", "11");

        Assert.Equal("view", link.Mode);
        Assert.Equal("https://cloud.example.test/apps/files/files?dir=%2FReports&mode=view&openfile=11", link.Url);
    }

    [Fact]
    public void BuildOpenLink_RejectsDangerousPaths()
    {
        var adapter = CreateAdapter(new CapturingHandler(string.Empty));

        var error = Assert.Throws<DomainException>(
            () => adapter.BuildOpenLink(CreateConnection(), "/Reports/../secret.txt", "view"));

        Assert.Equal(5202, error.ErrorCode);
    }

    private static NextcloudFileProviderAdapter CreateAdapter(CapturingHandler handler)
        => new(new HttpClient(handler));

    private static FileProviderConnection CreateConnection()
        => new(
            ProviderId,
            "https://cloud.example.test",
            "http://nextcloud",
            "alice",
            "app-password");

    private static void AssertBasicAuth(AuthenticationHeaderValue? authorization, string username, string password)
    {
        Assert.NotNull(authorization);
        Assert.Equal("Basic", authorization.Scheme);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        Assert.Equal(encoded, authorization.Parameter);
    }

    private static HttpResponseMessage XmlResponse(string xml)
    {
        var response = new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        };
        return response;
    }

    private static string MultistatusForReports()
        => """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/alice/Reports/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype><oc:fileid>10</oc:fileid><d:getlastmodified>Wed, 20 May 2026 10:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/alice/Reports/report.docx</d:href>
            <d:propstat><d:prop><d:resourcetype /><oc:fileid>11</oc:fileid><d:getlastmodified>Wed, 20 May 2026 10:01:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private static string MultistatusForReportFile(string path = "/Reports/report.docx")
        => $"""
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/alice{path}</d:href>
            <d:propstat><d:prop><d:resourcetype /><oc:fileid>11</oc:fileid><d:getetag>&quot;file-etag&quot;</d:getetag><d:getcontentlength>123</d:getcontentlength><d:getcontenttype>application/vnd.openxmlformats-officedocument.wordprocessingml.document</d:getcontenttype><d:getlastmodified>Wed, 20 May 2026 10:01:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private static string TrashMultistatus()
        => """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/trashbin/alice/trash/report.docx.d1684580000</d:href>
            <d:propstat><d:prop><d:resourcetype /><oc:trashbin-filename>report.docx</oc:trashbin-filename><oc:trashbin-original-location>/Reports/report.docx</oc:trashbin-original-location><oc:trashbin-deletion-time>1684580000</oc:trashbin-deletion-time><d:getcontentlength>123</d:getcontentlength></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private static string VersionsMultistatus()
        => """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/versions/alice/versions/11/1684580000</d:href>
            <d:propstat><d:prop><d:getetag>&quot;version-etag&quot;</d:getetag><d:getcontentlength>120</d:getcontentlength><d:getlastmodified>Wed, 20 May 2026 09:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public CapturingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.MultiStatus)
            : this(_ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/xml")
            })
        {
        }

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray()),
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                body));

            return _respond(request);
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Url,
        IReadOnlyDictionary<string, string[]> Headers,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        string Body);

    private sealed class TrackingContent : HttpContent
    {
        private readonly TrackingStream _stream;

        public TrackingContent(TrackingStream stream)
        {
            _stream = stream;
        }

        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = _stream.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(_stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed = true;

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] buffer)
            : base(buffer)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
