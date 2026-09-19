using Xunit;
using System.Net;
using Pim.Module.Files.Providers;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveGraphClient 原始 HTTP 行为测试：请求构造、响应映射、错误分类。
/// </summary>
public class OneDriveGraphClientTests
{
    private const string ClientId = "82b1ee30-c6ed-4ff5-a70d-557c1d52222b";

    private static OneDriveGraphClient CreateClient(StubHttpHandler handler)
    {
        return new OneDriveGraphClient(new StubHttpClientFactory(handler));
    }

    private sealed class StubHttpClientFactory(StubHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task RequestDeviceCode_PostsClientIdAndScope_ToConsumersTenant()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new
            {
                device_code = "dc-123",
                user_code = "ABCD-1234",
                verification_uri = "https://www.microsoft.com/link",
                expires_in = 900,
            }),
        };
        var client = CreateClient(handler);

        var result = await client.RequestDeviceCodeAsync(ClientId);

        Assert.Equal("dc-123", result.DeviceCode);
        Assert.Equal("ABCD-1234", result.UserCode);
        Assert.Equal("https://www.microsoft.com/link", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Contains("login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", request.Url);
        Assert.Contains("client_id=" + ClientId, request.Body);
        Assert.Contains("Files.ReadWrite.All", request.Body);
        Assert.Contains("offline_access", request.Body);
    }

    [Fact]
    public async Task PollDeviceCode_ReturnsTokenMapping()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new
            {
                access_token = "at",
                refresh_token = "rt",
                expires_in = 3600,
                scope = "Files.ReadWrite.All",
            }),
        };
        var client = CreateClient(handler);

        var result = await client.PollDeviceCodeAsync(ClientId, "dc-123");

        Assert.Equal("at", result.AccessToken);
        Assert.Equal("rt", result.RefreshToken);
        Assert.Equal(3600, result.ExpiresIn);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", request.Body);
        Assert.Contains("device_code=dc-123", Uri.UnescapeDataString(request.Body!));
    }

    [Fact]
    public async Task Refresh_PostsRefreshTokenGrant()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new
            {
                access_token = "at-2",
                refresh_token = "rt-2",
                expires_in = 4200,
                scope = "Files.ReadWrite.All",
            }),
        };
        var client = CreateClient(handler);

        var result = await client.RefreshAsync(ClientId, "rt-old");

        Assert.Equal("at-2", result.AccessToken);
        Assert.Equal("rt-2", result.RefreshToken);
        Assert.Equal(4200, result.ExpiresIn);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("grant_type=refresh_token", request.Body);
        Assert.Contains(Uri.EscapeDataString("rt-old"), request.Body);
    }

    [Fact]
    public async Task GetDeltaPage_MapsFolderFileRemovedAndPagingLinks()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object>
            {
                ["value"] = new object[]
                {
                    new
                    {
                        id = "root-id",
                        name = "root",
                        folder = new { childCount = 3 },
                        root = new { },
                        parentReference = new { id = "root-id", path = "/drive" },
                        lastModifiedDateTime = "2026-09-01T08:00:00Z",
                        ctag = "root-ctag",
                    },
                    new
                    {
                        id = "folder-1",
                        name = "合同",
                        folder = new { childCount = 2 },
                        parentReference = new { id = "root-id", path = "/drive/root:" },
                        lastModifiedDateTime = "2026-09-01T08:00:00Z",
                        ctag = "folder-ctag",
                    },
                    new
                    {
                        id = "file-1",
                        name = "房屋租赁合同.pdf",
                        size = 2048,
                        file = new { mimeType = "application/pdf" },
                        parentReference = new { id = "folder-1", path = "/drive/root:/合同" },
                        lastModifiedDateTime = "2026-09-02T09:30:00Z",
                        ctag = "file-ctag",
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "gone-1",
                        ["name"] = "deleted",
                        ["parentReference"] = new { id = "folder-1", path = "/drive/root:/合同" },
                        ["lastModifiedDateTime"] = "2026-09-03T10:00:00Z",
                        ["@removed"] = new { reason = "deleted" },
                    },
                },
                ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/me/drive/root/delta?$skiptoken=next",
            }),
        };
        var client = CreateClient(handler);

        var page = await client.GetDeltaPageAsync("at", "https://graph.microsoft.com/v1.0/me/drive/root/delta");

        Assert.Equal(4, page.Items.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/root/delta?$skiptoken=next", page.NextLink);
        Assert.Null(page.DeltaLink);

        var root = page.Items[0];
        Assert.True(root.IsFolder);
        Assert.Equal("/drive", root.ParentPath);

        var folder = page.Items[1];
        Assert.True(folder.IsFolder);
        Assert.Null(folder.MimeType);

        var file = page.Items[2];
        Assert.False(file.IsFolder);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal(2048, file.Size);
        Assert.Equal("/drive/root:/合同", file.ParentPath);

        var removed = page.Items[3];
        Assert.True(removed.IsRemoved);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/root/delta", request.Url);
        Assert.Equal("Bearer at", request.Authorization);
    }

    [Fact]
    public async Task GetDeltaPage_ThrottledResponse_ThrowsWithRetryAfter()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(
                429,
                new { error = new { code = "activityLimitReached" } },
                new Dictionary<string, string> { ["Retry-After"] = "30" }),
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<OneDriveGraphException>(
            () => client.GetDeltaPageAsync("at", "https://graph.microsoft.com/v1.0/me/drive/root/delta"));

        Assert.Equal(429, error.StatusCode);
        Assert.Equal(30, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task GetDeltaPage_GoneResponse_Throws410()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(
                410,
                new { error = new { code = "resyncRequired" } }),
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<OneDriveGraphException>(
            () => client.GetDeltaPageAsync("at", "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=stale"));

        Assert.Equal(410, error.StatusCode);
    }

    [Fact]
    public async Task GetDrive_MapsQuotaAndType()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new
            {
                id = "drive-1",
                driveType = "personal",
                quota = new { used = 378_000_000_000, total = 1_100_000_000_000 },
            }),
        };
        var client = CreateClient(handler);

        var drive = await client.GetDriveAsync("at");

        Assert.Equal("drive-1", drive.DriveId);
        Assert.Equal("personal", drive.DriveType);
        Assert.Equal(378_000_000_000, drive.QuotaUsed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive", request.Url);
        Assert.Equal("Bearer at", request.Authorization);
    }

    [Fact]
    public async Task PollDeviceCode_ErrorResponse_ThrowsWithStatusCode()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(400, new
            {
                error = "access_denied",
                error_description = "The user denied the access.",
            }),
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<OneDriveGraphException>(
            () => client.PollDeviceCodeAsync(ClientId, "dc-123"));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("access_denied", error.Message);
    }
}
