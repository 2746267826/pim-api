using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MicrosoftAuthorizationSessionIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "outlook_authorization_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "UX_outlook_authorization_sessions_active_connection",
                table: "outlook_authorization_sessions",
                column: "connection_id",
                unique: true,
                filter: "\"status\" IN ('starting', 'waiting-for-user')");

            migrationBuilder.AddForeignKey(
                name: "FK_outlook_authorization_sessions_outlook_connections_connection_id",
                table: "outlook_authorization_sessions",
                column: "connection_id",
                principalTable: "outlook_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_outlook_authorization_sessions_outlook_connections_connection_id",
                table: "outlook_authorization_sessions");

            migrationBuilder.DropIndex(
                name: "UX_outlook_authorization_sessions_active_connection",
                table: "outlook_authorization_sessions");

            migrationBuilder.DropColumn(
                name: "version",
                table: "outlook_authorization_sessions");
        }
    }
}
