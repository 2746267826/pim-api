using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEndpointStatusPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "endpoint_notification_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    risk_level = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    result = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    detail_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    confirmation_id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    related_object_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    related_object_id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_notification_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "endpoint_statuses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    platform = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "windows"),
                    app_version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    upload_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Unknown"),
                    collection_cache_count = table.Column<int>(type: "integer", nullable: false),
                    online_only_blocked_count = table.Column<int>(type: "integer", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_statuses", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_notification_actions_confirmation_id",
                table: "endpoint_notification_actions",
                column: "confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_notification_actions_created_at",
                table: "endpoint_notification_actions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_notification_actions_device_id",
                table: "endpoint_notification_actions",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_notification_actions_user_id",
                table: "endpoint_notification_actions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_statuses_last_heartbeat_at",
                table: "endpoint_statuses",
                column: "last_heartbeat_at");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_statuses_user_id_device_id",
                table: "endpoint_statuses",
                columns: new[] { "user_id", "device_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "endpoint_notification_actions");

            migrationBuilder.DropTable(
                name: "endpoint_statuses");
        }
    }
}
