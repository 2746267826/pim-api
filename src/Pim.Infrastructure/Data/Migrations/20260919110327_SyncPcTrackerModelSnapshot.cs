using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// 快照同步迁移（issue #320）：把 <c>PimDbContextModelSnapshot</c>/Designer 与当前 EF 模型对齐，
    /// 自身<b>有意为空实现</b>，对任何库状态都是安全 no-op，只留下「已应用」这一历史标记。
    ///
    /// <para>
    /// 背景：09-14 的 <c>20260914155131_AddMobileAnalyticsMaterialization</c> 之后，PcTracker 的
    /// 实体变更（8 列 + 4 表）只进了实体与运行时 <c>PcTrackerSchemaInitializer</c> 的幂等 SQL，
    /// 没有进快照。此后任何人执行 <c>dotnet ef migrations add</c>，这 12 个对象都会被吸收进新迁移
    /// （#319、#321 两次实测并手工剔除）；一旦照单吸收成非幂等 DDL，存量库
    /// （pim_prod/pim_test，对象已由 initializer 建好）启动迁移即撞 42701/42P07 →
    /// Log.Fatal 退出、supervisord 反复拉起 —— #271 同款事故。
    /// </para>
    ///
    /// <para>
    /// 归属约定：这 12 个对象的唯一所有者是运行时 <c>PcTrackerSchemaInitializer</c>
    /// （每次启动、迁移之后执行，<c>CREATE TABLE IF NOT EXISTS</c> / <c>ADD COLUMN IF NOT EXISTS</c>），
    /// 迁移永远不去碰它们 —— 与 <c>20260906132048_AddDaemonHeartbeatsUniqueIndex</c>（#271）的
    /// 处理一致。快照/Designer 记录的是「EF 模型」而非「某张库的物理 schema」：
    /// 模型里有这些实体，物理对象由 initializer 收敛，两条线互不越界。
    /// </para>
    ///
    /// <para>
    /// 本次快照还顺带修复了 <c>file_text_snapshots</c> 实体块在 #321 手工剔除漂移对象时被削坏的
    /// 问题（快照里只剩 <c>ToTable</c>，列与外键全丢；真实迁移 <c>20260919092837_AddFileTextSnapshots</c>
    /// 是完整的，所有库上 FK 都已存在，因此生成的 <c>AddForeignKey</c> 同样必须剔除，不能执行）。
    /// </para>
    ///
    /// <para>
    /// 回归防护：<c>MigrationSnapshotSyncTests.ModelSnapshot_IsInSyncWithCurrentModel</c> 用与
    /// dotnet ef 相同的 differ 断言「快照 vs 模型」零差异；<c>Migration320RealDbTests</c> 在真库上
    /// 验证存量库形态下迁移链安全跑完。
    /// </para>
    /// </summary>
    public partial class SyncPcTrackerModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 有意为空：见类型注释。全部被吸收的操作（12 个 PcTracker 对象 + file_text_snapshots 的
            // 外键重放）都属于运行时 initializer 或已完成的历史迁移，重复执行必撞 42701/42P07。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 有意为空：本迁移不创建任何对象，回滚自然也不该删除任何对象。
        }
    }
}
