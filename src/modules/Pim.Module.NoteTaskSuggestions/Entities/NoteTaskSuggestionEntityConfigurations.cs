using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.NoteTaskSuggestions.Entities;

public sealed class NoteTaskSuggestionExtractionEntityConfiguration
    : IEntityTypeConfiguration<NoteTaskSuggestionExtractionEntity>
{
    public void Configure(EntityTypeBuilder<NoteTaskSuggestionExtractionEntity> builder)
    {
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => new { x.UserId, x.RequestId }).IsUnique();
        builder.HasIndex(x => new { x.UserId, x.Status, x.UpdatedAt });
        builder.HasIndex(x => new { x.UserId, x.NoteId, x.CreatedAt });
        builder.HasIndex(x => x.AiCallLogId);
    }
}

public sealed class NoteTaskSuggestionEntityConfiguration
    : IEntityTypeConfiguration<NoteTaskSuggestionEntity>
{
    public void Configure(EntityTypeBuilder<NoteTaskSuggestionEntity> builder)
    {
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => new { x.UserId, x.ExtractionId, x.Ordinal }).IsUnique();
        builder.HasIndex(x => new { x.UserId, x.NoteId, x.Status, x.UpdatedAt });
        builder.HasIndex(x => new { x.UserId, x.ConfirmRequestId }).IsUnique()
            .HasFilter("\"confirm_request_id\" IS NOT NULL");
        builder.HasIndex(x => x.TaskId).IsUnique()
            .HasFilter("\"task_id\" IS NOT NULL");
        builder.HasOne(x => x.Extraction)
            .WithMany()
            .HasForeignKey(x => x.ExtractionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
