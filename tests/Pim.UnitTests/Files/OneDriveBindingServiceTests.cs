using Xunit;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveBindingService 测试：绑定启动（复用唯一约束）、状态流转 pending→connected/expired/denied、token 加密落库、断开清理。
/// </summary>
public class OneDriveBindingServiceTests
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
            .UseInMemoryDatabase($"onedrive-bind-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static OneDriveBindingService CreateService(PimDbContext db, FakeOneDriveGraphClient graph)
        => new(db, graph, new TestSecretProtector(), NullLogger<OneDriveBindingService>.Instance, new FixedClock(Now));

    [Fact]
    public async Task StartBinding_CreatesPendingProvider_AndReturnsUserCode()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var result = await service.StartBindingAsync(UserId, "cid-1");

        Assert.Equal("USER-CODE", result.UserCode);
        Assert.Equal("https://www.microsoft.com/link", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.UserId == UserId && p.Provider == "onedrive");
        Assert.Equal("pending", provider.Status);
        Assert.Equal("cid-1", provider.ClientId);
        Assert.NotNull(provider.DeviceCodeEncrypted);
        // device code 必须经加密器存储（测试加密器带 protected:: 前缀，非明文等值）
        var stored = Encoding.UTF8.GetString(provider.DeviceCodeEncrypted!);
        Assert.NotEqual("device-code", stored);
        Assert.StartsWith("protected::", stored);
        Assert.NotNull(provider.DeviceCodeExpiresAt);
    }

    [Fact]
    public async Task StartBinding_Rebind_UsesSameProviderRow()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var first = await service.StartBindingAsync(UserId, "cid-1");
        var second = await service.StartBindingAsync(UserId, "cid-2");

        Assert.Equal(first.ProviderId, second.ProviderId);
        Assert.Equal(1, await db.Set<FileProviderEntity>().CountAsync(p => p.UserId == UserId && p.Provider == "onedrive"));
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == second.ProviderId);
        Assert.Equal("cid-2", provider.ClientId);
        Assert.Equal("pending", provider.Status);
    }

    [Fact]
    public async Task BindingStatus_Confirmed_PersistsTokens_Drive_AndClearsDeviceCode()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartBindingAsync(UserId, "cid-1");

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

        Assert.Equal("connected", status.Status);
        Assert.Equal("drive-1", status.DriveId);
        Assert.Equal("acc-1", status.AccountId);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        Assert.Equal("connected", provider.Status);
        Assert.Equal("drive-1", provider.DriveId);
        Assert.NotNull(provider.RefreshTokenEncrypted);
        Assert.Null(provider.DeviceCodeEncrypted);
        Assert.Null(provider.UserCode);
        Assert.True(provider.TokenExpiresAt > Now.AddMinutes(30));
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
        var start = await service.StartBindingAsync(UserId, "cid-1");

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

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
        var start = await service.StartBindingAsync(UserId, "cid-1");

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

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
        var start = await service.StartBindingAsync(UserId, "cid-1");

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

        Assert.Equal("denied", status.Status);
    }

    [Fact]
    public async Task BindingStatus_DeviceCodeExceededWallClock_ExpiresWithoutPolling()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartBindingAsync(UserId, "cid-1");
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        provider.DeviceCodeExpiresAt = Now.AddMinutes(-1);
        db.SaveChanges();

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

        Assert.Equal("expired", status.Status);
        Assert.Equal(0, graph.PollCalls);
    }

    [Fact]
    public async Task BindingStatus_OtherUsersProvider_Throws()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartBindingAsync(UserId, "cid-1");

        // 服务按显式 userId 校验归属（与 EF 全局过滤双保险）
        await Assert.ThrowsAsync<DomainException>(
            () => service.GetBindingStatusAsync(OtherUserId, start.ProviderId));
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
        var start = await service.StartBindingAsync(UserId, "cid-1");

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

        Assert.Equal("pending", status.Status);
        Assert.Equal(7, status.PollIntervalSeconds);
    }

    [Fact]
    public async Task BindingStatus_RebindDuringPoll_DiscardsStaleResult()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        // Graph 往返期间模拟「重新绑定」：轮询发生时偷换设备码（复审 I3）
        graph.OnPollAsync = () =>
        {
            var provider = db.Set<FileProviderEntity>().Single();
            provider.DeviceCodeEncrypted = Encoding.UTF8.GetBytes("protected::new-device-code");
            provider.UserCode = "NEW-CODE";
            db.SaveChanges();
        };
        var service = CreateService(db, graph);
        var start = await service.StartBindingAsync(UserId, "cid-1");

        var status = await service.GetBindingStatusAsync(UserId, start.ProviderId);

        // 旧设备码的授权结果必须作废，绑定保持 pending 等待新设备码
        Assert.Equal("pending", status.Status);
        var provider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == start.ProviderId);
        Assert.Equal("pending", provider.Status);
        Assert.Null(provider.RefreshTokenEncrypted);
        Assert.NotNull(provider.DeviceCodeEncrypted);
    }

    [Fact]
    public async Task Disconnect_RemovesProviderAndItsItems()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartBindingAsync(UserId, "cid-1");
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

    [Fact]
    public async Task Disconnect_OtherUsersProvider_Throws()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        var start = await service.StartBindingAsync(UserId, "cid-1");

        await Assert.ThrowsAsync<DomainException>(
            () => service.DisconnectAsync(OtherUserId, start.ProviderId));
    }

    [Fact]
    public async Task StartBinding_WithEmptyClientId_Throws()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.StartBindingAsync(UserId, "  "));
    }
}
