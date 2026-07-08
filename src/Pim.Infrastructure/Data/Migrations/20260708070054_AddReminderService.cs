using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_object_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    related_object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    trigger_reason = table.Column<string>(type: "text", nullable: false),
                    risk_level = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    channels_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    dnd_start = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    dnd_end = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Open"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reminder_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reminder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Created"),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_reminder_deliveries_reminders_reminder_id",
                        column: x => x.reminder_id,
                        principalTable: "reminders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_deliveries_reminder_id",
                table: "reminder_deliveries",
                column: "reminder_id");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_deliveries_user_id_created_at",
                table: "reminder_deliveries",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reminders_related_object_type_related_object_id",
                table: "reminders",
                columns: new[] { "related_object_type", "related_object_id" });

            migrationBuilder.CreateIndex(
                name: "IX_reminders_user_id_status_scheduled_at",
                table: "reminders",
                columns: new[] { "user_id", "status", "scheduled_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminder_deliveries");

            migrationBuilder.DropTable(
                name: "reminders");
        }
    }
}
