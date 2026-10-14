using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleOpenTaskExecutionSegment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 复审 I1：同一任务同时只允许一条「开放段」（ends_at IS NULL，且未被软删）。
            //
            // 为什么用原生 SQL 而不是 EF 的 HasIndex：EF Core 的索引标识是**属性集合**，
            // 同一列上无法同时建模「普通索引（IX_task_execution_segments_task_id，供按 task_id 查询）」
            // 与「带过滤的唯一索引」—— 在实体配置里声明后者会把前者合并掉。
            //
            // 过滤条件与实体上的全局软删过滤器一致：软删的开放段不参与判重，
            // 否则「删除任务 → 恢复」会因历史开放段而无法再开始计时。
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_task_execution_segments_open_per_task " +
                "ON task_execution_segments (task_id) " +
                "WHERE ends_at IS NULL AND deleted_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_task_execution_segments_open_per_task;");
        }
    }
}
