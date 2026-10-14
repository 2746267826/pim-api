using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventMuteFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_muted",
                table: "events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "default_muted",
                table: "calendars",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_muted",
                table: "events");

            migrationBuilder.DropColumn(
                name: "default_muted",
                table: "calendars");
        }
    }
}
