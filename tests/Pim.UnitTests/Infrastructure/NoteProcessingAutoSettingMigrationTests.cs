using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>Real PostgreSQL upgrade regression for the persisted auto-note-processing switch.</summary>
[Trait("DataSource", "RealDb")]
public sealed class NoteProcessingAutoSettingMigrationTests
{
    private const string PreviousPublishedHead = "20261117165057_AddEndpointLastWakeBlockReason";

    [SkippableFact]
    public async Task PublishedDatabase_UpgradesWithDefaultOff_AndPreservesExistingUsers()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_note_processing_auto_upgrade");

        await database.MigrateToAsync(PreviousPublishedHead);
        Assert.False(await database.ColumnExistsAsync("note_processing_preferences",
            "auto_note_processing_enabled"));
        var owner = Guid.NewGuid();
        await database.ExecuteAsync($"""
INSERT INTO users (id, username, email, password_hash, role, is_active, created_at, updated_at)
VALUES ('{owner:D}', 'note-processing-owner', 'note-processing-owner@example.invalid', 'test', 'user', true, now(), now());
""");

        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        Assert.True(await database.HasMigrationLikeAsync("AddNoteProcessingAutoSetting"));
        Assert.True(await database.ColumnExistsAsync("note_processing_preferences",
            "auto_note_processing_enabled"));
        Assert.Equal("1", (await ScalarAsync(database,
            "SELECT count(*) FROM users WHERE id = @owner", ("owner", owner)))?.ToString());

        Assert.Null(await Record.ExceptionAsync(database.MigrateAsync));
        Assert.Equal(await database.MigrationsOnDiskAsync(),
            await database.AppliedMigrationCountAsync());
    }

    [SkippableFact]
    public async Task FreshDatabase_AppliesWholeChainAndColumnIsNotNullWithFalseDefault()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_note_processing_auto_fresh");

        var error = await Record.ExceptionAsync(database.MigrateAsync);
        Assert.Null(error);

        Assert.True(await database.HasMigrationLikeAsync("AddNoteProcessingAutoSetting"));
        Assert.True(await database.ColumnExistsAsync("note_processing_preferences",
            "auto_note_processing_enabled"));
        Assert.Equal("NO", await ColumnNullableAsync(database,
            "note_processing_preferences", "auto_note_processing_enabled"));
        Assert.Equal("false", await ColumnDefaultAsync(database,
            "note_processing_preferences", "auto_note_processing_enabled"));
        Assert.Equal(await database.MigrationsOnDiskAsync(),
            await database.AppliedMigrationCountAsync());
    }

    private static async Task<string?> ColumnNullableAsync(TempMigrationDatabase database,
        string table, string column) =>
        (await ScalarAsync(database, """
SELECT is_nullable FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
""", ("table", table), ("column", column)))?.ToString();

    private static async Task<string?> ColumnDefaultAsync(TempMigrationDatabase database,
        string table, string column) =>
        (await ScalarAsync(database, """
SELECT column_default FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
""", ("table", table), ("column", column)))?.ToString();

    private static async Task<object?> ScalarAsync(TempMigrationDatabase database,
        string sql, params (string Name, object Value)[] parameters)
    {
        var connection = (NpgsqlConnection)database.Db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
}
