using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// 工单 05 / F2 + F3：
    /// <list type="bullet">
    /// <item><b>F3</b>：删掉 Nextcloud 时代的四个死字段（<c>base_url</c> / <c>internal_base_url</c>
    /// / <c>username</c> / <c>app_password_secret</c>），去掉 <c>provider</c> 列的
    /// <c>nextcloud</c> 默认值（列本身保留：它承载 onedrive / google 的区分）。</item>
    /// <item><b>F2</b>：唯一键从 <c>(user_id, provider, base_url, username)</c> 换成
    /// <c>(user_id, provider, account_id) WHERE account_id IS NOT NULL</c>；
    /// 新增 <c>is_default</c>（附件/闪念的落点账号）与 <c>deferred_block_count</c>
    /// （同步「解析不出父路径」的连续阻塞计数，用于有界重试）。</item>
    /// <item>闪念附件新增 <c>storage_account_id</c>（多账号后必须能定位附件在哪个账号）。</item>
    /// </list>
    ///
    /// <para><b>可逆性（如实说明）</b>：结构可逆、**数据不可逆**。Down 会把四个列以空串重建，
    /// 但被删掉的原值无法还原；更关键的是**降级回不去多账号** —— 旧唯一键
    /// <c>(user_id, provider, base_url, username)</c> 在 base_url/username 都是空串时，
    /// 同一 (user_id, provider) 下**只允许一行**（包括未完成的待绑定行）。
    /// Down 里因此加了一道显式守卫：存在同 user+provider 的多行时明确报错，
    /// 而不是抛一个看不懂的唯一约束冲突、把库停在半途。</para>
    ///
    /// <para><b>对现有生产数据的影响</b>：迁移前 <c>file_providers</c> 只有 1 行
    /// （<c>provider='onedrive'</c>），而 OneDrive 绑定路径从不写这四个字段
    /// （<c>FileProviderAuthorizationService.StartAsync</c> 只设 UserId/Provider/ClientId），
    /// 因此它们的值就是实体默认的空串 —— 删除它们是纯粹的 schema 收敛。
    /// <b>新唯一索引的创建不做去重兜底</b>：若迁移前真的存在同 user+provider+account 的重复行，
    /// 建索引会**当场失败并停下**，由人工判断如何合并 —— 静默删行比迁移失败更危险。</para>
    /// </summary>
    public partial class MultiProviderAccountsAndDropNextcloudFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_providers_user_id_provider_base_url_username",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "app_password_secret",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "base_url",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "internal_base_url",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "username",
                table: "file_providers");

            migrationBuilder.AlterColumn<string>(
                name: "storage_provider",
                table: "quick_note_attachments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "minio");

            migrationBuilder.AddColumn<Guid>(
                name: "storage_account_id",
                table: "quick_note_attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "provider",
                table: "file_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "nextcloud");

            migrationBuilder.AddColumn<int>(
                name: "deferred_block_count",
                table: "file_providers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "file_providers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_file_providers_user_id_is_default",
                table: "file_providers",
                columns: new[] { "user_id", "is_default" });

            migrationBuilder.CreateIndex(
                name: "IX_file_providers_user_id_provider_account_id",
                table: "file_providers",
                columns: new[] { "user_id", "provider", "account_id" },
                unique: true,
                filter: "account_id IS NOT NULL");

            // 回填默认账号：迁移前每个用户至多一个已连接来源，因此这件事没有歧义。
            // 不回填的话 is_default 全为 false，附件落点会退化成「按创建时间最早的那个」——
            // 对单账号用户结果一样，但语义上应当有且只有一个默认账号。
            migrationBuilder.Sql("""
                UPDATE file_providers p
                SET is_default = true
                WHERE p.status = 'connected'
                  AND NOT EXISTS (
                      SELECT 1 FROM file_providers d
                      WHERE d.user_id = p.user_id AND d.is_default
                  )
                  AND p.id = (
                      SELECT p2.id FROM file_providers p2
                      WHERE p2.user_id = p.user_id AND p2.status = 'connected'
                      ORDER BY p2.created_at, p2.id
                      LIMIT 1
                  );
                """);

            // 回填附件的落点账号：仅在该用户**只有一个**已连接账号时做。
            // 迁移前的附件只可能落在那个账号上，因此这个回填是确定的；
            // 多账号的用户不做猜测（留 NULL，由 FileAttachmentStore 的回退查找处理）。
            migrationBuilder.Sql("""
                UPDATE quick_note_attachments a
                SET storage_account_id = p.id,
                    storage_provider = p.provider
                FROM file_providers p
                WHERE a.storage_account_id IS NULL
                  AND p.user_id = a.user_id
                  AND p.status = 'connected'
                  AND (SELECT count(*) FROM file_providers p2
                       WHERE p2.user_id = a.user_id AND p2.status = 'connected') = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 守卫：重建后的旧唯一键 (user_id, provider, base_url, username) 在
            // base_url / username 都恢复成空串时，**同一 (user_id, provider) 只允许一行** ——
            // 既包括两个正式账号，也包括任何未完成的待绑定行。
            // 所以这里按 (user_id, provider) 的**总行数**判断，不能只数 account_id 非空的行：
            // 否则「1 个正式账号 + 1 个 pending 绑定尝试」会通过守卫、
            // 然后在 CREATE UNIQUE INDEX 处硬失败，把库停在半途（复审 Important）。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM file_providers
                        GROUP BY user_id, provider
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION
                            '无法降级：同一用户在同一 provider 下存在多行（多个账号或未完成的绑定），旧唯一键 (user_id, provider, base_url, username) 无法承载。请先删除多余账号/待绑定记录后再降级。';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_file_providers_user_id_is_default",
                table: "file_providers");

            migrationBuilder.DropIndex(
                name: "IX_file_providers_user_id_provider_account_id",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "storage_account_id",
                table: "quick_note_attachments");

            migrationBuilder.DropColumn(
                name: "deferred_block_count",
                table: "file_providers");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "file_providers");

            migrationBuilder.AlterColumn<string>(
                name: "storage_provider",
                table: "quick_note_attachments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "minio",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "provider",
                table: "file_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "nextcloud",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<string>(
                name: "app_password_secret",
                table: "file_providers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "base_url",
                table: "file_providers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "internal_base_url",
                table: "file_providers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "file_providers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_file_providers_user_id_provider_base_url_username",
                table: "file_providers",
                columns: new[] { "user_id", "provider", "base_url", "username" },
                unique: true);
        }
    }
}
