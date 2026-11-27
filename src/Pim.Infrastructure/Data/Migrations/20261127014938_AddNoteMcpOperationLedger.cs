using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteMcpOperationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_mcp_operations",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    response_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    response_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    receipt_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    revision = table.Column<Guid>(type: "uuid", nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    error_code = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_mcp_operations", x => new { x.owner_id, x.request_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_note_mcp_operations_owner_id_state_completed_at",
                table: "note_mcp_operations",
                columns: new[] { "owner_id", "state", "completed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_mcp_operations");
        }
    }
}
