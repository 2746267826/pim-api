using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// #247②：把"派生分析数据"真正落库。
    ///
    /// 1. <c>mobile_analytics_materializations</c>：记录哪些窗口被物化过（读取派生表的前提）；
    /// 2. <c>mobile_timeline_blocks.block_id</c>：端点返回的块 Id 必须原样回放，
    ///    否则 timeline-blocks/{blockId}/sessions 无法再从物化块解析出明细。
    /// </summary>
    public partial class AddMobileAnalyticsMaterialization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "block_id",
                table: "mobile_timeline_blocks",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "mobile_analytics_materializations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "Asia/Shanghai"),
                    covered_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    covered_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_analytics_materializations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_analytics_materializations_user_id_device_id_covered~",
                table: "mobile_analytics_materializations",
                columns: new[] { "user_id", "device_id", "covered_from_utc", "covered_to_utc" },
                unique: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_analytics_materializations");

            migrationBuilder.DropColumn(
                name: "block_id",
                table: "mobile_timeline_blocks");
        }
    }
}
