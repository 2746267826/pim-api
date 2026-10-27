using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModelProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "client_request_id",
                table: "ai_request_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "content_redacted",
                table: "ai_request_logs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "execution_expires_at",
                table: "ai_request_logs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finish_reason",
                table: "ai_request_logs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_endpoint",
                table: "ai_request_logs",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "model_profile_id",
                table: "ai_request_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_profile_name",
                table: "ai_request_logs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "model_revision",
                table: "ai_request_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_model_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    base_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    capabilities = table.Column<int>(type: "integer", nullable: false),
                    api_key_secret = table.Column<string>(type: "text", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_model_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_model_runtime",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    text_model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vision_model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transcription_model_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_model_runtime", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_model_runtime_ai_model_profiles_text_model_id",
                        column: x => x.text_model_id,
                        principalTable: "ai_model_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_model_runtime_ai_model_profiles_transcription_model_id",
                        column: x => x.transcription_model_id,
                        principalTable: "ai_model_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_model_runtime_ai_model_profiles_vision_model_id",
                        column: x => x.vision_model_id,
                        principalTable: "ai_model_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_status_execution_expires_at",
                table: "ai_request_logs",
                columns: new[] { "status", "execution_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_user_id_client_request_id",
                table: "ai_request_logs",
                columns: new[] { "user_id", "client_request_id" },
                unique: true,
                filter: "client_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_profiles_normalized_name",
                table: "ai_model_profiles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_runtime_text_model_id",
                table: "ai_model_runtime",
                column: "text_model_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_runtime_transcription_model_id",
                table: "ai_model_runtime",
                column: "transcription_model_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_runtime_vision_model_id",
                table: "ai_model_runtime",
                column: "vision_model_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_model_runtime");

            migrationBuilder.DropTable(
                name: "ai_model_profiles");

            migrationBuilder.DropIndex(
                name: "IX_ai_request_logs_status_execution_expires_at",
                table: "ai_request_logs");

            migrationBuilder.DropIndex(
                name: "IX_ai_request_logs_user_id_client_request_id",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "client_request_id",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "content_redacted",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "execution_expires_at",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "finish_reason",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "model_endpoint",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "model_profile_id",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "model_profile_name",
                table: "ai_request_logs");

            migrationBuilder.DropColumn(
                name: "model_revision",
                table: "ai_request_logs");
        }
    }
}
