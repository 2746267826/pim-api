using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage0OperationsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    error_code = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "daemon_heartbeats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    daemon_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "windows"),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    server_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    last_successful_upload_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempted_upload_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    upload_queue_count = table.Column<int>(type: "integer", nullable: true),
                    activity_watch_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Unknown"),
                    key_stats_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Unknown"),
                    collection_paused = table.Column<bool>(type: "boolean", nullable: false),
                    status_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daemon_heartbeats", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operation_confirmations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    risk_level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    preview_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Pending"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_confirmations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_action",
                table: "audit_logs",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_correlation_id",
                table: "audit_logs",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_created_at",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_resource_type",
                table: "audit_logs",
                column: "resource_type");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_user_id",
                table: "audit_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_daemon_heartbeats_device_id_daemon_kind",
                table: "daemon_heartbeats",
                columns: new[] { "device_id", "daemon_kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_daemon_heartbeats_received_at",
                table: "daemon_heartbeats",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "IX_operation_confirmations_expires_at",
                table: "operation_confirmations",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_operation_confirmations_operation_type",
                table: "operation_confirmations",
                column: "operation_type");

            migrationBuilder.CreateIndex(
                name: "IX_operation_confirmations_requested_by_user_id",
                table: "operation_confirmations",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operation_confirmations_status",
                table: "operation_confirmations",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "daemon_heartbeats");

            migrationBuilder.DropTable(
                name: "operation_confirmations");
        }
    }
}
