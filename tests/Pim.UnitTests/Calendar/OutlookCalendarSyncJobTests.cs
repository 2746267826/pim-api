using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookCalendarSyncJobTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private static PimDbContext CreateDb(string? name = null)
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        return new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(name ?? "job-" + Guid.NewGuid()).Options);
    }

    private static async Task<OutlookConnectionEntity> SeedConnectionAsync(
        PimDbContext db, Guid userId, string status = "connected",
        string clientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
    {
        var conn = new OutlookConnectionEntity
        {
            UserId = userId,
            ClientId = clientId,
            Status = status,
            TokenHealth = status == "connected" ? "healthy" : "missing"
        };
        db.Set<OutlookConnectionEntity>().Add(conn);
        await db.SaveChangesAsync();
        return conn;
    }

    private static async Task<OutlookCalendarBindingEntity> SeedFullUserAsync(
        PimDbContext db, Guid userId)
    {
        var conn = await SeedConnectionAsync(db, userId);
        var cal = new CalendarEntity { UserId = userId, Name = "Cal", Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();
        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = conn.Id,
            PimCalendarId = cal.Id,
            GraphCalendarId = "g-" + userId.ToString("N")[..6],
            Name = "Calendar",
            IsSelected = true,
            RemoteState = "active"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();
        return binding;
    }

    [Fact]
    public async Task RunAllAsync_DelegatesToService_ForEachRunnableUser()
    {
        var dbName = "j5-" + Guid.NewGuid();
        using var db = CreateDb(dbName);
        await SeedFullUserAsync(db, UserId);
        await SeedFullUserAsync(db, OtherUserId);

        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var graph = new GraphCalendarClient(
            new StubHttpClientFactory(new ContractGraphHandler()),
            new FakeContractTokenProvider(),
            new StubContractTimeProvider());
        var svc = new OutlookCalendarSyncService(
            new PimDbContext(options), graph,
            new StubContractTimeProvider(), NullLogger<OutlookCalendarSyncService>.Instance);
        var job = new OutlookCalendarSyncJob(svc);

        await job.RunAllAsync();

        using var verifyDb = new PimDbContext(options);
        var batches = await verifyDb.Set<OutlookSyncBatchEntity>().ToListAsync();
        var userIds = batches.Select(b => b.UserId).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(2, userIds.Count);
    }

    // ===== A2: Hangfire initialization contract =====

    [Fact]
    public async Task InitializeAsync_MarksRunningBatchInterrupted()
    {
        var dbName = "init-interrupt-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        using (var seedDb = new PimDbContext(dbOptions))
        {
            seedDb.Set<OutlookSyncBatchEntity>().Add(new OutlookSyncBatchEntity
            {
                UserId = UserId,
                Status = "running",
                Mode = "normal",
                StartedAt = now.AddHours(-2)
            });
            await seedDb.SaveChangesAsync();
        }

        var fakeJobs = new FakeBackgroundJobClient();
        var fakeRecurring = new FakeRecurringJobManager();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new StubTimeProvider { UtcNowValue = now });
        services.AddSingleton<IBackgroundJobClient>(fakeJobs);
        services.AddSingleton<IRecurringJobManager>(fakeRecurring);
        services.AddSingleton<ILogger<CalendarModule>>(NullLogger<CalendarModule>.Instance);
        services.AddScoped(_ => new PimDbContext(dbOptions));
        var module = new CalendarModule();
        await module.InitializeAsync(services.BuildServiceProvider());

        using var verifyDb = new PimDbContext(dbOptions);
        var batches = await verifyDb.Set<OutlookSyncBatchEntity>().OrderBy(b => b.StartedAt).ToListAsync();
        var interrupted = batches.First(b => b.Status == "interrupted");
        Assert.Equal(now, interrupted.FinishedAt);
        Assert.Equal(now, interrupted.UpdatedAt);
    }

    [Fact]
    public async Task InitializeAsync_EnqueuesStartupJob()
    {
        var dbName = "init-enqueue-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        var fakeJobs = new FakeBackgroundJobClient();
        var fakeRecurring = new FakeRecurringJobManager();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new StubTimeProvider { UtcNowValue = now });
        services.AddSingleton<IBackgroundJobClient>(fakeJobs);
        services.AddSingleton<IRecurringJobManager>(fakeRecurring);
        services.AddSingleton<ILogger<CalendarModule>>(NullLogger<CalendarModule>.Instance);
        services.AddScoped(_ => new PimDbContext(dbOptions));
        var module = new CalendarModule();
        await module.InitializeAsync(services.BuildServiceProvider());

        Assert.Single(fakeJobs.CreatedJobs);
        var job = fakeJobs.CreatedJobs[0];
        Assert.Equal(typeof(OutlookCalendarSyncJob), job.Type);
        Assert.Equal("RunAllAsync", job.Method.Name);
    }

    [Fact]
    public async Task InitializeAsync_SchedulesRecurringJob()
    {
        var dbName = "init-recurring-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        var fakeJobs = new FakeBackgroundJobClient();
        var fakeRecurring = new FakeRecurringJobManager();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new StubTimeProvider { UtcNowValue = now });
        services.AddSingleton<IBackgroundJobClient>(fakeJobs);
        services.AddSingleton<IRecurringJobManager>(fakeRecurring);
        services.AddSingleton<ILogger<CalendarModule>>(NullLogger<CalendarModule>.Instance);
        services.AddScoped(_ => new PimDbContext(dbOptions));
        var module = new CalendarModule();
        await module.InitializeAsync(services.BuildServiceProvider());

        Assert.Single(fakeRecurring.AddedOrUpdated);
        var (id, job, cron) = fakeRecurring.AddedOrUpdated[0];
        Assert.Equal("outlook-calendar-sync", id);
        Assert.Equal(typeof(OutlookCalendarSyncJob), job.Type);
        Assert.Equal("RunAllAsync", job.Method.Name);
        Assert.Equal("*/5 * * * *", cron);
    }

    [Fact]
    public async Task InitializeAsync_WithoutHangfire_DoesNotThrow()
    {
        var dbName = "init-no-hangfire-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new StubTimeProvider { UtcNowValue = now });
        services.AddSingleton<ILogger<CalendarModule>>(NullLogger<CalendarModule>.Instance);
        services.AddScoped(_ => new PimDbContext(dbOptions));
        var module = new CalendarModule();
        await module.InitializeAsync(services.BuildServiceProvider());
    }
}

internal sealed class FakeBackgroundJobClient : IBackgroundJobClient
{
    public List<Job> CreatedJobs { get; } = [];

    public string Create(Job job, IState state)
    {
        CreatedJobs.Add(job);
        return Guid.NewGuid().ToString();
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}

internal sealed class FakeRecurringJobManager : IRecurringJobManager
{
    public List<(string Id, Job Job, string Cron)> AddedOrUpdated { get; } = [];

    public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
    {
        AddedOrUpdated.Add((recurringJobId, job, cronExpression));
    }

    public void Trigger(string recurringJobId) { }
    public void RemoveIfExists(string recurringJobId) { }
}
