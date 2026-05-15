using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.Calendar.Entities;

public class CalendarEntityConfiguration : IEntityTypeConfiguration<CalendarEntity>
{
    public void Configure(EntityTypeBuilder<CalendarEntity> builder)
    {
        builder.HasQueryFilter(c => c.DeletedAt == null);
        builder.HasIndex(c => c.UserId);
    }
}

public class EventEntityConfiguration : IEntityTypeConfiguration<EventEntity>
{
    public void Configure(EntityTypeBuilder<EventEntity> builder)
    {
        builder.HasQueryFilter(e => e.DeletedAt == null);
        builder.HasIndex(e => e.CalendarId);
        builder.HasIndex(e => e.Uid);
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
        builder.HasOne(t => t.Calendar)
            .WithMany(c => c.Tasks)
            .HasForeignKey(t => t.CalendarId);
        builder.HasOne(t => t.ParentTask)
            .WithMany(t => t.SubTasks)
            .HasForeignKey(t => t.ParentTaskId);
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
