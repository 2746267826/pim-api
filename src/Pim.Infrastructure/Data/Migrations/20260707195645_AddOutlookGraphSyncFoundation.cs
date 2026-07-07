using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookGraphSyncFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_id",
                table: "outlook_connections",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delta_link",
                table: "outlook_connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "outlook_connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "outlook_connections",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "outlook");

            migrationBuilder.AddColumn<string>(
                name: "scopes",
                table: "outlook_connections",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "Calendars.ReadWrite offline_access User.Read openid profile");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "outlook_connections",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "not-connected");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "outlook_connections",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "common");

            migrationBuilder.AddColumn<string>(
                name: "token_health",
                table: "outlook_connections",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "missing");

            migrationBuilder.CreateTable(
                name: "outlook_sync_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "outlook"),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "running"),
                    read_count = table.Column<int>(type: "integer", nullable: false),
                    created_count = table.Column<int>(type: "integer", nullable: false),
                    updated_count = table.Column<int>(type: "integer", nullable: false),
                    conflict_count = table.Column<int>(type: "integer", nullable: false),
                    confirmation_count = table.Column<int>(type: "integer", nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    steps_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    errors_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    error_summary = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlook_sync_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outlook_sync_batches_user_id",
                table: "outlook_sync_batches",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_outlook_sync_batches_user_id_provider_started_at",
                table: "outlook_sync_batches",
                columns: new[] { "user_id", "provider", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_outlook_sync_batches_user_id_started_at",
                table: "outlook_sync_batches",
                columns: new[] { "user_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outlook_sync_batches");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "delta_link",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "scopes",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "status",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "token_health",
                table: "outlook_connections");
        }
    }
}
