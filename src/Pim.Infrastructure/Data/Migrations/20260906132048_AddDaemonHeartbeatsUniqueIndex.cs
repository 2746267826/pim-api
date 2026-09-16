using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// 历史空迁移：本迁移要建的对象全部由别处负责，自身不再做任何 schema 变更
    /// （PIM-199 / issue #271）。
    ///
    /// <para>
    /// 它原本想做的四件事，今天都已经有明确且更早/更可靠的归属，重复执行只会互相打架：
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>IX_daemon_heartbeats_device_id_daemon_kind</c>（唯一）由 Stage0 迁移
    ///     <c>20260524170037_Stage0OperationsTables</c> 创建，且<b>从第一版起就是 unique</b>；
    ///     Stage0 在迁移链里永远先于本迁移执行，所以这里的 CREATE 在<b>任何库上都会撞 42P07</b>。
    ///   </description></item>
    ///   <item><description>
    ///     <c>pc_tracker_events</c> 及其 browser/instance_id 列、<c>idx_tracker_events_browser</c>/
    ///     <c>idx_tracker_events_instance</c> 索引由运行时 <c>PcTrackerSchemaInitializer</c> 的幂等
    ///     SQL 维护，<b>不在 EF 模型里</b>；该 initializer 每次启动都执行（在迁移之后），
    ///     是这些对象的唯一所有者。EF 迁移去碰别人的表，正是 #271 的根因。
    ///   </description></item>
    ///   <item><description>
    ///     清理 daemon_heartbeats 重复行是<b>死代码</b>：唯一索引自 Stage0 起就存在，
    ///     该表从未出现过 <c>(device_id, daemon_kind)</c> 重复行，因此这句 DELETE 永远是 no-op。
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// #271 的事故正是「把这些语句写成非幂等」导致的：存量库上 ADD COLUMN browser 撞 42701、
    /// 全新库上 pc_tracker_events 尚不存在而撞 42P01，两者都会让启动迁移失败 → <c>Log.Fatal</c>
    /// 退出 → supervisord 反复拉起，API 永远不健康。改成空实现后，这条迁移对任何库状态都是
    /// 安全 no-op，只留下「已应用」这一历史标记。
    /// </para>
    ///
    /// <para>
    /// 保留本类（而不是删除迁移文件）是为了不破坏已应用过它的库的
    /// <c>__EFMigrationsHistory</c> 及其迁移 ID 顺序。
    /// </para>
    /// </summary>
    public partial class AddDaemonHeartbeatsUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 有意为空：见类型注释。所有目标对象均由 Stage0 迁移或运行时
            // PcTrackerSchemaInitializer 拥有，重复创建只会撞 42701/42P07/42P01。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 有意为空：本迁移不创建任何对象，回滚自然也不该删除任何对象。
            // 原实现会删掉 tracker 的 browser/instance_id 列（不可逆的数据丢失），
            // 并重建一个不带 COALESCE 的 ux_tracker_events_dedup（回归 #173 的 NULL 去重缺陷），
            // 且这些对象本来就不归本迁移所有。
        }
    }
}
