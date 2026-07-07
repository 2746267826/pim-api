using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskExecutionSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_execution_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    planning_reason = table.Column<string>(type: "text", nullable: true),
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_execution_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_execution_segments_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_execution_segments_confirmation_id",
                table: "task_execution_segments",
                column: "confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_execution_segments_task_id",
                table: "task_execution_segments",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_execution_segments_user_id",
                table: "task_execution_segments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_execution_segments_user_id_task_id_starts_at",
                table: "task_execution_segments",
                columns: new[] { "user_id", "task_id", "starts_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_execution_segments");
        }
    }
}
