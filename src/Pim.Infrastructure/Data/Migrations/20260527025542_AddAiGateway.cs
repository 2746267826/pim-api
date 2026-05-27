using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_provider_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "litellm"),
                    base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    virtual_key_secret = table.Column<string>(type: "text", nullable: false),
                    default_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "disabled"),
                    last_health_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_request_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    module = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    purpose = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_object_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_object_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "litellm"),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    litellm_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    request_messages_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    request_payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    response_raw_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    response_text = table.Column<string>(type: "text", nullable: true),
                    parsed_output_json = table.Column<string>(type: "jsonb", nullable: true),
                    schema_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    schema_json_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    schema_validation_errors_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    total_tokens = table.Column<int>(type: "integer", nullable: true),
                    estimated_cost = table.Column<decimal>(type: "numeric", nullable: true),
                    currency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    input_chars = table.Column<int>(type: "integer", nullable: false),
                    output_chars = table.Column<int>(type: "integer", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    output_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_provider_settings_provider",
                table: "ai_provider_settings",
                column: "provider",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_provider_settings_status",
                table: "ai_provider_settings",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_correlation_id",
                table: "ai_request_logs",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_model",
                table: "ai_request_logs",
                column: "model");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_module",
                table: "ai_request_logs",
                column: "module");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_purpose",
                table: "ai_request_logs",
                column: "purpose");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_source_object_type_source_object_id",
                table: "ai_request_logs",
                columns: new[] { "source_object_type", "source_object_id" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_started_at",
                table: "ai_request_logs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_status",
                table: "ai_request_logs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_logs_user_id",
                table: "ai_request_logs",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_provider_settings");

            migrationBuilder.DropTable(
                name: "ai_request_logs");
        }
    }
}
