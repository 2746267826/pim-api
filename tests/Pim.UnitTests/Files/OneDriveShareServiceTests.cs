using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// REQ-21 分享链接（PR-2）。重点是**安全边界**：
/// 敏感路径不得分享（AC-21.4）、他人条目不可见、链接不得进审计（AC-21.4）、
/// 有效期只接受已确认的档位（P6）。
/// </summary>
public class OneDriveShareServiceTests
{
    private static readonly Guid UserId = Guid.Parse("dddddddd-1111-2222-3333-444444444471");
    private static readonly Guid OtherUserId = Guid.Parse("dddddddd-9999-8888-7777-666666666671");

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    /// <summary>记录每次 createLink / revoke 的入参（含完整 URL 的那些**不得**被审计）。</summary>
    private sealed class RecordingGraphClient : IOneDriveGraphClient
    {
        public List<(string ItemId, OneDriveSharePermission Permission, DateTimeOffset? Expiration)> Created { get; } = [];
        public List<(string ItemId, string PermissionId)> Revoked { get; } = [];
        public List<OneDriveShareLink> Existing { get; } = [];

        public Task<OneDriveShareLink> CreateShareLinkAsync(
            string accessToken, string itemId, OneDriveSharePermission permission, DateTimeOffset? expiration, CancellationToken ct = default)
        {
            Created.Add((itemId, permission, expiration));
            var type = permission == OneDriveSharePermission.Edit ? "edit" : "view";
            return Task.FromResult(new OneDriveShareLink(
                $"https://1drv.ms/{type}/{itemId}?e=SECRETTOKEN", type, "perm-1", expiration));
        }

        public Task RevokeSharePermissionAsync(string accessToken, string itemId, string permissionId, CancellationToken ct = default)
        {
            Revoked.Add((itemId, permissionId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OneDriveShareLink>> ListSharePermissionsAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OneDriveShareLink>>(Existing);

        // 其余成员与本用例无关
        public Task<OneDriveDeviceCodeStart> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OneDriveTokenResult> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>token 未命中缓存时会被刷新；返回一个足够长的有效期，避免用例依赖时间。</summary>
        public Task<OneDriveTokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
            => Task.FromResult(new OneDriveTokenResult("access-token", "refresh-token", 3600, "Files.ReadWrite.All"));
        public Task<OneDriveDriveInfo> GetDriveAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OneDriveAccountInfo> GetMeAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OneDriveDeltaPage> GetDeltaPageAsync(string accessToken, string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetThumbnailUrlAsync(string accessToken, string itemId, string size, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetPreviewUrlAsync(string accessToken, string itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OneDriveSmallContent?> DownloadSmallAsync(string accessToken, string itemId, long maxBytes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PutSmallContentAsync(string accessToken, string itemId, byte[] bytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> PatchItemAsync(string accessToken, string itemId, string? newName, string? newParentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteItemAsync(string accessToken, string itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> PutNewFileByPathAsync(string accessToken, string itemPath, byte[] bytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> CreateFolderAsync(string accessToken, string folderPath, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OneDrivePathItem?> GetItemByPathAsync(string accessToken, string itemPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OneDriveUploadSession> CreateUploadSessionAsync(string accessToken, string itemPath, string fileName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetItemWebUrlAsync(string accessToken, string itemId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"share-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>与 OneDriveWriteServiceTests 同款：假保护器 + 未过期 token，避免真实刷新。</summary>
    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (FileProviderEntity Provider, FileItemEntity Item) Seed(
        PimDbContext db, Guid userId, string path, string name, string type = "file")
    {
        var provider = new FileProviderEntity
        {
            UserId = userId,
            Provider = "onedrive",
            Status = "connected",
            ClientId = "test-client",
            // token 未过期 -> 直接用缓存，不触发刷新
            RefreshTokenEncrypted = System.Text.Encoding.UTF8.GetBytes("protected::refresh-token"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();

        var item = new FileItemEntity
        {
            ProviderId = provider.Id,
            Provider = provider,
            ExternalFileId = Guid.NewGuid().ToString("N"),
            Path = path,
            Name = name,
            ItemType = type,
            IsDeleted = false,
        };
        db.Set<FileItemEntity>().Add(item);
        db.SaveChanges();
        return (provider, item);
    }

    private static OneDriveShareService CreateService(
        PimDbContext db, Guid? userId, IOneDriveGraphClient client, StubAuditLog? audit = null)
        => new(
            db,
            client,
            new OneDriveTokenService(db, client, new TestSecretProtector(), clock: new FixedClock(Now)),
            new StubCurrentUser(userId),
            audit ?? new StubAuditLog(),
            new SensitivePathPolicy(null));

    [Fact]
    public async Task CreateAsync_WithViewPermission_ReturnsMicrosoftLink()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        var share = await service.CreateAsync(item.Id, "view", null);

        Assert.Equal("view", share.PermissionType);
        Assert.StartsWith("https://1drv.ms/view/", share.WebUrl);
        var created = Assert.Single(client.Created);
        Assert.Equal(OneDriveSharePermission.View, created.Permission);
        Assert.Null(created.Expiration);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    public async Task CreateAsync_WithConfirmedExpirationChoices_PassesDeadline(int days)
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        await service.CreateAsync(item.Id, "edit", days);

        var created = Assert.Single(client.Created);
        Assert.Equal(OneDriveSharePermission.Edit, created.Permission);
        Assert.NotNull(created.Expiration);
        Assert.InRange(created.Expiration!.Value, DateTimeOffset.UtcNow.AddDays(days - 1), DateTimeOffset.UtcNow.AddDays(days + 1));
    }

    /// <summary>P6：未确认的档位必须明确拒绝，不得静默降级成「不过期」。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(-7)]
    public async Task CreateAsync_WithUnconfirmedExpiration_IsRejected(int days)
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(item.Id, "view", days));

        Assert.Equal(5300, error.ErrorCode);
        Assert.Empty(client.Created);
    }

    /// <summary>AC-21.4（反面）：敏感路径不得生成分享。</summary>
    [Theory]
    [InlineData("/Secrets/密码.txt")]
    [InlineData("/Passwords/清单.txt")]
    public async Task CreateAsync_OnSensitivePath_IsRefused(string path)
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, path, System.IO.Path.GetFileName(path));
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(item.Id, "view", null));

        Assert.Equal(40303, error.ErrorCode);
        // 关键：不得有任何 Graph 调用（不能先建链再报错）
        Assert.Empty(client.Created);
    }

    /// <summary>AC-21.4：审计里绝不能出现完整链接或 webUrl。</summary>
    [Fact]
    public async Task CreateAsync_DoesNotWriteTheLinkIntoAudit()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        var audit = new StubAuditLog();
        var service = CreateService(db, UserId, client, audit);

        await service.CreateAsync(item.Id, "view", null);

        Assert.Contains("files.share_create", audit.Actions);
        // 审计动作名里不含链接；链接只出现在返回值里
        Assert.All(audit.Actions, action => Assert.DoesNotContain("1drv.ms", action));
    }

    [Fact]
    public async Task CreateAsync_ForAnotherUsersItem_IsNotFound()
    {
        await using var db = CreateDb();
        var (_, foreign) = Seed(db, OtherUserId, "/文档/别人的.pdf", "别人的.pdf");
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(foreign.Id, "view", null));

        Assert.Equal(5104, error.ErrorCode);
        Assert.Empty(client.Created);
    }

    [Fact]
    public async Task RevokeAsync_DelegatesToGraphWithPermissionId()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        var audit = new StubAuditLog();
        var service = CreateService(db, UserId, client, audit);

        await service.RevokeAsync(item.Id, "perm-1");

        var revoked = Assert.Single(client.Revoked);
        Assert.Equal("perm-1", revoked.PermissionId);
        Assert.Contains("files.share_revoke", audit.Actions);
    }

    /// <summary>AC-21.4：敏感路径同样不得被撤销接口当作跳板（先拦归属与闸门）。</summary>
    [Fact]
    public async Task RevokeAsync_OnSensitivePath_IsRefused()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/Secrets/密码.txt", "密码.txt");
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.RevokeAsync(item.Id, "perm-1"));

        Assert.Equal(40303, error.ErrorCode);
        Assert.Empty(client.Revoked);
    }

    /// <summary>AC-21.3：列出某条目的分享，供预览面板就地撤销。</summary>
    [Fact]
    public async Task ListForItemAsync_ReturnsExistingLinks()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        client.Existing.Add(new OneDriveShareLink("https://1drv.ms/view/x", "view", "perm-9", null));
        var service = CreateService(db, UserId, client);

        var shares = await service.ListForItemAsync(item.Id);

        var share = Assert.Single(shares);
        Assert.Equal("perm-9", share.PermissionId);
        Assert.Equal("view", share.PermissionType);
    }

    /// <summary>「我的分享」必须跳过敏感路径条目（不得把受保护条目列出来）。</summary>
    [Fact]
    public async Task ListAllAsync_SkipsSensitiveItems()
    {
        await using var db = CreateDb();
        Seed(db, UserId, "/Secrets/密码.txt", "密码.txt");
        var (_, ok) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        client.Existing.Add(new OneDriveShareLink("https://1drv.ms/view/x", "view", "perm-9", null));
        var service = CreateService(db, UserId, client);

        var shares = await service.ListAllAsync();

        Assert.All(shares, s => Assert.DoesNotContain("/Secrets", s.Path));
        Assert.Contains(shares, s => s.ItemId == ok.Id);
    }

    [Fact]
    public async Task CreateAsync_WhenNotLoggedIn_Throws1002()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var service = CreateService(db, null, new RecordingGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(item.Id, "view", null));

        Assert.Equal(1002, error.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownPermissionType_IsRejected()
    {
        await using var db = CreateDb();
        var (_, item) = Seed(db, UserId, "/文档/报告.pdf", "报告.pdf");
        var client = new RecordingGraphClient();
        var service = CreateService(db, UserId, client);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(item.Id, "embed", null));

        Assert.Equal(5300, error.ErrorCode);
        Assert.Empty(client.Created);
    }
}
