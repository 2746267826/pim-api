using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileForensics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mobile_dropped_reason_daily",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    local_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_dropped_reason_daily", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_forensic_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_item_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_forensic_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_dropped_reason_daily_user_id_device_id_local_date",
                table: "mobile_dropped_reason_daily",
                columns: new[] { "user_id", "device_id", "local_date" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_dropped_reason_daily_user_id_device_id_local_date_re~",
                table: "mobile_dropped_reason_daily",
                columns: new[] { "user_id", "device_id", "local_date", "reason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_forensic_events_user_id_device_id_client_item_key",
                table: "mobile_forensic_events",
                columns: new[] { "user_id", "device_id", "client_item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_forensic_events_user_id_device_id_event_type_occurre~",
                table: "mobile_forensic_events",
                columns: new[] { "user_id", "device_id", "event_type", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_forensic_events_user_id_device_id_occurred_at_utc",
                table: "mobile_forensic_events",
                columns: new[] { "user_id", "device_id", "occurred_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_dropped_reason_daily");

            migrationBuilder.DropTable(
                name: "mobile_forensic_events");
        }
    }
}
