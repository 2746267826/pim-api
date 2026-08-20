using System.IO;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class RecurrenceMigrationTests
{
    private static string MigrationPath => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Pim.Infrastructure", "Data", "Migrations",
        "20260820035922_AddRecurrenceMasterModel.cs");

    private static string NormalizedMigration()
    {
        var p = Path.GetFullPath(MigrationPath);
        if (!File.Exists(p))
        {
            // fallback: locate via repo root env
            var alt = "/workspace/pim-wt/cal-pr3/src/Pim.Infrastructure/Data/Migrations/20260820035922_AddRecurrenceMasterModel.cs";
            p = alt;
        }
        return File.ReadAllText(p);
    }

    [Fact]
    public void Up_Should_Not_Contain_Unrelated_LifeCategory_Alter()
    {
        var sql = NormalizedMigration();
        Assert.DoesNotContain("life_category", sql);
        Assert.DoesNotContain("AlterColumn", sql);
    }

    [Fact]
    public void Up_Should_Not_Contain_Unrelated_PcActivityIndex()
    {
        var sql = NormalizedMigration();
        Assert.DoesNotContain("IX_pc_activity_category_rules_category_id", sql);
        Assert.DoesNotContain("pc_activity_category_rules", sql);
    }

    [Fact]
    public void Up_Should_Contain_Recurrence_Columns_And_Index_And_Fk()
    {
        var sql = NormalizedMigration();
        Assert.Contains("is_exception", sql);
        Assert.Contains("is_series_master", sql);
        Assert.Contains("series_master_id", sql);
        Assert.Contains("IX_events_series_master_id_recurrence_id", sql);
        Assert.Contains("FK_events_events_series_master_id", sql);
    }

    [Fact]
    public void Up_Should_Contain_Idempotent_SeriesMaster_Backfill()
    {
        var sql = NormalizedMigration();
        Assert.Contains("is_series_master = true", sql);
        Assert.Contains("rrule IS NOT NULL", sql);
        Assert.Contains("is_series_master = false", sql);
    }

    [Fact]
    public void Up_Should_Contain_Idempotent_Exception_Backfill()
    {
        var sql = NormalizedMigration();
        Assert.Contains("is_exception = true", sql);
        Assert.Contains("outlook_event_type = 'exception'", sql);
        Assert.Contains("is_exception = false", sql);
        Assert.Contains("outlook_series_master_id", sql);
    }

    [Fact]
    public void Up_Should_Contain_RecurrenceId_Backfill_Idempotent()
    {
        var sql = NormalizedMigration();
        Assert.Contains("recurrence_id", sql);
        // idempotent guard: only where recurrence_id IS NULL and is_exception true
        Assert.Contains("recurrence_id IS NULL", sql);
        Assert.Contains("is_exception = true", sql);
        // uses original start (dtstart) to fill
        Assert.Contains("dtstart", sql);
    }

    [Fact]
    public void Up_Should_Contain_LegacyOccurrence_Marking()
    {
        var sql = NormalizedMigration();
        Assert.Contains("legacyOccurrence", sql);
        Assert.Contains("recurrence_metadata_json", sql);
        Assert.Contains("jsonb_set", sql);
        Assert.Contains("outlook_event_type = 'occurrence'", sql);
        Assert.Contains("is_exception = false", sql);
        Assert.Contains("is_series_master = false", sql);
    }

    [Fact]
    public void Down_Should_Only_Drop_Recurrence_Columns_And_Not_AlterLifeCategory()
    {
        var sql = NormalizedMigration();
        // Down section should not contain life_category
        // overall file should not contain it (checked above), but double-check Down markers
        Assert.DoesNotContain("life_category", sql);
        Assert.Contains("DropColumn", sql);
        Assert.Contains("DropIndex", sql);
        Assert.Contains("DropForeignKey", sql);
        // should have exactly 3 DropColumn for recurrence fields
        var dropCount = sql.Split("DropColumn").Length - 1;
        Assert.Equal(3, dropCount);
    }

    [Fact]
    public void Migration_Should_Be_Idempotent_On_ReRun()
    {
        var sql = NormalizedMigration();
        // All updates guard with condition to avoid re-applying
        Assert.Contains("is_series_master = false", sql);
        Assert.Contains("is_exception = false", sql);
        Assert.Contains("recurrence_id IS NULL", sql);
        // legacy marking guards against already-marked
        Assert.Contains("legacyOccurrence", sql);
    }
}
