using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiPurposeModelDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "calendar_planning_model_id",
                table: "ai_model_runtime",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "note_organization_model_id",
                table: "ai_model_runtime",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "note_todo_model_id",
                table: "ai_model_runtime",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_runtime_calendar_planning_model_id",
                table: "ai_model_runtime",
                column: "calendar_planning_model_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_runtime_note_organization_model_id",
                table: "ai_model_runtime",
                column: "note_organization_model_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_model_runtime_note_todo_model_id",
                table: "ai_model_runtime",
                column: "note_todo_model_id");

            migrationBuilder.AddForeignKey(
                name: "FK_ai_model_runtime_ai_model_profiles_calendar_planning_model_~",
                table: "ai_model_runtime",
                column: "calendar_planning_model_id",
                principalTable: "ai_model_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ai_model_runtime_ai_model_profiles_note_organization_model_~",
                table: "ai_model_runtime",
                column: "note_organization_model_id",
                principalTable: "ai_model_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ai_model_runtime_ai_model_profiles_note_todo_model_id",
                table: "ai_model_runtime",
                column: "note_todo_model_id",
                principalTable: "ai_model_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_model_runtime_ai_model_profiles_calendar_planning_model_~",
                table: "ai_model_runtime");

            migrationBuilder.DropForeignKey(
                name: "FK_ai_model_runtime_ai_model_profiles_note_organization_model_~",
                table: "ai_model_runtime");

            migrationBuilder.DropForeignKey(
                name: "FK_ai_model_runtime_ai_model_profiles_note_todo_model_id",
                table: "ai_model_runtime");

            migrationBuilder.DropIndex(
                name: "IX_ai_model_runtime_calendar_planning_model_id",
                table: "ai_model_runtime");

            migrationBuilder.DropIndex(
                name: "IX_ai_model_runtime_note_organization_model_id",
                table: "ai_model_runtime");

            migrationBuilder.DropIndex(
                name: "IX_ai_model_runtime_note_todo_model_id",
                table: "ai_model_runtime");

            migrationBuilder.DropColumn(
                name: "calendar_planning_model_id",
                table: "ai_model_runtime");

            migrationBuilder.DropColumn(
                name: "note_organization_model_id",
                table: "ai_model_runtime");

            migrationBuilder.DropColumn(
                name: "note_todo_model_id",
                table: "ai_model_runtime");
        }
    }
}
