using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    risk_level = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "L0AutomaticArtifact"),
                    inputs_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    metrics_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    content_markdown = table.Column<string>(type: "text", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    changed_fields_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "Open"),
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "FK_report_suggestions_report_artifacts_report_id",
                        column: x => x.report_id,
                        principalTable: "report_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_user_id_kind_generated_at",
                table: "report_artifacts",
                columns: new[] { "user_id", "kind", "generated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_user_id_project_id",
                table: "report_artifacts",
                columns: new[] { "user_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_report_suggestions_confirmation_id",
                table: "report_suggestions",
                column: "confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_suggestions_report_id",
                table: "report_suggestions",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_suggestions_user_id_status",
                table: "report_suggestions",
                columns: new[] { "user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_suggestions");

            migrationBuilder.DropTable(
                name: "report_artifacts");
        }
    }
}
