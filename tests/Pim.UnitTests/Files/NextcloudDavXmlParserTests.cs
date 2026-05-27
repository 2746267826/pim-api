using Pim.Core.Exceptions;
using Pim.Module.Files.Providers;
using Xunit;

namespace Pim.UnitTests.Files;

public class NextcloudDavXmlParserTests
{
    [Fact]
    public void ParseItems_MapsStableIdsPathsEtagsAndFolders()
    {
        var xml = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/alice/Reports/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype><oc:fileid>10</oc:fileid><oc:permissions>RGDNVCK</oc:permissions><d:getetag>&quot;folder-etag&quot;</d:getetag><d:getlastmodified>Wed, 20 May 2026 10:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/alice/Reports/report.docx</d:href>
            <d:propstat><d:prop><d:resourcetype /><oc:fileid>11</oc:fileid><oc:permissions>RGDNVW</oc:permissions><d:getetag>&quot;file-etag&quot;</d:getetag><d:getcontentlength>123</d:getcontentlength><d:getcontenttype>application/vnd.openxmlformats-officedocument.wordprocessingml.document</d:getcontenttype><d:getlastmodified>Wed, 20 May 2026 10:01:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

        var items = NextcloudDavXmlParser.ParseItems(xml, "/remote.php/dav/files/alice", "/Reports");

        Assert.Equal(2, items.Count);
        Assert.Equal("10", items[0].ExternalFileId);
        Assert.Null(items[0].ParentExternalFileId);
        Assert.Equal("/Reports", items[0].Path);
        Assert.Equal("Reports", items[0].Name);
        Assert.Equal("folder", items[0].ItemType);
        Assert.Equal("RGDNVCK", items[0].Permissions);
        Assert.Equal("11", items[1].ExternalFileId);
        Assert.Equal("10", items[1].ParentExternalFileId);
        Assert.Equal("/Reports/report.docx", items[1].Path);
        Assert.Equal("report.docx", items[1].Name);
        Assert.Equal("file", items[1].ItemType);
        Assert.Equal(123, items[1].Size);
        Assert.Equal("\"file-etag\"", items[1].Etag);
    }

    [Fact]
    public void ParseItems_UrlDecodesHrefAndNormalizesRootPath()
    {
        var xml = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/alice/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype><oc:fileid>1</oc:fileid><d:getlastmodified>Wed, 20 May 2026 10:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/alice/Q1%20Report.docx</d:href>
            <d:propstat><d:prop><d:resourcetype /><oc:fileid>2</oc:fileid><d:getlastmodified>Wed, 20 May 2026 10:01:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

        var items = NextcloudDavXmlParser.ParseItems(xml, "/remote.php/dav/files/alice", "/");

        Assert.Equal("/", items[0].Path);
        Assert.Equal("Q1 Report.docx", items[1].Name);
        Assert.Equal("/Q1 Report.docx", items[1].Path);
        Assert.Equal("1", items[1].ParentExternalFileId);
    }

    [Fact]
    public void ParseItems_ThrowsWhenNormalFileEntryDoesNotIncludeFileId()
    {
        var xml = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/alice/Reports/report.docx</d:href>
            <d:propstat><d:prop><d:resourcetype /><d:getlastmodified>Wed, 20 May 2026 10:01:00 GMT</d:getlastmodified></d:prop></d:propstat>
          </d:response>
        </d:multistatus>
        """;

        var error = Assert.Throws<DomainException>(
            () => NextcloudDavXmlParser.ParseItems(xml, "/remote.php/dav/files/alice", "/Reports"));

        Assert.Equal(5201, error.ErrorCode);
        Assert.Equal("Nextcloud response did not include a file id", error.Message);
    }

    [Fact]
    public void ParseItems_UsesSuccessfulPropstatWhenErrorPropstatAppearsFirst()
    {
        var xml = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/alice/Reports/report.docx</d:href>
            <d:propstat>
              <d:prop><d:getcontentlength /></d:prop>
              <d:status>HTTP/1.1 404 Not Found</d:status>
            </d:propstat>
            <d:propstat>
              <d:prop><d:resourcetype /><oc:fileid>11</oc:fileid><d:getetag>&quot;file-etag&quot;</d:getetag><d:getcontentlength>123</d:getcontentlength><d:getlastmodified>Wed, 20 May 2026 10:01:00 GMT</d:getlastmodified></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

        var item = Assert.Single(NextcloudDavXmlParser.ParseItems(xml, "/remote.php/dav/files/alice", "/Reports"));

        Assert.Equal("11", item.ExternalFileId);
        Assert.Equal(123, item.Size);
        Assert.Equal("\"file-etag\"", item.Etag);
    }
}
