using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditVersionOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "audit_versions",
                type: "uuid",
                nullable: true);

            // Backfill ownership for existing audit rows from the confirmation that
            // requested them; rows without a confirmation stay NULL (not visible to any user).
            migrationBuilder.Sql("""
                UPDATE audit_versions v
                SET user_id = c.requested_by_user_id
                FROM operation_confirmations c
                WHERE v.confirmation_id = c.id
                  AND c.requested_by_user_id IS NOT NULL
                  AND v.user_id IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_audit_versions_user_id",
                table: "audit_versions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_versions_user_id",
                table: "audit_versions");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "audit_versions");
        }
    }
}
