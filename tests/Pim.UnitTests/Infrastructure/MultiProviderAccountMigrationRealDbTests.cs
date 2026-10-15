using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 工单 05 / F2 + F3 的**真库**迁移验收（AC-3）。
///
/// 用真 PostgreSQL 跑完整迁移链：先迁到上一条迁移、灌入迁移前的真实行形状，
/// 再迁到本迁移，然后逐条核对「死列没了 / 新唯一键是部分唯一索引且真的生效 /
/// 多账号能落两行 / 同账号第二行被拒 / 回填正确」。
/// 这些结论 EF InMemory 一条都证明不了（它没有唯一索引、没有部分索引、没有 SQL 回填）。
/// </summary>
public class MultiProviderAccountMigrationRealDbTests
{
    /// <summary>本迁移之前的最后一条迁移（前缀是生成时间戳，后缀才是名字）。</summary>
    private const string PreviousMigration = "20260930111749_AddSuggestionGeneratedForDate";

    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string AttachmentId = "22222222-2222-2222-2222-222222222222";
    private const string ProviderRowId = "33333333-3333-3333-3333-333333333333";

    private static async Task<TempMigrationDatabase> CreateSeededAtPreviousMigrationAsync()
    {
        var db = await TempMigrationDatabase.CreateAsync("pim_files_migration");
        await db.MigrateToAsync(PreviousMigration);

        // 迁移前的真实行形状：OneDrive 绑定只写 user_id/provider/client_id...
        // 四个 Nextcloud 死字段保持实体默认的空串（绑定路径从不写它们）。
        await db.ExecuteAsync($"""
            INSERT INTO file_providers
                (id, user_id, provider, base_url, internal_base_url, username, app_password_secret,
                 status, client_id, drive_id, account_id, account_name, created_at, updated_at)
            VALUES
                ('{ProviderRowId}', '{UserId}', 'onedrive', '', NULL, '', '', 'connected',
                 'cid-1', 'drive-1', 'acc-1', 'Test User', now(), now());
            """);

        await db.ExecuteAsync($"""
            INSERT INTO quick_note_attachments
                (id, user_id, storage_provider, object_key, file_name, content_type,
                 size_bytes, created_at)
            VALUES
                ('{AttachmentId}', '{UserId}', 'minio', 'drive-item-1', 'a.png', 'image/png',
                 3, now());
            """);

        return db;
    }

    [Fact]
    public async Task Migration_DropsNextcloudColumns_AndSwapsUniqueKeyToAccountScope()
    {
        await using var db = await CreateSeededAtPreviousMigrationAsync();

        // 迁移前：四个死列与旧唯一索引确实在
        Assert.True(await db.ColumnExistsAsync("file_providers", "base_url"));
        Assert.True(await db.IndexExistsAsync("IX_file_providers_user_id_provider_base_url_username"));

        await db.MigrateAsync();

        // F3：四个列必须真的消失（这是「零残留」的数据库侧证据）
        Assert.False(await db.ColumnExistsAsync("file_providers", "base_url"));
        Assert.False(await db.ColumnExistsAsync("file_providers", "internal_base_url"));
        Assert.False(await db.ColumnExistsAsync("file_providers", "username"));
        Assert.False(await db.ColumnExistsAsync("file_providers", "app_password_secret"));

        // 旧唯一索引消失，新的账号维度索引是**部分**唯一索引
        Assert.False(await db.IndexExistsAsync("IX_file_providers_user_id_provider_base_url_username"));
        Assert.True(await db.IndexExistsAsync("IX_file_providers_user_id_provider_account_id"));
        Assert.True(await db.IsUniqueIndexAsync("IX_file_providers_user_id_provider_account_id"));
        Assert.Contains(
            "account_id IS NOT NULL",
            await FilterOfIndexAsync(db, "IX_file_providers_user_id_provider_account_id"));

        // provider 列保留（它承载 onedrive/google 的区分），只是不再有 nextcloud 默认值
        Assert.True(await db.ColumnExistsAsync("file_providers", "provider"));
        Assert.False(await HasColumnDefaultAsync(db, "file_providers", "provider"));
        Assert.True(await db.ColumnExistsAsync("file_providers", "is_default"));
    }

    [Fact]
    public async Task Migration_AllowsSecondAccountForSameProvider_ButNotDuplicateAccount()
    {
        await using var db = await CreateSeededAtPreviousMigrationAsync();
        await db.MigrateAsync();

        // F2：同一用户 + 同一 provider 的**第二个账号**必须能落库
        await db.ExecuteAsync($"""
            INSERT INTO file_providers
                (id, user_id, provider, status, client_id, account_id, account_name, is_default, created_at, updated_at)
            VALUES
                (gen_random_uuid(), '{UserId}', 'onedrive', 'connected', 'cid-1', 'acc-2', 'Second', false, now(), now());
            """);

        // 注意：SqlQuery 的插值会被参数化，写成 '{expr}' 会变成字面量 '@p0'（踩过一次）。
        var count = await db.Db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM file_providers WHERE user_id = {Guid.Parse(UserId)}")
            .SingleAsync();
        Assert.Equal(2, count);

        // 同一个账号再来一行必须被唯一键拒绝（不是靠代码约定，是数据库约束）
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
            INSERT INTO file_providers
                (id, user_id, provider, status, account_id, created_at, updated_at)
            VALUES
                (gen_random_uuid(), '{UserId}', 'onedrive', 'connected', 'acc-2', now(), now());
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // 待绑定行（account_id IS NULL）允许多条并存：并发/重试的绑定尝试不能被唯一键挡死
        await db.ExecuteAsync($"""
            INSERT INTO file_providers (id, user_id, provider, status, created_at, updated_at)
            VALUES (gen_random_uuid(), '{UserId}', 'onedrive', 'pending', now(), now()),
                   (gen_random_uuid(), '{UserId}', 'onedrive', 'pending', now(), now());
            """);
    }

    [Fact]
    public async Task Migration_BackfillsDefaultAccountAndAttachmentAccount()
    {
        await using var db = await CreateSeededAtPreviousMigrationAsync();
        await db.MigrateAsync();

        var isDefault = await db.Db.Database
            .SqlQuery<bool>($"SELECT is_default AS \"Value\" FROM file_providers WHERE id = {Guid.Parse(ProviderRowId)}")
            .SingleAsync();
        Assert.True(isDefault);

        // 单账号用户的历史附件：落点账号是确定的，必须回填（多账号用户留 NULL 由回退查找处理）
        var storageAccount = await db.Db.Database
            .SqlQuery<Guid?>(
                $"SELECT storage_account_id AS \"Value\" FROM quick_note_attachments WHERE id = {Guid.Parse(AttachmentId)}")
            .SingleAsync();
        Assert.Equal(Guid.Parse(ProviderRowId), storageAccount);

        var provider = await db.Db.Database
            .SqlQuery<string>($"SELECT storage_provider AS \"Value\" FROM quick_note_attachments WHERE id = {Guid.Parse(AttachmentId)}")
            .SingleAsync();
        Assert.Equal("onedrive", provider);

        // minio 默认值随 F3 一并去掉
        Assert.False(await HasColumnDefaultAsync(db, "quick_note_attachments", "storage_provider"));
    }

    /// <summary>
    /// 降级：结构可逆（四个列能重建），但**多账号时会明确拒绝降级** ——
    /// 旧唯一键承载不了多账号，与其抛一个看不懂的唯一约束冲突，不如说清原因。
    /// </summary>
    [Fact]
    public async Task Down_RestoresColumnsOnSingleAccount_ButRefusesWhenMultipleAccountsExist()
    {
        await using var db = await CreateSeededAtPreviousMigrationAsync();
        await db.MigrateAsync();

        // 单账号：可以降级，四个列重建为 NOT NULL（默认空串，非空表也能加列）
        await db.MigrateToAsync(PreviousMigration);
        Assert.True(await db.ColumnExistsAsync("file_providers", "base_url"));
        Assert.True(await db.ColumnExistsAsync("file_providers", "username"));
        Assert.True(await db.ColumnExistsAsync("file_providers", "app_password_secret"));
        Assert.True(await db.IndexExistsAsync("IX_file_providers_user_id_provider_base_url_username"));

        // 回到最新，再制造第二账号
        await db.MigrateAsync();
        await db.ExecuteAsync($"""
            INSERT INTO file_providers
                (id, user_id, provider, status, account_id, created_at, updated_at)
            VALUES (gen_random_uuid(), '{UserId}', 'onedrive', 'connected', 'acc-2', now(), now());
            """);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.MigrateToAsync(PreviousMigration));
        Assert.Contains("无法降级", failure.Message);

        // 失败是「拒绝执行」而不是「半途改坏」：新结构仍在
        Assert.True(await db.ColumnExistsAsync("file_providers", "is_default"));
        Assert.False(await db.ColumnExistsAsync("file_providers", "base_url"));
    }

    /// <summary>
    /// 取索引定义。走 <c>pg_indexes.indexdef</c> 而不是 <c>::regclass</c>：
    /// 迁移建的索引名是大小写混排（EF 带引号建），<c>::regclass</c> 会被 Postgres
    /// 折成小写而找不到；<c>pg_indexes</c> 直接按名字查，且定义里能看到 WHERE 子句。
    /// </summary>
    /// <summary>
    /// 降级守卫必须按 (user_id, provider) 的**总行数**判断，不能只看 account_id 非空的行：
    /// 「1 个正式账号 + 1 个未完成的待绑定行」在重建后的旧唯一键下同样会冲突
    /// （base_url / username 都恢复成空串），守卫漏掉它就会在 CREATE UNIQUE INDEX 处硬失败、
    /// 把库停在半途（复审 Important）。
    /// </summary>
    [Fact]
    public async Task Down_RefusesWhenAPendingRowCoexistsWithTheConnectedAccount()
    {
        await using var db = await CreateSeededAtPreviousMigrationAsync();
        await db.MigrateAsync();

        // 只有一个正式账号 + 一个 pending 待绑定行（account_id 为 NULL）
        await db.ExecuteAsync($"""
            INSERT INTO file_providers (id, user_id, provider, status, created_at, updated_at)
            VALUES (gen_random_uuid(), '{UserId}', 'onedrive', 'pending', now(), now());
            """);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.MigrateToAsync(PreviousMigration));
        Assert.Contains("无法降级", failure.Message);

        // 拒绝执行而不是半途改坏
        Assert.True(await db.ColumnExistsAsync("file_providers", "is_default"));
        Assert.False(await db.ColumnExistsAsync("file_providers", "base_url"));
    }

    private static async Task<string> FilterOfIndexAsync(TempMigrationDatabase db, string indexName)
        => await db.Db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = {indexName}")
            .SingleAsync();

    private static async Task<bool> HasColumnDefaultAsync(TempMigrationDatabase db, string table, string column)
    {
        var count = await db.Db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = {table} AND column_name = {column}
                  AND column_default IS NOT NULL
                """)
            .SingleAsync();
        return count > 0;
    }
}
