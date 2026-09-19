using Xunit;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveSyncJob 测试：遍历所有已连接 onedrive provider、单个失败不影响其余、失败写回 provider。
/// </summary>
public class OneDriveSyncJobTests
{
    private static readonly Guid UserIdA = Guid.Parse("eeeeeeee-1111-2222-3333-444444444471");
    private static readonly Guid UserIdB = Guid.Parse("eeeeeeee-1111-2222-3333-444444444472");
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
            .UseInMemoryDatabase($"onedrive-job-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(PimDbContext db, Guid userId, string status, string providerType = "onedrive")
    {
        var provider = new FileProviderEntity
        {
            UserId = userId,
            Provider = providerType,
            ClientId = "cid",
            Status = status,
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes("protected::refresh-token"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static OneDriveSyncJob CreateJob(PimDbContext db, FakeOneDriveGraphClient graph)
    {
        var protector = new TestSecretProtector();
        var scopeFactory = new StubScopeFactory(db, graph, protector);
        return new OneDriveSyncJob(scopeFactory, NullLogger<OneDriveSyncJob>.Instance);
    }

    private sealed class StubScopeFactory(PimDbContext db, FakeOneDriveGraphClient graph, ISecretProtector protector) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope(db, graph, protector);

        private sealed class StubScope(PimDbContext db, FakeOneDriveGraphClient graph, ISecretProtector protector) : IServiceScope
        {
            private readonly ServiceProvider _container = new ServiceCollection()
                .AddSingleton(db)
                .AddSingleton<IOneDriveGraphClient>(graph)
                .AddSingleton(protector)
                .AddSingleton<OneDriveTokenService>()
                .AddSingleton<OneDriveSyncService>()
                .AddLogging()
                .BuildServiceProvider();

            public IServiceProvider ServiceProvider => _container;

            public void Dispose() => _container.Dispose();
        }
    }

    [Fact]
    public async Task RunAll_SyncsEveryConnectedOneDriveProvider()
    {
        await using var db = CreateDb();
        SeedProvider(db, UserIdA, "connected");
        SeedProvider(db, UserIdB, "connected");
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(OneDriveDeltaPageFactory.File("f-a", "a.txt")));
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(OneDriveDeltaPageFactory.File("f-b", "b.txt")));
        var job = CreateJob(db, graph);

        await job.RunAllAsync();

        Assert.Equal(2, await db.Set<FileItemEntity>().CountAsync());
        Assert.Equal(2, graph.DeltaRequests.Count);
    }

    [Fact]
    public async Task RunAll_SkipsPendingAndNonOneDrive_Providers()
    {
        await using var db = CreateDb();
        SeedProvider(db, UserIdA, "pending");
        SeedProvider(db, UserIdB, "connected", providerType: "nextcloud");
        var graph = new FakeOneDriveGraphClient();
        var job = CreateJob(db, graph);

        await job.RunAllAsync();

        Assert.Empty(graph.DeltaRequests);
    }

    [Fact]
    public async Task RunAll_OneProviderFails_OthersStillSync_AndFailureRecorded()
    {
        await using var db = CreateDb();
        var providerA = SeedProvider(db, UserIdA, "connected");
        var providerB = SeedProvider(db, UserIdB, "connected");
        var graph = new FakeOneDriveGraphClient();
        // A：delta 持续 429 耗尽重试预算（3 次后放弃）；B：正常页
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(OneDriveDeltaPageFactory.File("f-b", "b.txt")));
        var job = CreateJob(db, graph);

        var exception = await Record.ExceptionAsync(() => job.RunAllAsync());

        Assert.Null(exception);
        var a = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == providerA.Id);
        var b = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == providerB.Id);
        Assert.Equal("error", a.SyncStatus);
        Assert.NotNull(a.LastError);
        // B 不受 A 影响，正常完成
        Assert.Equal("idle", b.SyncStatus);
        Assert.True(await db.Set<FileItemEntity>().AnyAsync(i => i.ExternalFileId == "f-b"));
    }
}
