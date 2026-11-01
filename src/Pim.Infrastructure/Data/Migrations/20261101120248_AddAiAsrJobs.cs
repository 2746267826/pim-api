using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAsrJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_asr_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    preset_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    preset_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_text = table.Column<string>(type: "text", nullable: false),
                    prompt_language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prompt_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prompt_template_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prompt_template_revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    audio_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    audio_format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    audio_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    audio_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    consent_json = table.Column<string>(type: "jsonb", nullable: false),
                    estimated_audio_seconds = table.Column<double>(type: "double precision", nullable: true),
                    estimated_completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_limit_exceeded = table.Column<bool>(type: "boolean", nullable: false),
                    abort_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome_known = table.Column<bool>(type: "boolean", nullable: false),
                    gateway_status = table.Column<int>(type: "integer", nullable: true),
                    upstream_status = table.Column<int>(type: "integer", nullable: true),
                    provider_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    response_body_complete = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    next_action = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    execution_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_asr_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_asr_jobs_ai_request_logs_log_id",
                        column: x => x.log_id,
                        principalTable: "ai_request_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_asr_jobs_log_id",
                table: "ai_asr_jobs",
                column: "log_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_asr_jobs_owner_id_client_request_id",
                table: "ai_asr_jobs",
                columns: new[] { "owner_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_asr_jobs_status_execution_expires_at",
                table: "ai_asr_jobs",
                columns: new[] { "status", "execution_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_asr_jobs");
        }
    }
}
