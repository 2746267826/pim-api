using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPcAppKnowledgeContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pc_app_knowledge_contexts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_signature_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    pattern_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pattern_value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    target_category_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    project_tag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    scope_summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    affected_record_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    affected_duration_seconds = table.Column<double>(type: "double precision", nullable: false, defaultValue: 0.0),
                    last_matched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_suggestion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pc_app_knowledge_contexts", x => x.id);
                    table.ForeignKey(
                        name: "FK_pc_app_knowledge_contexts_pc_app_signatures_app_signature_id",
                        column: x => x.app_signature_id,
                        principalTable: "pc_app_signatures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pc_app_knowledge_contexts_app_pattern",
                table: "pc_app_knowledge_contexts",
                columns: new[] { "process_name", "pattern_type", "pattern_value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pc_app_knowledge_contexts_app_signature_id",
                table: "pc_app_knowledge_contexts",
                column: "app_signature_id");

            migrationBuilder.CreateIndex(
                name: "ix_pc_app_knowledge_contexts_category",
                table: "pc_app_knowledge_contexts",
                column: "target_category_name");

            migrationBuilder.CreateIndex(
                name: "ix_pc_app_knowledge_contexts_source_suggestion",
                table: "pc_app_knowledge_contexts",
                column: "source_suggestion_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pc_app_knowledge_contexts");
        }
    }
}
