using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileSyncIngestTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mobile_sync_ingest_timings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    batch_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    window_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_wait_ms = table.Column<long>(type: "bigint", nullable: false),
                    advisory_lock_wait_ms = table.Column<long>(type: "bigint", nullable: false),
                    session_rebuild_ms = table.Column<long>(type: "bigint", nullable: false),
                    materialize_ms = table.Column<long>(type: "bigint", nullable: false),
                    compute_ms = table.Column<long>(type: "bigint", nullable: false),
                    commit_ms = table.Column<long>(type: "bigint", nullable: false),
                    total_ms = table.Column<long>(type: "bigint", nullable: false),
                    blockers = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_sync_ingest_timings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_sync_ingest_timings_created_at",
                table: "mobile_sync_ingest_timings",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_sync_ingest_timings_user_id_device_id_batch_id",
                table: "mobile_sync_ingest_timings",
                columns: new[] { "user_id", "device_id", "batch_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_sync_ingest_timings");
        }
    }
}
