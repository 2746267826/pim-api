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
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId, "admin"));

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
            "report-suggestion",
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
        db.Users.Add(new UserEntity
        {
            Id = UserId,
            Username = "data-center-audit-isolation",
            Email = "audit-isolation@example.com",
            PasswordHash = "synthetic",
            Role = "admin",
            IsActive = true
        });
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId, "admin"));

        var result = await service.QueryAsync(
            new DataCenterQueryRequest(null, null, null, false, 1, 200),
            CancellationToken.None);

        Assert.DoesNotContain(result.Items, item => item.ObjectType == "audit-version");
    }

    [Fact]
    public async Task OrdinaryUserDoesNotSeeAuditVersionsOrInternalSyncDiagnostics_AdminDoes()
    {
        await using var db = CreateDb();
        SeedFullDataCenterFixture(db);
        await db.SaveChangesAsync();

        var ordinary = await new DataCenterQueryService(db, new FixedCurrentUserService(UserId))
            .QueryAsync(new DataCenterQueryRequest(null, null, null, false, 1, 200));
        Assert.DoesNotContain(ordinary.Items, item => item.ObjectType == "audit-version");
        var directAuditQuery = await new DataCenterQueryService(db, new FixedCurrentUserService(UserId))
            .QueryAsync(new DataCenterQueryRequest("audit-version", null, null, false, 1, 200));
        Assert.Empty(directAuditQuery.Items);
        Assert.Equal("同步冲突需核对", Assert.Single(ordinary.Items, item => item.ObjectType == "sync-conflict").Summary);
        Assert.Equal("同步批次", Assert.Single(ordinary.Items, item => item.ObjectType == "sync-batch").Summary);
        Assert.Equal("Task for Data Center coverage",
            Assert.Single(ordinary.Items, item => item.ObjectType == "task-segment").Summary);
        Assert.Equal("待确认操作",
            Assert.Single(ordinary.Items, item => item.ObjectType == "confirmation").Summary);
        Assert.DoesNotContain(ordinary.Items, item =>
            item.ObjectType == "confirmation" && item.Title == "data-center.global");

        var directConfirmation = await new DataCenterQueryService(db, new FixedCurrentUserService(UserId))
            .QueryAsync(new DataCenterQueryRequest("confirmation", null, null, false, 1, 20));
        Assert.Equal("待确认操作", Assert.Single(directConfirmation.Items).Summary);

        var admin = await new DataCenterQueryService(db, new FixedCurrentUserService(UserId, "admin"))
            .QueryAsync(new DataCenterQueryRequest(null, null, null, false, 1, 200));
        Assert.Contains(admin.Items, item => item.ObjectType == "audit-version");
        Assert.Contains("internal-sync-detail",
            Assert.Single(admin.Items, item => item.ObjectType == "sync-conflict").Summary);
        Assert.Contains("read=1",
            Assert.Single(admin.Items, item => item.ObjectType == "sync-batch").Summary);
        Assert.Equal("coverage segment",
            Assert.Single(admin.Items, item => item.ObjectType == "task-segment").Summary);
        Assert.Contains(admin.Items, item =>
            item.ObjectType == "confirmation" && item.Title == "data-center.global"
            && item.Summary == "Global confidential confirmation");
        Assert.Contains("Coverage confirmation",
            admin.Items.Where(item => item.ObjectType == "confirmation").Select(item => item.Summary));
    }

    [Theory]
    [InlineData("task-segment", "coverage segment")]
    [InlineData("reminder", "due-soon")]
    [InlineData("report", "internal-report-metric")]
    [InlineData("report-suggestion", "internal-report-payload")]
    [InlineData("confirmation", "Coverage confirmation")]
    [InlineData("confirmation", "Global confidential confirmation")]
    public async Task OrdinaryUserCannotProbeHiddenDiagnosticsThroughSearch_AdminCan(
        string objectType, string search)
    {
        await using var db = CreateDb();
        SeedFullDataCenterFixture(db);
        await db.SaveChangesAsync();

        var ordinary = await new DataCenterQueryService(db, new FixedCurrentUserService(UserId))
            .QueryAsync(new DataCenterQueryRequest(objectType, search, null, false, 1, 50));
        Assert.Empty(ordinary.Items);

        var admin = await new DataCenterQueryService(db, new FixedCurrentUserService(UserId, "admin"))
            .QueryAsync(new DataCenterQueryRequest(objectType, search, null, false, 1, 50));
        Assert.Contains(admin.Items, item => item.ObjectType == objectType);
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
            MetricsJson = """{"diagnostic":"internal-report-metric"}""",
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
        var globalConfirmation = new OperationConfirmationEntity
        {
            RequestedByUserId = null,
            OperationType = "data-center.global",
            Summary = "Global confidential confirmation",
            RiskLevel = OperationRiskLevel.L4BatchOrDestructiveGovernance.ToString(),
            Source = "data-center",
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var reportSuggestion = new ReportSuggestionEntity
        {
            UserId = UserId,
            Report = report,
            ReportId = report.Id,
            Action = "review",
            Summary = "Coverage report suggestion",
            PayloadJson = """{"diagnostic":"internal-report-payload"}"""
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
            Status = "open",
            PimSnapshotJson = """{"diagnostic":"internal-sync-detail"}""",
            ExternalSnapshotJson = """{"diagnostic":"external-sync-detail"}"""
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
        db.Set<ReportSuggestionEntity>().Add(reportSuggestion);
        db.OperationConfirmations.AddRange(confirmation, globalConfirmation);
        db.Set<OutlookSyncBatchEntity>().Add(syncBatch);
        db.Set<SyncConflictEntity>().Add(syncConflict);
        db.AuditVersions.Add(auditVersion);
        db.Users.Add(new UserEntity
        {
            Id = UserId,
            Username = "data-center-coverage",
            Email = "coverage@example.com",
            PasswordHash = "synthetic",
            Role = "admin",
            IsActive = true
        });
    }

    private sealed class FixedCurrentUserService(Guid userId, string role = "user") : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role { get; } = role;
    }
}
