using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteProcessingStandingAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "standing_authorization_cost_notice",
                table: "note_processing_preferences",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "standing_authorization_material_scope",
                table: "note_processing_preferences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "standing_authorization_possible_charge_acknowledged",
                table: "note_processing_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "standing_authorization_purpose",
                table: "note_processing_preferences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "standing_authorization_revision",
                table: "note_processing_preferences",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "standing_authorization_cost_notice",
                table: "note_processing_preferences");

            migrationBuilder.DropColumn(
                name: "standing_authorization_material_scope",
                table: "note_processing_preferences");

            migrationBuilder.DropColumn(
                name: "standing_authorization_possible_charge_acknowledged",
                table: "note_processing_preferences");

            migrationBuilder.DropColumn(
                name: "standing_authorization_purpose",
                table: "note_processing_preferences");

            migrationBuilder.DropColumn(
                name: "standing_authorization_revision",
                table: "note_processing_preferences");
        }
    }
}
