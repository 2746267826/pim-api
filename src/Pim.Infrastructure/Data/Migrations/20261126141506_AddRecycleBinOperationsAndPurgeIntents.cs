using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecycleBinOperationsAndPurgeIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recycle_bin_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    processing_token = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    receipt_json = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recycle_bin_operations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recycle_bin_purge_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expected_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    phase = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    material_versions = table.Column<int>(type: "integer", nullable: false),
                    asr_tasks = table.Column<int>(type: "integer", nullable: false),
                    derived_tasks = table.Column<int>(type: "integer", nullable: false),
                    ai_calls = table.Column<int>(type: "integer", nullable: false),
                    ai_images = table.Column<int>(type: "integer", nullable: false),
                    ai_audio = table.Column<int>(type: "integer", nullable: false),
                    asr_task_ids = table.Column<string>(type: "text", nullable: false),
                    derived_task_ids = table.Column<string>(type: "text", nullable: false),
                    ai_log_ids = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recycle_bin_purge_intents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recycle_bin_operations_owner_id_operation_id",
                table: "recycle_bin_operations",
                columns: new[] { "owner_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recycle_bin_operations_owner_id_status_lease_expires_at",
                table: "recycle_bin_operations",
                columns: new[] { "owner_id", "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_recycle_bin_purge_intents_owner_id_domain_item_id_generation",
                table: "recycle_bin_purge_intents",
                columns: new[] { "owner_id", "domain", "item_id", "generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recycle_bin_purge_intents_owner_id_phase_lease_expires_at",
                table: "recycle_bin_purge_intents",
                columns: new[] { "owner_id", "phase", "lease_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recycle_bin_operations");

            migrationBuilder.DropTable(
                name: "recycle_bin_purge_intents");
        }
    }
}
