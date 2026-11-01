using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteAsrRoutingPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "asr_batch_base_url",
                table: "ai_model_profiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "asr_prompt",
                table: "ai_model_profiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "note_asr_ack_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    segment_index = table.Column<int>(type: "integer", nullable: false),
                    provider_handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    result_text = table.Column<string>(type: "text", nullable: true),
                    coverage_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_ack_receipts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "note_asr_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    route = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    stop_requested = table.Column<bool>(type: "boolean", nullable: false),
                    config_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_revision = table.Column<Guid>(type: "uuid", nullable: true),
                    preset_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    preset_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    prompt_template = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    prompt_template_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_template_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_rendered = table.Column<string>(type: "character varying(4200)", maxLength: 4200, nullable: true),
                    prompt_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    execution_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    local_model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    local_model_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    expected_segments = table.Column<int>(type: "integer", nullable: false),
                    completed_segments = table.Column<int>(type: "integer", nullable: false),
                    failed_segments = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "note_asr_batch_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
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
                    profile_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_secret = table.Column<string>(type: "text", nullable: false),
                    consent_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    completion_window = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expected_custom_ids_json = table.Column<string>(type: "text", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "note_asr_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model_verified = table.Column<bool>(type: "boolean", nullable: false),
                    capabilities_json = table.Column<string>(type: "text", nullable: false),
                    runtime_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    runtime_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    resource_snapshot_json = table.Column<string>(type: "text", nullable: false),
                    sampled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_nodes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "note_asr_route_configs",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    default_route = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    local_model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    local_model_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cloud_model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_route_configs", x => x.owner_id);
                });

            migrationBuilder.CreateTable(
                name: "note_asr_leases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_leases", x => x.id);
                    table.ForeignKey(
                        name: "FK_note_asr_leases_note_asr_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "note_asr_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_asr_segment_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    segment_index = table.Column<int>(type: "integer", nullable: false),
                    recording_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recording_segment = table.Column<int>(type: "integer", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    source_file_index = table.Column<int>(type: "integer", nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_offset_samples = table.Column<long>(type: "bigint", nullable: false),
                    samples = table.Column<long>(type: "bigint", nullable: false),
                    receipt_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    result_text = table.Column<string>(type: "text", nullable: false),
                    result_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    coverage_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_asr_segment_evidence", x => x.id);
                    table.ForeignKey(
                        name: "FK_note_asr_segment_evidence_note_asr_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "note_asr_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "IX_note_asr_attempts_owner_id_state_created_at",
                table: "note_asr_attempts",
                columns: new[] { "owner_id", "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_attempts_owner_id_task_id",
                table: "note_asr_attempts",
                columns: new[] { "owner_id", "task_id" },
                unique: true);

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
                name: "IX_note_asr_nodes_owner_id_last_heartbeat_at",
                table: "note_asr_nodes",
                columns: new[] { "owner_id", "last_heartbeat_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_nodes_owner_id_node_id",
                table: "note_asr_nodes",
                columns: new[] { "owner_id", "node_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_segment_evidence_attempt_id",
                table: "note_asr_segment_evidence",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_segment_evidence_owner_id_attempt_id_fencing_token~",
                table: "note_asr_segment_evidence",
                columns: new[] { "owner_id", "attempt_id", "fencing_token", "segment_index", "receipt_kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_asr_segment_evidence_owner_id_task_id_version_id",
                table: "note_asr_segment_evidence",
                columns: new[] { "owner_id", "task_id", "version_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_asr_ack_receipts");

            migrationBuilder.DropTable(
                name: "note_asr_batch_jobs");

            migrationBuilder.DropTable(
                name: "note_asr_leases");

            migrationBuilder.DropTable(
                name: "note_asr_nodes");

            migrationBuilder.DropTable(
                name: "note_asr_route_configs");

            migrationBuilder.DropTable(
                name: "note_asr_segment_evidence");

            migrationBuilder.DropTable(
                name: "note_asr_attempts");

            migrationBuilder.DropColumn(
                name: "asr_batch_base_url",
                table: "ai_model_profiles");

            migrationBuilder.DropColumn(
                name: "asr_prompt",
                table: "ai_model_profiles");
        }
    }
}
