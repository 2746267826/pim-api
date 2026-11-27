using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookWritebackOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outlook_writeback_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    undo_of_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    calendar_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "processing"),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    result_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    previous_draft_json = table.Column<string>(type: "jsonb", nullable: true),
                    undo_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "unavailable"),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlook_writeback_operations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outlook_writeback_operations_state_updated_at",
                table: "outlook_writeback_operations",
                columns: new[] { "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_outlook_writeback_operations_user_id_event_id_created_at",
                table: "outlook_writeback_operations",
                columns: new[] { "user_id", "event_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "UX_outlook_writeback_operations_user_client_operation",
                table: "outlook_writeback_operations",
                columns: new[] { "user_id", "client_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_outlook_writeback_operations_user_undo_of",
                table: "outlook_writeback_operations",
                columns: new[] { "user_id", "undo_of_operation_id" },
                unique: true,
                filter: "\"undo_of_operation_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outlook_writeback_operations");
        }
    }
}
