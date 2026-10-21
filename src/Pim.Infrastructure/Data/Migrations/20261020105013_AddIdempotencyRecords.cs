using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    route = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    response_headers = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_expires_at",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_user_id_method_route_idempotency_key",
                table: "idempotency_records",
                columns: new[] { "user_id", "method", "route", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");
        }
    }
}
