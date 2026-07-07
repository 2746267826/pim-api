using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mobile_app_catalog_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name_override = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    life_category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "未分类"),
                    is_system_noise = table.Column<bool>(type: "boolean", nullable: false),
                    hide_short_events = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_app_catalog_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_app_category_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "package-exact"),
                    pattern = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    life_category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "未分类"),
                    display_name_override = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_system_noise = table.Column<bool>(type: "boolean", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_app_category_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_timeline_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    local_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "Asia/Shanghai"),
                    life_category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "未分类"),
                    foreground_seconds = table.Column<long>(type: "bigint", nullable: false),
                    session_count = table.Column<int>(type: "integer", nullable: false),
                    app_count = table.Column<int>(type: "integer", nullable: false),
                    top_apps_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    source_mix_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    quality_flags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    includes_system_noise = table.Column<bool>(type: "boolean", nullable: false),
                    is_stale = table.Column<bool>(type: "boolean", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_timeline_blocks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_usage_aggregates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    granularity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "hour"),
                    bucket_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    bucket_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "Asia/Shanghai"),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    life_category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "未分类"),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "events"),
                    foreground_seconds = table.Column<long>(type: "bigint", nullable: false),
                    session_count = table.Column<int>(type: "integer", nullable: false),
                    launch_count = table.Column<int>(type: "integer", nullable: false),
                    switch_or_pickup_count = table.Column<int>(type: "integer", nullable: false),
                    is_system_noise = table.Column<bool>(type: "boolean", nullable: false),
                    short_event_seconds = table.Column<long>(type: "bigint", nullable: false),
                    quality_flags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    is_stale = table.Column<bool>(type: "boolean", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_usage_aggregates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_usage_goals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "total-daily"),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    life_category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "每日手机总时长"),
                    limit_seconds = table.Column<long>(type: "bigint", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "Asia/Shanghai"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_usage_goals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_catalog_overrides_user_id_is_system_noise",
                table: "mobile_app_catalog_overrides",
                columns: new[] { "user_id", "is_system_noise" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_catalog_overrides_user_id_life_category",
                table: "mobile_app_catalog_overrides",
                columns: new[] { "user_id", "life_category" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_catalog_overrides_user_id_package_name",
                table: "mobile_app_catalog_overrides",
                columns: new[] { "user_id", "package_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_category_rules_user_id_is_enabled_priority",
                table: "mobile_app_category_rules",
                columns: new[] { "user_id", "is_enabled", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_category_rules_user_id_life_category",
                table: "mobile_app_category_rules",
                columns: new[] { "user_id", "life_category" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_category_rules_user_id_rule_type_pattern",
                table: "mobile_app_category_rules",
                columns: new[] { "user_id", "rule_type", "pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_timeline_blocks_user_id_device_id_start_utc",
                table: "mobile_timeline_blocks",
                columns: new[] { "user_id", "device_id", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_timeline_blocks_user_id_is_stale",
                table: "mobile_timeline_blocks",
                columns: new[] { "user_id", "is_stale" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_timeline_blocks_user_id_life_category_start_utc",
                table: "mobile_timeline_blocks",
                columns: new[] { "user_id", "life_category", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_timeline_blocks_user_id_local_date",
                table: "mobile_timeline_blocks",
                columns: new[] { "user_id", "local_date" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_aggregates_user_id_device_id_bucket_start_utc",
                table: "mobile_usage_aggregates",
                columns: new[] { "user_id", "device_id", "bucket_start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_aggregates_user_id_device_id_granularity_bucke~",
                table: "mobile_usage_aggregates",
                columns: new[] { "user_id", "device_id", "granularity", "bucket_start_utc", "bucket_end_utc", "package_name", "life_category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_aggregates_user_id_is_stale",
                table: "mobile_usage_aggregates",
                columns: new[] { "user_id", "is_stale" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_aggregates_user_id_life_category_bucket_start_~",
                table: "mobile_usage_aggregates",
                columns: new[] { "user_id", "life_category", "bucket_start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_aggregates_user_id_package_name_bucket_start_u~",
                table: "mobile_usage_aggregates",
                columns: new[] { "user_id", "package_name", "bucket_start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_goals_user_id_is_enabled",
                table: "mobile_usage_goals",
                columns: new[] { "user_id", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_goals_user_id_life_category",
                table: "mobile_usage_goals",
                columns: new[] { "user_id", "life_category" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_goals_user_id_package_name",
                table: "mobile_usage_goals",
                columns: new[] { "user_id", "package_name" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_goals_user_id_scope_package_name_life_category",
                table: "mobile_usage_goals",
                columns: new[] { "user_id", "scope", "package_name", "life_category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_app_catalog_overrides");

            migrationBuilder.DropTable(
                name: "mobile_app_category_rules");

            migrationBuilder.DropTable(
                name: "mobile_timeline_blocks");

            migrationBuilder.DropTable(
                name: "mobile_usage_aggregates");

            migrationBuilder.DropTable(
                name: "mobile_usage_goals");
        }
    }
}
