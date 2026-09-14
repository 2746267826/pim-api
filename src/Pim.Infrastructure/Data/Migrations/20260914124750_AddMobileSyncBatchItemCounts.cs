using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileSyncBatchItemCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rejected_count",
                table: "mobile_sync_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "skipped_count",
                table: "mobile_sync_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // #241：条目级校验拒绝曾被误当作批次失败，留下了 102 个
            // status = completed-with-errors 但 failed_count = 0 的批次。
            // 这里只修正状态标签（不删任何业务数据），让状态语义与计数自洽（EPIC #254 S11 / INV-M21）。
            migrationBuilder.Sql(
                "UPDATE mobile_sync_batches SET status = 'completed' " +
                "WHERE status = 'completed-with-errors' AND failed_count = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 注意：Up 里的状态标签修正（completed-with-errors -> completed，仅限 failed_count = 0 的行）
            // 不回滚 —— 'completed' 对这些行本来就是正确语义，回滚只会重新引入 #241 的状态失真。
            migrationBuilder.DropColumn(
                name: "rejected_count",
                table: "mobile_sync_batches");

            migrationBuilder.DropColumn(
                name: "skipped_count",
                table: "mobile_sync_batches");
        }
    }
}
