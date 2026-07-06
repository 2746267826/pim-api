using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "explanation",
                table: "pc_activity_classifications",
                type: "text",
                nullable: false,
                defaultValue: "没有匹配到规则或启发式分类。",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "No rule or heuristic matched.");

            migrationBuilder.CreateTable(
                name: "mobile_app_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    version_code = table.Column<long>(type: "bigint", nullable: true),
                    is_system_app = table.Column<bool>(type: "boolean", nullable: false),
                    category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    installer_package = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    first_install_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_update_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_app_catalog", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    device_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    brand = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    os_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    api_level = table.Column<int>(type: "integer", nullable: false),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_location_points",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    horizontal_accuracy_meters = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    altitude_meters = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    vertical_accuracy_meters = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    speed_meters_per_second = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    speed_accuracy_meters_per_second = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    bearing_degrees = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    bearing_accuracy_degrees = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    is_mock = table.Column<bool>(type: "boolean", nullable: false),
                    quality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "usable"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_location_points", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_sync_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    batch_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    window_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "completed"),
                    error_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_sync_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_usage_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_timestamp_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    class_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    source_window_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_window_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    collected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    quality_flags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_usage_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_usage_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    quality_flags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_usage_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_usage_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    window_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    total_time_visible_ms = table.Column<long>(type: "bigint", nullable: false),
                    last_time_used_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    quality_flags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_usage_summaries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_app_catalog_user_id_device_id_package_name",
                table: "mobile_app_catalog",
                columns: new[] { "user_id", "device_id", "package_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_user_id_device_id",
                table: "mobile_devices",
                columns: new[] { "user_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_user_id_last_seen_at_utc",
                table: "mobile_devices",
                columns: new[] { "user_id", "last_seen_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_points_user_id_device_id_recorded_at_utc",
                table: "mobile_location_points",
                columns: new[] { "user_id", "device_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_points_user_id_quality_recorded_at_utc",
                table: "mobile_location_points",
                columns: new[] { "user_id", "quality", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_sync_batches_user_id_device_id_batch_id",
                table: "mobile_sync_batches",
                columns: new[] { "user_id", "device_id", "batch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_sync_batches_user_id_device_id_created_at",
                table: "mobile_sync_batches",
                columns: new[] { "user_id", "device_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_events_user_id_device_id_event_timestamp_utc",
                table: "mobile_usage_events",
                columns: new[] { "user_id", "device_id", "event_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_events_user_id_device_id_package_name_event_ty~",
                table: "mobile_usage_events",
                columns: new[] { "user_id", "device_id", "package_name", "event_type", "event_timestamp_utc", "class_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_sessions_user_id_device_id_start_utc",
                table: "mobile_usage_sessions",
                columns: new[] { "user_id", "device_id", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_sessions_user_id_package_name_start_utc",
                table: "mobile_usage_sessions",
                columns: new[] { "user_id", "package_name", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_summaries_user_id_device_id_package_name_windo~",
                table: "mobile_usage_summaries",
                columns: new[] { "user_id", "device_id", "package_name", "window_start_utc", "window_end_utc", "source_kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_usage_summaries_user_id_device_id_window_start_utc",
                table: "mobile_usage_summaries",
                columns: new[] { "user_id", "device_id", "window_start_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_app_catalog");

            migrationBuilder.DropTable(
                name: "mobile_devices");

            migrationBuilder.DropTable(
                name: "mobile_location_points");

            migrationBuilder.DropTable(
                name: "mobile_sync_batches");

            migrationBuilder.DropTable(
                name: "mobile_usage_events");

            migrationBuilder.DropTable(
                name: "mobile_usage_sessions");

            migrationBuilder.DropTable(
                name: "mobile_usage_summaries");

            migrationBuilder.AlterColumn<string>(
                name: "explanation",
                table: "pc_activity_classifications",
                type: "text",
                nullable: false,
                defaultValue: "No rule or heuristic matched.",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "没有匹配到规则或启发式分类。");
        }
    }
}
