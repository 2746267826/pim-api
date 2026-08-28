using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Today;
using Pim.Core.Operations;
using Pim.Core.Today;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.QuickNotes.DTOs;
using Pim.Module.QuickNotes.Entities;
using Pim.Module.QuickNotes.Services;
using Pim.Module.Stats.DTOs;
using Pim.Module.Stats.Entities;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class StatsTodayQuickNotesTests
{
    // ---------- StatsService ----------

    [Fact]
    public async Task Stats_IngestBatchAsync_ShouldPersistEntries()
    {
        await using var db = ServiceTestBase.CreateDb();
        var service = ServiceTestBase.CreateStatsService(db);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("device-1", new List<AppUsageEntry>
        {
            new("com.example.app1", nowMs - 60000, nowMs, 60000, nowMs),
            new("com.example.app2", nowMs - 120000, nowMs - 60000, 60000, nowMs - 60000),
        });

        var count = await service.IngestBatchAsync(batch, CancellationToken.None);

        Assert.Equal(2, count);
        var stored = await db.Set<AppUsageEntity>().ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, e => e.PackageName == "com.example.app1" && e.DeviceId == "device-1");
        Assert.Contains(stored, e => e.PackageName == "com.example.app2");
    }

    [Fact]
    public async Task Stats_IngestBatchAsync_EmptyBatch_ShouldReturnZero()
    {
        await using var db = ServiceTestBase.CreateDb();
        var service = ServiceTestBase.CreateStatsService(db);
        var batch = new UploadBatch("device-empty", new List<AppUsageEntry>());

        var count = await service.IngestBatchAsync(batch, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(await db.Set<AppUsageEntity>().ToListAsync());
    }

    [Fact]
    public async Task Stats_IngestBatchAsync_MultipleBatches_ShouldAccumulate()
    {
        await using var db = ServiceTestBase.CreateDb();
        var service = ServiceTestBase.CreateStatsService(db);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch1 = new UploadBatch("device-x", new List<AppUsageEntry> { new("com.a", nowMs - 1000, nowMs, 1000, nowMs) });
        var batch2 = new UploadBatch("device-x", new List<AppUsageEntry> { new("com.b", nowMs - 2000, nowMs - 1000, 1000, nowMs) });

        await service.IngestBatchAsync(batch1, CancellationToken.None);
        await service.IngestBatchAsync(batch2, CancellationToken.None);

        var stored = await db.Set<AppUsageEntity>().ToListAsync();
        Assert.Equal(2, stored.Count);
    }

    // ---------- TodaySectionService ----------

    [Fact]
    public async Task Today_GetRegistryAsync_ShouldReturnDedupedSections()
    {
        var providerA = new FakeProvider("calendar.schedule", "calendar.schedule");
        var providerB = new FakeProvider("calendar.tasks", "calendar.tasks");
        // duplicate id should be deduped by Service ctor
        var dup = new FakeProvider("calendar.schedule", "calendar.schedule");
        var service = ServiceTestBase.CreateTodaySectionService(providerA, providerB, dup);

        var registry = await service.GetRegistryAsync("2026-07-06", CancellationToken.None);

        Assert.Equal("2026-07-06", registry.Date);
        // deduped: only 2 unique ids
        Assert.Equal(2, registry.Sections.Count);
        Assert.Contains(registry.Sections, s => s.Id == "calendar.schedule");
        Assert.Contains(registry.Sections, s => s.Id == "calendar.tasks");
        // sections sorted by SectionId ordinal
        Assert.Equal("calendar.schedule", registry.Sections[0].Id);
    }

    [Fact]
    public async Task Today_GetSectionAsync_ShouldReturnAvailableSection()
    {
        var provider = new FakeProvider("pc.activity", "pc.activity", status: TodaySectionStatuses.Normal);
        var service = ServiceTestBase.CreateTodaySectionService(provider);

        var section = await service.GetSectionAsync("pc.activity", "2026-07-06", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Equal("pc.activity", section!.Id);
        Assert.Equal(TodaySectionStatuses.Normal, section.Status);
        Assert.Null(section.Error);
    }

    [Fact]
    public async Task Today_GetSectionAsync_UnknownId_ShouldReturnNull()
    {
        var provider = new FakeProvider("pc.activity", "pc.activity");
        var service = ServiceTestBase.CreateTodaySectionService(provider);

        var section = await service.GetSectionAsync("not.exist", "2026-07-06", CancellationToken.None);

        Assert.Null(section);
    }

    [Fact]
    public async Task Today_GetSectionAsync_ProviderThrows_ShouldReturnUnavailable()
    {
        var throwing = new ThrowingProvider("sync.outlook", "sync.outlook");
        var service = ServiceTestBase.CreateTodaySectionService(throwing);

        var section = await service.GetSectionAsync("sync.outlook", "2026-07-06", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Equal(TodaySectionStatuses.Unavailable, section!.Status);
        Assert.NotNull(section.Error);
        Assert.Equal("section_unavailable", section.Error!.Code);
    }

    // ---------- QuickNoteService ----------

    [Fact]
    public async Task QuickNotes_CreateAndGetAsync_ShouldRoundTrip()
    {
        await using var db = CreateQuickNoteDb();
        var service = CreateQuickNoteService(db, ServiceTestBase.DefaultUserId);

        var created = await service.CreateAsync(new CreateQuickNoteRequest("hello **world**", "web-page", null));
        var fetched = await service.GetAsync(created.Id);

        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("hello **world**", fetched.ContentMarkdown);
        Assert.Equal(QuickNoteStatuses.Inbox, fetched.Status);
    }

    [Fact]
    public async Task QuickNotes_ListAndGet_ShouldFilterAndPaginate()
    {
        await using var db = CreateQuickNoteDb();
        var service = CreateQuickNoteService(db, ServiceTestBase.DefaultUserId);

        var a = await service.CreateAsync(new CreateQuickNoteRequest("alpha note", "web-page", null));
        var b = await service.CreateAsync(new CreateQuickNoteRequest("beta note archived", "web-page", null));
        await service.ArchiveAsync(b.Id);

        var inbox = await service.ListAsync(new QuickNoteListQuery(QuickNoteStatuses.Inbox, null, 1, 20));
        Assert.Single(inbox.Items);
        Assert.Equal(a.Id, inbox.Items[0].Id);

        var search = await service.ListAsync(new QuickNoteListQuery(null, "beta", 1, 20));
        Assert.Single(search.Items);
        Assert.Equal(b.Id, search.Items[0].Id);
    }

    [Fact]
    public async Task QuickNotes_UpdateProcessArchiveRestoreDelete_ShouldTransition()
    {
        await using var db = CreateQuickNoteDb();
        var service = CreateQuickNoteService(db, ServiceTestBase.DefaultUserId);

        var note = await service.CreateAsync(new CreateQuickNoteRequest("lifecycle", "web-page", null));

        var updated = await service.UpdateAsync(note.Id, new UpdateQuickNoteRequest("updated content", null, null));
        Assert.Equal("updated content", updated.ContentMarkdown);

        var processed = await service.ProcessAsync(note.Id);
        Assert.Equal(QuickNoteStatuses.Processed, processed.Status);

        var archived = await service.ArchiveAsync(note.Id);
        Assert.Equal(QuickNoteStatuses.Archived, archived.Status);
        Assert.NotNull(archived.ArchivedAt);

        var restored = await service.RestoreAsync(note.Id, new RestoreQuickNoteRequest(QuickNoteStatuses.Inbox));
        Assert.Equal(QuickNoteStatuses.Inbox, restored.Status);
        Assert.Null(restored.ArchivedAt);

        await service.DeleteAsync(note.Id);
        var all = await db.Set<QuickNoteEntity>().IgnoreQueryFilters().ToListAsync();
        Assert.Single(all);
        Assert.NotNull(all[0].DeletedAt);
        // filtered view should be empty
        Assert.Empty(await db.Set<QuickNoteEntity>().ToListAsync());
    }

    // ---------- Helpers ----------

    private static PimDbContext CreateQuickNoteDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"qn-test-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static QuickNoteService CreateQuickNoteService(PimDbContext db, Guid userId)
    {
        var user = new StubUser(userId);
        var attachments = new QuickNoteAttachmentService(db, user, new FakeStorage());
        return new QuickNoteService(db, user, new AuditLogService(db), attachments);
    }

    private sealed class StubUser(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeStorage : IQuickNoteObjectStorage
    {
        public Task<string> StoreAsync(string objectKey, Stream content, string contentType, long sizeBytes, CancellationToken ct = default) => Task.FromResult(objectKey);
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default) => Task.FromResult<Stream>(new System.IO.MemoryStream());
        public Task DeleteAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProvider(string id, string kind, string status = TodaySectionStatuses.Available) : ITodaySectionProvider
    {
        public string SectionId => id;
        public string Kind => kind;
        public Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
            => Task.FromResult(new TodaySectionDto(id, kind, status, DateTimeOffset.UtcNow, new { query.Date }, Array.Empty<TodayLinkDto>(), null));
    }

    private sealed class ThrowingProvider(string id, string kind) : ITodaySectionProvider
    {
        public string SectionId => id;
        public string Kind => kind;
        public Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
