using Microsoft.EntityFrameworkCore;
using Pim.Core.Data;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Endpoints;
using Pim.Module.Calendar.Entities;
using Pim.Module.Files.Entities;
using Pim.Module.Mobile.Entities;
using Pim.Module.PcTracker.Entities;
using Pim.Module.QuickNotes.Entities;
using Xunit;

namespace Pim.UnitTests.DataIsolation;

/// <summary>
/// 集中式数据隔离测试：IUserOwnedEntity 全局查询过滤器的行为与完整性防护。
/// 关键语义：
/// - 业务上下文（有当前用户）：只能看到本人的数据；
/// - 系统上下文（CurrentUserId == null，后台任务/启动引导/手工构造）：不过滤；
/// - 与软删过滤器以 AND 组合；
/// - 反射防护：任何带映射 UserId 列的实体必须实现 IUserOwnedEntity，且过滤器必须挂载。
/// </summary>
public class UserIsolationFilterTests
{
    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public FakeCurrentUser(Guid? userId) => UserId = userId;
        public Guid? UserId { get; }
        public string? Role => "user";
    }

    static UserIsolationFilterTests()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(MobileLocationPointEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(PcCategoryEntity).Assembly);
    }

    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static PimDbContext NewDb(string name, Guid? asUser)
        => new(new DbContextOptionsBuilder<PimDbContext>().UseInMemoryDatabase(name).Options,
               new FakeCurrentUser(asUser));

    // ---------- 反射防护 ----------

    [Fact]
    public void AllUserOwnedEntities_HaveQueryFilter()
    {
        using var db = NewDb($"iso-guard-{Guid.NewGuid()}", UserA);
        var missing = db.Model.GetEntityTypes()
            .Where(t => typeof(IUserOwnedEntity).IsAssignableFrom(t.ClrType))
            .Where(t => t.GetQueryFilter() is null)
            .Select(t => t.ClrType.Name)
            .ToList();
        Assert.True(missing.Count == 0,
            "以下 IUserOwnedEntity 实体未挂查询过滤器: " + string.Join(", ", missing));
    }

    [Fact]
    public void AllEntitiesWithMappedUserIdColumn_ImplementUserOwned()
    {
        using var db = NewDb($"iso-guard-{Guid.NewGuid()}", UserA);
        var violations = db.Model.GetEntityTypes()
            .Where(t => !typeof(IUserOwnedEntity).IsAssignableFrom(t.ClrType))
            .Where(t => t.FindProperty("UserId") is { } p
                        && p.ClrType == typeof(Guid)
                        && p.GetColumnName() is not null)
            .Select(t => t.ClrType.Name)
            .ToList();
        Assert.True(violations.Count == 0,
            "以下实体有映射的非空 Guid UserId 列却未实现 IUserOwnedEntity（新实体必须实现，否则会造成越权）: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void IsolationFilter_AllowsAllWhenNoCurrentUser()
    {
        // 系统上下文：过滤器表达式必须允许全部行（后台任务语义）
        using var db = NewDb($"iso-sys-{Guid.NewGuid()}", null);
        db.Set<QuickNoteEntity>().AddRange(
            new QuickNoteEntity { UserId = UserA, ContentMarkdown = "a" },
            new QuickNoteEntity { UserId = UserB, ContentMarkdown = "b" });
        db.SaveChanges();

        Assert.Equal(2, db.Set<QuickNoteEntity>().Count());
    }

    // ---------- QuickNotes（软删实体：过滤器组合） ----------

    [Fact]
    public async Task QuickNotes_UserSeesOnlyOwn()
    {
        var name = $"iso-qn-{Guid.NewGuid()}";
        using (var seed = NewDb(name, null))
        {
            seed.Set<QuickNoteEntity>().AddRange(
                new QuickNoteEntity { UserId = UserA, ContentMarkdown = "note-a" },
                new QuickNoteEntity { UserId = UserB, ContentMarkdown = "note-b" });
            await seed.SaveChangesAsync();
        }

        using var asA = NewDb(name, UserA);
        var notesA = await asA.Set<QuickNoteEntity>().ToListAsync();
        Assert.Single(notesA);
        Assert.Equal("note-a", notesA[0].ContentMarkdown);

        using var asB = NewDb(name, UserB);
        var notesB = await asB.Set<QuickNoteEntity>().ToListAsync();
        Assert.Single(notesB);
        Assert.Equal("note-b", notesB[0].ContentMarkdown);
    }

    [Fact]
    public async Task QuickNotes_GetById_OtherUsersNote_IsInvisible()
    {
        var name = $"iso-qn-{Guid.NewGuid()}";
        Guid noteBId;
        using (var seed = NewDb(name, null))
        {
            var noteB = new QuickNoteEntity { UserId = UserB, ContentMarkdown = "note-b" };
            seed.Set<QuickNoteEntity>().Add(noteB);
            await seed.SaveChangesAsync();
            noteBId = noteB.Id;
        }

        using var asA = NewDb(name, UserA);
        // 服务端「先查后改/删」模式在查询阶段就返回 null → 上层得到 404，无法触及他人数据
        Assert.Null(await asA.Set<QuickNoteEntity>().FirstOrDefaultAsync(n => n.Id == noteBId));
    }

    [Fact]
    public async Task QuickNotes_SoftDeleteAndIsolation_FiltersCompose()
    {
        var name = $"iso-qn-{Guid.NewGuid()}";
        using (var seed = NewDb(name, null))
        {
            seed.Set<QuickNoteEntity>().AddRange(
                new QuickNoteEntity { UserId = UserA, ContentMarkdown = "a-active" },
                new QuickNoteEntity { UserId = UserA, ContentMarkdown = "a-deleted", DeletedAt = DateTimeOffset.UtcNow },
                new QuickNoteEntity { UserId = UserB, ContentMarkdown = "b-active", DeletedAt = DateTimeOffset.UtcNow },
                new QuickNoteEntity { UserId = UserB, ContentMarkdown = "b-live" });
            await seed.SaveChangesAsync();
        }

        using var asA = NewDb(name, UserA);
        var notesA = await asA.Set<QuickNoteEntity>().ToListAsync();
        Assert.Single(notesA);
        Assert.Equal("a-active", notesA[0].ContentMarkdown);

        // 系统上下文仍受软删过滤（AND 组合），但不受用户隔离
        using var sys = NewDb(name, null);
        var sysNotes = await sys.Set<QuickNoteEntity>().ToListAsync();
        Assert.Equal(2, sysNotes.Count);
    }

    // ---------- Calendar / Mobile / Files / Infrastructure 代表实体 ----------

    [Fact]
    public async Task AvailabilityWindow_UserSeesOnlyOwn()
    {
        var name = $"iso-aw-{Guid.NewGuid()}";
        using (var seed = NewDb(name, null))
        {
            seed.Set<AvailabilityWindowEntity>().AddRange(
                new AvailabilityWindowEntity { UserId = UserA, StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1) },
                new AvailabilityWindowEntity { UserId = UserB, StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(2) });
            await seed.SaveChangesAsync();
        }

        using var asA = NewDb(name, UserA);
        Assert.Single(await asA.Set<AvailabilityWindowEntity>().ToListAsync());
        using var asB = NewDb(name, UserB);
        Assert.Single(await asB.Set<AvailabilityWindowEntity>().ToListAsync());
        using var sys = NewDb(name, null);
        Assert.Equal(2, await sys.Set<AvailabilityWindowEntity>().CountAsync());
    }

    [Fact]
    public async Task MobileLocationPoint_UserSeesOnlyOwn()
    {
        var name = $"iso-mlp-{Guid.NewGuid()}";
        using (var seed = NewDb(name, null))
        {
            seed.Set<MobileLocationPointEntity>().AddRange(
                new MobileLocationPointEntity { UserId = UserA, DeviceId = "dev-a" },
                new MobileLocationPointEntity { UserId = UserB, DeviceId = "dev-b" });
            await seed.SaveChangesAsync();
        }

        using var asA = NewDb(name, UserA);
        var points = await asA.Set<MobileLocationPointEntity>().ToListAsync();
        Assert.Single(points);
        Assert.Equal("dev-a", points[0].DeviceId);
    }

    [Fact]
    public async Task FileProvider_UserSeesOnlyOwn()
    {
        var name = $"iso-fp-{Guid.NewGuid()}";
        using (var seed = NewDb(name, null))
        {
            seed.Set<FileProviderEntity>().AddRange(
                new FileProviderEntity { UserId = UserA, Provider = "nextcloud" },
                new FileProviderEntity { UserId = UserB, Provider = "local" });
            await seed.SaveChangesAsync();
        }

        using var asB = NewDb(name, UserB);
        var providers = await asB.Set<FileProviderEntity>().ToListAsync();
        Assert.Single(providers);
        Assert.Equal("local", providers[0].Provider);
    }

    [Fact]
    public async Task RefreshToken_And_EndpointStatus_UserSeesOnlyOwn()
    {
        var name = $"iso-infra-{Guid.NewGuid()}";
        using (var seed = NewDb(name, null))
        {
            seed.RefreshTokens.AddRange(
                new RefreshTokenEntity { UserId = UserA, TokenHash = "hash-a", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) },
                new RefreshTokenEntity { UserId = UserB, TokenHash = "hash-b", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            seed.EndpointStatuses.AddRange(
                new EndpointStatusEntity { UserId = UserA, DeviceId = "dev-a" },
                new EndpointStatusEntity { UserId = UserB, DeviceId = "dev-b" });
            await seed.SaveChangesAsync();
        }

        using var asA = NewDb(name, UserA);
        Assert.Single(await asA.RefreshTokens.ToListAsync());
        Assert.Single(await asA.EndpointStatuses.ToListAsync());
        Assert.Equal("dev-a", (await asA.EndpointStatuses.SingleAsync()).DeviceId);
    }

    // ---------- 写入后再读：跨上下文可见性 ----------

    [Fact]
    public async Task WriteInUserContext_NotVisibleToOtherUser()
    {
        var name = $"iso-write-{Guid.NewGuid()}";
        Guid noteId;
        using (var asA = NewDb(name, UserA))
        {
            var note = new QuickNoteEntity { UserId = UserA, ContentMarkdown = "created-by-a" };
            asA.Set<QuickNoteEntity>().Add(note);
            await asA.SaveChangesAsync();
            noteId = note.Id;
        }

        using var asB = NewDb(name, UserB);
        Assert.Null(await asB.Set<QuickNoteEntity>().FirstOrDefaultAsync(n => n.Id == noteId));
        using var sys = NewDb(name, null);
        Assert.NotNull(await sys.Set<QuickNoteEntity>().FirstOrDefaultAsync(n => n.Id == noteId));
    }
}
