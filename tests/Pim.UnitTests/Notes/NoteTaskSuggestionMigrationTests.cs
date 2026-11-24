using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.NoteTaskSuggestions;
using Pim.Module.NoteTaskSuggestions.Entities;
using Pim.Module.NoteTaskSuggestions.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteTaskSuggestionMigrationTests
{
    private const string PreviousMigration = "20261031123002_AddNoteAsrRoutingPersistence";

    [SkippableFact]
    public async Task FreshDatabaseRunsCompleteMigrationChainAndCreatesSchema()
    {
        PimDbContext.RegisterModuleAssembly(typeof(NoteTaskSuggestionsModule).Assembly);
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_note_task_suggestion_migration_fresh");

        await database.MigrateAsync();

        await AssertSchemaAsync(database);
        Assert.Equal(await database.MigrationsOnDiskAsync(), await database.AppliedMigrationCountAsync());
    }

    [SkippableFact]
    public async Task ExistingSchemaUpgradesFromPreviousMigration()
    {
        PimDbContext.RegisterModuleAssembly(typeof(NoteTaskSuggestionsModule).Assembly);
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_note_task_suggestion_migration_upgrade");

        await database.MigrateToAsync(PreviousMigration);
        Assert.False(await database.TableExistsAsync("note_task_suggestion_extractions"));
        Assert.False(await database.TableExistsAsync("note_task_suggestions"));

        await database.MigrateAsync();

        await AssertSchemaAsync(database);
        Assert.True(await database.HasMigrationLikeAsync("AddNoteTaskSuggestions"));
    }

    [SkippableFact]
    public async Task UpgradeBackfillsStableFingerprintOnlyForEarliestHistoricalDuplicate()
    {
        PimDbContext.RegisterModuleAssembly(typeof(NoteTaskSuggestionsModule).Assembly);
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_note_task_suggestion_migration_backfill");
        await database.MigrateToAsync("20261101141517_AddNoteTaskSuggestions");

        var owner = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var extractionId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var artifactVersionId = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT INTO note_task_suggestion_extractions
                (id, user_id, request_id, request_fingerprint, note_id, artifact_id,
                 artifact_version_id, source_content_sha256, source_input_sha256,
                 source_manifest_sha256, source_content_markdown, model_profile_id,
                 model_revision, model_endpoint, prompt_version, status, revision,
                 attempt_count, suggestion_count, created_at, updated_at)
            VALUES
                ('{extractionId}', '{owner}', '{Guid.NewGuid()}', '{new string('a', 64)}',
                 '{noteId}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{new string('b', 64)}',
                 '{new string('c', 64)}', '{new string('d', 64)}', 'history',
                 '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'https://history.invalid/v1',
                 'note-task-suggestion-v1', 'Succeeded', '{Guid.NewGuid()}',
                 1, 2, '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z');
            INSERT INTO note_task_suggestions
                (id, user_id, extraction_id, ordinal, note_id, artifact_id, artifact_version_id,
                 source_content_sha256, source_input_sha256, source_manifest_sha256, title,
                 priority, due, status, revision, task_id, created_at, updated_at, confirmed_at)
            VALUES
                ('{firstId}', '{owner}', '{extractionId}', 0, '{noteId}', '{artifactId}',
                 '{artifactVersionId}', '{new string('b', 64)}', '{new string('c', 64)}',
                 '{new string('d', 64)}', '历史复盘', 5, '2026-11-20T09:00:00+08:00',
                 'Confirmed', '{Guid.NewGuid()}', '{Guid.NewGuid()}',
                 '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z', '2026-11-01T00:00:00Z'),
                ('{duplicateId}', '{owner}', '{extractionId}', 1, '{noteId}', '{artifactId}',
                 '{Guid.NewGuid()}', '{new string('e', 64)}', '{new string('f', 64)}',
                 '{new string('1', 64)}', '历史复盘', 5, '2026-11-20T09:00:00+08:00',
                 'Confirmed', '{Guid.NewGuid()}', '{Guid.NewGuid()}',
                 '2026-11-01T00:01:00Z', '2026-11-01T00:01:00Z', '2026-11-01T00:01:00Z');
            """);

        await database.MigrateAsync();

        var rows = await database.Db.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.Id == firstId || x.Id == duplicateId)
            .OrderBy(x => x.Id == firstId ? 0 : 1)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].ItemFingerprint);
        Assert.Null(rows[1].ItemFingerprint);
        Assert.Equal(NoteTaskSuggestionService.ItemFingerprint(rows[0]), rows[0].ItemFingerprint);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteAsync($"""
                INSERT INTO note_task_suggestions
                    (id, user_id, extraction_id, ordinal, note_id, artifact_id,
                     artifact_version_id, source_content_sha256, source_input_sha256,
                     source_manifest_sha256, title, priority, due, status, revision,
                     item_fingerprint, created_at, updated_at, confirmed_at)
                VALUES
                    ('{Guid.NewGuid()}', '{owner}', '{extractionId}', 2, '{noteId}',
                     '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{new string('2', 64)}',
                     '{new string('3', 64)}', '{new string('4', 64)}', '历史复盘', 5,
                     '2026-11-20T09:00:00+08:00', 'Confirmed', '{Guid.NewGuid()}',
                     '{rows[0].ItemFingerprint}', '2026-11-01T00:02:00Z',
                     '2026-11-01T00:02:00Z', '2026-11-01T00:02:00Z');
                """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    private static async Task AssertSchemaAsync(TempMigrationDatabase database)
    {
        Assert.True(await database.TableExistsAsync("note_task_suggestion_extractions"));
        Assert.True(await database.TableExistsAsync("note_task_suggestions"));
        Assert.True(await database.ColumnExistsAsync(
            "note_task_suggestion_extractions", "source_content_markdown"));
        Assert.True(await database.ColumnExistsAsync(
            "note_task_suggestion_extractions", "request_fingerprint"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "confirm_request_id"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "task_id"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "kind"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "start_at"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "end_at"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "time_zone_id"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "event_id"));
        Assert.True(await database.ColumnExistsAsync("note_task_suggestions", "item_fingerprint"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestion_extractions_user_id_request_id"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_user_id_extraction_id_ordinal"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_user_id_confirm_request_id"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_task_id"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_event_id"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_note_fingerprint_confirmed"));
    }
}
