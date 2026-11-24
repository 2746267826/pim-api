using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "initiated_at",
                table: "ai_request_logs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provider_connection_id",
                table: "ai_request_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provider_connection_id",
                table: "ai_model_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_provider_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_limit_leases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    purpose = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_limit_leases", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_usage_limit_leases_ai_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "ai_provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_limit_rules",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    warning_percent = table.Column<int>(type: "integer", nullable: false),
                    token_limit = table.Column<long>(type: "bigint", nullable: true),
                    cost_limit = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_limit_rules", x => x.connection_id);
                    table.ForeignKey(
                        name: "FK_ai_usage_limit_rules_ai_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "ai_provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_reminder_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cycle_key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    warning_percent = table.Column<int>(type: "integer", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    metrics_json = table.Column<string>(type: "jsonb", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_reminder_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_usage_reminder_events_ai_provider_connections_connection~",
                        column: x => x.connection_id,
                        principalTable: "ai_provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_reminder_event_reads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_reminder_event_reads", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_usage_reminder_event_reads_ai_usage_reminder_events_even~",
                        column: x => x.event_id,
                        principalTable: "ai_usage_reminder_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO ai_provider_connections
                    (id, revision, name, normalized_name, created_at, updated_at)
                SELECT id, revision, name, normalized_name, created_at, updated_at
                FROM ai_model_profiles;

                INSERT INTO ai_usage_limit_rules
                    (connection_id, revision, enabled, period, warning_percent,
                     token_limit, cost_limit, currency, time_zone_id, updated_at)
                SELECT id, gen_random_uuid(), FALSE, 'month', 80,
                       NULL, NULL, NULL, 'Asia/Shanghai', updated_at
                FROM ai_model_profiles;

                UPDATE ai_model_profiles
                SET provider_connection_id = id
                WHERE provider_connection_id IS NULL;

                UPDATE ai_request_logs AS logs
                SET provider_connection_id = profiles.provider_connection_id
                FROM ai_model_profiles AS profiles
                WHERE logs.provider_connection_id IS NULL
                  AND logs.model_profile_id = profiles.id
                  AND profiles.provider_connection_id IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_provider_connection_id_initiated_at",
                table: "ai_request_logs",
                columns: new[] { "provider_connection_id", "initiated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_provider_connection_id_started_at",
                table: "ai_request_logs",
                columns: new[] { "provider_connection_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_profiles_provider_connection_id",
                table: "ai_model_profiles",
                column: "provider_connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_provider_connections_normalized_name",
                table: "ai_provider_connections",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_limit_leases_connection_id_expires_at",
                table: "ai_usage_limit_leases",
                columns: new[] { "connection_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_limit_leases_connection_id_request_key",
                table: "ai_usage_limit_leases",
                columns: new[] { "connection_id", "request_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_reminder_event_reads_event_id_user_id",
                table: "ai_usage_reminder_event_reads",
                columns: new[] { "event_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_reminder_events_connection_id_period_cycle_key_lev~",
                table: "ai_usage_reminder_events",
                columns: new[] { "connection_id", "period", "cycle_key", "level" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ai_model_profiles_ai_provider_connections_provider_connecti~",
                table: "ai_model_profiles",
                column: "provider_connection_id",
                principalTable: "ai_provider_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ai_request_logs_ai_provider_connections_provider_connection~",
                table: "ai_request_logs",
                column: "provider_connection_id",
                principalTable: "ai_provider_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_model_profiles_ai_provider_connections_provider_connecti~",
                table: "ai_model_profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_ai_request_logs_ai_provider_connections_provider_connection~",
                table: "ai_request_logs");

            migrationBuilder.DropTable(
                name: "ai_usage_limit_leases");

            migrationBuilder.DropTable(
                name: "ai_usage_limit_rules");

            migrationBuilder.DropTable(
                name: "ai_usage_reminder_event_reads");

            migrationBuilder.DropTable(
                name: "ai_usage_reminder_events");

            migrationBuilder.DropTable(
                name: "ai_provider_connections");

            migrationBuilder.DropIndex(
                name: "IX_ai_request_logs_provider_connection_id_initiated_at",
                table: "ai_request_logs");

            migrationBuilder.DropIndex(
                name: "IX_ai_request_logs_provider_connection_id_started_at",
                table: "ai_request_logs");

            migrationBuilder.DropIndex(
                name: "IX_ai_model_profiles_provider_connection_id",
                table: "ai_model_profiles");

            migrationBuilder.DropColumn(
                name: "initiated_at",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "provider_connection_id",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "provider_connection_id",
                table: "ai_model_profiles");
        }
    }
}
