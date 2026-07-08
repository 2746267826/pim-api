using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompletePlanningObjectModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "domain_project_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_outcome",
                table: "tasks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "tasks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "state_reason",
                table: "tasks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "task_book_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_planning_placeholders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_planning_placeholders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "availability_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "domain_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "habit_routines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    cadence = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    rule_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_habit_routines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_checklist_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_done = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_checklist_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_checklist_items_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_books",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_books", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_books_domain_projects_domain_project_id",
                        column: x => x.domain_project_id,
                        principalTable: "domain_projects",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "habit_occurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    habit_routine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    confirmation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_habit_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "FK_habit_occurrences_habit_routines_habit_routine_id",
                        column: x => x.habit_routine_id,
                        principalTable: "habit_routines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_domain_project_id",
                table: "tasks",
                column: "domain_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_task_book_id",
                table: "tasks",
                column: "task_book_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_user_id_domain_project_id",
                table: "tasks",
                columns: new[] { "user_id", "domain_project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_user_id_task_book_id",
                table: "tasks",
                columns: new[] { "user_id", "task_book_id" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_planning_placeholders_confirmation_id",
                table: "ai_planning_placeholders",
                column: "confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_planning_placeholders_user_id_starts_at_ends_at",
                table: "ai_planning_placeholders",
                columns: new[] { "user_id", "starts_at", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_planning_placeholders_user_id_status",
                table: "ai_planning_placeholders",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_windows_user_id_kind",
                table: "availability_windows",
                columns: new[] { "user_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_windows_user_id_starts_at_ends_at",
                table: "availability_windows",
                columns: new[] { "user_id", "starts_at", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "IX_domain_projects_user_id_name",
                table: "domain_projects",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_domain_projects_user_id_status",
                table: "domain_projects",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_habit_occurrences_confirmation_id",
                table: "habit_occurrences",
                column: "confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_habit_occurrences_habit_routine_id",
                table: "habit_occurrences",
                column: "habit_routine_id");

            migrationBuilder.CreateIndex(
                name: "IX_habit_occurrences_user_id_starts_at_ends_at",
                table: "habit_occurrences",
                columns: new[] { "user_id", "starts_at", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "IX_habit_routines_user_id_cadence",
                table: "habit_routines",
                columns: new[] { "user_id", "cadence" });

            migrationBuilder.CreateIndex(
                name: "IX_habit_routines_user_id_status",
                table: "habit_routines",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_task_books_domain_project_id",
                table: "task_books",
                column: "domain_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_task_books_user_id_name_domain_project_id",
                table: "task_books",
                columns: new[] { "user_id", "name", "domain_project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_task_books_user_id_status",
                table: "task_books",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_task_checklist_items_task_id_sort_order",
                table: "task_checklist_items",
                columns: new[] { "task_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "IX_task_checklist_items_user_id",
                table: "task_checklist_items",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_tasks_domain_projects_domain_project_id",
                table: "tasks",
                column: "domain_project_id",
                principalTable: "domain_projects",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_tasks_task_books_task_book_id",
                table: "tasks",
                column: "task_book_id",
                principalTable: "task_books",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tasks_domain_projects_domain_project_id",
                table: "tasks");

            migrationBuilder.DropForeignKey(
                name: "FK_tasks_task_books_task_book_id",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "ai_planning_placeholders");

            migrationBuilder.DropTable(
                name: "availability_windows");

            migrationBuilder.DropTable(
                name: "habit_occurrences");

            migrationBuilder.DropTable(
                name: "task_books");

            migrationBuilder.DropTable(
                name: "task_checklist_items");

            migrationBuilder.DropTable(
                name: "habit_routines");

            migrationBuilder.DropTable(
                name: "domain_projects");

            migrationBuilder.DropIndex(
                name: "IX_tasks_domain_project_id",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_tasks_task_book_id",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_tasks_user_id_domain_project_id",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_tasks_user_id_task_book_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "domain_project_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "review_outcome",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "source",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "state_reason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "task_book_id",
                table: "tasks");
        }
    }
}
