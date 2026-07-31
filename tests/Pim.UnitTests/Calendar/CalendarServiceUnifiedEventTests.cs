using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class CalendarServiceUnifiedEventTests
{
    private static readonly Guid UserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OtherUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccd");

    [Fact]
    public async Task CreateEventAsync_PersistsAndReturnsAllUnifiedFields()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var (_, file) = SeedFileItem(db, UserId);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "Full field event", "Description", "Location",
                start, end, null,
                DescriptionFormat: "html",
                ShowAs: "busy",
                Importance: "high",
                Sensitivity: "private",
                Categories: new List<string> { "重要", "项目" },
                IsReminderOn: true,
                ReminderMinutesBeforeStart: 15,
                Organizer: new EventPersonDto("Owner", "owner@example.com"),
                Attendees: new List<EventAttendeeDto>
                {
                    new("Alice", "alice@example.com", "required"),
                    new("Bob", "bob@example.com", "optional")
                },
                IsOnlineMeeting: true,
                OnlineMeetingProvider: "teams",
                OnlineMeetingUrl: "https://teams.example/meet/123",
                ExternalLink: "https://outlook.office.com/calendar/item/abc",
                AttachmentReferences: new List<EventAttachmentReferenceDto>
                {
                    new("pimFile", file.Id.ToString(), "Contract.pdf", "application/pdf", 1024, true)
                }),
            default);

        Assert.Equal("html", response.DescriptionFormat);
        Assert.Equal("busy", response.ShowAs);
        Assert.Equal("high", response.Importance);
        Assert.Equal("private", response.Sensitivity);

        Assert.NotNull(response.Categories);
        Assert.Equal(2, response.Categories.Count);
        Assert.Contains("重要", response.Categories);
        Assert.Contains("项目", response.Categories);

        Assert.True(response.IsReminderOn);
        Assert.Equal(15, response.ReminderMinutesBeforeStart);

        Assert.NotNull(response.Organizer);
        Assert.Equal("Owner", response.Organizer.Name);
        Assert.Equal("owner@example.com", response.Organizer.Email);

        Assert.NotNull(response.Attendees);
        Assert.Equal(2, response.Attendees.Count);
        Assert.Contains(response.Attendees, a => a.Email == "alice@example.com" && a.Type == "required");
        Assert.Contains(response.Attendees, a => a.Email == "bob@example.com" && a.Type == "optional");

        Assert.True(response.IsOnlineMeeting);
        Assert.Equal("teams", response.OnlineMeetingProvider);
        Assert.Equal("https://teams.example/meet/123", response.OnlineMeetingUrl);
        Assert.Equal("https://outlook.office.com/calendar/item/abc", response.ExternalLink);

        Assert.NotNull(response.AttachmentReferences);
        var attRef = Assert.Single(response.AttachmentReferences);
        Assert.Equal("pimFile", attRef.Kind);
        Assert.Equal(file.Id.ToString(), attRef.Id);
        Assert.Equal("Contract.pdf", attRef.Name);
        Assert.Equal("application/pdf", attRef.ContentType);
        Assert.Equal(1024, attRef.Size);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("html", entity.DescriptionFormat);
        Assert.Equal("busy", entity.ShowAs);
        Assert.Equal("high", entity.Importance);
        Assert.Equal("private", entity.Sensitivity);
        Assert.True(entity.IsReminderOn);
        Assert.Equal(15, entity.ReminderMinutesBeforeStart);
    }

    [Fact]
    public async Task UpdateEventAsync_ClearsOptionalUnifiedFields()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = "Original",
            DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            CategoriesJson = """["重要"]""",
            AttendeesJson = """[{"name":"Alice","email":"a@b.com","type":"required"}]""",
            IsOnlineMeeting = true,
            OnlineMeetingProvider = "teams",
            OnlineMeetingUrl = "https://example.com/meet",
            ExternalLink = "https://example.com/link",
            AttachmentReferencesJson = """[{"kind":"pimFile","id":"f1","name":"doc.pdf"}]""",
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.UpdateEventAsync(
            evt.Id,
            new UpdateEventRequest(
                calendar.Id, "Cleared", null, null,
                start, end, null,
                Categories: [],
                Attendees: [],
                OnlineMeetingUrl: null,
                ExternalLink: null,
                AttachmentReferences: []),
            default);

        Assert.NotNull(response.Categories);
        Assert.Empty(response.Categories);
        Assert.NotNull(response.Attendees);
        Assert.Empty(response.Attendees);
        Assert.Null(response.OnlineMeetingUrl);
        Assert.Null(response.ExternalLink);
        Assert.NotNull(response.AttachmentReferences);
        Assert.Empty(response.AttachmentReferences);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("[]", entity.CategoriesJson);
        Assert.Equal("[]", entity.AttendeesJson);
        Assert.Null(entity.OnlineMeetingUrl);
        Assert.Null(entity.ExternalLink);
        Assert.Equal("[]", entity.AttachmentReferencesJson);
    }

    [Fact]
    public async Task CreateEventAsync_ReminderOffClearsReminderMinutes()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "Test", null, null,
                start, end, null,
                IsReminderOn: false,
                ReminderMinutesBeforeStart: 15),
            default);

        Assert.False(response.IsReminderOn);
        Assert.Null(response.ReminderMinutesBeforeStart);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.False(entity.IsReminderOn);
        Assert.Null(entity.ReminderMinutesBeforeStart);
    }

    [Theory]
    [InlineData("DescriptionFormat", "invalid-format")]
    [InlineData("ShowAs", "invalid-showas")]
    [InlineData("Importance", "invalid-importance")]
    [InlineData("Sensitivity", "invalid-sensitivity")]
    [InlineData("OnlineMeetingProvider", "invalid-provider")]
    public async Task CreateEventAsync_InvalidEnum_Returns02009(string field, string value)
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var baseReq = new CreateEventRequest(calendar.Id, "Test", null, null, start, end, null);
        var invalidReq = field switch
        {
            "DescriptionFormat" => baseReq with { DescriptionFormat = value },
            "ShowAs" => baseReq with { ShowAs = value },
            "Importance" => baseReq with { Importance = value },
            "Sensitivity" => baseReq with { Sensitivity = value },
            "OnlineMeetingProvider" => baseReq with { OnlineMeetingProvider = value },
            _ => baseReq,
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(invalidReq, default));

        Assert.Equal(02009, ex.ErrorCode);
        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_InvalidAttendeeType_Returns02009()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                new CreateEventRequest(
                    calendar.Id, "Test", null, null, start, end, null,
                    Attendees: new List<EventAttendeeDto>
                    {
                        new("Alice", "alice@example.com", "invalid-type")
                    }),
                default));

        Assert.Equal(02009, ex.ErrorCode);
        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_HtmlDescription_IsSanitizedBeforeStorage()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "Test",
                "<p><strong>ok</strong><script>alert(1)</script></p>",
                null, start, end, null,
                DescriptionFormat: "html"),
            default);

        Assert.Equal("<p><strong>ok</strong></p>", response.Description);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("<p><strong>ok</strong></p>", entity.Description);
    }

    [Fact]
    public async Task CreateEventAsync_HtmlDescription_RemovesJavascriptLinks()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "Test",
                """<a href="javascript:alert(1)">x</a>""",
                null, start, end, null,
                DescriptionFormat: "html"),
            default);

        Assert.Equal("""<a>x</a>""", response.Description);
    }

    [Fact]
    public async Task UpdateEventAsync_NullOrganizer_ClearsOrganizerJson()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = "Original",
            DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            OrganizerJson = """{"Name":"Owner","Email":"owner@test.com"}""",
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.UpdateEventAsync(
            evt.Id,
            new UpdateEventRequest(
                calendar.Id, "Updated", null, null,
                start, end, null,
                Organizer: null),
            default);

        Assert.Null(response.Organizer);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Null(entity.OrganizerJson);
    }

    [Fact]
    public async Task CreateEventAsync_BlankEnumStrings_NormalizesToNull()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "Test", null, null,
                start, end, null,
                DescriptionFormat: "",
                ShowAs: "",
                Importance: "",
                Sensitivity: "",
                OnlineMeetingProvider: ""),
            default);

        Assert.Null(response.DescriptionFormat);
        Assert.Null(response.ShowAs);
        Assert.Null(response.Importance);
        Assert.Null(response.Sensitivity);
        Assert.Null(response.OnlineMeetingProvider);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Null(entity.DescriptionFormat);
        Assert.Null(entity.ShowAs);
        Assert.Null(entity.Importance);
        Assert.Null(entity.Sensitivity);
        Assert.Null(entity.OnlineMeetingProvider);
    }

    // --- Helpers ---

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FileItemEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"unified-events-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private static CalendarEntity SeedCalendar(PimDbContext db, string name, string kind)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = name,
            Kind = kind,
        };
        db.Set<CalendarEntity>().Add(calendar);
        return calendar;
    }

    [Fact]
    public async Task CreateEventAsync_WhitespaceStrings_NormalizesToNull()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.CreateEventAsync(
            new CreateEventRequest(
                calendar.Id, "Test", null, null,
                start, end, null,
                OnlineMeetingProvider: " ",
                OnlineMeetingUrl: " ",
                ExternalLink: " ",
                Organizer: new EventPersonDto(" ", " ")),
            default);

        Assert.Null(response.OnlineMeetingProvider);
        Assert.Null(response.OnlineMeetingUrl);
        Assert.Null(response.ExternalLink);
        Assert.Null(response.Organizer);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Null(entity.OnlineMeetingProvider);
        Assert.Null(entity.OnlineMeetingUrl);
        Assert.Null(entity.ExternalLink);
        Assert.Null(entity.OrganizerJson);
    }

    // ===== Task 5: pimFile attachment reference validation =====

    private static CreateEventRequest BaseRequest(Guid calendarId, IReadOnlyList<EventAttachmentReferenceDto>? refs = null)
    {
        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        return new CreateEventRequest(
            calendarId, "Test", null, null, start, end, null,
            AttachmentReferences: refs);
    }

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

    [Fact]
    public async Task CreateEventAsync_PimFileReference_Accepted_WhenFileItemOwnedAndActive()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var (_, item) = SeedFileItem(db, UserId);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.CreateEventAsync(
            BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
            {
                new("pimFile", item.Id.ToString(), "Contract.pdf", "application/pdf", 1024, true)
            }),
            default);

        Assert.NotNull(response.AttachmentReferences);
        var reference = Assert.Single(response.AttachmentReferences);
        Assert.Equal("pimFile", reference.Kind);
        Assert.Equal(item.Id.ToString(), reference.Id);
        Assert.Equal("Contract.pdf", reference.Name);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        var stored = EventFieldCodec.DeserializeAttachments(entity.AttachmentReferencesJson);
        Assert.Equal(item.Id.ToString(), Assert.Single(stored).Id);
    }

    [Fact]
    public async Task CreateEventAsync_PimFileReference_Rejected_WhenFileItemMissing()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
                {
                    new("pimFile", Guid.NewGuid().ToString(), "Missing.pdf")
                }),
                default));

        Assert.True(ex.ErrorCode > 0);
        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_PimFileReference_Rejected_WhenIdIsNotGuid()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
                {
                    new("pimFile", "not-a-guid", "Broken.pdf")
                }),
                default));

        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_PimFileReference_Rejected_WhenFileItemSoftDeleted()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var (_, item) = SeedFileItem(db, UserId, deleted: true);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
                {
                    new("pimFile", item.Id.ToString(), item.Name)
                }),
                default));

        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_PimFileReference_Rejected_WhenFileOwnedByAnotherUser()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var (_, item) = SeedFileItem(db, OtherUserId);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
                {
                    new("pimFile", item.Id.ToString(), item.Name)
                }),
                default));

        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_PimFileReference_Rejected_WhenFileItemIsFolder()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var (_, item) = SeedFileItem(db, UserId, itemType: "folder");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
                {
                    new("pimFile", item.Id.ToString(), item.Name)
                }),
                default));

        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task CreateEventAsync_OutlookKindReference_Rejected()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateEventAsync(
                BaseRequest(calendar.Id, new List<EventAttachmentReferenceDto>
                {
                    new("outlook", "att-1", "Report.pdf", "application/pdf", 2048, true)
                }),
                default));

        Assert.Equal(02009, ex.ErrorCode);
        Assert.Equal(0, await db.Set<EventEntity>().CountAsync());
    }

    [Fact]
    public async Task UpdateEventAsync_OutlookKindReference_Rejected_ExistingReferencesUnchanged()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = "Original",
            DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            AttachmentReferencesJson = """
                [{"kind":"outlook","id":"att-1","name":"Synced.pdf","contentType":"application/pdf","size":20,"canDownload":true}]
                """,
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(
                evt.Id,
                new UpdateEventRequest(
                    calendar.Id, "Updated", null, null,
                    start, end, null,
                    AttachmentReferences: new List<EventAttachmentReferenceDto>
                    {
                        new("outlook", "att-forged", "Forged.pdf")
                    }),
                default));

        Assert.Equal(02009, ex.ErrorCode);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("Original", entity.Title);
        var stored = EventFieldCodec.DeserializeAttachments(entity.AttachmentReferencesJson);
        var reference = Assert.Single(stored);
        Assert.Equal("outlook", reference.Kind);
        Assert.Equal("att-1", reference.Id);
        Assert.Equal("Synced.pdf", reference.Name);
    }

    [Fact]
    public async Task UpdateEventAsync_PimFileReference_Rejected_WhenFileItemMissing()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = "Original",
            DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            AttachmentReferencesJson = """
                [{"kind":"pimFile","id":"f1","name":"doc.pdf"}]
                """,
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateEventAsync(
                evt.Id,
                new UpdateEventRequest(
                    calendar.Id, "Updated", null, null,
                    start, end, null,
                    AttachmentReferences: new List<EventAttachmentReferenceDto>
                    {
                        new("pimFile", Guid.NewGuid().ToString(), "Missing.pdf")
                    }),
                default));

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("Original", entity.Title);
        var stored = EventFieldCodec.DeserializeAttachments(entity.AttachmentReferencesJson);
        Assert.Equal("f1", Assert.Single(stored).Id);
    }

    [Fact]
    public async Task UpdateEventAsync_NullAttachmentReferences_PreservesExistingReferences()
    {
        await using var db = CreateDb();
        var calendar = SeedCalendar(db, "My Calendar", "calendar");
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid()}@pim",
            Title = "Original",
            DtStart = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            AttachmentReferencesJson = """
                [{"kind":"pimFile","id":"f1","name":"doc.pdf"}]
                """,
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

        var response = await service.UpdateEventAsync(
            evt.Id,
            new UpdateEventRequest(
                calendar.Id, "Updated", null, null,
                start, end, null,
                AttachmentReferences: null),
            default);

        Assert.NotNull(response.AttachmentReferences);
        var reference = Assert.Single(response.AttachmentReferences);
        Assert.Equal("f1", reference.Id);

        var entity = await db.Set<EventEntity>().AsNoTracking().SingleAsync();
        var stored = EventFieldCodec.DeserializeAttachments(entity.AttachmentReferencesJson);
        Assert.Equal("f1", Assert.Single(stored).Id);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
