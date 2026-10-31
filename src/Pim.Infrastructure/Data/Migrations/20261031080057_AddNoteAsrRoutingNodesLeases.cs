using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteAsrRoutingNodesLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_asr_batch_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    intent_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_raw = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    batch_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    input_file_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    output_file_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_file_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    input_expire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    batch_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    base_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    consent_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    completion_window = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total_items = table.Column<int>(type: "integer", nullable: true),
                    completed_items = table.Column<int>(type: "integer", nullable: true),
                    failed_items = table.Column<int>(type: "integer", nullable: true),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    output_imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_batch_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_nodes_owner_id_last_heartbeat_at",
                table: "note_asr_nodes",
                columns: new[] { "owner_id", "last_heartbeat_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_nodes_owner_id_node_id",
                table: "note_asr_nodes",
                columns: new[] { "owner_id", "node_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_leases_attempt_id",
                table: "note_asr_leases",
                column: "attempt_id",
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_leases_owner_id_attempt_id_fencing_token",
                table: "note_asr_leases",
                columns: new[] { "owner_id", "attempt_id", "fencing_token" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_leases_owner_id_node_id",
                table: "note_asr_leases",
                columns: new[] { "owner_id", "node_id" },
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_leases_owner_id_status_expires_at",
                table: "note_asr_leases",
                columns: new[] { "owner_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_attempts_owner_id_state_created_at",
                table: "note_asr_attempts",
                columns: new[] { "owner_id", "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_attempts_owner_id_task_id",
                table: "note_asr_attempts",
                columns: new[] { "owner_id", "task_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_ack_receipts_attempt_id_fencing_token_kind",
                table: "note_asr_ack_receipts",
                columns: new[] { "attempt_id", "fencing_token", "kind" },
                unique: true,
                filter: "\"kind\" IN ('complete', 'fail')");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_ack_receipts_attempt_id_fencing_token_segment_index",
                table: "note_asr_ack_receipts",
                columns: new[] { "attempt_id", "fencing_token", "segment_index" },
                unique: true,
                filter: "\"kind\" = 'segment'");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_ack_receipts_owner_id_attempt_id_fencing_token",
                table: "note_asr_ack_receipts",
                columns: new[] { "owner_id", "attempt_id", "fencing_token" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_ack_receipts_owner_id_kind_provider_handle",
                table: "note_asr_ack_receipts",
                columns: new[] { "owner_id", "kind", "provider_handle" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_ack_receipts_owner_id_provider_handle",
                table: "note_asr_ack_receipts",
                columns: new[] { "owner_id", "provider_handle" },
                unique: true,
                filter: "\"kind\" = 'claim'");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_batch_jobs_batch_id",
                table: "note_asr_batch_jobs",
                column: "batch_id",
                unique: true,
                filter: "\"batch_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_batch_jobs_owner_id_status_updated_at",
                table: "note_asr_batch_jobs",
                columns: new[] { "owner_id", "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_batch_jobs_owner_id_task_id",
                table: "note_asr_batch_jobs",
                columns: new[] { "owner_id", "task_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_note_asr_leases_note_asr_attempts_attempt_id",
                table: "note_asr_leases",
                column: "attempt_id",
                principalTable: "note_asr_attempts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_note_asr_leases_note_asr_attempts_attempt_id",
                table: "note_asr_leases");

            migrationBuilder.DropTable(
                name: "note_asr_batch_jobs");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_nodes_owner_id_last_heartbeat_at",
                table: "note_asr_nodes");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_nodes_owner_id_node_id",
                table: "note_asr_nodes");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_leases_attempt_id",
                table: "note_asr_leases");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_leases_owner_id_attempt_id_fencing_token",
                table: "note_asr_leases");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_leases_owner_id_node_id",
                table: "note_asr_leases");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_leases_owner_id_status_expires_at",
                table: "note_asr_leases");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_attempts_owner_id_state_created_at",
                table: "note_asr_attempts");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_attempts_owner_id_task_id",
                table: "note_asr_attempts");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_ack_receipts_attempt_id_fencing_token_kind",
                table: "note_asr_ack_receipts");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_ack_receipts_attempt_id_fencing_token_segment_index",
                table: "note_asr_ack_receipts");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_ack_receipts_owner_id_attempt_id_fencing_token",
                table: "note_asr_ack_receipts");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_ack_receipts_owner_id_kind_provider_handle",
                table: "note_asr_ack_receipts");

            migrationBuilder.DropIndex(
                name: "IX_note_asr_ack_receipts_owner_id_provider_handle",
                table: "note_asr_ack_receipts");
        }
    }
}
