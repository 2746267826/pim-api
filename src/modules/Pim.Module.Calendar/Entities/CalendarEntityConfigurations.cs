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
        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => new { t.UserId, t.DeletedAt });
        builder.HasIndex(t => new { t.UserId, t.DtStart, t.PlannedEnd });
        builder.HasIndex(t => t.DeletedByOperationId);
        builder.HasOne(t => t.Calendar)
            .WithMany(c => c.Tasks)
            .HasForeignKey(t => t.CalendarId);
        builder.HasOne(t => t.ParentTask)
            .WithMany(t => t.SubTasks)
            .HasForeignKey(t => t.ParentTaskId);
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
        builder.HasIndex(o => o.UserId).IsUnique();
    }
}
