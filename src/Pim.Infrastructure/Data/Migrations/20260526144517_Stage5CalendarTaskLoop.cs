using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage5CalendarTaskLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by_operation_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by_operation_kind",
                table: "tasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "planned_end",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by_operation_id",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by_operation_kind",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "exdates_json",
                table: "events",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "external_metadata_json",
                table: "events",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<bool>(
                name: "is_all_day",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "recurrence_id",
                table: "events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recurrence_metadata_json",
                table: "events",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "source_ics_component",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_time_zone_id",
                table: "events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_uid",
                table: "events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                table: "events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by_operation_id",
                table: "calendars",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by_operation_kind",
                table: "calendars",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_deleted_by_operation_id",
                table: "tasks",
                column: "deleted_by_operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_user_id_deleted_at",
                table: "tasks",
                columns: new[] { "user_id", "deleted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_user_id_dtstart_planned_end",
                table: "tasks",
                columns: new[] { "user_id", "dtstart", "planned_end" });

            migrationBuilder.CreateIndex(
                name: "IX_events_deleted_at_dtstart",
                table: "events",
                columns: new[] { "deleted_at", "dtstart" });

            migrationBuilder.CreateIndex(
                name: "IX_events_deleted_by_operation_id",
                table: "events",
                column: "deleted_by_operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_source_uid",
                table: "events",
                column: "source_uid");

            migrationBuilder.CreateIndex(
                name: "IX_calendars_deleted_by_operation_id",
                table: "calendars",
                column: "deleted_by_operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_calendars_user_id_deleted_at",
                table: "calendars",
                columns: new[] { "user_id", "deleted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_deleted_by_operation_id",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_tasks_user_id_deleted_at",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_tasks_user_id_dtstart_planned_end",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_events_deleted_at_dtstart",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_events_deleted_by_operation_id",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_events_source_uid",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_calendars_deleted_by_operation_id",
                table: "calendars");

            migrationBuilder.DropIndex(
                name: "IX_calendars_user_id_deleted_at",
                table: "calendars");

            migrationBuilder.DropColumn(
                name: "deleted_by_operation_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "deleted_by_operation_kind",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "planned_end",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "deleted_by_operation_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "deleted_by_operation_kind",
                table: "events");

            migrationBuilder.DropColumn(
                name: "exdates_json",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_metadata_json",
                table: "events");

            migrationBuilder.DropColumn(
                name: "is_all_day",
                table: "events");

            migrationBuilder.DropColumn(
                name: "recurrence_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "recurrence_metadata_json",
                table: "events");

            migrationBuilder.DropColumn(
                name: "source_ics_component",
                table: "events");

            migrationBuilder.DropColumn(
                name: "source_time_zone_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "source_uid",
                table: "events");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "deleted_by_operation_id",
                table: "calendars");

            migrationBuilder.DropColumn(
                name: "deleted_by_operation_kind",
                table: "calendars");
        }
    }
}
