using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Today;
using Pim.Core.Today;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar;
using Pim.Module.Calendar.Services;
using Pim.Module.Mobile;
using Pim.Module.Mobile.Services;
using Pim.Module.PcTracker;
using Pim.Module.PcTracker.Services;
using Pim.Module.Stats;
using Pim.Module.Stats.Services;

namespace Pim.UnitTests.Harness;

/// <summary>
/// Base class for Service-layer tests.
/// Provides InMemory <see cref="PimDbContext"/> with all module assemblies registered,
/// plus stubs for <see cref="ICurrentUserService"/> and <see cref="TimeProvider"/>.
/// Also exposes factory helpers for the main domain services (Mobile, PcTracker, Calendar, Stats, Today).
/// </summary>
public abstract class ServiceTestBase
{
    public static readonly Guid DefaultUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // Keep alias required by some existing helpers / future tests.
    public static Guid UserId => DefaultUserId;

    public static PimDbContext CreateDb()
    {
        // 注册所有模块的 EF 配置，确保 InMemory 模型完整
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Mobile.Entities.MobileUsageSessionEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.PcTracker.Entities.AwEventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Calendar.Entities.EventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Stats.Entities.AppUsageEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.QuickNotes.Entities.QuickNoteEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Files.Entities.FileItemEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"pim-test-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    public static ICurrentUserService CurrentUser(Guid? userId = null)
        => new StubCurrentUserService(userId ?? DefaultUserId);

    public static TimeProvider Time(DateTimeOffset utcNow)
        => new FixedTimeProvider(utcNow);

    // ---- Service factories ----

    // Mobile
    public static MobileLocationService CreateMobileLocationService(
        PimDbContext db,
        ICurrentUserService? user = null,
        TimeProvider? time = null)
        => new(db, user ?? CurrentUser(), time ?? Time(DateTimeOffset.UtcNow));

    public static MobileLocationQueryService CreateMobileLocationQueryService(TimeProvider? time = null)
        => new(time ?? Time(DateTimeOffset.UtcNow));

    public static MobileUsageQueryService CreateMobileUsageQueryService(
        PimDbContext db,
        ICurrentUserService? user = null,
        TimeProvider? time = null)
        => new(db, user ?? CurrentUser(), time ?? Time(DateTimeOffset.UtcNow));

    public static MobileAnalyticsQueryService CreateMobileAnalyticsQueryService(TimeProvider? time = null)
        => new(time ?? Time(DateTimeOffset.UtcNow));

    // PcTracker
    public static PcActivityAggregationService CreatePcAggregationService(PimDbContext db)
        => new(db);

    public static PcTrackerService CreatePcTrackerService(PimDbContext db)
    {
        var snapshots = new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance);
        var settings = new ActivityClassificationSettingsService(db);
        var smoothing = new ActivityTimelineSmoothingService();
        var rules = new ActivityClassificationRuleService(db);
        return new PcTrackerService(db, snapshots, settings, smoothing, rules);
    }

    public static PcActivityAnalysisService CreatePcActivityAnalysisService(PimDbContext db)
        => new(CreatePcTrackerService(db));

    // Calendar
    public static CalendarService CreateCalendarService(
        PimDbContext db,
        ICurrentUserService? user = null,
        TimeProvider? time = null)
    {
        var recurrence = new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var attachments = new EventAttachmentService(db);
        return new CalendarService(db, user ?? CurrentUser(), recurrence, attachments, time ?? Time(DateTimeOffset.UtcNow), null);
    }

    public static PlanningModelService CreatePlanningModelService(
        PimDbContext db,
        ICurrentUserService? user = null)
        => new(db, user ?? CurrentUser(), null);

    public static ReminderService CreateReminderService(
        PimDbContext db,
        ICurrentUserService? user = null)
        => new(db, user ?? CurrentUser());

    // Stats
    public static StatsService CreateStatsService(PimDbContext db)
        => new(db);

    // Today
    public static TodaySectionService CreateTodaySectionService(params ITodaySectionProvider[] providers)
        => new(providers, NullLogger<TodaySectionService>.Instance);

    // ---- Stubs ----

    private sealed class StubCurrentUserService : ICurrentUserService
    {
        public StubCurrentUserService(Guid userId) => UserId = userId;
        public Guid? UserId { get; }
        public string? Role => "user";
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
