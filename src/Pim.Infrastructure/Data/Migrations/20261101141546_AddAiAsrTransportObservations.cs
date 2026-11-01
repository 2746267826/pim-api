using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAsrTransportObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gateway_evidence_source",
                table: "ai_asr_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gateway_name",
                table: "ai_asr_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "gateway_observed_at",
                table: "ai_asr_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_asr_transport_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transport_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    gateway_status = table.Column<int>(type: "integer", nullable: true),
                    gateway_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_asr_transport_observations", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_asr_transport_observations_ai_request_logs_log_id",
                        column: x => x.log_id,
                        principalTable: "ai_request_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_asr_transport_observations_log_id",
                table: "ai_asr_transport_observations",
                column: "log_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_asr_transport_observations_owner_kind_source",
                table: "ai_asr_transport_observations",
                columns: new[] { "owner_id", "client_request_id", "transport_kind", "source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_asr_transport_observations");

            migrationBuilder.DropColumn(
                name: "gateway_evidence_source",
                table: "ai_asr_jobs");

            migrationBuilder.DropColumn(
                name: "gateway_name",
                table: "ai_asr_jobs");

            migrationBuilder.DropColumn(
                name: "gateway_observed_at",
                table: "ai_asr_jobs");
        }
    }
}
