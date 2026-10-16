using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowOpenTaskExecutionSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ends_at",
                table: "task_execution_segments",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回滚前先给「开放段」（ends_at IS NULL，正在计时中）补一个收口时刻，
            // 否则 SET NOT NULL 会因存在 NULL 行而失败。用 starts_at 收口 = 该段时长记 0，
            // 不臆造时长、也不丢行。
            migrationBuilder.Sql(
                "UPDATE task_execution_segments SET ends_at = starts_at WHERE ends_at IS NULL;");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ends_at",
                table: "task_execution_segments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
