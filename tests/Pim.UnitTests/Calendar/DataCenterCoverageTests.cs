using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class DataCenterCoverageTests
{
    private static readonly Guid UserId = Guid.Parse("92929292-9292-9292-9292-929292929292");

    [Fact]
    public async Task GlobalSearchCoversAllApprovedObjectTypes()
    {
        await using var db = CreateDb();
        SeedFullDataCenterFixture(db);
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId));

        var result = await service.QueryAsync(
            new DataCenterQueryRequest(null, null, null, false, 1, 200),
            CancellationToken.None);

        foreach (var type in new[]
        {
            "task",
            "event",
            "task-segment",
            "habit",
            "reminder",
            "report",
            "confirmation",
            "sync-batch",
            "sync-conflict",
            "audit-version",
            "recycle-bin"
        })
        {
            Assert.Contains(result.Items, item => item.ObjectType == type);
        }
    }

    [Fact]
    public async Task QueryDoesNotExposeOtherUsersAuditVersions()
    {
        await using var db = CreateDb();
        var otherAudit = new AuditVersionEntity
        {
            ObjectType = "task",
            ObjectId = Guid.NewGuid(),
            Source = "outlook-sync",
            Actor = "system",
            UserId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            BeforeJson = """{"title":"Other","OutlookChangeKey":"ck-other"}""",
            AfterJson = "{}",
            ChangedFieldsJson = """["title"]""",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AuditVersions.Add(otherAudit);
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId));

        var result = await service.QueryAsync(
            new DataCenterQueryRequest(null, null, null, false, 1, 200),
            CancellationToken.None);

        Assert.DoesNotContain(result.Items, item => item.ObjectType == "audit-version");
    }

    [Fact]
    public async Task QueryRedactsProviderTokensFromSummaries()
    {
        await using var db = CreateDb();
        SeedFullDataCenterFixture(db);
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId));

        var result = await service.QueryAsync(
            new DataCenterQueryRequest(null, null, null, false, 1, 200),
            CancellationToken.None);

        Assert.DoesNotContain(result.Items, item => item.Summary.Contains("graph-event-coverage"));
        Assert.DoesNotContain(result.Items, item => item.Summary.Contains("GraphEventId="));
        Assert.DoesNotContain(result.Items, item => item.Summary.Contains("ChangeKey="));
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"data-center-coverage-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static void SeedFullDataCenterFixture(PimDbContext db)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Work",
            Kind = "calendar"
        };
        var activeEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "coverage-event@pim",
            Title = "Coverage event",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook-graph",
            OutlookEventId = "graph-event-coverage",
            OutlookChangeKey = "change-key-coverage"
        };
        var deletedEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "coverage-deleted-event@pim",
            Title = "Deleted coverage event",
            DtStart = new DateTimeOffset(2026, 7, 8, 11, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero),
            Source = "manual",
            DeletedAt = new DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero)
        };
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "coverage-task@pim",
            Title = "Coverage task",
            Description = "Task for Data Center coverage",
            DtStart = new DateTimeOffset(2026, 7, 8, 13, 0, 0, TimeSpan.Zero),
            PlannedEnd = new DateTimeOffset(2026, 7, 8, 14, 0, 0, TimeSpan.Zero)
        };
        var segment = new TaskExecutionSegmentEntity
        {
            UserId = UserId,
            Task = task,
            TaskId = task.Id,
            StartsAt = new DateTimeOffset(2026, 7, 8, 13, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 7, 8, 13, 30, 0, TimeSpan.Zero),
            Source = "manual",
            Status = "planned",
            PlanningReason = "coverage segment"
        };
        var habit = new HabitRoutineEntity
        {
            UserId = UserId,
            Title = "Coverage habit",
            Cadence = "Daily",
            Status = "Active"
        };
        var reminder = new ReminderEntity
        {
            UserId = UserId,
            RelatedObjectType = "task",
            RelatedObjectId = task.Id,
            Title = "Coverage reminder",
            Body = "Reminder body",
            TriggerReason = "due-soon",
            ScheduledAt = new DateTimeOffset(2026, 7, 8, 8, 0, 0, TimeSpan.Zero)
        };
        var report = new ReportArtifactEntity
        {
            UserId = UserId,
            Kind = "Daily",
            ContentMarkdown = "# Coverage",
            InputsJson = "{}",
            MetricsJson = "{}",
            GeneratedAt = new DateTimeOffset(2026, 7, 8, 7, 0, 0, TimeSpan.Zero)
        };
        var confirmation = new OperationConfirmationEntity
        {
            RequestedByUserId = UserId,
            OperationType = "data-center.batch",
            Summary = "Coverage confirmation",
            RiskLevel = OperationRiskLevel.L4BatchOrDestructiveGovernance.ToString(),
            Source = "data-center",
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var syncBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            Status = "completed",
            ReadCount = 1,
            StartedAt = new DateTimeOffset(2026, 7, 8, 6, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 7, 8, 6, 5, 0, TimeSpan.Zero)
        };
        var syncConflict = new SyncConflictEntity
        {
            UserId = UserId,
            ObjectType = "event",
            ObjectId = activeEvent.Id,
            GraphEventId = "graph-event-coverage",
            ConflictKind = "both_sides_changed",
            Status = "open"
        };
        var auditVersion = new AuditVersionEntity
        {
            ObjectType = "task",
            ObjectId = task.Id,
            Source = "data-center",
            Actor = "system",
            UserId = UserId,
            BeforeJson = "{}",
            AfterJson = """{"title":"Coverage task"}""",
            ChangedFieldsJson = """["title"]""",
            CreatedAt = new DateTimeOffset(2026, 7, 8, 5, 0, 0, TimeSpan.Zero)
        };

        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().AddRange(activeEvent, deletedEvent);
        db.Set<TaskEntity>().Add(task);
        db.Set<TaskExecutionSegmentEntity>().Add(segment);
        db.Set<HabitRoutineEntity>().Add(habit);
        db.Set<ReminderEntity>().Add(reminder);
        db.Set<ReportArtifactEntity>().Add(report);
        db.OperationConfirmations.Add(confirmation);
        db.Set<OutlookSyncBatchEntity>().Add(syncBatch);
        db.Set<SyncConflictEntity>().Add(syncConflict);
        db.AuditVersions.Add(auditVersion);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
