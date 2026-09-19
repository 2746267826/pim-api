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
/// OneDriveTokenService 测试：内存缓存命中不刷、过期刷新并加密落库、缓存失效、授权失效错误。
/// </summary>
public class OneDriveTokenServiceTests
{
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-1111-2222-3333-444444444455");
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
            .UseInMemoryDatabase($"onedrive-token-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedConnectedProvider(PimDbContext db)
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
            ClientId = "cid",
            Status = "connected",
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes("protected::refresh-token"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static OneDriveTokenService CreateService(
        PimDbContext db,
        FakeOneDriveGraphClient graph,
        TimeProvider? clock = null)
        => new(db, graph, new TestSecretProtector(), NullLogger<OneDriveTokenService>.Instance, clock ?? new FixedClock(Now));

    [Fact]
    public async Task ValidCachedExpiry_DoesNotCallRefresh()
    {
        await using var db = CreateDb();
        var provider = SeedConnectedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var first = await service.GetAccessTokenAsync(provider.Id);
        var second = await service.GetAccessTokenAsync(provider.Id);

        Assert.Equal("access-token-1", first);
        // 第一遍没有内存缓存 → 刷新一次取 access token；第二遍必须命中内存缓存
        Assert.Equal(1, graph.RefreshCalls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ExpiredToken_Refreshes_AndPersistsEncryptedRefreshToken()
    {
        await using var db = CreateDb();
        var provider = SeedConnectedProvider(db);
        provider.TokenExpiresAt = Now.AddMinutes(-5);
        db.SaveChanges();
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var token = await service.GetAccessTokenAsync(provider.Id);

        Assert.Equal("access-token-1", token);
        Assert.Equal(1, graph.RefreshCalls);
        var updated = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.NotNull(updated.RefreshTokenEncrypted);
        var decrypted = new TestSecretProtector().Unprotect(Encoding.UTF8.GetString(updated.RefreshTokenEncrypted!));
        Assert.Equal("refresh-token", decrypted);
        // 新过期时间 = Now + expires_in(3600) - 120s 缓冲
        Assert.True(updated.TokenExpiresAt > Now.AddMinutes(55));
    }

    [Fact]
    public async Task RefreshResponseWithoutNewRefreshToken_KeepsOldOne()
    {
        await using var db = CreateDb();
        var provider = SeedConnectedProvider(db);
        provider.TokenExpiresAt = Now.AddMinutes(-5);
        db.SaveChanges();
        var graph = new FakeOneDriveGraphClient
        {
            Token = new OneDriveTokenResult("access-token-1", null, 3600, null),
        };
        var service = CreateService(db, graph);

        await service.GetAccessTokenAsync(provider.Id);

        var updated = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        var decrypted = new TestSecretProtector().Unprotect(Encoding.UTF8.GetString(updated.RefreshTokenEncrypted!));
        Assert.Equal("refresh-token", decrypted);
    }

    [Fact]
    public async Task RefreshRejected_ThrowsAuthorizationExpired()
    {
        await using var db = CreateDb();
        var provider = SeedConnectedProvider(db);
        provider.TokenExpiresAt = Now.AddMinutes(-5);
        db.SaveChanges();
        var graph = new FakeOneDriveGraphClient
        {
            RefreshException = new OneDriveGraphException(400, null, "invalid_grant"),
        };
        var service = CreateService(db, graph);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.GetAccessTokenAsync(provider.Id));

        Assert.Contains("重新绑定", error.Message);
        var updated = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("expired", updated.Status);
    }

    [Fact]
    public async Task UnknownProvider_Throws()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.GetAccessTokenAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task InvalidateCached_ForcesRefreshOnNextCall()
    {
        await using var db = CreateDb();
        var provider = SeedConnectedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        await service.GetAccessTokenAsync(provider.Id);
        service.InvalidateCached(provider.Id);
        await service.GetAccessTokenAsync(provider.Id);

        Assert.Equal(2, graph.RefreshCalls);
    }
}
