using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDaemonHeartbeatsUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_tracker_events_dedup",
                table: "pc_tracker_events");

            migrationBuilder.AddColumn<string>(
                name: "browser",
                table: "pc_tracker_events",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instance_id",
                table: "pc_tracker_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_tracker_events_browser",
                table: "pc_tracker_events",
                column: "browser");

            migrationBuilder.CreateIndex(
                name: "idx_tracker_events_instance",
                table: "pc_tracker_events",
                column: "instance_id");

            // PIM-199: 清理历史重复脏行并补齐唯一索引
            migrationBuilder.Sql(@"
                DELETE FROM daemon_heartbeats a USING daemon_heartbeats b
                WHERE a.device_id = b.device_id
                  AND a.daemon_kind = b.daemon_kind
                  AND (a.received_at < b.received_at OR (a.received_at = b.received_at AND a.id < b.id));
            ");

            migrationBuilder.CreateIndex(
                name: "IX_daemon_heartbeats_device_id_daemon_kind",
                table: "daemon_heartbeats",
                columns: new[] { "device_id", "daemon_kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_daemon_heartbeats_device_id_daemon_kind",
                table: "daemon_heartbeats");

            migrationBuilder.DropIndex(
                name: "idx_tracker_events_browser",
                table: "pc_tracker_events");

            migrationBuilder.DropIndex(
                name: "idx_tracker_events_instance",
                table: "pc_tracker_events");

            migrationBuilder.DropColumn(
                name: "browser",
                table: "pc_tracker_events");

            migrationBuilder.DropColumn(
                name: "instance_id",
                table: "pc_tracker_events");

            migrationBuilder.CreateIndex(
                name: "ux_tracker_events_dedup",
                table: "pc_tracker_events",
                columns: new[] { "device_id", "timestamp", "duration", "event_type", "app_name" },
                unique: true);
        }
    }
}
