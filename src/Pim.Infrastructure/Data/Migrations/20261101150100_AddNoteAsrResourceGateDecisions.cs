using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteAsrResourceGateDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "consecutive_automatic_resource_failures",
                table: "note_asr_nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resource_decision_at",
                table: "note_asr_nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resource_gate_failure_reasons_json",
                table: "note_asr_nodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resource_gate_status",
                table: "note_asr_nodes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "note_asr_resource_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_automatic = table.Column<bool>(type: "boolean", nullable: false),
                    gate_passed = table.Column<bool>(type: "boolean", nullable: false),
                    reasons_json = table.Column<string>(type: "text", nullable: false),
                    sampled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consecutive_failures_after = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_resource_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_note_asr_resource_decisions_note_asr_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "note_asr_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_resource_decisions_node_id",
                table: "note_asr_resource_decisions",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_resource_decisions_owner_id_node_id_claim_key",
                table: "note_asr_resource_decisions",
                columns: new[] { "owner_id", "node_id", "claim_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_resource_decisions_owner_id_node_id_created_at",
                table: "note_asr_resource_decisions",
                columns: new[] { "owner_id", "node_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_asr_resource_decisions");

            migrationBuilder.DropColumn(
                name: "consecutive_automatic_resource_failures",
                table: "note_asr_nodes");

            migrationBuilder.DropColumn(
                name: "resource_decision_at",
                table: "note_asr_nodes");

            migrationBuilder.DropColumn(
                name: "resource_gate_failure_reasons_json",
                table: "note_asr_nodes");

            migrationBuilder.DropColumn(
                name: "resource_gate_status",
                table: "note_asr_nodes");
        }
    }
}
