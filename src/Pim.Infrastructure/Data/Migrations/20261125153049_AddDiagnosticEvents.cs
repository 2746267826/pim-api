using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnosticEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "diagnostic_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    client_batch_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    os_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    stack_trace = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_events_user_id_device_id_client_event_id",
                table: "diagnostic_events",
                columns: new[] { "user_id", "device_id", "client_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_events_user_id_device_id_occurred_at_utc",
                table: "diagnostic_events",
                columns: new[] { "user_id", "device_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_events_user_id_received_at_utc",
                table: "diagnostic_events",
                columns: new[] { "user_id", "received_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagnostic_events");
        }
    }
}
