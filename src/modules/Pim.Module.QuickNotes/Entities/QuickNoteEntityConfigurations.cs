using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.QuickNotes.Entities;

public sealed class QuickNoteEntityConfiguration : IEntityTypeConfiguration<QuickNoteEntity>
{
    public void Configure(EntityTypeBuilder<QuickNoteEntity> builder)
    {
        builder.HasQueryFilter(n => n.DeletedAt == null);
        builder.Property(n => n.ContentMarkdown).HasDefaultValue("");
        builder.Property(n => n.Status).HasDefaultValue(QuickNoteStatuses.Inbox);
        builder.Property(n => n.Source).HasDefaultValue(QuickNoteSources.WebPage);
        builder.Property(n => n.MetadataJson).HasDefaultValue("{}");
        builder.Property(n => n.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(n => n.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(n => new { n.UserId, n.Status, n.UpdatedAt });
        builder.HasIndex(n => new { n.UserId, n.CreatedAt });
        builder.HasMany(n => n.Attachments)
            .WithOne(a => a.QuickNote)
            .HasForeignKey(a => a.QuickNoteId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class QuickNoteAttachmentEntityConfiguration : IEntityTypeConfiguration<QuickNoteAttachmentEntity>
{
    public void Configure(EntityTypeBuilder<QuickNoteAttachmentEntity> builder)
    {
        builder.HasQueryFilter(a => a.DeletedAt == null);
        builder.Property(a => a.StorageProvider).HasDefaultValue("minio");
        builder.Property(a => a.ContentType).HasDefaultValue("application/octet-stream");
        builder.Property(a => a.MetadataJson).HasDefaultValue("{}");
        builder.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(a => a.QuickNoteId);
        builder.HasIndex(a => new { a.UserId, a.CreatedAt });
        builder.HasIndex(a => new { a.UserId, a.DeletedAt });
    }
}
