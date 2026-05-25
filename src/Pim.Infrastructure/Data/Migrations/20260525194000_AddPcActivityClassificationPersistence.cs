using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPcActivityClassificationPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pc_activity_classification_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    settings_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "default"),
                    recommended_minimum_classification_duration_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_activity_classification_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pc_activity_classifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    record_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    record_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_event_ids = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    category_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "其他"),
                    category_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false, defaultValue: "#64748b"),
                    project_tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: false, defaultValue: 0.2),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "fallback"),
                    source_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    explanation = table.Column<string>(type: "text", nullable: false, defaultValue: "No rule or heuristic matched."),
                    classifier_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "local-v1"),
                    classified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    audit_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_activity_classifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pc_activity_classification_settings_key",
                table: "pc_activity_classification_settings",
                column: "settings_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classifications_category_name",
                table: "pc_activity_classifications",
                column: "category_name");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classifications_device_id",
                table: "pc_activity_classifications",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classifications_project_tag",
                table: "pc_activity_classifications",
                column: "project_tag");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classifications_source_rule_id",
                table: "pc_activity_classifications",
                column: "source_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_activity_classifications_started_at",
                table: "pc_activity_classifications",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ux_pc_activity_classifications_record_key",
                table: "pc_activity_classifications",
                column: "record_key",
                unique: true);

            migrationBuilder.Sql("""
INSERT INTO pc_activity_classification_settings (
    settings_key,
    recommended_minimum_classification_duration_minutes
)
VALUES ('default', 5)
ON CONFLICT (settings_key) DO NOTHING;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pc_activity_classification_settings");

            migrationBuilder.DropTable(
                name: "pc_activity_classifications");
        }
    }
}
