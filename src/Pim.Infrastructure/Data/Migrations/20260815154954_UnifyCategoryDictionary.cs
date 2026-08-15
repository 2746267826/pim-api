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
            // 0) 先 upsert 7 大类（本迁移在应用启动 seed 之前执行，存量库可能还没有这 7 行；
            //    先保证 pc_categories 存在 7 大类，后续回填 category_id 才能命中）。
            //    **按名 upsert**：存量库中同名的旧 builtin 行（如「学习」「视频」「游戏」「文档」
            //    「其他」等）直接 UPDATE 收敛，避免按 id ON CONFLICT 造成同名双行；
            //    名不存在时才按固定 id INSERT（id 与 PcCategoryService.SeedDefaultsAsync 一致，
            //    20000000-...-{index+1:D12}，index 0-6），颜色/图标与 CategoryLegacyMapper
            //    UnifiedColors/UnifiedIcons 一致。
            migrationBuilder.Sql(
                """
                UPDATE pc_categories SET color='#6B5EE4', icon='💻', sort_order=10, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='编程/折腾';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000001', NULL, '编程/折腾', '#6B5EE4', '💻', 'neutral', 10, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='编程/折腾');

                UPDATE pc_categories SET color='#14b8a6', icon='📚', sort_order=20, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='学习';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000002', NULL, '学习', '#14b8a6', '📚', 'neutral', 20, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='学习');

                UPDATE pc_categories SET color='#F97316', icon='📺', sort_order=30, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='视频';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000003', NULL, '视频', '#F97316', '📺', 'neutral', 30, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='视频');

                UPDATE pc_categories SET color='#3B82F6', icon='💬', sort_order=40, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='聊天';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000004', NULL, '聊天', '#3B82F6', '💬', 'neutral', 40, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='聊天');

                UPDATE pc_categories SET color='#F59E0B', icon='📄', sort_order=50, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='文档';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000005', NULL, '文档', '#F59E0B', '📄', 'neutral', 50, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='文档');

                UPDATE pc_categories SET color='#F43F5E', icon='🎮', sort_order=60, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='游戏';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000006', NULL, '游戏', '#F43F5E', '🎮', 'neutral', 60, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='游戏');

                UPDATE pc_categories SET color='#64748b', icon='📋', sort_order=99, is_builtin=true, productivity='neutral', parent_id=NULL, updated_at=now() WHERE name='其他';
                INSERT INTO pc_categories (id, parent_id, name, color, icon, productivity, sort_order, is_builtin, created_at, updated_at)
                SELECT '20000000-0000-0000-0000-000000000007', NULL, '其他', '#64748b', '📋', 'neutral', 99, true, now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM pc_categories WHERE name='其他');
                """);

            // 1) 先断开所有引用待删旧 builtin 细分支的行（包括用户自建 is_builtin=false 子行、
            //    以及 name 属于 7 大类但挂在旧树下的行如旧「视频」挂在「娱乐」下），
            //    外键 RESTRICT 会阻塞删除，先置空 parent_id 再删除旧 builtin 细分支。
            migrationBuilder.Sql(
                """
                UPDATE pc_categories SET parent_id = NULL
                 WHERE parent_id IN (SELECT id FROM pc_categories
                                      WHERE is_builtin AND name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他'));
                """);
            migrationBuilder.Sql(
                """
                DELETE FROM pc_categories
                 WHERE is_builtin AND name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他');
                """);

            // 2) 历史快照重映射（UPDATE + CASE；NULL/未知值归「其他」符合快照语义）
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
                 WHERE (category_name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他')
                        OR category_name IS NULL);
                """);

            // 2b) 手机端存量 life_category 旧分类 → 7 大类（ToolsSystem 相关值保留不动）。
            //     旧值集合核对自 MobileAnalyticsDtos.cs 改动前的 MobileLifeCategories 常量
            //     （社交通讯/短视频娱乐/阅读资讯/工作生产力/音乐音频/浏览器搜索/出行地图/购物外卖/
            //      金融支付/健康运动/相机创作/生活服务/未分类）及历史上曾出现的别名（社交/短视频/视频/
            //      阅读/生产力/办公/文档/购物/金融/娱乐/新闻/工具/系统工具/教育/学习/游戏/其他）。
            migrationBuilder.Sql(
                """
                UPDATE mobile_usage_aggregates SET life_category = CASE life_category
                  WHEN '社交通讯' THEN '聊天' WHEN '社交' THEN '聊天' WHEN '即时通讯' THEN '聊天'
                  WHEN '短视频/娱乐' THEN '视频' WHEN '短视频' THEN '视频' WHEN '短视频娱乐' THEN '视频' WHEN '视频' THEN '视频'
                  WHEN '阅读/资讯' THEN '学习' WHEN '阅读' THEN '学习' WHEN '学习' THEN '学习'
                  WHEN '工作/生产力' THEN '文档' WHEN '生产力' THEN '文档' WHEN '办公' THEN '文档' WHEN '文档' THEN '文档'
                  WHEN '游戏' THEN '游戏'
                  WHEN '系统工具' THEN '系统工具' WHEN '工具/系统' THEN '工具/系统'
                  ELSE '其他' END
                 WHERE life_category NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他','工具/系统');
                """);
            migrationBuilder.Sql(
                """
                UPDATE mobile_app_category_rules SET life_category = CASE life_category
                  WHEN '社交通讯' THEN '聊天' WHEN '社交' THEN '聊天' WHEN '即时通讯' THEN '聊天'
                  WHEN '短视频/娱乐' THEN '视频' WHEN '短视频' THEN '视频' WHEN '短视频娱乐' THEN '视频' WHEN '视频' THEN '视频'
                  WHEN '阅读/资讯' THEN '学习' WHEN '阅读' THEN '学习' WHEN '学习' THEN '学习'
                  WHEN '工作/生产力' THEN '文档' WHEN '生产力' THEN '文档' WHEN '办公' THEN '文档' WHEN '文档' THEN '文档'
                  WHEN '游戏' THEN '游戏'
                  WHEN '系统工具' THEN '系统工具' WHEN '工具/系统' THEN '工具/系统'
                  ELSE '其他' END
                 WHERE life_category NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他','工具/系统');
                """);
            migrationBuilder.Sql(
                """
                UPDATE mobile_app_catalog_overrides SET life_category = CASE life_category
                  WHEN '社交通讯' THEN '聊天' WHEN '社交' THEN '聊天' WHEN '即时通讯' THEN '聊天'
                  WHEN '短视频/娱乐' THEN '视频' WHEN '短视频' THEN '视频' WHEN '短视频娱乐' THEN '视频' WHEN '视频' THEN '视频'
                  WHEN '阅读/资讯' THEN '学习' WHEN '阅读' THEN '学习' WHEN '学习' THEN '学习'
                  WHEN '工作/生产力' THEN '文档' WHEN '生产力' THEN '文档' WHEN '办公' THEN '文档' WHEN '文档' THEN '文档'
                  WHEN '游戏' THEN '游戏'
                  WHEN '系统工具' THEN '系统工具' WHEN '工具/系统' THEN '工具/系统'
                  ELSE '其他' END
                 WHERE life_category NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他','工具/系统');
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

            // 3d) 剩余规则 category_name 重映射。
            //    3b 已删除空分类规则，剩余规则若 category_name 不在旧值集合里即为用户自定义分类名，
            //    不能静默改成「其他」→ ELSE 保留原值（NULL 走 ELSE 同样不变）。
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
                  ELSE category_name END
                 WHERE (category_name NOT IN ('编程/折腾','学习','视频','聊天','文档','游戏','其他')
                        OR category_name IS NULL);
                """);

            // 4) pc_app_signatures.category_path 重映射
            //    注意 category_path 形如 '工作·编程'（点分隔路径）。种子（PcTrackerSchemaInitializer
            //    171 条）distinct 旧路径核对结果（14 个非空值 + 6 行 NULL）：
            //      工作·编程 / 工作·文档 / 工作·设计 / 工作·运维 / 沟通·会议 /
            //      沟通·即时消息 / 沟通·邮件 / 浏览 / 娱乐 / 娱乐·游戏 / 娱乐·游戏·单机 /
            //      娱乐·游戏·网络 / 娱乐·视频 / 娱乐·音乐
            //    计划 CASE 全覆盖上述值（'浏览' 由 '工作·浏览' 行缺漏补齐为本行；
            //    '沟通·会议' 已补入 CASE；NULL 行由 WHERE 条件排除，不映射）。
            //    逐字面量映射（含层级组合，末段按 §0.2 规则映射）：
            migrationBuilder.Sql(
                """
                UPDATE pc_app_signatures SET category_path = CASE category_path
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
                  WHEN '沟通' THEN '聊天' WHEN '沟通·会议' THEN '聊天' WHEN '沟通·即时消息' THEN '聊天' WHEN '沟通·邮件' THEN '聊天'
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
