using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pim.Module.PcTracker.Entities;

public class KeystatsDailyEntityConfiguration : IEntityTypeConfiguration<KeystatsDailyEntity>
{
    public void Configure(EntityTypeBuilder<KeystatsDailyEntity> builder)
    {
        builder.HasIndex(e => e.DeviceId);
        builder.HasIndex(e => e.SnapshotDate);
        builder.HasIndex(e => new { e.DeviceId, e.SnapshotDate }).IsUnique();
        builder.HasMany(e => e.KeyCounts)
            .WithOne(k => k.DailySnapshot)
            .HasForeignKey(k => k.DailySnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.AppBreakdowns)
            .WithOne(a => a.DailySnapshot)
            .HasForeignKey(a => a.DailySnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AwEventEntityConfiguration : IEntityTypeConfiguration<AwEventEntity>
{
    public void Configure(EntityTypeBuilder<AwEventEntity> builder)
    {
        builder.HasIndex(e => e.DeviceId)
            .HasDatabaseName("ix_pc_aw_events_device_id");
        builder.HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_pc_aw_events_timestamp");
        builder.HasIndex(e => e.EventType)
            .HasDatabaseName("ix_pc_aw_events_event_type");
        builder.HasIndex(e => e.BucketId)
            .HasDatabaseName("ix_pc_aw_events_bucket_id");
        builder.HasIndex(e => e.SourceEventId)
            .HasDatabaseName("ix_pc_aw_events_source_event_id");
        builder.HasIndex(e => e.AppNameNormalized)
            .HasDatabaseName("ix_pc_aw_events_app_name_normalized");
        builder.HasIndex(e => new { e.DeviceId, e.BucketId, e.SourceEventId })
            .IsUnique()
            .HasDatabaseName("ux_pc_aw_events_source")
            .HasFilter("bucket_id IS NOT NULL AND source_event_id IS NOT NULL");
    }
}

public class AwBucketEntityConfiguration : IEntityTypeConfiguration<AwBucketEntity>
{
    public void Configure(EntityTypeBuilder<AwBucketEntity> builder)
    {
        builder.HasIndex(e => new { e.PimDeviceId, e.BucketId })
            .IsUnique()
            .HasDatabaseName("ux_pc_aw_buckets_device_bucket");
        builder.HasIndex(e => e.BucketType)
            .HasDatabaseName("ix_pc_aw_buckets_type");
        builder.HasIndex(e => e.SeenAt)
            .HasDatabaseName("ix_pc_aw_buckets_seen_at");
    }
}

public class KeystatsSampleEntityConfiguration : IEntityTypeConfiguration<KeystatsSampleEntity>
{
    public void Configure(EntityTypeBuilder<KeystatsSampleEntity> builder)
    {
        builder.HasIndex(e => new { e.PimDeviceId, e.SampledAtUtc })
            .IsUnique()
            .HasDatabaseName("ux_pc_keystats_samples_device_minute");
        builder.HasIndex(e => e.StatsDate)
            .HasDatabaseName("ix_pc_keystats_samples_stats_date");
    }
}

public class AppCategoryEntityConfiguration : IEntityTypeConfiguration<AppCategoryEntity>
{
    public void Configure(EntityTypeBuilder<AppCategoryEntity> builder)
    {
        builder.ToTable("pc_app_categories");
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => e.CategoryName);
        builder.HasIndex(e => e.Priority);
    }
}

public class ActivityCategoryRuleEntityConfiguration : IEntityTypeConfiguration<ActivityCategoryRuleEntity>
{
    public void Configure(EntityTypeBuilder<ActivityCategoryRuleEntity> builder)
    {
        builder.ToTable("pc_activity_category_rules");
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => e.RuleName)
            .IsUnique()
            .HasDatabaseName("ux_pc_activity_category_rules_rule_name");
        builder.HasIndex(e => e.Status).HasDatabaseName("ix_pc_activity_category_rules_status");
        builder.HasIndex(e => e.Priority).HasDatabaseName("ix_pc_activity_category_rules_priority");
        builder.HasIndex(e => e.CategoryName).HasDatabaseName("ix_pc_activity_category_rules_category_name");
        builder.HasIndex(e => e.ProjectTag).HasDatabaseName("ix_pc_activity_category_rules_project_tag");
        builder.Property(e => e.CategoryId).HasColumnName("category_id");
        builder.HasOne<PcCategoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class ActivityClassificationSuggestionEntityConfiguration : IEntityTypeConfiguration<ActivityClassificationSuggestionEntity>
{
    public void Configure(EntityTypeBuilder<ActivityClassificationSuggestionEntity> builder)
    {
        builder.ToTable("pc_activity_classification_suggestions");
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => e.ClusterKey).HasDatabaseName("ix_pc_activity_classification_suggestions_cluster_key");
        builder.HasIndex(e => e.ClusterKey)
            .IsUnique()
            .HasDatabaseName("ux_pc_activity_classification_suggestions_pending_cluster")
            .HasFilter("status = 'pending'");
        builder.HasIndex(e => e.Status).HasDatabaseName("ix_pc_activity_classification_suggestions_status");
        builder.HasIndex(e => e.UpdatedAt).HasDatabaseName("ix_pc_activity_classification_suggestions_updated_at");
    }
}

public class ActivityClassificationEntityConfiguration : IEntityTypeConfiguration<ActivityClassificationEntity>
{
    public void Configure(EntityTypeBuilder<ActivityClassificationEntity> builder)
    {
        builder.ToTable("pc_activity_classifications");
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.SourceEventIdsJson).HasDefaultValueSql("'[]'::jsonb");
        builder.Property(e => e.RecordKeyVersion).HasDefaultValue("pc-fallback-v1");
        builder.Property(e => e.RecordKeyStability).HasDefaultValue("low");
        builder.Property(e => e.SourceType).HasDefaultValue("fallback");
        builder.Property(e => e.SourceBucketIdsJson).HasDefaultValueSql("'[]'::jsonb");
        builder.Property(e => e.InterpretationVersion).HasDefaultValue("interpreted-aw-v1");
        builder.Property(e => e.CategoryName).HasDefaultValue("其他");
        builder.Property(e => e.CategoryColor).HasDefaultValue("#64748b");
        builder.Property(e => e.Confidence).HasDefaultValue(0.2);
        builder.Property(e => e.Source).HasDefaultValue("fallback");
        builder.Property(e => e.Explanation).HasDefaultValue("没有匹配到规则或启发式分类。");
        builder.Property(e => e.ClassifierVersion).HasDefaultValue("local-v1");
        builder.Property(e => e.ClassifiedAt).HasDefaultValueSql("NOW()");
        builder.HasIndex(e => e.RecordKey)
            .IsUnique()
            .HasDatabaseName("ux_pc_activity_classifications_record_key");
        builder.HasIndex(e => e.StartedAt)
            .HasDatabaseName("ix_pc_activity_classifications_started_at");
        builder.HasIndex(e => e.DeviceId)
            .HasDatabaseName("ix_pc_activity_classifications_device_id");
        builder.HasIndex(e => e.CategoryName)
            .HasDatabaseName("ix_pc_activity_classifications_category_name");
        builder.HasIndex(e => e.ProjectTag)
            .HasDatabaseName("ix_pc_activity_classifications_project_tag");
        builder.HasIndex(e => e.SourceRuleId)
            .HasDatabaseName("ix_pc_activity_classifications_source_rule_id");
        builder.HasIndex(e => e.RecordKeyVersion)
            .HasDatabaseName("ix_pc_activity_classifications_record_key_version");
        builder.HasIndex(e => e.SourceType)
            .HasDatabaseName("ix_pc_activity_classifications_source_type");
    }
}

public class ActivityClassificationAuditEntityConfiguration : IEntityTypeConfiguration<ActivityClassificationAuditEntity>
{
    public void Configure(EntityTypeBuilder<ActivityClassificationAuditEntity> builder)
    {
        builder.ToTable("pc_activity_classification_audits");
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.AffectedRecordKeysJson).HasDefaultValueSql("'[]'::jsonb");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.HasIndex(e => e.RuleId).HasDatabaseName("ix_pc_activity_classification_audits_rule_id");
        builder.HasIndex(e => e.SuggestionId).HasDatabaseName("ix_pc_activity_classification_audits_suggestion_id");
        builder.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_pc_activity_classification_audits_created_at");
    }
}

public class ActivityClassificationSettingsEntityConfiguration : IEntityTypeConfiguration<ActivityClassificationSettingsEntity>
{
    public void Configure(EntityTypeBuilder<ActivityClassificationSettingsEntity> builder)
    {
        builder.ToTable("pc_activity_classification_settings");
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.SettingsKey).HasDefaultValue("default");
        builder.Property(e => e.RecommendedMinimumClassificationDurationMinutes).HasDefaultValue(5);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
        builder.HasIndex(e => e.SettingsKey)
            .IsUnique()
            .HasDatabaseName("ux_pc_activity_classification_settings_key");
    }
}

public class PcCategoryEntityConfiguration : IEntityTypeConfiguration<PcCategoryEntity>
{
    public void Configure(EntityTypeBuilder<PcCategoryEntity> builder)
    {
        builder.ToTable("pc_categories");
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ParentId).HasColumnName("parent_id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Color).HasColumnName("color").HasMaxLength(7).HasDefaultValue("#64748b");
        builder.Property(e => e.Icon).HasColumnName("icon").HasMaxLength(32);
        builder.Property(e => e.Productivity).HasColumnName("productivity").HasMaxLength(16).HasDefaultValue("neutral");
        builder.Property(e => e.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
        builder.Property(e => e.IsBuiltin).HasColumnName("is_builtin").HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");
        builder.HasOne(e => e.Parent)
            .WithMany(e => e.Children)
            .HasForeignKey(e => e.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => e.ParentId).HasDatabaseName("ix_pc_categories_parent_id");
        builder.HasIndex(e => e.Name).HasDatabaseName("ix_pc_categories_name");
        builder.HasIndex(e => e.SortOrder).HasDatabaseName("ix_pc_categories_sort_order");
    }
}

public class AppSignatureEntityConfiguration : IEntityTypeConfiguration<AppSignatureEntity>
{
    public void Configure(EntityTypeBuilder<AppSignatureEntity> builder)
    {
        builder.ToTable("pc_app_signatures");
        builder.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ProcessName).HasColumnName("process_name").HasMaxLength(256).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
        builder.Property(e => e.CategoryPath).HasColumnName("category_path").HasMaxLength(256);
        builder.Property(e => e.Productivity).HasColumnName("productivity").HasMaxLength(32).HasDefaultValue("neutral");
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32).HasDefaultValue("builtin");
        builder.Property(e => e.Confidence).HasColumnName("confidence").HasDefaultValue(1.0);
        builder.Property(e => e.Icon).HasColumnName("icon").HasMaxLength(16);
        builder.Property(e => e.SearchKeywords).HasColumnName("search_keywords").HasMaxLength(512);
        builder.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");
        builder.HasIndex(e => e.ProcessName)
            .IsUnique()
            .HasDatabaseName("ux_pc_app_signatures_process_name");
        builder.HasIndex(e => e.DisplayName)
            .HasDatabaseName("ix_pc_app_signatures_display_name");
    }
}

public class AppKnowledgeContextEntityConfiguration : IEntityTypeConfiguration<AppKnowledgeContextEntity>
{
    public void Configure(EntityTypeBuilder<AppKnowledgeContextEntity> builder)
    {
        builder.ToTable("pc_app_knowledge_contexts");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.AppSignatureId).HasColumnName("app_signature_id");
        builder.Property(item => item.ProcessName).HasColumnName("process_name").HasMaxLength(256).IsRequired();
        builder.Property(item => item.PatternType).HasColumnName("pattern_type").HasMaxLength(64).IsRequired();
        builder.Property(item => item.PatternValue).HasColumnName("pattern_value").HasMaxLength(512).IsRequired();
        builder.Property(item => item.TargetCategoryName).HasColumnName("target_category_name").HasMaxLength(256);
        builder.Property(item => item.ProjectTag).HasColumnName("project_tag").HasMaxLength(256);
        builder.Property(item => item.ScopeSummary).HasColumnName("scope_summary").HasMaxLength(512).IsRequired();
        builder.Property(item => item.Source).HasColumnName("source").HasMaxLength(64).IsRequired();
        builder.Property(item => item.Confidence).HasColumnName("confidence");
        builder.Property(item => item.Enabled).HasColumnName("enabled");
        builder.Property(item => item.AffectedRecordCount).HasColumnName("affected_record_count").HasDefaultValue(0);
        builder.Property(item => item.AffectedDurationSeconds).HasColumnName("affected_duration_seconds").HasDefaultValue(0);
        builder.Property(item => item.LastMatchedAt).HasColumnName("last_matched_at");
        builder.Property(item => item.SourceRuleId).HasColumnName("source_rule_id");
        builder.Property(item => item.SourceSuggestionId).HasColumnName("source_suggestion_id");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at");
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(item => new { item.ProcessName, item.PatternType, item.PatternValue })
            .IsUnique()
            .HasDatabaseName("ix_pc_app_knowledge_contexts_app_pattern");
        builder.HasIndex(item => item.TargetCategoryName)
            .HasDatabaseName("ix_pc_app_knowledge_contexts_category");
        builder.HasIndex(item => item.AppSignatureId)
            .HasDatabaseName("ix_pc_app_knowledge_contexts_app_signature_id");
        builder.HasIndex(item => item.SourceSuggestionId)
            .HasDatabaseName("ix_pc_app_knowledge_contexts_source_suggestion");
        builder.HasOne(item => item.AppSignature)
            .WithMany()
            .HasForeignKey(item => item.AppSignatureId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class TrackerEventEntityConfiguration : IEntityTypeConfiguration<TrackerEventEntity>
{
    public void Configure(EntityTypeBuilder<TrackerEventEntity> builder)
    {
        builder.HasIndex(e => new { e.DeviceId, e.Date })
            .HasDatabaseName("idx_tracker_events_device_date");
        builder.HasIndex(e => e.Timestamp)
            .HasDatabaseName("idx_tracker_events_timestamp");
        builder.HasIndex(e => new { e.AppName, e.Date })
            .HasDatabaseName("idx_tracker_events_app");
        builder.HasIndex(e => e.EventType)
            .HasDatabaseName("idx_tracker_events_event_type");
        builder.HasIndex(e => e.IsIdle)
            .HasDatabaseName("idx_tracker_events_is_idle");
        builder.HasIndex(e => new { e.DeviceId, e.Timestamp, e.Duration, e.EventType, e.AppName })
            .IsUnique()
            .HasDatabaseName("ux_tracker_events_dedup");
    }
}

public class TrackerHealthEntityConfiguration : IEntityTypeConfiguration<TrackerHealthEntity>
{
    public void Configure(EntityTypeBuilder<TrackerHealthEntity> builder)
    {
        builder.HasIndex(e => e.DeviceId)
            .HasDatabaseName("ix_pc_tracker_health_device_id");
        builder.HasIndex(e => e.ReportedAt)
            .HasDatabaseName("ix_pc_tracker_health_reported_at");
        builder.HasIndex(e => new { e.DeviceId, e.ReportedAt })
            .HasDatabaseName("ix_pc_tracker_health_device_reported");
    }
}
