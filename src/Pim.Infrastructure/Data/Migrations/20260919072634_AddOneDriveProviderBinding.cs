using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOneDriveProviderBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account_id",
                table: "file_providers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_name",
                table: "file_providers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_id",
                table: "file_providers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delta_link",
                table: "file_providers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delta_reset_at",
                table: "file_providers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "device_code_encrypted",
                table: "file_providers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "device_code_expires_at",
                table: "file_providers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "drive_id",
                table: "file_providers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "refresh_token_encrypted",
                table: "file_providers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_status",
                table: "file_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "idle");

            migrationBuilder.AddColumn<long>(
                name: "synced_item_count",
                table: "file_providers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "token_expires_at",
                table: "file_providers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_code",
                table: "file_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_uri",
                table: "file_providers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "account_id",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "account_name",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "delta_link",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "delta_reset_at",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "device_code_encrypted",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "device_code_expires_at",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "drive_id",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "refresh_token_encrypted",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "sync_status",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "synced_item_count",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "token_expires_at",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "user_code",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "verification_uri",
                table: "file_providers");
        }
    }
}
