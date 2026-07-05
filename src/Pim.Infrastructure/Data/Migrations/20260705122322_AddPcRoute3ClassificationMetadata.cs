using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPcRoute3ClassificationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
ALTER TABLE pc_activity_classifications ADD COLUMN IF NOT EXISTS interpretation_version VARCHAR(32) NOT NULL DEFAULT 'interpreted-aw-v1';
ALTER TABLE pc_activity_classifications ADD COLUMN IF NOT EXISTS record_key_stability VARCHAR(16) NOT NULL DEFAULT 'low';
ALTER TABLE pc_activity_classifications ADD COLUMN IF NOT EXISTS record_key_version VARCHAR(32) NOT NULL DEFAULT 'pc-fallback-v1';
ALTER TABLE pc_activity_classifications ADD COLUMN IF NOT EXISTS source_bucket_ids JSONB NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE pc_activity_classifications ADD COLUMN IF NOT EXISTS source_type VARCHAR(32) NOT NULL DEFAULT 'fallback';
CREATE INDEX IF NOT EXISTS ix_pc_activity_classifications_record_key_version
    ON pc_activity_classifications (record_key_version);
CREATE INDEX IF NOT EXISTS ix_pc_activity_classifications_source_type
    ON pc_activity_classifications (source_type);

CREATE TABLE IF NOT EXISTS pc_activity_classification_audits (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operation VARCHAR(64) NOT NULL,
    rule_id UUID,
    suggestion_id UUID,
    range_mode VARCHAR(16) NOT NULL,
    date_from VARCHAR(16),
    date_to VARCHAR(16),
    affected_record_count INT NOT NULL DEFAULT 0,
    affected_duration_seconds DOUBLE PRECISION NOT NULL DEFAULT 0,
    affected_record_keys JSONB NOT NULL DEFAULT '[]'::jsonb,
    created_by_user_id UUID,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_pc_activity_classification_audits_created_at
    ON pc_activity_classification_audits (created_at);
CREATE INDEX IF NOT EXISTS ix_pc_activity_classification_audits_rule_id
    ON pc_activity_classification_audits (rule_id);
CREATE INDEX IF NOT EXISTS ix_pc_activity_classification_audits_suggestion_id
    ON pc_activity_classification_audits (suggestion_id);

CREATE TABLE IF NOT EXISTS pc_app_signatures (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    process_name VARCHAR(256) NOT NULL,
    display_name VARCHAR(256) NOT NULL,
    category_path VARCHAR(256),
    productivity VARCHAR(32) DEFAULT 'neutral',
    description TEXT,
    source VARCHAR(32) NOT NULL DEFAULT 'builtin',
    confidence DOUBLE PRECISION NOT NULL DEFAULT 1,
    icon VARCHAR(16),
    search_keywords VARCHAR(512),
    last_seen_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pc_app_signatures_process_name
    ON pc_app_signatures (process_name);
CREATE INDEX IF NOT EXISTS ix_pc_app_signatures_display_name
    ON pc_app_signatures (display_name);
ALTER TABLE pc_app_signatures ALTER COLUMN source SET DEFAULT 'builtin';
UPDATE pc_app_signatures SET source = 'builtin' WHERE source IS NULL;
ALTER TABLE pc_app_signatures ALTER COLUMN source SET NOT NULL;
ALTER TABLE pc_app_signatures ALTER COLUMN confidence SET DEFAULT 1;
UPDATE pc_app_signatures SET confidence = 1 WHERE confidence IS NULL;
ALTER TABLE pc_app_signatures ALTER COLUMN confidence SET NOT NULL;

CREATE TABLE IF NOT EXISTS pc_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_id UUID REFERENCES pc_categories(id) ON DELETE RESTRICT,
    name VARCHAR(64) NOT NULL,
    color VARCHAR(7) NOT NULL DEFAULT '#64748b',
    icon VARCHAR(32),
    productivity VARCHAR(16) NOT NULL DEFAULT 'neutral',
    sort_order INT NOT NULL DEFAULT 0,
    is_builtin BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_pc_categories_name
    ON pc_categories (name);
CREATE INDEX IF NOT EXISTS ix_pc_categories_parent_id
    ON pc_categories (parent_id);
CREATE INDEX IF NOT EXISTS ix_pc_categories_sort_order
    ON pc_categories (sort_order);
ALTER TABLE pc_categories ALTER COLUMN name TYPE VARCHAR(64) USING LEFT(name, 64);
ALTER TABLE pc_categories ALTER COLUMN color TYPE VARCHAR(7) USING LEFT(color, 7);
ALTER TABLE pc_categories ALTER COLUMN icon TYPE VARCHAR(32) USING LEFT(icon, 32);
ALTER TABLE pc_categories ALTER COLUMN productivity TYPE VARCHAR(16) USING LEFT(productivity, 16);
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DROP TABLE IF EXISTS pc_activity_classification_audits;
DROP INDEX IF EXISTS ix_pc_activity_classifications_record_key_version;
DROP INDEX IF EXISTS ix_pc_activity_classifications_source_type;
ALTER TABLE pc_activity_classifications DROP COLUMN IF EXISTS interpretation_version;
ALTER TABLE pc_activity_classifications DROP COLUMN IF EXISTS record_key_stability;
ALTER TABLE pc_activity_classifications DROP COLUMN IF EXISTS record_key_version;
ALTER TABLE pc_activity_classifications DROP COLUMN IF EXISTS source_bucket_ids;
ALTER TABLE pc_activity_classifications DROP COLUMN IF EXISTS source_type;
""");
        }
    }
}
