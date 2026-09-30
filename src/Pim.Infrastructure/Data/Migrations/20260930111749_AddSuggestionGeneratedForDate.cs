using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// PC 分类建议的业务日列（WO-PC-BACKEND-20260930 REQ-5 / #366，P-3 默认方案）。
    ///
    /// <para>
    /// 归属约定沿用 <c>20260919110327_SyncPcTrackerModelSnapshot</c>（issue #320）：PcTracker 的物理对象
    /// 唯一所有者是运行时 <c>PcTrackerSchemaInitializer</c>（每次启动执行、全部 <c>IF NOT EXISTS</c>），
    /// 迁移只做幂等 DDL，绝不发非幂等语句 —— 否则存量库启动迁移会撞 42701（列已存在）→ 进程退出（#271 同款）。
    /// </para>
    /// <para>
    /// 可空：历史遗留行没有该值，读取侧会回退到「样本最新时刻 → 最后刷新时刻」推导业务日（AC-5.1）。
    /// </para>
    /// </summary>
    public partial class AddSuggestionGeneratedForDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE pc_activity_classification_suggestions " +
                "ADD COLUMN IF NOT EXISTS generated_for_date DATE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE pc_activity_classification_suggestions " +
                "DROP COLUMN IF EXISTS generated_for_date;");
        }
    }
}
