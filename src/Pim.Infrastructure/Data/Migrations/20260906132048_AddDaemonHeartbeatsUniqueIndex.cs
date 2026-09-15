using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// 守护进程心跳唯一索引 + PC 追踪器 browser/instance_id 列（PIM-199 / issue #271）。
    ///
    /// <para>
    /// <b>本迁移必须整体可重复执行</b>：它要创建的对象在「已经跑过运行时初始化」的存量库上全部已存在，
    /// 而这条迁移又从未成功写入 <c>__EFMigrationsHistory</c>。一旦某条语句不是幂等的，升级启动就会
    /// 撞 42701（列已存在）/42P07（对象已存在），Program.cs 的 fail-fast 让进程退出、supervisord
    /// 反复拉起 —— 这就是 #271 里 API 永远不健康的原因。
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>pc_tracker_events</c> 及其 browser/instance_id 列、两个索引 <b>不在 EF 模型里</b>，
    ///     由运行时 <c>PcTrackerSchemaInitializer</c> 的幂等 SQL 维护（该 initializer 在模块初始化时
    ///     执行，也就是在本迁移<b>之后</b>）。因此全新库上这张表此刻还不存在，语句必须能安全跳过，
    ///     而不能让 42P01 中断整条迁移链。
    ///   </description></item>
    ///   <item><description>
    ///     <c>IX_daemon_heartbeats_device_id_daemon_kind</c> 已由 Stage0 迁移
    ///     （<c>20260524170037_Stage0OperationsTables</c>）以同名唯一索引建出，这里只做「确保存在」。
    ///   </description></item>
    /// </list>
    /// </summary>
    public partial class AddDaemonHeartbeatsUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pc_tracker_events 归运行时 initializer 所有：表在全新库上尚不存在（用 to_regclass 判空跳过），
            // 在存量库上列/索引已由 initializer 建好（用 IF NOT EXISTS 把重复创建降级为 no-op）。
            // to_regclass 与下面的 DDL 一样不带 schema 前缀：本仓库所有迁移（含 __EFMigrationsHistory）
            // 都按 search_path 解析表名，两处写法一致才能保证「判空的那张表」就是「要改的那张表」。
            // 注意：这里不再 DROP ux_tracker_events_dedup —— 该索引同样是 initializer 的
            // COALESCE 表达式索引（见 #173），删掉它只会短暂失去去重保护，而不是去掉反而让本迁移
            // 对「不由自己拥有的对象」保持只读。
            migrationBuilder.Sql("""
                DO $pim271$
                BEGIN
                    IF to_regclass('pc_tracker_events') IS NOT NULL THEN
                        ALTER TABLE pc_tracker_events ADD COLUMN IF NOT EXISTS browser character varying(16);
                        ALTER TABLE pc_tracker_events ADD COLUMN IF NOT EXISTS instance_id character varying(128);
                        CREATE INDEX IF NOT EXISTS idx_tracker_events_browser ON pc_tracker_events (browser);
                        CREATE INDEX IF NOT EXISTS idx_tracker_events_instance ON pc_tracker_events (instance_id);
                    END IF;
                END
                $pim271$;
                """);

            // PIM-199: 清理历史重复脏行并补齐唯一索引。
            // 唯一索引在 Stage0 迁移里就已建出，所以存量库上 CREATE 会撞 42P07 —— 用 IF NOT EXISTS
            // 幂等化；DELETE 本身重复执行是安全的（第一次清干净后就没有可删的行）。
            migrationBuilder.Sql("""
                DELETE FROM daemon_heartbeats a USING daemon_heartbeats b
                WHERE a.device_id = b.device_id
                  AND a.daemon_kind = b.daemon_kind
                  AND (a.received_at < b.received_at OR (a.received_at = b.received_at AND a.id < b.id));

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_daemon_heartbeats_device_id_daemon_kind"
                    ON daemon_heartbeats (device_id, daemon_kind);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 有意为空：本迁移的每一条语句都只是「确保对象存在」，且这些对象分别由 Stage0 迁移
            // （daemon_heartbeats 唯一索引）与运行时 PcTrackerSchemaInitializer（pc_tracker_events
            // 的列与索引）拥有。回滚时删掉它们既不属于本迁移的职责，也会让追踪器历史数据
            // （browser/instance_id）不可逆丢失；而 initializer 每次启动都会幂等地把 schema 收敛回
            // 期望状态。这里因此不做任何破坏性操作（原实现会删列并重建一个不带 COALESCE 的
            // ux_tracker_events_dedup —— 那正是 #173 修掉的 NULL 去重回归）。
        }
    }
}
