using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

[Trait("DataSource", "RealDb")]
public sealed class ActivityClassificationSnapshotRealDbTests
{
    [SkippableFact]
    public async Task EnsureClassificationsAsync_ConcurrentMaterializationDoesNotThrow()
    {
        var connectionString = RealDbTestConnection.Require();
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;

        var records = Enumerable.Range(0, 4)
            .Select(index => new PcDetailRecord(
                "window",
                $"2026-09-17T08:{index:00}:00Z",
                $"2026-09-17T08:{index:00}:30Z",
                30,
                $"issue-284-{Guid.NewGuid():N}",
                "Code.exe",
                "Code.exe",
                "其他",
                $"issue-284-{index}.cs",
                null,
                null,
                null,
                null,
                null,
                null))
            .ToList();
        var keys = records.Select(ActivityClassificationRecordKey.FromRecord).ToArray();

        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => EnsureAsync(options, records))
                .ToArray();
            await Task.WhenAll(tasks);

            await using var verify = new PimDbContext(options);
            Assert.Equal(keys.Length, await verify.Set<ActivityClassificationEntity>()
                .CountAsync(snapshot => keys.Contains(snapshot.RecordKey)));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM pc_activity_classifications WHERE record_key = ANY(@keys)", cleanup);
            command.Parameters.AddWithValue("keys", keys);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureAsync(
        DbContextOptions<PimDbContext> options,
        IReadOnlyCollection<PcDetailRecord> records)
    {
        await using var db = new PimDbContext(options);
        var service = new ActivityClassificationSnapshotService(
            db,
            NullLogger<ActivityClassificationSnapshotService>.Instance);
        await service.EnsureClassificationsAsync(records, [], null, CancellationToken.None);
    }
}
