using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// Real PostgreSQL regression for the ASR routing persistence migration. The migration must
/// create all seven tables from an empty database and must also upgrade a database stopped at
/// the last published master migration without touching any published migration.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class NoteAsrRoutingMigrationTests
{
    private const string PublishedHead = "20261027110728_AddAsrPresetsAndAudio";
    private const string ReplacementSuffix = "AddNoteAsrRoutingPersistence";

    private static readonly string[] NoteAsrTables =
    [
        "note_asr_route_configs",
        "note_asr_nodes",
        "note_asr_attempts",
        "note_asr_leases",
        "note_asr_ack_receipts",
        "note_asr_segment_evidence",
        "note_asr_batch_jobs",
    ];

    [SkippableFact]
    public async Task FreshDatabase_MigratesWholeChain_AndReplaysIdempotently()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_note_asr_fresh");

        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());
        Assert.True(await database.HasMigrationAsync(PublishedHead));
        Assert.True(await database.HasMigrationLikeAsync(ReplacementSuffix));

        await AssertNoteAsrSchemaAsync(database);
        Assert.True(await database.TableExistsAsync("users"));

        // The API may be restarted against an already migrated database.
        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());
    }

    [SkippableFact]
    public async Task PublishedMasterDatabase_UpgradesToReplacementMigration()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_note_asr_upgrade");

        await database.MigrateToAsync(PublishedHead);
        Assert.True(await database.HasMigrationAsync(PublishedHead));
        Assert.False(await database.HasMigrationLikeAsync(ReplacementSuffix));
        Assert.Equal(await database.MigrationsOnDiskAsync() - 1, await database.AppliedMigrationCountAsync());

        foreach (var table in NoteAsrTables)
        {
            Assert.False(await database.TableExistsAsync(table), $"{table} must not exist before the replacement migration");
        }

        // Published schema is intact at the upgrade starting point.
        Assert.True(await database.TableExistsAsync("users"));
        Assert.True(await database.ColumnExistsAsync("ai_model_profiles", "asr_preset_id"));

        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());
        Assert.True(await database.HasMigrationLikeAsync(ReplacementSuffix));
        await AssertNoteAsrSchemaAsync(database);

        // Published columns stay present and only the replacement adds its own columns.
        Assert.True(await database.ColumnExistsAsync("ai_model_profiles", "asr_preset_id"));
        Assert.True(await database.TableExistsAsync("users"));

        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());
    }

    private static async Task AssertNoteAsrSchemaAsync(TempMigrationDatabase database)
    {
        foreach (var table in NoteAsrTables)
        {
            Assert.True(await database.TableExistsAsync(table), $"missing table {table}");
        }

        foreach (var column in new[] { "revision", "profile_revision", "credential_secret", "expected_custom_ids_json" })
        {
            Assert.True(
                await database.ColumnExistsAsync("note_asr_batch_jobs", column),
                $"missing note_asr_batch_jobs.{column}");
        }

        foreach (var column in new[] { "runtime_id", "runtime_version", "resource_snapshot_json", "sampled_at" })
        {
            Assert.True(
                await database.ColumnExistsAsync("note_asr_nodes", column),
                $"missing note_asr_nodes.{column}");
        }

        foreach (var column in new[] { "asr_prompt", "asr_batch_base_url" })
        {
            Assert.True(
                await database.ColumnExistsAsync("ai_model_profiles", column),
                $"missing ai_model_profiles.{column}");
        }

        Assert.True(await database.IndexExistsAsync("IX_note_asr_nodes_owner_id_node_id"));
        Assert.True(await database.IsUniqueIndexAsync("IX_note_asr_nodes_owner_id_node_id"));
        Assert.True(await database.IndexExistsAsync("IX_note_asr_attempts_owner_id_task_id"));
        Assert.True(await database.IsUniqueIndexAsync("IX_note_asr_attempts_owner_id_task_id"));
        Assert.True(await database.IndexExistsAsync("IX_note_asr_batch_jobs_batch_id"));
        Assert.True(await database.IsUniqueIndexAsync("IX_note_asr_batch_jobs_batch_id"));
        Assert.True(await database.IndexExistsAsync("IX_note_asr_leases_attempt_id"));
        Assert.True(await database.IsUniqueIndexAsync("IX_note_asr_leases_attempt_id"));

        Assert.Equal(1, await ForeignKeyCountAsync(database, "note_asr_leases"));
        Assert.Equal(1, await ForeignKeyCountAsync(database, "note_asr_segment_evidence"));
    }

    private static async Task<int> ForeignKeyCountAsync(TempMigrationDatabase database, string table)
    {
        var connection = (NpgsqlConnection)database.Db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.table_constraints "
            + "WHERE constraint_type = 'FOREIGN KEY' AND table_schema = 'public' AND table_name = @table",
            connection);
        command.Parameters.AddWithValue("table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
