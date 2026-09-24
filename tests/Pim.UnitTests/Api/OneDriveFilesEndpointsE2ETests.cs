using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// OneDrive 文件端点端到端测试：绑定 → 状态确认 → 手动同步 → 数据落库 → 越权隔离。
/// WebApplicationFactory + InMemory 数据库 + fake IOneDriveGraphClient，全程不访问真实 OneDrive。
/// </summary>
public class OneDriveFilesEndpointsE2ETests
{
    public sealed class E2EGraphClient : IOneDriveGraphClient
    {
        public OneDriveTokenResult Token { get; set; } = new("e2e-access", "e2e-refresh", 3600, "Files.ReadWrite.All");
        public OneDriveDriveInfo Drive { get; set; } = new("drive-e2e", "personal", 1000, 2000);
        public OneDriveAccountInfo Me { get; set; } = new("acc-e2e", "E2E User");
        public OneDriveDeltaPage DeltaPage { get; set; } =
            new([new OneDriveDeltaItem(
                    "f-1", "p-1", "/drive/root:/合同", "房屋租赁合同.pdf",
                    IsFolder: false, IsRemoved: false, Size: 2048,
                    MimeType: "application/pdf", Ctag: "ctag-1",
                    ModifiedAt: DateTimeOffset.Parse("2026-09-19T08:00:00Z")),
                new OneDriveDeltaItem(
                    "p-1", null, "/drive/root:", "合同",
                    IsFolder: true, IsRemoved: false, Size: null,
                    MimeType: null, Ctag: "ctag-p",
                    ModifiedAt: DateTimeOffset.Parse("2026-09-19T08:00:00Z"))],
                NextLink: null,
                DeltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=e2e");

        public Task<OneDriveDeviceCodeStart> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default)
            => Task.FromResult(new OneDriveDeviceCodeStart("e2e-device", "E2E-CODE", "https://www.microsoft.com/link", 900));

        public Task<OneDriveTokenResult> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct = default)
            => Task.FromResult(Token);

        public Task<OneDriveTokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
            => Task.FromResult(Token);

        public Task<OneDriveDriveInfo> GetDriveAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(Drive);

        public Task<OneDriveAccountInfo> GetMeAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(Me);

        public Task<OneDriveDeltaPage> GetDeltaPageAsync(string accessToken, string url, CancellationToken ct = default)
            => Task.FromResult(DeltaPage);

        public List<(string AccessToken, string ItemId)> DownloadUrlCalls { get; } = [];
        public List<(string AccessToken, string ItemId, long MaxBytes)> DownloadSmallCalls { get; } = [];
        public List<(string AccessToken, string ItemId, byte[] Bytes, string ContentType)> PutCalls { get; } = [];
        public List<(string AccessToken, string ItemId)> PreviewCalls { get; } = [];
        public List<string> TextContents { get; } = [];

        public Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
        {
            DownloadUrlCalls.Add((accessToken, itemId));
            return Task.FromResult<string?>("https://dl.e2e.example.com/x?tempauth=e2e");
        }

        public Task<string?> GetThumbnailUrlAsync(string accessToken, string itemId, string size, CancellationToken ct = default)
            => Task.FromResult<string?>("https://thumb.e2e.example.com/medium");

        public Task<string?> GetPreviewUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
        {
            PreviewCalls.Add((accessToken, itemId));
            return Task.FromResult<string?>("https://preview.e2e.example.com/embed");
        }

        public Task<OneDriveSmallContent?> DownloadSmallAsync(string accessToken, string itemId, long maxBytes, CancellationToken ct = default)
        {
            DownloadSmallCalls.Add((accessToken, itemId, maxBytes));
            return Task.FromResult<OneDriveSmallContent?>(SmallContent ?? new("e2e 文本内容"u8.ToArray(), "text/plain"));
        }

        /// <summary>DownloadSmallAsync 的返回内容；null 时用默认的 "e2e 文本内容"。</summary>
        public OneDriveSmallContent? SmallContent { get; set; }

        public Task PutSmallContentAsync(string accessToken, string itemId, byte[] bytes, string contentType, CancellationToken ct = default)
        {
            PutCalls.Add((accessToken, itemId, bytes, contentType));
            TextContents.Add(System.Text.Encoding.UTF8.GetString(bytes));
            return Task.CompletedTask;
        }

        public List<(string AccessToken, string ItemId, string? NewName, string? NewParentId)> PatchCalls { get; } = [];
        public List<(string AccessToken, string ItemId)> DeleteCalls { get; } = [];

        public Task<string> PatchItemAsync(string accessToken, string itemId, string? newName, string? newParentId, CancellationToken ct = default)
        {
            PatchCalls.Add((accessToken, itemId, newName, newParentId));
            return Task.FromResult(itemId);
        }

        public Task DeleteItemAsync(string accessToken, string itemId, CancellationToken ct = default)
        {
            DeleteCalls.Add((accessToken, itemId));
            return Task.CompletedTask;
        }

        public Task<string> PutNewFileByPathAsync(string accessToken, string itemPath, byte[] bytes, string contentType, CancellationToken ct = default)
            => Task.FromResult("new-uploaded-item");

        public Task<string?> GetItemWebUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Task.FromResult<string?>("https://onedrive.live.com/redir?resid=x");

        public Task<OneDriveUploadSession> CreateUploadSessionAsync(
            string accessToken,
            string itemPath,
            string fileName,
            CancellationToken ct = default)
            => Task.FromResult(new OneDriveUploadSession(
                "https://upload.example.com/session-test",
                DateTimeOffset.UtcNow.AddHours(1)));
        public Task<OneDriveShareLink> CreateShareLinkAsync(
            string accessToken,
            string itemId,
            OneDriveSharePermission permission,
            DateTimeOffset? expiration,
            CancellationToken ct = default)
            => Task.FromResult(new OneDriveShareLink(
                "https://1drv.ms/" + (permission == OneDriveSharePermission.Edit ? "edit" : "view") + "/" + itemId,
                permission == OneDriveSharePermission.Edit ? "edit" : "view",
                "perm-1",
                expiration));

        public Task RevokeSharePermissionAsync(
            string accessToken,
            string itemId,
            string permissionId,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OneDriveShareLink>> ListSharePermissionsAsync(
            string accessToken,
            string itemId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OneDriveShareLink>>([]);

        public Task<string> CreateFolderAsync(
            string accessToken,
            string folderPath,
            string name,
            CancellationToken ct = default)
            => Task.FromResult("new-folder-item");
        public Task<OneDrivePathItem?> GetItemByPathAsync(
            string accessToken,
            string itemPath,
            CancellationToken ct = default)
            => Task.FromResult<OneDrivePathItem?>(null);
        public Task<OneDrivePathItem?> GetItemByIdAsync(
            string accessToken,
            string itemId,
            CancellationToken ct = default)
            => Task.FromResult<OneDrivePathItem?>(null);



    }
    internal static WebApplicationFactory<Program> CreateFactory(string dbName, E2EGraphClient graph)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(dbName));
                services.AddScoped<IOneDriveGraphClient>(_ => graph);
            });
        });

    internal static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string username)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = username,
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    internal static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Bind_StatusConfirm_Sync_ItemsLandInDatabase()
    {
        var graph = new E2EGraphClient();
        using var factory = CreateFactory($"onedrive-e2e-{Guid.NewGuid()}", graph);
        var anon = factory.CreateClient();
        var aliceToken = await RegisterAndGetTokenAsync(anon, "files-alice");
        var alice = Authed(factory, aliceToken);

        // 1. 启动绑定
        var startResp = await alice.PostAsJsonAsync("/api/v1/files/providers/onedrive", new { clientId = "cid-e2e" });
        Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
        using (var doc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("E2E-CODE", data.GetProperty("userCode").GetString());
            Assert.Equal("https://www.microsoft.com/link", data.GetProperty("verificationUri").GetString());
        }

        var providerId = await GetSingleProviderIdAsync(factory);

        // 2. 轮询绑定状态 → connected（fake 完成 device-code 兑换 + drive/me 读取）
        var statusResp = await alice.GetAsync($"/api/v1/files/providers/{providerId}/binding-status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        using (var doc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("connected", data.GetProperty("status").GetString());
            Assert.Equal("drive-e2e", data.GetProperty("driveId").GetString());
        }

        // 3. 手动同步 → 结果统计
        var syncResp = await alice.PostAsync($"/api/v1/files/providers/{providerId}/sync", null);
        Assert.Equal(HttpStatusCode.OK, syncResp.StatusCode);
        using (var doc = JsonDocument.Parse(await syncResp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetProperty("pagesProcessed").GetInt32());
            Assert.Equal(2, data.GetProperty("itemsApplied").GetInt32());
            Assert.False(data.GetProperty("fullRecrawl").GetBoolean());
        }

        // 4. 元数据落库（只存元数据：路径派生 + 父子链 + ctag）
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var items = await db.Set<FileItemEntity>()
                .Where(item => item.ProviderId == providerId)
                .ToListAsync();
            Assert.Equal(2, items.Count);
            var folder = items.Single(item => item.ExternalFileId == "p-1");
            Assert.Equal("folder", folder.ItemType);
            Assert.Equal("/合同", folder.Path);
            var file = items.Single(item => item.ExternalFileId == "f-1");
            Assert.Equal("/合同/房屋租赁合同.pdf", file.Path);
            Assert.Equal("ctag-1", file.Etag);

            var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == providerId);
            Assert.Equal("connected", provider.Status);
            Assert.Contains("$deltatoken=e2e", provider.DeltaLink);
            Assert.NotNull(provider.RefreshTokenEncrypted);
            // refresh token 必须密文落库
            Assert.DoesNotContain("e2e-refresh", System.Text.Encoding.UTF8.GetString(provider.RefreshTokenEncrypted!));
        }
    }

    [Fact]
    public async Task OtherUser_CannotSeeOrOperateForeignProvider()
    {
        var graph = new E2EGraphClient();
        using var factory = CreateFactory($"onedrive-e2e-iso-{Guid.NewGuid()}", graph);
        var anon = factory.CreateClient();
        var aliceToken = await RegisterAndGetTokenAsync(anon, "files-carol");
        var bobToken = await RegisterAndGetTokenAsync(anon, "files-dave");

        var alice = Authed(factory, aliceToken);
        await alice.PostAsJsonAsync("/api/v1/files/providers/onedrive", new { clientId = "cid-e2e" });
        var providerId = await GetSingleProviderIdAsync(factory);

        // bob 操作 alice 的绑定：状态/断开 → 400（5320），同步 → 404（5104，不泄露存在性）
        var bob = Authed(factory, bobToken);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await bob.GetAsync($"/api/v1/files/providers/{providerId}/binding-status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/v1/files/providers/{providerId}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await bob.DeleteAsync($"/api/v1/files/providers/{providerId}")).StatusCode);

        // 同步端点对不存在的 provider → 404（5104）
        Assert.Equal(HttpStatusCode.NotFound,
            (await alice.PostAsync($"/api/v1/files/providers/{Guid.NewGuid()}/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Disconnect_RemovesProvider_AndSubsequentSyncFails()
    {
        var graph = new E2EGraphClient();
        using var factory = CreateFactory($"onedrive-e2e-disc-{Guid.NewGuid()}", graph);
        var anon = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(anon, "files-eve");
        var user = Authed(factory, token);

        await user.PostAsJsonAsync("/api/v1/files/providers/onedrive", new { clientId = "cid-e2e" });
        var providerId = await GetSingleProviderIdAsync(factory);
        await user.GetAsync($"/api/v1/files/providers/{providerId}/binding-status");

        var deleteResp = await user.DeleteAsync($"/api/v1/files/providers/{providerId}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await user.PostAsync($"/api/v1/files/providers/{providerId}/sync", null)).StatusCode);
    }

    internal static async Task<Guid> GetSingleProviderIdAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var provider = await db.Set<FileProviderEntity>().SingleAsync();
        return provider.Id;
    }
}
