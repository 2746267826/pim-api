using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedEventFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "attachment_references",
                table: "events",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "attendees",
                table: "events",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "categories",
                table: "events",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "description_format",
                table: "events",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_link",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "importance",
                table: "events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_online_meeting",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_reminder_on",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "online_meeting_provider",
                table: "events",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "online_meeting_url",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "organizer_json",
                table: "events",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE events
                SET organizer_json = jsonb_build_object('name', organizer, 'email', NULL)
                WHERE organizer IS NOT NULL
                  AND btrim(organizer) <> ''
                  AND organizer_json IS NULL;
                """);

            migrationBuilder.AddColumn<int>(
                name: "reminder_minutes_before_start",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sensitivity",
                table: "events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "show_as",
                table: "events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attachment_references",
                table: "events");

            migrationBuilder.DropColumn(
                name: "attendees",
                table: "events");

            migrationBuilder.DropColumn(
                name: "categories",
                table: "events");

            migrationBuilder.DropColumn(
                name: "description_format",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_link",
                table: "events");

            migrationBuilder.DropColumn(
                name: "importance",
                table: "events");

            migrationBuilder.DropColumn(
                name: "is_online_meeting",
                table: "events");

            migrationBuilder.DropColumn(
                name: "is_reminder_on",
                table: "events");

            migrationBuilder.DropColumn(
                name: "online_meeting_provider",
                table: "events");

            migrationBuilder.DropColumn(
                name: "online_meeting_url",
                table: "events");

            migrationBuilder.DropColumn(
                name: "organizer_json",
                table: "events");

            migrationBuilder.DropColumn(
                name: "reminder_minutes_before_start",
                table: "events");

            migrationBuilder.DropColumn(
                name: "sensitivity",
                table: "events");

            migrationBuilder.DropColumn(
                name: "show_as",
                table: "events");
        }
    }
}
