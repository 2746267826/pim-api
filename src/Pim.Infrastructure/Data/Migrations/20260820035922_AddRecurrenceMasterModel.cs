using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurrenceMasterModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_usage_aggregates",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "其他",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "未分类");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_timeline_blocks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "其他",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "未分类");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_app_category_rules",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "其他",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "未分类");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_app_catalog_overrides",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "其他",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "未分类");

            migrationBuilder.AddColumn<bool>(
                name: "is_exception",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_series_master",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "series_master_id",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE events
                SET is_series_master = true
                WHERE rrule IS NOT NULL
                  AND btrim(rrule) <> ''
                  AND is_series_master = false;
                """);

            migrationBuilder.Sql("""
                UPDATE events
                SET is_exception = true,
                    series_master_id = m.id
                FROM events m
                WHERE events.outlook_event_type = 'exception'
                  AND events.outlook_series_master_id IS NOT NULL
                  AND m.outlook_event_id = events.outlook_series_master_id
                  AND events.is_exception = false;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_pc_activity_category_rules_category_id",
                table: "pc_activity_category_rules",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_series_master_id_recurrence_id",
                table: "events",
                columns: new[] { "series_master_id", "recurrence_id" },
                unique: true,
                filter: "\"is_exception\" = true AND \"series_master_id\" IS NOT NULL AND \"recurrence_id\" IS NOT NULL AND \"deleted_at\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_events_events_series_master_id",
                table: "events",
                column: "series_master_id",
                principalTable: "events",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_events_events_series_master_id",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_pc_activity_category_rules_category_id",
                table: "pc_activity_category_rules");

            migrationBuilder.DropIndex(
                name: "IX_events_series_master_id_recurrence_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "is_exception",
                table: "events");

            migrationBuilder.DropColumn(
                name: "is_series_master",
                table: "events");

            migrationBuilder.DropColumn(
                name: "series_master_id",
                table: "events");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_usage_aggregates",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "未分类",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "其他");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_timeline_blocks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "未分类",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "其他");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_app_category_rules",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "未分类",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "其他");

            migrationBuilder.AlterColumn<string>(
                name: "life_category",
                table: "mobile_app_catalog_overrides",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "未分类",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldDefaultValue: "其他");
        }
    }
}
