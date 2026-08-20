using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class EventAttachmentServiceTests
{
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid OtherUserId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    // ===== Hydration: metadata -> unified outlook reference =====

    [Fact]
    public async Task GetOutlookAttachmentReferencesAsync_MapsFileAttachmentMetadataToOutlookReference()
    {
        var db = CreateDb();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
                "value": [
                    {
                        "@odata.type": "#microsoft.graph.fileAttachment",
                        "id": "att-1",
                        "name": "Report.pdf",
                        "contentType": "application/pdf",
                        "size": 2048,
                        "isInline": false
                    }
                ]
            }
            """);
        var service = CreateService(db, CreateGraphClient(handler));

        var refs = await service.GetOutlookAttachmentReferencesAsync(
            ConnectionId, "cal-1", "event-1", default);

        var reference = Assert.Single(refs);
        Assert.Equal("outlook", reference.Kind);
        Assert.Equal("att-1", reference.Id);
        Assert.Equal("Report.pdf", reference.Name);
        Assert.Equal("application/pdf", reference.ContentType);
        Assert.Equal(2048, reference.Size);
        Assert.True(reference.CanDownload);
    }

    [Fact]
    public async Task GetOutlookAttachmentReferencesAsync_ExcludesInlineFileAttachments()
    {
        var db = CreateDb();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
                "value": [
                    {
                        "@odata.type": "#microsoft.graph.fileAttachment",
                        "id": "att-inline",
                        "name": "Inline.png",
                        "contentType": "image/png",
                        "size": 512,
                        "isInline": true
                    },
                    {
                        "@odata.type": "#microsoft.graph.fileAttachment",
                        "id": "att-2",
                        "name": "Contract.docx",
                        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        "size": 4096,
                        "isInline": false
                    }
                ]
            }
            """);
        var service = CreateService(db, CreateGraphClient(handler));

        var refs = await service.GetOutlookAttachmentReferencesAsync(
            ConnectionId, "cal-1", "event-1", default);

        var reference = Assert.Single(refs);
        Assert.Equal("att-2", reference.Id);
        Assert.Equal("Contract.docx", reference.Name);
        Assert.True(reference.CanDownload);
    }

    [Fact]
    public async Task GetOutlookAttachmentReferencesAsync_ExcludesNonFileAttachmentTypes()
    {
        var db = CreateDb();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
                "value": [
                    {
                        "@odata.type": "#microsoft.graph.referenceAttachment",
                        "id": "att-link",
                        "name": "https://example.com/doc",
                        "contentType": "text/plain",
                        "size": 0,
                        "isInline": false
                    },
                    {
                        "@odata.type": "#microsoft.graph.fileAttachment",
                        "id": "att-file",
                        "name": "notes.txt",
                        "contentType": "text/plain",
                        "size": 128,
                        "isInline": false
                    }
                ]
            }
            """);
        var service = CreateService(db, CreateGraphClient(handler));

        var refs = await service.GetOutlookAttachmentReferencesAsync(
            ConnectionId, "cal-1", "event-1", default);

        var reference = Assert.Single(refs);
        Assert.Equal("att-file", reference.Id);
        Assert.Equal("notes.txt", reference.Name);
        Assert.True(reference.CanDownload);
    }

    // ===== pimFile ownership validation =====

    [Fact]
    public async Task ValidatePimFileReferenceAsync_AcceptsExistingActiveFileItemOwnedByUser()
    {
        var db = CreateDb();
        var (_, item) = SeedFileItem(db, UserId);
        await db.SaveChangesAsync();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        await service.ValidatePimFileReferenceAsync(
            UserId,
            new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name),
            default);
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_RejectsMissingFileItem()
    {
        var db = CreateDb();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ValidatePimFileReferenceAsync(
                UserId,
                new EventAttachmentReferenceDto("pimFile", Guid.NewGuid().ToString(), "Missing.pdf"),
                default));

        Assert.True(ex.ErrorCode > 0);
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_RejectsNonGuidFileItemId()
    {
        var db = CreateDb();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ValidatePimFileReferenceAsync(
                UserId,
                new EventAttachmentReferenceDto("pimFile", "not-a-guid", "Broken.pdf"),
                default));
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_RejectsSoftDeletedFileItem()
    {
        var db = CreateDb();
        var (_, item) = SeedFileItem(db, UserId, deleted: true);
        await db.SaveChangesAsync();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ValidatePimFileReferenceAsync(
                UserId,
                new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name),
                default));
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_RejectsFolderItem()
    {
        var db = CreateDb();
        var (_, item) = SeedFileItem(db, UserId, itemType: "folder");
        await db.SaveChangesAsync();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ValidatePimFileReferenceAsync(
                UserId,
                new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name),
                default));
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_RejectsFileItemOwnedByAnotherUser()
    {
        var db = CreateDb();
        var (_, item) = SeedFileItem(db, OtherUserId);
        await db.SaveChangesAsync();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        await Assert.ThrowsAsync<DomainException>(() =>
            service.ValidatePimFileReferenceAsync(
                UserId,
                new EventAttachmentReferenceDto("pimFile", item.Id.ToString(), item.Name),
                default));
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_IgnoresOutlookKindReferences()
    {
        var db = CreateDb();
        var service = CreateService(db, CreateGraphClient(new ScriptedHttpMessageHandler()));

        await service.ValidatePimFileReferenceAsync(
            UserId,
            new EventAttachmentReferenceDto("outlook", "att-1", "Report.pdf",
                "application/pdf", 2048, true),
            default);
    }

    [Fact]
    public async Task ValidatePimFileReferenceAsync_PimFileLookupPredicateTranslatesWithNpgsql()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var sentinel = new InvalidOperationException("SENTINEL_REACHED_CONNECTION_OPENING");
        var interceptor = new SentinelConnectionInterceptor(sentinel);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_test;Username=test;Password=test")
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new PimDbContext(options);
        var service = new EventAttachmentService(db);

        // The real production predicate must survive EF Core/Npgsql translation;
        // reaching the connection-open sentinel proves the SQL was fully built.
        // Untranslatable comparisons (e.g. string.Equals with StringComparison)
        // fail earlier with an InvalidOperationException from the query pipeline.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ValidatePimFileReferenceAsync(
                UserId,
                new EventAttachmentReferenceDto("pimFile", Guid.NewGuid().ToString(), "Contract.pdf"),
                default));

        Assert.Equal("SENTINEL_REACHED_CONNECTION_OPENING", ex.Message);
    }

    // ===== Reauth state persistence concurrency =====

    [Fact]
    public async Task DownloadOutlookAttachmentAsync_ReauthRequired_IncrementsVersionExactlyOnceAndPersistsState()
    {
        var db = CreateDb();
        var (connectionId, eventId) = await SeedDownloadContextAsync(db, UserId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var service = CreateService(db, CreateGraphClient(handler));

        var ex = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            service.DownloadOutlookAttachmentAsync(UserId, eventId, "att-1", default));
        Assert.Equal("graph-unauthorized", ex.Code);

        db.ChangeTracker.Clear();
        var connection = await db.Set<OutlookConnectionEntity>().AsNoTracking()
            .SingleAsync(c => c.Id == connectionId);
        Assert.Equal("reauth-required", connection.Status);
        Assert.Equal("interaction-required", connection.TokenHealth);
        Assert.Equal(1, connection.Version);
    }

    [Fact]
    public async Task DownloadOutlookAttachmentAsync_ReauthSaveConcurrencyConflict_StillThrowsReauth()
    {
        var failInterceptor = new FailReauthSaveInterceptor();
        var db = CreateDb(failInterceptor);
        var (_, eventId) = await SeedDownloadContextAsync(db, UserId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var service = CreateService(db, CreateGraphClient(handler));

        var ex = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            service.DownloadOutlookAttachmentAsync(UserId, eventId, "att-1", default));
        Assert.Equal("graph-unauthorized", ex.Code);
        Assert.Equal(1, failInterceptor.FailureCount);
    }

    [Fact]
    public async Task DownloadOutlookAttachmentAsync_UnselectedBinding_ReturnsNullWithoutGraphRequest()
    {
        var db = CreateDb();
        var (_, eventId) = await SeedDownloadContextAsync(db, UserId, bindingIsSelected: false);
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, CreateGraphClient(handler));

        var result = await service.DownloadOutlookAttachmentAsync(UserId, eventId, "att-1", default);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadOutlookAttachmentAsync_InactiveBinding_ReturnsNullWithoutGraphRequest()
    {
        var db = CreateDb();
        var (_, eventId) = await SeedDownloadContextAsync(db, UserId, bindingRemoteState: "remote-missing");
        var handler = new ScriptedHttpMessageHandler();
        var service = CreateService(db, CreateGraphClient(handler));

        var result = await service.DownloadOutlookAttachmentAsync(UserId, eventId, "att-1", default);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    // ===== Helpers =====

    private static PimDbContext CreateDb(ISaveChangesInterceptor? interceptor = null)
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var builder = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("event-attachments-" + Guid.NewGuid());
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new PimDbContext(builder.Options);
    }

    private static async Task<(Guid ConnectionId, Guid EventId)> SeedDownloadContextAsync(
        PimDbContext db, Guid userId, bool bindingIsSelected = true, string bindingRemoteState = "active")
    {
        var calendar = new CalendarEntity
        {
            UserId = userId,
            Name = "Cal",
            Source = "outlook"
        };
        var connection = new OutlookConnectionEntity
        {
            UserId = userId,
            Status = "connected",
            TokenHealth = "healthy"
        };
        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = connection.Id,
            PimCalendarId = calendar.Id,
            GraphCalendarId = "cal-graph",
            Name = "Cal",
            IsSelected = bindingIsSelected,
            RemoteState = bindingRemoteState
        };
        var evt = new EventEntity
        {
            CalendarId = calendar.Id,
            Uid = Guid.NewGuid().ToString(),
            Title = "Download Event",
            DtStart = DateTimeOffset.UtcNow,
            DtEnd = DateTimeOffset.UtcNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = "graph-event-1",
            OutlookCalendarBindingId = binding.Id,
            OutlookConnectionId = connection.Id,
            AttachmentReferencesJson =
                """[{"kind":"outlook","id":"att-1","name":"Report.pdf","contentType":"application/pdf","size":1024,"canDownload":true}]"""
        };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<OutlookConnectionEntity>().Add(connection);
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        return (connection.Id, evt.Id);
    }

    private static GraphCalendarClient CreateGraphClient(ScriptedHttpMessageHandler handler)
    {
        var tokens = new FakeOutlookAccessTokenProvider();
        var factory = new StubHttpClientFactory(handler);
        return new GraphCalendarClient(factory, tokens, new StubTimeProvider());
    }

    private static EventAttachmentService CreateService(PimDbContext db, GraphCalendarClient graph)
        => new(db, graph);

    private static (FileProviderEntity Provider, FileItemEntity Item) SeedFileItem(
        PimDbContext db, Guid ownerUserId, bool deleted = false, string itemType = "file")
    {
        var provider = new FileProviderEntity
        {
            Id = Guid.NewGuid(),
            UserId = ownerUserId,
            Provider = "nextcloud",
            BaseUrl = "https://nc.example",
            Username = "test-user"
        };
        var item = new FileItemEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            Name = "Contract.pdf",
            ItemType = itemType,
            Path = "/Contract.pdf",
            IsDeleted = deleted
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        return (provider, item);
    }

    private sealed class SentinelConnectionInterceptor : DbConnectionInterceptor
    {
        private readonly InvalidOperationException _exception;
        public SentinelConnectionInterceptor(InvalidOperationException exception) => _exception = exception;

        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData data, InterceptionResult result)
            => throw _exception;

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData data, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => throw _exception;
    }

    private sealed class FailReauthSaveInterceptor : SaveChangesInterceptor
    {
        public int FailureCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isReauthSave = eventData.Context?.ChangeTracker
                .Entries<OutlookConnectionEntity>()
                .Any(entry => entry.State == EntityState.Modified
                    && entry.Entity.Status == "reauth-required") == true;
            if (isReauthSave)
            {
                FailureCount++;
                throw new DbUpdateConcurrencyException("Simulated reauth state save conflict.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
