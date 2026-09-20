using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// FileProviderBindingService（文件模块 v2）：只做来源列表读取。
/// Nextcloud 绑定/连接测试随 P4 退役，因此这里锁定的是「只返回当前用户自己的来源」
/// 以及 DTO 字段映射。
/// </summary>
public class FileProviderBindingServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-binding-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderBindingService CreateService(PimDbContext db, Guid? userId)
        => new(db, new StubCurrentUser(userId));

    [Fact]
    public async Task ListProvidersAsync_ReturnsOnlyCurrentUsersProviders()
    {
        await using var db = CreateDb();
        db.Set<FileProviderEntity>().AddRange(
            new FileProviderEntity
            {
                UserId = UserId,
                Provider = "onedrive",
                ClientId = "cid-mine",
                DriveId = "drive-1",
                AccountName = "me@example.com",
                Status = "connected",
                SyncStatus = "idle",
                SyncedItemCount = 42,
            },
            new FileProviderEntity
            {
                UserId = OtherUserId,
                Provider = "onedrive",
                ClientId = "cid-other",
                Status = "connected",
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var providers = await service.ListProvidersAsync();

        var provider = Assert.Single(providers);
        Assert.Equal("onedrive", provider.Provider);
        Assert.Equal("cid-mine", provider.ClientId);
        Assert.Equal("drive-1", provider.DriveId);
        Assert.Equal("me@example.com", provider.AccountName);
        Assert.Equal("idle", provider.SyncStatus);
        Assert.Equal(42, provider.SyncedItemCount);
    }

    [Fact]
    public async Task ListProvidersAsync_WhenNotLoggedIn_Throws1002()
    {
        await using var db = CreateDb();
        var service = CreateService(db, null);

        var error = await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(
            () => service.ListProvidersAsync());

        Assert.Equal(1002, error.ErrorCode);
    }
}
