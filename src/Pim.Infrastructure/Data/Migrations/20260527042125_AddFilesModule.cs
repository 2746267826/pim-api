using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFilesModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "nextcloud"),
                    base_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    internal_base_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    app_password_secret = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "file_ai_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    tags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    sensitivity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ai_request_log_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evidence_chunk_ids_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_ai_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "file_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    text_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    start_offset = table.Column<int>(type: "integer", nullable: false),
                    end_offset = table.Column<int>(type: "integer", nullable: false),
                    qdrant_point_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_chunks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "file_index_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "metadata"),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_index_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "file_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_file_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    parent_external_file_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    path = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    item_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "file"),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    etag = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    content_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    permissions = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_file_items_file_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "file_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggestion_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    ai_request_log_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_suggestions", x => x.id);
                    table.CheckConstraint("CK_file_suggestions_confidence_range", "confidence >= 0 AND confidence <= 1");
                    table.ForeignKey(
                        name: "FK_file_suggestions_file_items_file_item_id",
                        column: x => x.file_item_id,
                        principalTable: "file_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_version_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    etag = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "history"),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_versions", x => x.id);
                    table.UniqueConstraint("AK_file_versions_file_item_id_id", x => new { x.file_item_id, x.id });
                    table.ForeignKey(
                        name: "FK_file_versions_file_items_file_item_id",
                        column: x => x.file_item_id,
                        principalTable: "file_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_file_ai_results_ai_request_log_id",
                table: "file_ai_results",
                column: "ai_request_log_id");

            migrationBuilder.CreateIndex(
                name: "IX_file_ai_results_file_item_id_version_id",
                table: "file_ai_results",
                columns: new[] { "file_item_id", "version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_chunks_file_item_id_version_id_chunk_index",
                table: "file_chunks",
                columns: new[] { "file_item_id", "version_id", "chunk_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_chunks_qdrant_point_id",
                table: "file_chunks",
                column: "qdrant_point_id",
                unique: true,
                filter: "qdrant_point_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_file_index_jobs_file_item_id_status",
                table: "file_index_jobs",
                columns: new[] { "file_item_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_file_index_jobs_file_item_id_version_id",
                table: "file_index_jobs",
                columns: new[] { "file_item_id", "version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_index_jobs_status_stage",
                table: "file_index_jobs",
                columns: new[] { "status", "stage" });

            migrationBuilder.CreateIndex(
                name: "IX_file_items_id_current_version_id",
                table: "file_items",
                columns: new[] { "id", "current_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_items_provider_id_external_file_id",
                table: "file_items",
                columns: new[] { "provider_id", "external_file_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_items_provider_id_is_deleted",
                table: "file_items",
                columns: new[] { "provider_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_file_items_provider_id_parent_external_file_id",
                table: "file_items",
                columns: new[] { "provider_id", "parent_external_file_id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_items_provider_id_path",
                table: "file_items",
                columns: new[] { "provider_id", "path" });

            migrationBuilder.CreateIndex(
                name: "IX_file_providers_user_id_provider_base_url_username",
                table: "file_providers",
                columns: new[] { "user_id", "provider", "base_url", "username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_providers_user_id_status",
                table: "file_providers",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_file_suggestions_ai_request_log_id",
                table: "file_suggestions",
                column: "ai_request_log_id");

            migrationBuilder.CreateIndex(
                name: "IX_file_suggestions_file_item_id_status",
                table: "file_suggestions",
                columns: new[] { "file_item_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_file_suggestions_suggestion_type",
                table: "file_suggestions",
                column: "suggestion_type");

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_file_item_id_external_version_id",
                table: "file_versions",
                columns: new[] { "file_item_id", "external_version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_file_item_id_is_current",
                table: "file_versions",
                columns: new[] { "file_item_id", "is_current" },
                unique: true,
                filter: "is_current = true");

            migrationBuilder.AddForeignKey(
                name: "FK_file_ai_results_file_items_file_item_id",
                table: "file_ai_results",
                column: "file_item_id",
                principalTable: "file_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_file_ai_results_file_versions_file_item_id_version_id",
                table: "file_ai_results",
                columns: new[] { "file_item_id", "version_id" },
                principalTable: "file_versions",
                principalColumns: new[] { "file_item_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_file_chunks_file_items_file_item_id",
                table: "file_chunks",
                column: "file_item_id",
                principalTable: "file_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_file_chunks_file_versions_file_item_id_version_id",
                table: "file_chunks",
                columns: new[] { "file_item_id", "version_id" },
                principalTable: "file_versions",
                principalColumns: new[] { "file_item_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_file_index_jobs_file_items_file_item_id",
                table: "file_index_jobs",
                column: "file_item_id",
                principalTable: "file_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_file_index_jobs_file_versions_file_item_id_version_id",
                table: "file_index_jobs",
                columns: new[] { "file_item_id", "version_id" },
                principalTable: "file_versions",
                principalColumns: new[] { "file_item_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_file_items_file_versions_id_current_version_id",
                table: "file_items",
                columns: new[] { "id", "current_version_id" },
                principalTable: "file_versions",
                principalColumns: new[] { "file_item_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_file_versions_file_items_file_item_id",
                table: "file_versions");

            migrationBuilder.DropTable(
                name: "file_ai_results");

            migrationBuilder.DropTable(
                name: "file_chunks");

            migrationBuilder.DropTable(
                name: "file_index_jobs");

            migrationBuilder.DropTable(
                name: "file_suggestions");

            migrationBuilder.DropTable(
                name: "file_items");

            migrationBuilder.DropTable(
                name: "file_providers");

            migrationBuilder.DropTable(
                name: "file_versions");
        }
    }
}
