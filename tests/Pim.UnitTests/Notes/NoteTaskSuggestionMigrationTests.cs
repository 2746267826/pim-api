using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.NoteTaskSuggestions;
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
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestion_extractions_user_id_request_id"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_user_id_extraction_id_ordinal"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_user_id_confirm_request_id"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_note_task_suggestions_task_id"));
    }
}
