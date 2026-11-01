using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteTaskSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_task_suggestion_extractions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_input_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_generation_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_content_markdown = table.Column<string>(type: "text", nullable: false),
                    model_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    model_endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    suggestion_count = table.Column<int>(type: "integer", nullable: false),
                    ai_call_log_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_task_suggestion_extractions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "note_task_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_input_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_generation_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    due = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    task_book_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    confirm_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_task_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "FK_note_task_suggestions_note_task_suggestion_extractions_extr~",
                        column: x => x.extraction_id,
                        principalTable: "note_task_suggestion_extractions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestion_extractions_ai_call_log_id",
                table: "note_task_suggestion_extractions",
                column: "ai_call_log_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestion_extractions_user_id_note_id_created_at",
                table: "note_task_suggestion_extractions",
                columns: new[] { "user_id", "note_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestion_extractions_user_id_request_id",
                table: "note_task_suggestion_extractions",
                columns: new[] { "user_id", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestion_extractions_user_id_status_updated_at",
                table: "note_task_suggestion_extractions",
                columns: new[] { "user_id", "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_extraction_id",
                table: "note_task_suggestions",
                column: "extraction_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_task_id",
                table: "note_task_suggestions",
                column: "task_id",
                unique: true,
                filter: "\"task_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_user_id_confirm_request_id",
                table: "note_task_suggestions",
                columns: new[] { "user_id", "confirm_request_id" },
                unique: true,
                filter: "\"confirm_request_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_user_id_extraction_id_ordinal",
                table: "note_task_suggestions",
                columns: new[] { "user_id", "extraction_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_note_task_suggestions_user_id_note_id_status_updated_at",
                table: "note_task_suggestions",
                columns: new[] { "user_id", "note_id", "status", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_task_suggestions");

            migrationBuilder.DropTable(
                name: "note_task_suggestion_extractions");
        }
    }
}
