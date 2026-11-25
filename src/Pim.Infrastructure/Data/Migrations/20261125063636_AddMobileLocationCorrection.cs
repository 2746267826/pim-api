using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileLocationCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at_utc",
                table: "mobile_location_points",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by_user_id",
                table: "mobile_location_points",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "excluded_at_utc",
                table: "mobile_location_points",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "excluded_by_user_id",
                table: "mobile_location_points",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_excluded",
                table: "mobile_location_points",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "original_latitude",
                table: "mobile_location_points",
                type: "numeric(10,7)",
                precision: 10,
                scale: 7,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "original_longitude",
                table: "mobile_location_points",
                type: "numeric(10,7)",
                precision: 10,
                scale: 7,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "mobile_location_points",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "mobile_location_points",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mobile_location_point_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    revision_before = table.Column<int>(type: "integer", nullable: false),
                    revision_after = table.Column<int>(type: "integer", nullable: false),
                    original_latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    original_longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    new_latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    new_longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    excluded_before = table.Column<bool>(type: "boolean", nullable: true),
                    excluded_after = table.Column<bool>(type: "boolean", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_location_point_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_location_segment_merges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    before_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    after_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "move"),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_location_segment_merges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_saved_places",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    place_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "other"),
                    center_latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    center_longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    radius_meters = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_saved_places", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_points_user_id_is_excluded_recorded_at_utc",
                table: "mobile_location_points",
                columns: new[] { "user_id", "is_excluded", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_point_revisions_user_id_created_at",
                table: "mobile_location_point_revisions",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_point_revisions_user_id_point_id_created_at",
                table: "mobile_location_point_revisions",
                columns: new[] { "user_id", "point_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_segment_merges_user_id_device_id",
                table: "mobile_location_segment_merges",
                columns: new[] { "user_id", "device_id" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_location_segment_merges_user_id_device_id_before_poi~",
                table: "mobile_location_segment_merges",
                columns: new[] { "user_id", "device_id", "before_point_id", "after_point_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_saved_places_user_id_center_latitude_center_longitude",
                table: "mobile_saved_places",
                columns: new[] { "user_id", "center_latitude", "center_longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_saved_places_user_id_is_enabled",
                table: "mobile_saved_places",
                columns: new[] { "user_id", "is_enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_location_point_revisions");

            migrationBuilder.DropTable(
                name: "mobile_location_segment_merges");

            migrationBuilder.DropTable(
                name: "mobile_saved_places");

            migrationBuilder.DropIndex(
                name: "IX_mobile_location_points_user_id_is_excluded_recorded_at_utc",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "deleted_at_utc",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "deleted_by_user_id",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "excluded_at_utc",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "excluded_by_user_id",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "is_excluded",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "original_latitude",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "original_longitude",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "mobile_location_points");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "mobile_location_points");
        }
    }
}
