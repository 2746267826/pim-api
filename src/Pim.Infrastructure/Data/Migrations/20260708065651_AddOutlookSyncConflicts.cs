using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookSyncConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_conflicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "outlook"),
                    object_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "event"),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    graph_event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    conflict_kind = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "open"),
                    pim_snapshot_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    external_snapshot_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    resolved_confirmation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_conflicts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_conflicts_graph_event_id",
                table: "sync_conflicts",
                column: "graph_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_conflicts_object_type_object_id",
                table: "sync_conflicts",
                columns: new[] { "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_conflicts_resolved_confirmation_id",
                table: "sync_conflicts",
                column: "resolved_confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_conflicts_user_id_provider_status",
                table: "sync_conflicts",
                columns: new[] { "user_id", "provider", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_conflicts");
        }
    }
}
