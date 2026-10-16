using Xunit;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Google;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// FileProviderAuthorizationService 测试（工单 05 / F1、F2、F4）：
/// 绑定启动、状态流转 pending→connected/expired/denied、令牌加密落库、断开清理、
/// **多账号并存**（不同账号开出新行）与**同账号重绑复用同一行**，
/// 以及浏览器跳转授权的 state 校验。
/// </summary>
public class FileProviderAuthorizationServiceTests
{
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-1111-2222-3333-444444444466");
    private static readonly Guid OtherUserId = Guid.Parse("eeeeeeee-1111-2222-3333-444444444467");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"files-bind-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderAuthorizationService CreateService(PimDbContext db, FakeOneDriveGraphClient graph)
        => new(
            db,
            new FilesProviderHarness(db, graph, new TestSecretProtector(), new FixedClock(Now)).Providers,
            new IFileProviderAuthorizer[] { new OneDriveAuthorizer(graph), new GoogleAuthorizer(new GoogleDriveClient(new StubHttpClientFactory(new StubHttpHandler { Responder = _ => new HttpResponseMessage() }))) },
            new TestSecretProtector(),
            NullLogger<FileProviderAuthorizationService>.Instance,
            new FixedClock(Now));

    [Fact]
    public async Task StartBinding_CreatesPendingProvider_AndReturnsUserCode()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var result = await service.StartAsync(UserId, "onedrive", "cid-1");

        Assert.Equal("USER-CODE", result.UserCode);
        Assert.Equal("https://www.microsoft.com/link", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        Assert.Equal("device-code", result.Mode);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.UserId == UserId && p.Provider == "onedrive");
        Assert.Equal("pending", provider.Status);
        Assert.Equal("cid-1", provider.ClientId);
        Assert.NotNull(provider.PendingAuthorizationSecretEncrypted);
        // 设备码必须经加密器存储（测试加密器带 protected:: 前缀，非明文等值）
        var stored = Encoding.UTF8.GetString(provider.PendingAuthorizationSecretEncrypted!);
        Assert.NotEqual("device-code", stored);
        Assert.StartsWith("protected::", stored);
        Assert.NotNull(provider.PendingAuthorizationExpiresAt);
    }

    /// <summary>
    /// F2 的行为变更：**同一 provider 的不同账号必须各占一行**。
    /// 旧实现「每个用户只允许一条 onedrive 绑定」正是多账号能力的根本障碍，这里把新口径钉死。
    /// </summary>
    [Fact]
    public async Task StartBinding_DifferentAccounts_ProduceSeparateRows()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var first = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, first.ProviderId);
        // 第二个账号：换一个账号身份
        graph.Me = new OneDriveAccountInfo("acc-2", "Second User");
        graph.Drive = new OneDriveDriveInfo("drive-2", "personal", 1, 2);
        var second = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, second.ProviderId);

        Assert.NotEqual(first.ProviderId, second.ProviderId);
        var rows = await db.Set<FileProviderEntity>()
            .Where(p => p.UserId == UserId && p.Provider == "onedrive")
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "acc-1", "acc-2" }, rows.Select(r => r.AccountId).OrderBy(a => a).ToArray());
        // 两个账号的令牌与游标各自独立
        Assert.All(rows, row => Assert.Equal("connected", row.Status));
    }

    /// <summary>
    /// 同一个账号重复绑定必须回到同一行：唯一键 (user_id, provider, account_id) 不允许两行，
    /// 且旧游标必须作废（否则新授权会带着旧盘的游标去增量，游标语义错乱）。
    /// </summary>
    [Fact]
    public async Task StartBinding_SameAccountRebind_ReusesExistingRowAndResetsCursor()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var first = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, first.ProviderId);
        var existing = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == first.ProviderId);
        existing.DeltaLink = "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=old";
        existing.SyncedItemCount = 123;
        await db.SaveChangesAsync();

        var second = await service.StartAsync(UserId, "onedrive", "cid-1");
        var status = await service.GetStatusAsync(UserId, second.ProviderId);

        Assert.Equal("connected", status.Status);
        var rows = await db.Set<FileProviderEntity>()
            .Where(p => p.UserId == UserId && p.Provider == "onedrive")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(first.ProviderId, row.Id);
        Assert.Null(row.DeltaLink);
        Assert.Equal(0, row.SyncedItemCount);
        Assert.Equal("connected", row.Status);
    }

    [Fact]
    public async Task BindingStatus_Confirmed_PersistsTokens_Drive_AndClearsPendingSecret()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("connected", status.Status);
        Assert.Equal("drive-1", status.DriveId);
        Assert.Equal("acc-1", status.AccountId);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        Assert.Equal("connected", provider.Status);
        Assert.Equal("drive-1", provider.DriveId);
        Assert.NotNull(provider.RefreshTokenEncrypted);
        Assert.Null(provider.PendingAuthorizationSecretEncrypted);
        Assert.Null(provider.UserCode);
        Assert.True(provider.TokenExpiresAt > Now.AddMinutes(30));
        // 第一个连接成功的账号自动成为附件/闪念的默认落点
        Assert.True(provider.IsDefault);
    }

    [Fact]
    public async Task BindingStatus_Pending_WhenAuthorizationStillPending()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient
        {
            PollException = new OneDriveGraphException(400, null, "authorization_pending"),
        };
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("pending", status.Status);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        Assert.Equal("pending", provider.Status);
    }

    [Fact]
    public async Task BindingStatus_Expired_WhenDeviceCodeExpired()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient
        {
            PollException = new OneDriveGraphException(400, null, "expired_token"),
        };
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("expired", status.Status);
    }

    [Fact]
    public async Task BindingStatus_Denied_WhenUserRejected()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient
        {
            PollException = new OneDriveGraphException(400, null, "access_denied"),
        };
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("denied", status.Status);
    }

    [Fact]
    public async Task BindingStatus_DeviceCodeExceededWallClock_ExpiresWithoutPolling()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        provider.PendingAuthorizationExpiresAt = Now.AddMinutes(-1);
        db.SaveChanges();

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("expired", status.Status);
        Assert.Equal(0, graph.PollCalls);
    }

    [Fact]
    public async Task BindingStatus_OtherUsersProvider_Throws()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        // 服务按显式 userId 校验归属（与 EF 全局过滤双保险）
        await Assert.ThrowsAsync<DomainException>(
            () => service.GetStatusAsync(OtherUserId, start.ProviderId));
    }

    [Fact]
    public async Task BindingStatus_SlowDown_ReportsPollIntervalHint()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient
        {
            PollException = new OneDriveGraphException(400, null, "slow_down; please retry with increasing interval"),
        };
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("pending", status.Status);
        Assert.Equal(7, status.PollIntervalSeconds);
    }

    [Fact]
    public async Task BindingStatus_RebindDuringPoll_DiscardsStaleResult()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        // Graph 往返期间模拟「同一行被新一轮绑定偷换一次性机密」
        graph.OnPollAsync = () =>
        {
            var provider = db.Set<FileProviderEntity>().Single(p => p.Status == "pending");
            provider.PendingAuthorizationSecretEncrypted = Encoding.UTF8.GetBytes("protected::new-device-code");
            provider.UserCode = "NEW-CODE";
            db.SaveChanges();
        };
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        // 旧设备码的授权结果必须作废，绑定保持 pending 等待新设备码
        Assert.Equal("pending", status.Status);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        Assert.Equal("pending", provider.Status);
        Assert.Null(provider.RefreshTokenEncrypted);
        Assert.NotNull(provider.PendingAuthorizationSecretEncrypted);
    }

    [Fact]
    public async Task Disconnect_RemovesProviderAndItsItems()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = start.ProviderId,
            ExternalFileId = "file-1",
            Path = "/a.txt",
            Name = "a.txt",
            ItemType = "file",
        });
        db.SaveChanges();

        await service.DisconnectAsync(UserId, start.ProviderId);

        Assert.Equal(0, await db.Set<FileProviderEntity>().CountAsync(p => p.Id == start.ProviderId));
        Assert.Equal(0, await db.Set<FileItemEntity>().CountAsync(i => i.ProviderId == start.ProviderId));
    }

    /// <summary>默认账号被断开后必须补位，否则附件/闪念会「没有落点」。</summary>
    [Fact]
    public async Task Disconnect_DefaultAccount_PromotesAnotherConnectedAccount()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var first = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, first.ProviderId);

        graph.Me = new OneDriveAccountInfo("acc-2", "Second User");
        var second = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, second.ProviderId);
        Assert.True((await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == first.ProviderId)).IsDefault);

        await service.DisconnectAsync(UserId, first.ProviderId);

        var remaining = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == second.ProviderId);
        Assert.True(remaining.IsDefault);
    }

    [Fact]
    public async Task Disconnect_OtherUsersProvider_Throws()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        await Assert.ThrowsAsync<DomainException>(
            () => service.DisconnectAsync(OtherUserId, start.ProviderId));
    }

    [Fact]
    public async Task StartBinding_WithEmptyClientId_Throws()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(UserId, "onedrive", "  "));
    }

    /// <summary>未注册的 provider key 必须给出明确错误，而不是建一条永远 pending 的脏行。</summary>
    [Fact]
    public async Task StartBinding_WithUnsupportedProvider_ThrowsAndLeavesNoRow()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.StartAsync(UserId, "nextcloud", "cid-1"));

        Assert.Equal(5323, error.ErrorCode);
        Assert.Equal(0, await db.Set<FileProviderEntity>().CountAsync());
    }

    /// <summary>
    /// F2：切换默认账号（附件/闪念图片的落点）。同时锁定「每个用户至多一个默认账号」。
    /// </summary>
    [Fact]
    public async Task SetDefaultAsync_SwitchesExactlyOneDefault()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var first = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, first.ProviderId);
        graph.Me = new OneDriveAccountInfo("acc-2", "Second User");
        var second = await service.StartAsync(UserId, "onedrive", "cid-1");
        await service.GetStatusAsync(UserId, second.ProviderId);

        await service.SetDefaultAsync(UserId, second.ProviderId);

        var rows = await db.Set<FileProviderEntity>().Where(p => p.UserId == UserId).ToListAsync();
        Assert.True(rows.Single(r => r.Id == second.ProviderId).IsDefault);
        Assert.False(rows.Single(r => r.Id == first.ProviderId).IsDefault);
        Assert.Single(rows.Where(r => r.IsDefault));
    }

    /// <summary>未连接的账号不能被设为默认落点。</summary>
    [Fact]
    public async Task SetDefaultAsync_PendingAccount_Throws5321()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());
        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SetDefaultAsync(UserId, start.ProviderId));

        Assert.Equal(5321, error.ErrorCode);
    }

    // ---- Google 浏览器跳转授权（F4.1）----

    /// <summary>
    /// 回调必须校验一次性 state：伪造/过期的 state 一律拒绝，
    /// 且**不因为「没有登录态」而放行**（回调端点是匿名的，这是它唯一的安全边界）。
    /// </summary>
    [Fact]
    public async Task CompleteRedirect_WithUnknownState_IsRejected()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<FileProviderAuthorizationFailedException>(
            () => service.CompleteRedirectAsync("google", "code-1", "forged-state", null));
    }

    [Fact]
    public async Task CompleteRedirect_WhenProviderReturnsError_IsRejectedAsDenied()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<FileProviderAuthorizationDeniedException>(
            () => service.CompleteRedirectAsync("google", null, "state", "access_denied"));
    }

    /// <summary>Google 缺凭据时必须**明确报错**，不能静默建一条 pending 行。</summary>
    [Fact]
    public async Task StartGoogleAuthorization_WithoutCredentials_FailsExplicitlyAndLeavesNoRow()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<FileProviderAuthorizationFailedException>(
            () => service.StartAsync(UserId, "google", null));

        Assert.Equal(0, await db.Set<FileProviderEntity>().CountAsync());
    }
}
