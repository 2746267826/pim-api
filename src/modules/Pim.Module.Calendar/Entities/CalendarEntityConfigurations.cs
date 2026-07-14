using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.Calendar.Entities;

public class CalendarEntityConfiguration : IEntityTypeConfiguration<CalendarEntity>
{
    public void Configure(EntityTypeBuilder<CalendarEntity> builder)
    {
        builder.HasQueryFilter(c => c.DeletedAt == null);
        builder.Property(c => c.Source).HasDefaultValue("manual");
        builder.Property(c => c.IsVisible).HasDefaultValue(true);
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
        builder.Property(e => e.GraphRecurrenceJson).HasDefaultValue("{}");
        builder.HasIndex(e => e.CalendarId);
        builder.HasIndex(e => e.Uid);
        builder.HasIndex(e => e.SourceUid);
        builder.HasIndex(e => e.OutlookEventId);
        builder.HasIndex(e => new { e.OutlookCalendarBindingId, e.OutlookEventId })
            .IsUnique()
            .HasFilter("\"outlook_calendar_binding_id\" IS NOT NULL AND \"outlook_event_id\" IS NOT NULL AND \"deleted_at\" IS NULL");
        builder.HasIndex(e => e.OutlookChangeKey);
        builder.HasIndex(e => new { e.DeletedAt, e.DtStart });
        builder.HasIndex(e => e.DeletedByOperationId);
        builder.HasOne(e => e.Calendar)
            .WithMany(c => c.Events)
            .HasForeignKey(e => e.CalendarId);
        builder.HasOne(e => e.OutlookCalendarBinding)
            .WithMany()
            .HasForeignKey(e => e.OutlookCalendarBindingId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<OutlookConnectionEntity>()
            .WithMany()
            .HasForeignKey(e => e.OutlookConnectionId)
            .OnDelete(DeleteBehavior.SetNull);
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
        builder.Property(o => o.Authority).HasDefaultValue("https://login.microsoftonline.com/common");
        builder.Property(o => o.Scopes).HasDefaultValue("Calendars.ReadWrite offline_access User.Read openid profile");
        builder.Property(o => o.Status).HasDefaultValue("not-connected");
        builder.Property(o => o.TokenHealth).HasDefaultValue("missing");
        builder.Property(o => o.Version).HasDefaultValue(0).IsConcurrencyToken();
        builder.HasIndex(o => o.UserId).IsUnique();
    }
}

public sealed class OutlookAuthorizationSessionEntityConfiguration
    : IEntityTypeConfiguration<OutlookAuthorizationSessionEntity>
{
    public void Configure(EntityTypeBuilder<OutlookAuthorizationSessionEntity> builder)
    {
        builder.Property(entity => entity.Status).HasDefaultValue("starting");
        builder.Property(entity => entity.Version).HasDefaultValue(0).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.UserId, entity.CreatedAt });
        builder.HasIndex(entity => new { entity.ConnectionId, entity.Status });
        builder.HasIndex(entity => entity.ConnectionId)
            .IsUnique()
            .HasFilter("\"status\" IN ('starting', 'waiting-for-user')")
            .HasDatabaseName("UX_outlook_authorization_sessions_active_connection");
        builder.HasOne<OutlookConnectionEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutlookCalendarBindingEntityConfiguration
    : IEntityTypeConfiguration<OutlookCalendarBindingEntity>
{
    public void Configure(EntityTypeBuilder<OutlookCalendarBindingEntity> builder)
    {
        builder.Property(entity => entity.IsSelected).HasDefaultValue(true);
        builder.Property(entity => entity.RemoteState).HasDefaultValue("active");
        builder.Property(entity => entity.SyncStrategy).HasDefaultValue("window-reconcile");
        builder.HasIndex(entity => new { entity.ConnectionId, entity.GraphCalendarId }).IsUnique();
        builder.HasIndex(entity => entity.PimCalendarId).IsUnique();
        builder.HasOne<OutlookConnectionEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CalendarEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.PimCalendarId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class OutlookSyncBatchEntityConfiguration : IEntityTypeConfiguration<OutlookSyncBatchEntity>
{
    public void Configure(EntityTypeBuilder<OutlookSyncBatchEntity> builder)
    {
        builder.Property(o => o.Provider).HasDefaultValue("outlook");
        builder.Property(o => o.Status).HasDefaultValue("running");
        builder.Property(o => o.Mode).HasDefaultValue("incremental");
        builder.Property(o => o.StepsJson).HasDefaultValue("[]");
        builder.Property(o => o.ErrorsJson).HasDefaultValue("[]");
        builder.Property(o => o.RequestedCalendarIdsJson).HasDefaultValue("[]");
        builder.Property(o => o.PerCalendarJson).HasDefaultValue("[]");
        builder.Property(o => o.StartedAt).HasDefaultValueSql("now()");
        builder.Property(o => o.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(o => o.UserId);
        builder.HasIndex(o => new { o.UserId, o.StartedAt });
        builder.HasIndex(o => new { o.UserId, o.Provider, o.StartedAt });
    }
}

public sealed class OutlookOperationExecutionEntityConfiguration
    : IEntityTypeConfiguration<OutlookOperationExecutionEntity>
{
    public void Configure(EntityTypeBuilder<OutlookOperationExecutionEntity> builder)
    {
        builder.Property(entity => entity.PayloadJson).HasDefaultValue("{}");
        builder.Property(entity => entity.State).HasDefaultValue("queued");
        builder.HasIndex(entity => entity.ConfirmationId).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.NextAttemptAt });
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
        builder.HasIndex(c => c.SourceConfirmationId);
    }
}

public class ReminderEntityConfiguration : IEntityTypeConfiguration<ReminderEntity>
{
    public void Configure(EntityTypeBuilder<ReminderEntity> builder)
    {
        builder.HasQueryFilter(r => r.DeletedAt == null);
        builder.Property(r => r.ChannelsJson).HasDefaultValue("[]");
        builder.Property(r => r.Status).HasDefaultValue("Open");
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(r => new { r.UserId, r.Status, r.ScheduledAt });
        builder.HasIndex(r => new { r.RelatedObjectType, r.RelatedObjectId });
    }
}

public class ReminderDeliveryEntityConfiguration : IEntityTypeConfiguration<ReminderDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<ReminderDeliveryEntity> builder)
    {
        builder.Property(d => d.PayloadJson).HasDefaultValue("{}");
        builder.Property(d => d.Status).HasDefaultValue("Created");
        builder.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(d => new { d.UserId, d.CreatedAt });
        builder.HasIndex(d => d.ReminderId);
        builder.HasOne(d => d.Reminder)
            .WithMany(r => r.Deliveries)
            .HasForeignKey(d => d.ReminderId);
    }
}

public class ReportArtifactEntityConfiguration : IEntityTypeConfiguration<ReportArtifactEntity>
{
    public void Configure(EntityTypeBuilder<ReportArtifactEntity> builder)
    {
        builder.HasQueryFilter(r => r.DeletedAt == null);
        builder.Property(r => r.RiskLevel).HasDefaultValue("L0AutomaticArtifact");
        builder.Property(r => r.InputsJson).HasDefaultValue("{}");
        builder.Property(r => r.MetricsJson).HasDefaultValue("{}");
        builder.Property(r => r.Status).HasDefaultValue("Active");
        builder.Property(r => r.GeneratedAt).HasDefaultValueSql("now()");
        builder.HasIndex(r => new { r.UserId, r.Kind, r.GeneratedAt });
        builder.HasIndex(r => new { r.UserId, r.ProjectId });
    }
}

public class ReportSuggestionEntityConfiguration : IEntityTypeConfiguration<ReportSuggestionEntity>
{
    public void Configure(EntityTypeBuilder<ReportSuggestionEntity> builder)
    {
        builder.Property(s => s.ChangedFieldsJson).HasDefaultValue("[]");
        builder.Property(s => s.PayloadJson).HasDefaultValue("{}");
        builder.Property(s => s.Status).HasDefaultValue("Open");
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(s => new { s.UserId, s.Status });
        builder.HasIndex(s => s.ConfirmationId);
        builder.HasOne(s => s.Report)
            .WithMany(r => r.Suggestions)
            .HasForeignKey(s => s.ReportId);
    }
}
