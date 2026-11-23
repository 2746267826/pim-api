using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteProcessingAutoSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_processing_preferences",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    auto_note_processing_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_processing_preferences", x => x.owner_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_processing_preferences");
        }
    }
}
