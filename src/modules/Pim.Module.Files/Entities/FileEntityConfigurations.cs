using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.Files.Entities;

public sealed class FileProviderEntityConfiguration : IEntityTypeConfiguration<FileProviderEntity>
{
    public void Configure(EntityTypeBuilder<FileProviderEntity> builder)
    {
        builder.ToTable("file_providers");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.UserId).HasColumnName("user_id");
        builder.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(32).HasDefaultValue("nextcloud");
        builder.Property(e => e.BaseUrl).HasColumnName("base_url").HasMaxLength(1024);
        builder.Property(e => e.InternalBaseUrl).HasColumnName("internal_base_url").HasMaxLength(1024);
        builder.Property(e => e.Username).HasColumnName("username").HasMaxLength(255);
        builder.Property(e => e.AppPasswordSecret).HasColumnName("app_password_secret");
        builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(32).HasDefaultValue("pending");
        builder.Property(e => e.LastSyncAt).HasColumnName("last_sync_at");
        builder.Property(e => e.LastError).HasColumnName("last_error");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.HasIndex(e => new { e.UserId, e.Provider, e.BaseUrl, e.Username }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.Status });
    }
}

public sealed class FileItemEntityConfiguration : IEntityTypeConfiguration<FileItemEntity>
{
    public void Configure(EntityTypeBuilder<FileItemEntity> builder)
    {
        builder.ToTable("file_items");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.ProviderId).HasColumnName("provider_id");
        builder.Property(e => e.ExternalFileId).HasColumnName("external_file_id").HasMaxLength(255);
        builder.Property(e => e.ParentExternalFileId).HasColumnName("parent_external_file_id").HasMaxLength(255);
        builder.Property(e => e.Path).HasColumnName("path").HasColumnType("text");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(512);
        builder.Property(e => e.ItemType).HasColumnName("item_type").HasMaxLength(16).HasDefaultValue("file");
        builder.Property(e => e.MimeType).HasColumnName("mime_type").HasMaxLength(255);
        builder.Property(e => e.Size).HasColumnName("size");
        builder.Property(e => e.Etag).HasColumnName("etag").HasMaxLength(255);
        builder.Property(e => e.ContentHash).HasColumnName("content_hash").HasMaxLength(128);
        builder.Property(e => e.CurrentVersionId).HasColumnName("current_version_id");
        builder.Property(e => e.Permissions).HasColumnName("permissions").HasMaxLength(64);
        builder.Property(e => e.IsDeleted).HasColumnName("is_deleted");
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(e => e.ModifiedAt).HasColumnName("modified_at").HasDefaultValueSql("now()");
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").HasDefaultValueSql("now()");
        builder.HasOne(e => e.Provider).WithMany(p => p.Items).HasForeignKey(e => e.ProviderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FileVersionEntity>()
            .WithMany()
            .HasForeignKey(e => new { e.Id, e.CurrentVersionId })
            .HasPrincipalKey(e => new { e.FileItemId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ProviderId, e.ExternalFileId }).IsUnique();
        builder.HasIndex(e => new { e.ProviderId, e.Path });
        builder.HasIndex(e => new { e.ProviderId, e.ParentExternalFileId });
        builder.HasIndex(e => new { e.ProviderId, e.IsDeleted });
    }
}

public sealed class FileVersionEntityConfiguration : IEntityTypeConfiguration<FileVersionEntity>
{
    public void Configure(EntityTypeBuilder<FileVersionEntity> builder)
    {
        builder.ToTable("file_versions");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.FileItemId).HasColumnName("file_item_id");
        builder.Property(e => e.ExternalVersionId).HasColumnName("external_version_id").HasMaxLength(255);
        builder.Property(e => e.Etag).HasColumnName("etag").HasMaxLength(255);
        builder.Property(e => e.Size).HasColumnName("size");
        builder.Property(e => e.ModifiedAt).HasColumnName("modified_at");
        builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32).HasDefaultValue("history");
        builder.Property(e => e.IsCurrent).HasColumnName("is_current");
        builder.Property(e => e.SyncedAt).HasColumnName("synced_at").HasDefaultValueSql("now()");
        builder.HasAlternateKey(e => new { e.FileItemId, e.Id });
        builder.HasOne(e => e.FileItem).WithMany(i => i.Versions).HasForeignKey(e => e.FileItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.FileItemId, e.ExternalVersionId }).IsUnique();
        builder.HasIndex(e => new { e.FileItemId, e.IsCurrent }).IsUnique().HasFilter("is_current = true");
    }
}

public sealed class FileIndexJobEntityConfiguration : IEntityTypeConfiguration<FileIndexJobEntity>
{
    public void Configure(EntityTypeBuilder<FileIndexJobEntity> builder)
    {
        builder.ToTable("file_index_jobs");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.FileItemId).HasColumnName("file_item_id");
        builder.Property(e => e.VersionId).HasColumnName("version_id");
        builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(32).HasDefaultValue("pending");
        builder.Property(e => e.Stage).HasColumnName("stage").HasMaxLength(32).HasDefaultValue("metadata");
        builder.Property(e => e.AttemptCount).HasColumnName("attempt_count");
        builder.Property(e => e.LastError).HasColumnName("last_error");
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.FinishedAt).HasColumnName("finished_at");
        builder.HasOne(e => e.FileItem).WithMany(i => i.IndexJobs).HasForeignKey(e => e.FileItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Version)
            .WithMany()
            .HasForeignKey(e => new { e.FileItemId, e.VersionId })
            .HasPrincipalKey(e => new { e.FileItemId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.FileItemId, e.Status });
        builder.HasIndex(e => new { e.Status, e.Stage });
    }
}

public sealed class FileChunkEntityConfiguration : IEntityTypeConfiguration<FileChunkEntity>
{
    public void Configure(EntityTypeBuilder<FileChunkEntity> builder)
    {
        builder.ToTable("file_chunks");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.FileItemId).HasColumnName("file_item_id");
        builder.Property(e => e.VersionId).HasColumnName("version_id");
        builder.Property(e => e.ChunkIndex).HasColumnName("chunk_index");
        builder.Property(e => e.Text).HasColumnName("text").HasColumnType("text");
        builder.Property(e => e.TextHash).HasColumnName("text_hash").HasMaxLength(128);
        builder.Property(e => e.StartOffset).HasColumnName("start_offset");
        builder.Property(e => e.EndOffset).HasColumnName("end_offset");
        builder.Property(e => e.QdrantPointId).HasColumnName("qdrant_point_id").HasMaxLength(128);
        builder.HasOne(e => e.FileItem).WithMany(i => i.Chunks).HasForeignKey(e => e.FileItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Version)
            .WithMany()
            .HasForeignKey(e => new { e.FileItemId, e.VersionId })
            .HasPrincipalKey(e => new { e.FileItemId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.FileItemId, e.VersionId, e.ChunkIndex }).IsUnique();
        builder.HasIndex(e => e.QdrantPointId).IsUnique().HasFilter("qdrant_point_id IS NOT NULL");
    }
}

public sealed class FileAiResultEntityConfiguration : IEntityTypeConfiguration<FileAiResultEntity>
{
    public void Configure(EntityTypeBuilder<FileAiResultEntity> builder)
    {
        builder.ToTable("file_ai_results");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.FileItemId).HasColumnName("file_item_id");
        builder.Property(e => e.VersionId).HasColumnName("version_id");
        builder.Property(e => e.Summary).HasColumnName("summary").HasColumnType("text");
        builder.Property(e => e.TagsJson).HasColumnName("tags_json").HasColumnType("jsonb").HasDefaultValue("[]");
        builder.Property(e => e.Language).HasColumnName("language").HasMaxLength(32);
        builder.Property(e => e.Sensitivity).HasColumnName("sensitivity").HasMaxLength(32);
        builder.Property(e => e.GeneratedAt).HasColumnName("generated_at").HasDefaultValueSql("now()");
        builder.Property(e => e.Model).HasColumnName("model").HasMaxLength(255);
        builder.Property(e => e.AiRequestLogId).HasColumnName("ai_request_log_id");
        builder.Property(e => e.EvidenceChunkIdsJson).HasColumnName("evidence_chunk_ids_json").HasColumnType("jsonb").HasDefaultValue("[]");
        builder.HasOne(e => e.FileItem).WithMany(i => i.AiResults).HasForeignKey(e => e.FileItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Version)
            .WithMany()
            .HasForeignKey(e => new { e.FileItemId, e.VersionId })
            .HasPrincipalKey(e => new { e.FileItemId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.FileItemId, e.VersionId }).IsUnique();
        builder.HasIndex(e => e.AiRequestLogId);
    }
}

public sealed class FileSuggestionEntityConfiguration : IEntityTypeConfiguration<FileSuggestionEntity>
{
    public void Configure(EntityTypeBuilder<FileSuggestionEntity> builder)
    {
        builder.ToTable("file_suggestions", table =>
            table.HasCheckConstraint("CK_file_suggestions_confidence_range", "confidence >= 0 AND confidence <= 1"));
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.FileItemId).HasColumnName("file_item_id");
        builder.Property(e => e.SuggestionType).HasColumnName("suggestion_type").HasMaxLength(32);
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(255);
        builder.Property(e => e.Reason).HasColumnName("reason").HasColumnType("text");
        builder.Property(e => e.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
        builder.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").HasDefaultValue("{}");
        builder.Property(e => e.Status).HasColumnName("status").HasMaxLength(32).HasDefaultValue("pending");
        builder.Property(e => e.AiRequestLogId).HasColumnName("ai_request_log_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.HasOne(e => e.FileItem).WithMany(i => i.Suggestions).HasForeignKey(e => e.FileItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.FileItemId, e.Status });
        builder.HasIndex(e => e.SuggestionType);
        builder.HasIndex(e => e.AiRequestLogId);
    }
}
