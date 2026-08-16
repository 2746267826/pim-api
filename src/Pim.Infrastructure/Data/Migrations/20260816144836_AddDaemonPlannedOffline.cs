using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// daemon_heartbeats 增加 planned_offline_at / offline_reason 两列（阶段 4 任务 1）。
    ///
    /// 背景：Windows 客户端在关机/休眠/注销/退出前上报 planned_offline 事件，
    /// 服务端以 planned 标记区分「正常下线」与「异常离线」，消除「关机=不健康」误报。
    /// planned_offline_at 语义 = 计划离线时间（客户端 OccurredAt 或服务端接收时刻），
    /// received_at 语义保持 = 最近一次普通心跳时间，两者独立。
    /// 生成时间：2026-08-16T14:48:36Z（UTC）。
    /// </summary>
    [DbContext(typeof(PimDbContext))]
    [Migration("20260816144836_AddDaemonPlannedOffline")]
    public partial class AddDaemonPlannedOffline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "planned_offline_at",
                table: "daemon_heartbeats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offline_reason",
                table: "daemon_heartbeats",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "offline_reason",
                table: "daemon_heartbeats");

            migrationBuilder.DropColumn(
                name: "planned_offline_at",
                table: "daemon_heartbeats");
        }
    }
}