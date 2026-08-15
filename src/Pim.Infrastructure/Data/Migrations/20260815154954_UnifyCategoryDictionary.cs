using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// 统一分类字典为 7 大类并迁移存量数据（阶段 1，任务 1）。
    ///
    /// 背景：原有 pc_categories 为多层树（娱乐/工作/学习/沟通 + 细分支），
    /// 现收窄为平铺 7 大类（全部 IsBuiltin=true、Productivity='neutral'、ParentId=null）。
    /// 本迁移一次性完成：删除旧 builtin 细分支、重映射历史快照/规则/app_signatures
    /// 的 category_name/category_path 到 7 大类、清洗垃圾规则，并为规则表新增
    /// category_id 外键列（任务 2 将消费该列）。
    ///
    /// 映射规则与 C# 侧 CategoryLegacyMapper 双份但互不依赖（测试覆盖 C# 侧）：
    ///   编程/前端/后端/终端/运维/设计 → 编程/折腾
    ///   学习/技术学习/外语学习/阅读     → 学习
    ///   视频                             → 视频
    ///   沟通/即时消息/邮件/社交/会议     → 聊天
    ///   文档/办公/文件/浏览              → 文档
    ///   游戏/单机游戏/网络游戏           → 游戏
    ///   娱乐/音乐/工作/其他/null/空串    → 其他
    /// 生成时间：2026-08-15T15:49:54Z（UTC）。
    /// </summary>
    [DbContext(typeof(PimDbContext))]
    [Migration("20260815154954_UnifyCategoryDictionary")]
    public partial class UnifyCategoryDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) 删除旧 builtin 细分支（用户自建 is_builtin=false 保留）
            migrationBuilder.Sql(
                """
                DELETE FROM pc_categories
                 WHERE is_builtin AND name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他');
                """);

            // 2) 历史快照重映射（UPDATE + CASE）
            migrationBuilder.Sql(
                """
                UPDATE pc_activity_classifications SET category_name = CASE category_name
                  WHEN '编程' THEN '编程/折腾' WHEN '前端' THEN '编程/折腾' WHEN '后端' THEN '编程/折腾'
                  WHEN '终端' THEN '编程/折腾' WHEN '运维' THEN '编程/折腾' WHEN '设计' THEN '编程/折腾'
                  WHEN '技术学习' THEN '学习' WHEN '外语学习' THEN '学习' WHEN '阅读' THEN '学习'
                  WHEN '视频' THEN '视频'
                  WHEN '沟通' THEN '聊天' WHEN '即时消息' THEN '聊天' WHEN '邮件' THEN '聊天'
                  WHEN '社交' THEN '聊天' WHEN '会议' THEN '聊天'
                  WHEN '办公' THEN '文档' WHEN '文件' THEN '文档' WHEN '浏览' THEN '文档'
                  WHEN '单机游戏' THEN '游戏' WHEN '网络游戏' THEN '游戏'
                  WHEN '娱乐' THEN '其他' WHEN '音乐' THEN '其他' WHEN '工作' THEN '其他'
                  ELSE '其他' END
                 WHERE category_name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他');
                """);

            // 3a) 垃圾规则删除（conditions_json 无合法 all 数组，含 {"test":true} 类）
            migrationBuilder.Sql(
                """
                DELETE FROM pc_activity_category_rules
                 WHERE jsonb_typeof(conditions_json->'all') IS DISTINCT FROM 'array'
                    OR jsonb_array_length(conditions_json->'all') = 0;
                """);

            // 3b) 空分类规则删除（mobaxterm/foxitpdfeditor 等）
            migrationBuilder.Sql(
                """
                DELETE FROM pc_activity_category_rules
                 WHERE category_name IS NULL OR btrim(category_name) = '';
                """);

            // 3c) Browser builtin 规则删除（浏览器交给域名层）
            migrationBuilder.Sql(
                """
                DELETE FROM pc_activity_category_rules WHERE rule_name = 'Builtin: Browser apps';
                """);

            // 3d) 剩余规则 category_name 重映射
            migrationBuilder.Sql(
                """
                UPDATE pc_activity_category_rules SET category_name = CASE category_name
                  WHEN '编程' THEN '编程/折腾' WHEN '前端' THEN '编程/折腾' WHEN '后端' THEN '编程/折腾'
                  WHEN '终端' THEN '编程/折腾' WHEN '运维' THEN '编程/折腾' WHEN '设计' THEN '编程/折腾'
                  WHEN '技术学习' THEN '学习' WHEN '外语学习' THEN '学习' WHEN '阅读' THEN '学习'
                  WHEN '视频' THEN '视频'
                  WHEN '沟通' THEN '聊天' WHEN '即时消息' THEN '聊天' WHEN '邮件' THEN '聊天'
                  WHEN '社交' THEN '聊天' WHEN '会议' THEN '聊天'
                  WHEN '办公' THEN '文档' WHEN '文件' THEN '文档' WHEN '浏览' THEN '文档'
                  WHEN '单机游戏' THEN '游戏' WHEN '网络游戏' THEN '游戏'
                  WHEN '娱乐' THEN '其他' WHEN '音乐' THEN '其他' WHEN '工作' THEN '其他'
                  ELSE '其他' END
                 WHERE category_name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他');
                """);

            // 4) app_signatures.category_path 重映射
            //    注意 category_path 形如 '工作·编程'（点分隔路径）。种子（PcTrackerSchemaInitializer
            //    171 条）distinct 旧路径核对结果（14 个非空值 + 6 行 NULL）：
            //      工作·编程 / 工作·文档 / 工作·设计 / 工作·运维 / 沟通·会议 /
            //      沟通·即时消息 / 沟通·邮件 / 浏览 / 娱乐 / 娱乐·游戏 / 娱乐·游戏·单机 /
            //      娱乐·游戏·网络 / 娱乐·视频 / 娱乐·音乐
            //    计划 CASE 全覆盖上述值（'浏览' 由 '工作·浏览' 行缺漏补齐为本行；
            //    NULL 行由 WHERE 条件排除，不映射）。逐字面量映射（含层级组合，末段按 §0.2 规则映射）：
            migrationBuilder.Sql(
                """
                UPDATE app_signatures SET category_path = CASE category_path
                  WHEN '工作' THEN '其他'
                  WHEN '工作·编程' THEN '编程/折腾' WHEN '工作·前端' THEN '编程/折腾' WHEN '工作·后端' THEN '编程/折腾'
                  WHEN '工作·终端' THEN '编程/折腾' WHEN '工作·运维' THEN '编程/折腾' WHEN '工作·设计' THEN '编程/折腾'
                  WHEN '工作·文档' THEN '文档' WHEN '工作·办公' THEN '文档' WHEN '工作·文件' THEN '文档' WHEN '工作·浏览' THEN '文档'
                  WHEN '工作·会议' THEN '聊天'
                  WHEN '浏览' THEN '文档'
                  WHEN '娱乐' THEN '其他'
                  WHEN '娱乐·游戏' THEN '游戏' WHEN '娱乐·游戏·单机' THEN '游戏' WHEN '娱乐·游戏·网络' THEN '游戏'
                  WHEN '娱乐·视频' THEN '视频' WHEN '娱乐·音乐' THEN '其他' WHEN '娱乐·社交' THEN '聊天'
                  WHEN '学习' THEN '学习'
                  WHEN '学习·技术学习' THEN '学习' WHEN '学习·外语学习' THEN '学习' WHEN '学习·阅读' THEN '学习'
                  WHEN '沟通' THEN '聊天' WHEN '沟通·即时消息' THEN '聊天' WHEN '沟通·邮件' THEN '聊天'
                  ELSE '其他' END
                 WHERE category_path IS NOT NULL AND category_path <> '';
                """);

            // 5) 规则表加 category_id 列 + 回填（任务 2 消费）
            migrationBuilder.Sql(
                """
                ALTER TABLE pc_activity_category_rules ADD COLUMN IF NOT EXISTS category_id UUID NULL
                  REFERENCES pc_categories(id);
                """);
            migrationBuilder.Sql(
                """
                UPDATE pc_activity_category_rules r
                   SET category_id = c.id
                  FROM pc_categories c
                 WHERE c.name = r.category_name AND r.category_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 仅回滚 schema 变更；数据重映射不可逆（存量已被覆盖），不做反向恢复。
            migrationBuilder.Sql(
                """
                ALTER TABLE pc_activity_category_rules DROP COLUMN IF EXISTS category_id;
                """);
        }
    }
}
