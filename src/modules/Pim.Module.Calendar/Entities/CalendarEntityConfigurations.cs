using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.Calendar.Entities;

public class CalendarEntityConfiguration : IEntityTypeConfiguration<CalendarEntity>
{
    public void Configure(EntityTypeBuilder<CalendarEntity> builder)
    {
        builder.HasQueryFilter(c => c.DeletedAt == null);
        builder.HasIndex(c => c.UserId);
        builder.HasIndex(c => new { c.UserId, c.DeletedAt });
        builder.HasIndex(c => c.DeletedByOperationId);
    }
}

public class EventEntityConfiguration : IEntityTypeConfiguration<EventEntity>
{
    public void Configure(EntityTypeBuilder<EventEntity> builder)
    {
        builder.HasQueryFilter(e => e.DeletedAt == null);
        builder.Property(e => e.ExternalMetadataJson).HasDefaultValue("{}");
        builder.Property(e => e.ExDatesJson).HasDefaultValue("[]");
        builder.Property(e => e.RecurrenceMetadataJson).HasDefaultValue("{}");
        builder.HasIndex(e => e.CalendarId);
        builder.HasIndex(e => e.Uid);
        builder.HasIndex(e => e.SourceUid);
        builder.HasIndex(e => e.OutlookEventId);
        builder.HasIndex(e => e.OutlookChangeKey);
        builder.HasIndex(e => new { e.DeletedAt, e.DtStart });
        builder.HasIndex(e => e.DeletedByOperationId);
        builder.HasOne(e => e.Calendar)
            .WithMany(c => c.Events)
            .HasForeignKey(e => e.CalendarId);
    }
}

public class TaskEntityConfiguration : IEntityTypeConfiguration<TaskEntity>
{
    public void Configure(EntityTypeBuilder<TaskEntity> builder)
    {
        builder.HasQueryFilter(t => t.DeletedAt == null);
        builder.HasIndex(t => t.UserId);
        builder.HasIndex(t => new { t.UserId, t.CalendarId });
        builder.HasIndex(t => new { t.UserId, t.DomainProjectId });
        builder.HasIndex(t => new { t.UserId, t.TaskBookId });
        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => new { t.UserId, t.DeletedAt });
        builder.HasIndex(t => new { t.UserId, t.DtStart, t.PlannedEnd });
        builder.HasIndex(t => t.DeletedByOperationId);
        builder.HasOne(t => t.Calendar)
            .WithMany(c => c.Tasks)
            .HasForeignKey(t => t.CalendarId);
        builder.HasOne(t => t.DomainProject)
            .WithMany(p => p.Tasks)
            .HasForeignKey(t => t.DomainProjectId);
        builder.HasOne(t => t.TaskBook)
            .WithMany(b => b.Tasks)
            .HasForeignKey(t => t.TaskBookId);
        builder.HasOne(t => t.ParentTask)
            .WithMany(t => t.SubTasks)
            .HasForeignKey(t => t.ParentTaskId);
    }
}

public class DomainProjectEntityConfiguration : IEntityTypeConfiguration<DomainProjectEntity>
{
    public void Configure(EntityTypeBuilder<DomainProjectEntity> builder)
    {
        builder.HasQueryFilter(p => p.DeletedAt == null);
        builder.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
        builder.HasIndex(p => new { p.UserId, p.Status });
    }
}

public class TaskBookEntityConfiguration : IEntityTypeConfiguration<TaskBookEntity>
{
    public void Configure(EntityTypeBuilder<TaskBookEntity> builder)
    {
        builder.HasQueryFilter(b => b.DeletedAt == null);
        builder.HasIndex(b => new { b.UserId, b.Name, b.DomainProjectId });
        builder.HasIndex(b => new { b.UserId, b.Status });
        builder.HasOne(b => b.DomainProject)
            .WithMany(p => p.TaskBooks)
            .HasForeignKey(b => b.DomainProjectId);
    }
}

public class TaskChecklistItemEntityConfiguration : IEntityTypeConfiguration<TaskChecklistItemEntity>
{
    public void Configure(EntityTypeBuilder<TaskChecklistItemEntity> builder)
    {
        builder.HasQueryFilter(i => i.DeletedAt == null);
        builder.HasIndex(i => i.UserId);
        builder.HasIndex(i => new { i.TaskId, i.SortOrder });
        builder.HasOne(i => i.Task)
            .WithMany(t => t.ChecklistItems)
            .HasForeignKey(i => i.TaskId);
    }
}

public class HabitRoutineEntityConfiguration : IEntityTypeConfiguration<HabitRoutineEntity>
{
    public void Configure(EntityTypeBuilder<HabitRoutineEntity> builder)
    {
        builder.HasQueryFilter(h => h.DeletedAt == null);
        builder.Property(h => h.RuleJson).HasDefaultValue("{}");
        builder.HasIndex(h => new { h.UserId, h.Status });
        builder.HasIndex(h => new { h.UserId, h.Cadence });
    }
}

public class HabitOccurrenceEntityConfiguration : IEntityTypeConfiguration<HabitOccurrenceEntity>
{
    public void Configure(EntityTypeBuilder<HabitOccurrenceEntity> builder)
    {
        builder.HasQueryFilter(o => o.DeletedAt == null);
        builder.HasIndex(o => o.HabitRoutineId);
        builder.HasIndex(o => new { o.UserId, o.StartsAt, o.EndsAt });
        builder.HasIndex(o => o.ConfirmationId);
        builder.HasOne(o => o.HabitRoutine)
            .WithMany(h => h.Occurrences)
            .HasForeignKey(o => o.HabitRoutineId);
    }
}

public class AvailabilityWindowEntityConfiguration : IEntityTypeConfiguration<AvailabilityWindowEntity>
{
    public void Configure(EntityTypeBuilder<AvailabilityWindowEntity> builder)
    {
        builder.HasQueryFilter(a => a.DeletedAt == null);
        builder.HasIndex(a => new { a.UserId, a.StartsAt, a.EndsAt });
        builder.HasIndex(a => new { a.UserId, a.Kind });
    }
}

public class AiPlanningPlaceholderEntityConfiguration : IEntityTypeConfiguration<AiPlanningPlaceholderEntity>
{
    public void Configure(EntityTypeBuilder<AiPlanningPlaceholderEntity> builder)
    {
        builder.HasQueryFilter(p => p.DeletedAt == null);
        builder.HasIndex(p => new { p.UserId, p.StartsAt, p.EndsAt });
        builder.HasIndex(p => p.ConfirmationId);
        builder.HasIndex(p => new { p.UserId, p.Status });
    }
}

public class TaskExecutionSegmentEntityConfiguration : IEntityTypeConfiguration<TaskExecutionSegmentEntity>
{
    public void Configure(EntityTypeBuilder<TaskExecutionSegmentEntity> builder)
    {
        builder.HasQueryFilter(s => s.DeletedAt == null);
        builder.HasIndex(s => s.UserId);
        builder.HasIndex(s => s.TaskId);
        builder.HasIndex(s => new { s.UserId, s.TaskId, s.StartsAt });
        builder.HasIndex(s => s.ConfirmationId);
        builder.HasOne(s => s.Task)
            .WithMany()
            .HasForeignKey(s => s.TaskId);
    }
}

public class PendingConfirmationEntityConfiguration : IEntityTypeConfiguration<PendingConfirmationEntity>
{
    public void Configure(EntityTypeBuilder<PendingConfirmationEntity> builder)
    {
        builder.HasIndex(p => p.UserId);
        builder.HasIndex(p => p.Status);
    }
}

public class SchedulingFeedbackEntityConfiguration : IEntityTypeConfiguration<SchedulingFeedbackEntity>
{
    public void Configure(EntityTypeBuilder<SchedulingFeedbackEntity> builder)
    {
        builder.HasIndex(s => s.UserId);
    }
}

public class OutlookConnectionEntityConfiguration : IEntityTypeConfiguration<OutlookConnectionEntity>
{
    public void Configure(EntityTypeBuilder<OutlookConnectionEntity> builder)
    {
        builder.Property(o => o.Provider).HasDefaultValue("outlook");
        builder.Property(o => o.TenantId).HasDefaultValue("common");
        builder.Property(o => o.Scopes).HasDefaultValue("Calendars.ReadWrite offline_access User.Read openid profile");
        builder.Property(o => o.Status).HasDefaultValue("not-connected");
        builder.Property(o => o.TokenHealth).HasDefaultValue("missing");
        builder.HasIndex(o => o.UserId).IsUnique();
    }
}

public class OutlookSyncBatchEntityConfiguration : IEntityTypeConfiguration<OutlookSyncBatchEntity>
{
    public void Configure(EntityTypeBuilder<OutlookSyncBatchEntity> builder)
    {
        builder.Property(o => o.Provider).HasDefaultValue("outlook");
        builder.Property(o => o.Status).HasDefaultValue("running");
        builder.Property(o => o.StepsJson).HasDefaultValue("[]");
        builder.Property(o => o.ErrorsJson).HasDefaultValue("[]");
        builder.Property(o => o.StartedAt).HasDefaultValueSql("now()");
        builder.HasIndex(o => o.UserId);
        builder.HasIndex(o => new { o.UserId, o.StartedAt });
        builder.HasIndex(o => new { o.UserId, o.Provider, o.StartedAt });
    }
}

public class SyncConflictEntityConfiguration : IEntityTypeConfiguration<SyncConflictEntity>
{
    public void Configure(EntityTypeBuilder<SyncConflictEntity> builder)
    {
        builder.Property(c => c.Provider).HasDefaultValue("outlook");
        builder.Property(c => c.ObjectType).HasDefaultValue("event");
        builder.Property(c => c.Status).HasDefaultValue("open");
        builder.Property(c => c.PimSnapshotJson).HasDefaultValue("{}");
        builder.Property(c => c.ExternalSnapshotJson).HasDefaultValue("{}");
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(c => new { c.UserId, c.Provider, c.Status });
        builder.HasIndex(c => new { c.ObjectType, c.ObjectId });
        builder.HasIndex(c => c.GraphEventId);
        builder.HasIndex(c => c.ResolvedConfirmationId);
    }
}
