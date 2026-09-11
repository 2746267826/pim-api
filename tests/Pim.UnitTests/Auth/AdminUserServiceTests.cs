using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Auth;

public class AdminUserServiceTests
{
    private static PimDbContext NewDb()
        => new(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"admin-user-svc-{Guid.NewGuid()}")
            .Options);

    private static AdminUserService NewService(PimDbContext db)
        => new(db, new AuditLogService(db));

    private static UserEntity NewUser(string name, string role = "user", bool isActive = true,
        DateTimeOffset? createdAt = null)
        => new()
        {
            Username = name,
            Email = $"{name}@example.com",
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task ListUsers_ReturnsAllOrderedByCreatedAt()
    {
        await using var db = NewDb();
        var b = NewUser("bob", createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        var a = NewUser("amy", createdAt: DateTimeOffset.UtcNow.AddDays(-5));
        db.Users.AddRange(b, a);
        await db.SaveChangesAsync();

        var users = await NewService(db).ListUsersAsync(CancellationToken.None);

        Assert.Equal(2, users.Count);
        Assert.Equal("amy", users[0].Username);
        Assert.Equal("bob", users[1].Username);
    }

    [Fact]
    public async Task ChangeRole_PromoteUserToAdmin_Succeeds()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var user = NewUser("amy");
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(admin.Id, user.Id, "admin", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.Ok, result.Status);
        Assert.Equal("admin", result.User!.Role);
        Assert.Equal("admin", (await db.Users.FindAsync(user.Id))!.Role);
    }

    [Fact]
    public async Task ChangeRole_DemoteAdmin_WhenOtherAdminExists_Succeeds()
    {
        await using var db = NewDb();
        var a1 = NewUser("boss1", role: "admin");
        var a2 = NewUser("boss2", role: "admin");
        db.Users.AddRange(a1, a2);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(a1.Id, a2.Id, "user", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.Ok, result.Status);
        Assert.Equal("user", (await db.Users.FindAsync(a2.Id))!.Role);
    }

    [Fact]
    public async Task ChangeRole_DemoteAdmin_WhenOnlyInactiveAdminExists_Protected()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin", isActive: true);
        var inactiveAdmin = NewUser("exboss", role: "admin", isActive: false);
        db.Users.AddRange(admin, inactiveAdmin);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(admin.Id, admin.Id, "user", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.LastAdminProtected, result.Status);
        Assert.Equal("admin", (await db.Users.FindAsync(admin.Id))!.Role);
    }

    [Fact]
    public async Task ChangeRole_DemoteLastAdmin_Protected()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(admin.Id, admin.Id, "user", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.LastAdminProtected, result.Status);
        Assert.Equal("admin", (await db.Users.FindAsync(admin.Id))!.Role);
    }

    [Fact]
    public async Task ChangeRole_InvalidRole_Rejected()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var user = NewUser("amy");
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(admin.Id, user.Id, "superuser", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.InvalidRole, result.Status);
        Assert.Equal("user", (await db.Users.FindAsync(user.Id))!.Role);
    }

    [Fact]
    public async Task ChangeRole_TargetNotFound_ReturnsNotFound()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(admin.Id, Guid.NewGuid(), "admin", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ChangeRole_SameRole_NoOpWithoutAudit()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var user = NewUser("amy");
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        var result = await NewService(db).ChangeRoleAsync(admin.Id, user.Id, "user", CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.Ok, result.Status);
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ChangeRole_Success_WritesAuditLog()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var user = NewUser("amy");
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        await NewService(db).ChangeRoleAsync(admin.Id, user.Id, "admin", CancellationToken.None);

        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal(admin.Id, log.UserId);
        Assert.Equal("admin.user.change_role", log.Action);
        Assert.Equal("user", log.ResourceType);
        Assert.Equal(user.Id.ToString(), log.ResourceId);
        Assert.Equal("admin", log.Source);
        Assert.Contains("user", log.MetadataJson);
        Assert.Contains("admin", log.MetadataJson);
    }

    [Fact]
    public async Task SetActive_DisableNormalUser_Succeeds()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var user = NewUser("amy");
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        var result = await NewService(db).SetActiveAsync(admin.Id, user.Id, false, CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.Ok, result.Status);
        Assert.False((await db.Users.FindAsync(user.Id))!.IsActive);
    }

    [Fact]
    public async Task SetActive_DisableLastActiveAdmin_Protected()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var inactiveAdmin = NewUser("exboss", role: "admin", isActive: false);
        db.Users.AddRange(admin, inactiveAdmin);
        await db.SaveChangesAsync();

        var result = await NewService(db).SetActiveAsync(admin.Id, admin.Id, false, CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.LastAdminProtected, result.Status);
        Assert.True((await db.Users.FindAsync(admin.Id))!.IsActive);
    }

    [Fact]
    public async Task SetActive_DisableAdmin_WhenOtherActiveAdminExists_Succeeds()
    {
        await using var db = NewDb();
        var a1 = NewUser("boss1", role: "admin");
        var a2 = NewUser("boss2", role: "admin");
        db.Users.AddRange(a1, a2);
        await db.SaveChangesAsync();

        var result = await NewService(db).SetActiveAsync(a1.Id, a2.Id, false, CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.Ok, result.Status);
        Assert.False((await db.Users.FindAsync(a2.Id))!.IsActive);
    }

    [Fact]
    public async Task SetActive_EnableUser_WritesAuditLog()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        var user = NewUser("amy", isActive: false);
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

        var result = await NewService(db).SetActiveAsync(admin.Id, user.Id, true, CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.Ok, result.Status);
        Assert.True((await db.Users.FindAsync(user.Id))!.IsActive);
        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal("admin.user.enable", log.Action);
    }

    [Fact]
    public async Task SetActive_TargetNotFound_ReturnsNotFound()
    {
        await using var db = NewDb();
        var admin = NewUser("boss", role: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var result = await NewService(db).SetActiveAsync(admin.Id, Guid.NewGuid(), false, CancellationToken.None);

        Assert.Equal(AdminUserActionStatus.NotFound, result.Status);
    }
}
