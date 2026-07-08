using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    before_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    after_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    changed_fields_json = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_versions_confirmation_id",
                table: "audit_versions",
                column: "confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_versions_object_type_object_id_created_at",
                table: "audit_versions",
                columns: new[] { "object_type", "object_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_versions");
        }
    }
}
