using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// #246：移动端定位点缺少天然键唯一约束，重试会静默叠加重复行
    /// （生产实测 6,280 行里有 1,065 行是重复点，单时间戳最多 21 行）。
    ///
    /// 先按 (user_id, device_id, recorded_at_utc, latitude, longitude) 去重
    /// （保留精度最好的一行，即 horizontal_accuracy_meters 最小；再按 created_at / id 取最早），
    /// 再建唯一索引 —— 否则存量重复行会让建索引直接失败。
    ///
    /// 规模与遗留：
    /// - 该表实测约 6.3k 行（迁移会清理约 673 行），自连接 DELETE 的执行与持锁时间可忽略；
    ///   若将来行数超过约 100 万，应改为按自然键分批删除，避免长事务持锁。
    /// - <b>不可逆点</b>：被清理的重复行不会在 <c>Down()</c> 里恢复（只回滚唯一索引），
    ///   因此上线前需备份 <c>mobile_location_points</c>。
    /// </summary>
    public partial class AddMobileLocationPointNaturalKey : Migration
    {
        private const string NaturalKeyIndexName =
            "IX_mobile_location_points_user_id_device_id_recorded_at_utc_la~";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM mobile_location_points AS duplicate
                USING mobile_location_points AS keeper
                WHERE duplicate.user_id = keeper.user_id
                  AND duplicate.device_id = keeper.device_id
                  AND duplicate.recorded_at_utc = keeper.recorded_at_utc
                  AND duplicate.latitude = keeper.latitude
                  AND duplicate.longitude = keeper.longitude
                  AND (duplicate.horizontal_accuracy_meters, duplicate.created_at, duplicate.id)
                      > (keeper.horizontal_accuracy_meters, keeper.created_at, keeper.id);
                """);

            migrationBuilder.CreateIndex(
                name: NaturalKeyIndexName,
                table: "mobile_location_points",
                columns: new[] { "user_id", "device_id", "recorded_at_utc", "latitude", "longitude" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: NaturalKeyIndexName,
                table: "mobile_location_points");
        }
    }
}
