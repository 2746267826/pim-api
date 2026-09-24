using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// REQ-2（工单 WO-FILES-20260923，PR-1）：给「按目录前缀取直属子项」的查询补一条可用的索引。
    ///
    /// 列表查询的判据是 <c>path LIKE 'prefix%'</c>（等价于 <c>string.StartsWith</c>）。在非 C
    /// 排序规则下（本库是 en_US.utf8），PostgreSQL **不能**用普通 btree 索引
    /// <c>(provider_id, path)</c> 来服务 <c>LIKE 'prefix%'</c>，只能整表扫描——扫描量随全树规模
    /// 线性增长，正是 REQ-2/AC-2.3 要消除的退化。加一条 <c>text_pattern_ops</c> 的同列索引后，
    /// 规划器会把它改写成 <c>path ~&gt;=~ prefix AND path ~&lt;~ prefixUpper</c> 走索引扫描。
    ///
    /// 用原生 SQL 是因为 opclass 无法用 EF 的模型 API 表达；该索引因此不在模型中，
    /// 也不会被后续 <c>migrations add</c> 生成的差异化脚本删除。见
    /// <c>FileEntityConfigurations</c> 同名注释。
    /// </summary>
    public partial class AddFileItemPathPatternIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF NOT EXISTS：历史库可能已有同义索引（人工或旧脚本建的），重复执行不得失败。
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS ix_file_items_provider_id_path_pattern
                    ON file_items (provider_id, path text_pattern_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_file_items_provider_id_path_pattern;");
        }
    }
}
