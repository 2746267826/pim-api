using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "notified_scheduled_at",
                table: "reminders",
                type: "timestamp with time zone",
                nullable: true);

            // 首次部署时，历史已到期提醒不应全部补发；只允许部署后新到期的提醒进入队列。
            migrationBuilder.Sql("""
                UPDATE reminders
                SET notified_scheduled_at = scheduled_at
                WHERE notified_scheduled_at IS NULL
                  AND scheduled_at <= now();
                """);

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    in_app_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    push_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    task_completed = table.Column<bool>(type: "boolean", nullable: false),
                    note_transcription_completed = table.Column<bool>(type: "boolean", nullable: false),
                    note_derived_completed = table.Column<bool>(type: "boolean", nullable: false),
                    calendar_reminder = table.Column<bool>(type: "boolean", nullable: false),
                    device_overrides_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    version = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "notification_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    endpoint_ciphertext = table.Column<string>(type: "text", nullable: false),
                    endpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    endpoint_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    p256dh_ciphertext = table.Column<string>(type: "text", nullable: false),
                    auth_ciphertext = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    detail_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    in_app_visible = table.Column<bool>(type: "boolean", nullable: false),
                    push_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    eligible_subscription_ids_json = table.Column<string>(type: "text", nullable: true),
                    fanout_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fanout_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_fanout_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    fanout_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    device_id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_events_event_id",
                        column: x => x.event_id,
                        principalTable: "notification_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_subscriptions_subscrip~",
                        column: x => x.subscription_id,
                        principalTable: "notification_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_event_id_subscription_id",
                table: "notification_deliveries",
                columns: new[] { "event_id", "subscription_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_status_next_attempt_at",
                table: "notification_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_subscription_id",
                table: "notification_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_user_id_created_at",
                table: "notification_deliveries",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_fanout_status_next_fanout_at",
                table: "notification_events",
                columns: new[] { "fanout_status", "next_fanout_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_sequence",
                table: "notification_events",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_user_id_created_at",
                table: "notification_events",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_user_id_dedupe_key",
                table: "notification_events",
                columns: new[] { "user_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_subscriptions_channel_endpoint_hash",
                table: "notification_subscriptions",
                columns: new[] { "channel", "endpoint_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_subscriptions_user_id_status",
                table: "notification_subscriptions",
                columns: new[] { "user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_events");

            migrationBuilder.DropTable(
                name: "notification_subscriptions");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "notified_scheduled_at",
                table: "reminders");
        }
    }
}
