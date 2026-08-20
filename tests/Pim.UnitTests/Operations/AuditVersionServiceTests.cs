using Microsoft.EntityFrameworkCore;
using Pim.Core.Audit;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Data;
using System.Text.Json;
using Xunit;

namespace Pim.UnitTests.Operations;

public class AuditVersionServiceTests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task RecordAsyncWritesBeforeAfterAuditVersion()
    {
        await using var db = CreateDb();
        var service = new AuditVersionService(db);
        var objectId = Guid.NewGuid();
        var confirmationId = Guid.NewGuid();

        var recorded = await service.RecordAsync(
            "event",
            objectId,
            new { title = "Before" },
            new { title = "After" },
            ["title"],
            confirmationId,
            "pim",
            UserA,
            CancellationToken.None);

        var timeline = await service.GetTimelineAsync("event", objectId, UserA, CancellationToken.None);
        var item = Assert.Single(timeline.Items);
        Assert.Equal(recorded.Id, item.Id);
        Assert.Equal(confirmationId, item.ConfirmationId);
        Assert.Contains("\"title\":\"Before\"", item.BeforeJson);
        Assert.Contains("\"title\":\"After\"", item.AfterJson);
        Assert.Contains("title", item.ChangedFieldsJson);
    }

    [Fact]
    public async Task PreviewRestoreAsyncCarriesStoredSnapshots()
    {
        await using var db = CreateDb();
        var service = new AuditVersionService(db);
        var objectId = Guid.NewGuid();

        var recorded = await service.RecordAsync(
            "event",
            objectId,
            new { title = "Before", start = "2026-08-01T09:00:00Z" },
            new { title = "After", start = "2026-08-02T09:00:00Z" },
            ["title", "start"],
            null,
            "pim",
            UserA,
            CancellationToken.None);

        var preview = await service.PreviewRestoreAsync(recorded.Id, UserA, CancellationToken.None);

        Assert.Equal(recorded.BeforeJson, preview.BeforeJson);
        Assert.Equal(recorded.AfterJson, preview.AfterJson);
        Assert.Equal(["title", "start"], preview.ChangedFields);
    }

    [Fact]
    public async Task GetTimelineAsync_FiltersByOwnerUser()
    {
        var objectId = Guid.NewGuid();
        await using var db = CreateDb();
        var service = new AuditVersionService(db);

        await service.RecordAsync(
            "event", objectId, new { title = "A" }, new { title = "A2" }, ["title"], null, "pim", UserA, CancellationToken.None);
        await service.RecordAsync(
            "event", objectId, new { title = "B" }, new { title = "B2" }, ["title"], null, "pim", UserB, CancellationToken.None);

        var timelineA = await service.GetTimelineAsync("event", objectId, UserA, CancellationToken.None);

        var item = Assert.Single(timelineA.Items);
        Assert.Contains("\"title\":\"A2\"", item.AfterJson);
        Assert.DoesNotContain("\"title\":\"B2\"", item.AfterJson);
    }

    [Fact]
    public async Task PreviewRestoreAsync_RejectsAuditVersionOfAnotherUser()
    {
        var objectId = Guid.NewGuid();
        await using var db = CreateDb();
        var service = new AuditVersionService(db);

        var recorded = await service.RecordAsync(
            "event", objectId, new { title = "A" }, new { title = "A2" }, ["title"], null, "pim", UserA, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.PreviewRestoreAsync(recorded.Id, UserB, CancellationToken.None));

        Assert.Equal(02056, ex.ErrorCode);
    }

    [Fact]
    public async Task ExportAsync_ExcludesAuditVersionsOfOtherUsers()
    {
        var objectId = Guid.NewGuid();
        await using var db = CreateDb();
        var service = new AuditVersionService(db);

        await service.RecordAsync(
            "event", objectId, new { title = "A" }, new { title = "A2" }, ["title"], null, "pim", UserA, CancellationToken.None);
        await service.RecordAsync(
            "event", objectId, new { title = "B" }, new { title = "B2" }, ["title"], null, "pim", UserB, CancellationToken.None);

        var export = await service.ExportAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, UserA, CancellationToken.None);

        var items = JsonSerializer.Deserialize<IReadOnlyList<AuditVersionDto>>(export.Content);
        var item = Assert.Single(items);
        Assert.Contains("\"title\":\"A2\"", item.AfterJson);
        Assert.DoesNotContain("B2", item.AfterJson);
    }

    [Fact]
    public async Task RecordAsync_StripsProviderTokensFromDtoSnapshots()
    {
        var objectId = Guid.NewGuid();
        await using var db = CreateDb();
        var service = new AuditVersionService(db);

        var before = new
        {
            title = "Before",
            OutlookChangeKey = "ck-aaaa",
            OutlookEtag = "etag-1",
            GraphEventId = "AAMk-bbbb"
        };
        var after = new { title = "After", OutlookChangeKey = "ck-cccc" };

        var recorded = await service.RecordAsync(
            "event", objectId, before, after, ["title", "outlookChangeKey"], null, "pim", UserA, CancellationToken.None);

        Assert.Contains("\"title\":\"Before\"", recorded.BeforeJson);
        Assert.Contains("\"title\":\"After\"", recorded.AfterJson);
        Assert.DoesNotContain("ck-aaaa", recorded.BeforeJson);
        Assert.DoesNotContain("ck-cccc", recorded.AfterJson);
        Assert.DoesNotContain("etag-1", recorded.BeforeJson);
        Assert.DoesNotContain("AAMk-bbbb", recorded.BeforeJson);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new PimDbContext(options);
    }
}
