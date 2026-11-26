using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Stores a hash of the caller-supplied writeback request in addition to the
    /// fully resolved payload hash. Missing/soft-deleted event replays no longer
    /// have the local event row needed to rebuild the resolved payload, so the
    /// replay must compare this durable request identity instead.
    /// </summary>
    public partial class AddOutlookWritebackRequestHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "request_hash",
                table: "outlook_writeback_operations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "request_hash",
                table: "outlook_writeback_operations");
        }
    }
}
