using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompleteOutlookGraphSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "access_token_expires_at",
                table: "outlook_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outlook_change_key",
                table: "events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outlook_etag",
                table: "events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_events_outlook_change_key",
                table: "events",
                column: "outlook_change_key");

            migrationBuilder.CreateIndex(
                name: "IX_events_outlook_event_id",
                table: "events",
                column: "outlook_event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_events_outlook_change_key",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_events_outlook_event_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "access_token_expires_at",
                table: "outlook_connections");

            migrationBuilder.DropColumn(
                name: "outlook_change_key",
                table: "events");

            migrationBuilder.DropColumn(
                name: "outlook_etag",
                table: "events");
        }
    }
}
