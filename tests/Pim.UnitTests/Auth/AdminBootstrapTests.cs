using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Auth;

public class AdminBootstrapTests
{
    private static PimDbContext NewDb()
        => new(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"admin-bootstrap-{Guid.NewGuid()}")
            .Options);

    private static UserEntity NewUser(string name, string role = "user", bool isActive = true,
        DateTimeOffset? createdAt = null, DateTimeOffset? deletedAt = null)
        => new()
        {
            Username = name,
            Email = $"{name}@example.com",
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            DeletedAt = deletedAt
        };

    [Fact]
    public async Task DetermineRegistrationRole_EmptyTable_ReturnsAdmin()
    {
        await using var db = NewDb();
        var role = await AdminBootstrap.DetermineRegistrationRoleAsync(db, CancellationToken.None);
        Assert.Equal(AdminBootstrap.AdminRole, role);
    }

    [Fact]
    public async Task DetermineRegistrationRole_ExistingUsers_ReturnsUser()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("alice"));
        await db.SaveChangesAsync();

        var role = await AdminBootstrap.DetermineRegistrationRoleAsync(db, CancellationToken.None);
        Assert.Equal(AdminBootstrap.UserRole, role);
    }

    [Fact]
    public async Task ConfirmFirstAdmin_OnlySelf_ReturnsTrue()
    {
        await using var db = NewDb();
        var u = NewUser("alice", role: "admin", createdAt: DateTimeOffset.UtcNow);
        db.Users.Add(u);
        await db.SaveChangesAsync();

        Assert.True(await AdminBootstrap.ConfirmFirstAdminAsync(db, u.Id, u.CreatedAt, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmFirstAdmin_OlderAdminExists_ReturnsFalse()
    {
        await using var db = NewDb();
        var older = NewUser("older", role: "admin", createdAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        var newer = NewUser("newer", role: "admin", createdAt: DateTimeOffset.UtcNow);
        db.Users.AddRange(older, newer);
        await db.SaveChangesAsync();

        // newer 在并发竞争中落败
        Assert.False(await AdminBootstrap.ConfirmFirstAdminAsync(db, newer.Id, newer.CreatedAt, CancellationToken.None));
        // older 保持管理员
        Assert.True(await AdminBootstrap.ConfirmFirstAdminAsync(db, older.Id, older.CreatedAt, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAdminExists_NoUsers_ReturnsNull()
    {
        await using var db = NewDb();
        Assert.Null(await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAdminExists_NoAdmin_PromotesEarliestActiveUser()
    {
        await using var db = NewDb();
        var newer = NewUser("newer", createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        var older = NewUser("older", createdAt: DateTimeOffset.UtcNow.AddDays(-10));
        db.Users.AddRange(newer, older);
        await db.SaveChangesAsync();

        var promoted = await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None);

        Assert.Equal(older.Id, promoted);
        Assert.Equal("admin", (await db.Users.FindAsync(older.Id))!.Role);
        Assert.Equal("user", (await db.Users.FindAsync(newer.Id))!.Role);
    }

    [Fact]
    public async Task EnsureAdminExists_SkipsInactiveAndSoftDeleted_PicksNextActive()
    {
        await using var db = NewDb();
        var softDeleted = NewUser("ghost", createdAt: DateTimeOffset.UtcNow.AddDays(-30), deletedAt: DateTimeOffset.UtcNow.AddDays(-1));
        var inactive = NewUser("inactive", isActive: false, createdAt: DateTimeOffset.UtcNow.AddDays(-20));
        var active = NewUser("active", createdAt: DateTimeOffset.UtcNow.AddDays(-10));
        db.Users.AddRange(softDeleted, inactive, active);
        await db.SaveChangesAsync();

        var promoted = await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None);

        Assert.Equal(active.Id, promoted);
    }

    [Fact]
    public async Task EnsureAdminExists_AllUsersInactive_ReturnsNull()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("inactive", isActive: false));
        await db.SaveChangesAsync();

        Assert.Null(await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAdminExists_ExistingAdmin_NoOp()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        Assert.Null(await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None));
        Assert.Equal(1, await db.Users.CountAsync(u => u.Role == "admin"));
    }

    [Fact]
    public async Task EnsureAdminExists_Idempotent_SecondRunNoOp()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("alice"));
        await db.SaveChangesAsync();

        var first = await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None);
        var second = await AdminBootstrap.EnsureAdminExistsAsync(db, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, await db.Users.CountAsync(u => u.Role == "admin"));
    }
}
