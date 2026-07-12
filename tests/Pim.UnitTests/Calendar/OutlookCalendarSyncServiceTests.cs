using System.Net;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookCalendarSyncServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    // Graph JSON samples
    private const string GroupsPage1 = """
        {
            "value": [
                {"id":"g-course","name":"课程表"},
                {"id":"g-work","name":"Work"}
            ]
        }
        """;

    private const string GroupsPageWithNext = """
        {
            "value": [{"id":"g-more","name":"More"}],
            "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/calendarGroups?$skiptoken=x"
        }
        """;

    private const string GroupsPage2 = """
        {
            "value": [{"id":"g-extra","name":"Extra"}]
        }
        """;

    private static string CalendarsOfGroup(string groupId, string extra = "")
        => $$"""
            {
                "value": [
                    {
                        "id":"{{groupId}}-cal",
                        "name":"Group Calendar",
                        "color":"lightBlue",
                        "owner":{"name":"U","address":"u@t"},
                        "isDefaultCalendar":false,
                        "canEdit":true,
                        "canViewPrivateItems":true
                    }
                ]{{extra}}
            }
            """;

    private static string CalendarsOfGroupPage1(string groupId)
        => $$"""
            {
                "value": [
                    {
                        "id":"{{groupId}}-cal-1",
                        "name":"Cal 1",
                        "color":"lightBlue",
                        "owner":{"name":"U","address":"u@t"},
                        "isDefaultCalendar":false,
                        "canEdit":true,
                        "canViewPrivateItems":true
                    }
                ],
                "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/calendarGroups/{{groupId}}/calendars?$skiptoken=y"
            }
            """;

    private static string CalendarsOfGroupPage2(string groupId)
        => $$"""
            {
                "value": [
                    {
                        "id":"{{groupId}}-cal-2",
                        "name":"Cal 2",
                        "color":"lightYellow",
                        "owner":{"name":"U","address":"u@t"},
                        "isDefaultCalendar":false,
                        "canEdit":true,
                        "canViewPrivateItems":true
                    }
                ]
            }
            """;

    private static string RootCalendarsPage1
        => $$"""
            {
                "value": [
                    {
                        "id":"root-cal",
                        "name":"Root Calendar",
                        "color":"auto",
                        "owner":{"name":"U","address":"u@t"},
                        "isDefaultCalendar":true,
                        "canEdit":true,
                        "canViewPrivateItems":true
                    }
                ],
                "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/calendars?$skiptoken=z"
            }
            """;

    private static string RootCalendarsPage2
        => $$"""
            {
                "value": [
                    {
                        "id":"root-cal-2",
                        "name":"Root Calendar 2",
                        "color":"lightGreen",
                        "owner":{"name":"U","address":"u@t"},
                        "isDefaultCalendar":false,
                        "canEdit":true,
                        "canViewPrivateItems":true
                    }
                ]
            }
            """;

    private const string CourseCalendar = """
        {
            "id":"course-cal",
            "name":"课程日历",
            "color":"lightBlue",
            "owner":{"name":"U","address":"u@t"},
            "isDefaultCalendar":false,
            "canEdit":true,
            "canViewPrivateItems":true
        }
        """;

    private const string RootOnlyCalendar = """
        {
            "id":"root-only",
            "name":"Root Only",
            "color":"lightYellow",
            "owner":{"name":"U","address":"u@t"},
            "isDefaultCalendar":false,
            "canEdit":true,
            "canViewPrivateItems":true
        }
        """;

    private const string ReadOnlyCalendar = """
        {
            "id":"readonly-cal",
            "name":"Read Only",
            "color":"lightGray",
            "owner":{"name":"U","address":"u@t"},
            "isDefaultCalendar":false,
            "canEdit":false,
            "canViewPrivateItems":true
        }
        """;

    private const string DefaultCalendar = """
        {
            "id":"default-cal",
            "name":"默认日历",
            "color":"auto",
            "owner":{"name":"U","address":"u@t"},
            "isDefaultCalendar":true,
            "canEdit":true,
            "canViewPrivateItems":true
        }
        """;

    private const string CalendarWithNullColor = """
        {
            "id":"null-color-cal",
            "name":"No Color",
            "color":null,
            "owner":{"name":"U","address":"u@t"},
            "isDefaultCalendar":false,
            "canEdit":true,
            "canViewPrivateItems":true
        }
        """;

    private const string CalendarWithUnknownColor = """
        {
            "id":"unknown-color-cal",
            "name":"Unknown Color",
            "color":"hotPink",
            "owner":{"name":"U","address":"u@t"},
            "isDefaultCalendar":false,
            "canEdit":true,
            "canViewPrivateItems":true
        }
        """;

    // --- Helper methods ---

    private static PimDbContext CreateDb(string? name = null)
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static GraphCalendarClient CreateGraphClient(ScriptedHttpMessageHandler handler)
    {
        var tokens = new FakeOutlookAccessTokenProvider();
        var clock = new StubTimeProvider { UtcNowValue = FixedNow };
        var factory = new StubHttpClientFactory(handler);
        return new GraphCalendarClient(factory, tokens, clock);
    }

    private static OutlookCalendarSyncService CreateService(PimDbContext db, GraphCalendarClient graph,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= new StubTimeProvider { UtcNowValue = FixedNow };
        return new OutlookCalendarSyncService(db, graph, timeProvider,
            NullLogger<OutlookCalendarSyncService>.Instance);
    }

    private static async Task SeedConnectionAsync(PimDbContext db, Guid userId, Guid? connectionId = null)
    {
        db.Set<OutlookConnectionEntity>().Add(new OutlookConnectionEntity
        {
            Id = connectionId ?? ConnectionId,
            UserId = userId,
            Status = "connected"
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBindingAndCalendarAsync(PimDbContext db, Guid userId, string graphCalendarId,
        bool isSelected = true, string remoteState = "active", Guid? connectionId = null)
    {
        var cal = new CalendarEntity
        {
            UserId = userId,
            Name = "Existing " + graphCalendarId,
            Source = "outlook",
            IsVisible = isSelected
        };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();

        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connectionId ?? ConnectionId,
            PimCalendarId = cal.Id,
            GraphCalendarId = graphCalendarId,
            Name = "Existing " + graphCalendarId,
            IsSelected = isSelected,
            RemoteState = remoteState
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBindingWithCalendarAsync(PimDbContext db, CalendarEntity calendar,
        string graphCalendarId, bool isSelected = true, Guid? connectionId = null)
    {
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connectionId ?? ConnectionId,
            PimCalendarId = calendar.Id,
            GraphCalendarId = graphCalendarId,
            Name = calendar.Name,
            IsSelected = isSelected,
            RemoteState = "active"
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedEventAsync(PimDbContext db, Guid calendarId, Guid bindingId,
        string outlookEventId = "event-1")
    {
        db.Set<EventEntity>().Add(new EventEntity
        {
            CalendarId = calendarId,
            Uid = outlookEventId + "@pim",
            Title = "Test Event",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = outlookEventId,
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = ConnectionId
        });
        await db.SaveChangesAsync();
    }

    private static IEnumerable<T> ToList<T>(IReadOnlyList<T> source) => source;

    [Fact]
    public async Task DiscoverAsync_NoConnection_ThrowsDomainException()
    {
        var db = CreateDb();
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.DiscoverAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(02005, ex.ErrorCode);
    }

    [Fact]
    public async Task DiscoverAsync_MergesGroupsAndRootAndDefaultsNewBindingsToSelected()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Groups: 课程表 + Work
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"g-course","name":"课程表"},{"id":"g-work","name":"Work"}]}""");
        // g-course calendars: course-cal
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{CourseCalendar}}]}""");
        // g-work calendars: empty
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        // Root calendars: root-only + default-cal + course-cal (dup)
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{RootOnlyCalendar}},{{DefaultCalendar}},{{CourseCalendar}}]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.All(result, item => Assert.True(item.IsSelected));

        var ids = result.Select(x => x.GraphCalendarId).Order().ToArray();
        Assert.Equal("course-cal", ids[0]);
        Assert.Equal("default-cal", ids[1]);
        Assert.Equal("root-only", ids[2]);

        var courseResult = result.Single(r => r.GraphCalendarId == "course-cal");
        Assert.Equal("课程表", courseResult.GroupName);
        Assert.True(courseResult.IsSelected);
    }

    [Fact]
    public async Task Discovery_Pagination_GroupsAndCalendarsAndRoot()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Group pages with nextLink
        handler.Enqueue(HttpStatusCode.OK, GroupsPageWithNext);
        handler.Enqueue(HttpStatusCode.OK, GroupsPage2);
        // Group g-more calendars (page 1) with nextLink
        handler.Enqueue(HttpStatusCode.OK, CalendarsOfGroupPage1("g-more"));
        // Group g-more calendars (page 2)
        handler.Enqueue(HttpStatusCode.OK, CalendarsOfGroupPage2("g-more"));
        // Group g-extra calendars (empty)
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        // Root calendars (page 1) with nextLink
        handler.Enqueue(HttpStatusCode.OK, RootCalendarsPage1);
        // Root calendars (page 2)
        handler.Enqueue(HttpStatusCode.OK, RootCalendarsPage2);

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Contains(result, r => r.GraphCalendarId == "g-more-cal-1");
        Assert.Contains(result, r => r.GraphCalendarId == "g-more-cal-2");
        Assert.Contains(result, r => r.GraphCalendarId == "root-cal");
        Assert.Contains(result, r => r.GraphCalendarId == "root-cal-2");
    }

    [Fact]
    public async Task Discovery_DeduplicatesByGraphId_PreservesGroupMetadata()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // One group page with one group
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"g-course","name":"课程表"}]}""");
        // Group calendars - has "course-cal"
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{CourseCalendar}}]}""");
        // Root calendars - has same "course-cal" (duplicate)
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{CourseCalendar}},{{RootOnlyCalendar}}]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        var courseCal = result.Single(r => r.GraphCalendarId == "course-cal");
        Assert.Equal("g-course", courseCal.GroupId);
        Assert.Equal("课程表", courseCal.GroupName);
        Assert.Equal("课程日历", courseCal.Name);
    }

    [Fact]
    public async Task Discovery_PersistsCanEditFalse()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}"""); // empty groups
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{ReadOnlyCalendar}}]}"""); // root calendars

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        var ro = result.Single(r => r.GraphCalendarId == "readonly-cal");
        Assert.False(ro.CanEdit);

        // Verify in DB
        var binding = await db.Set<OutlookCalendarBindingEntity>()
            .FirstAsync(b => b.GraphCalendarId == "readonly-cal");
        Assert.False(binding.CanEdit);
    }

    [Fact]
    public async Task Discovery_CreatesIndependentCalendarEntityPerBinding()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{DefaultCalendar}}]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        var binding = result.Single();
        var calendar = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == binding.PimCalendarId);

        Assert.Equal("outlook", calendar.Source);
        Assert.Equal("calendar", calendar.Kind);
        Assert.Equal(UserId, calendar.UserId);
        Assert.Equal("默认日历", calendar.Name);
        Assert.True(calendar.IsVisible);
    }

    [Fact]
    public async Task Discovery_MapsGraphColorToHex()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{DefaultCalendar}},{{CalendarWithNullColor}},{{CalendarWithUnknownColor}}]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        var defaultCal = result.Single(r => r.GraphCalendarId == "default-cal");
        Assert.Equal("auto", defaultCal.Color); // binding preserves original

        var nullCal = result.Single(r => r.GraphCalendarId == "null-color-cal");
        Assert.Null(nullCal.Color); // binding preserves null

        // CalendarEntity colors
        var defaultCalId = defaultCal.PimCalendarId;
        var nullCalId = nullCal.PimCalendarId;
        var unknownCalId = result.Single(r => r.GraphCalendarId == "unknown-color-cal").PimCalendarId;

        var defaultEntity = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == defaultCalId);
        Assert.Equal("#3B82F6", defaultEntity.Color); // auto maps to default blue

        var nullEntity = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == nullCalId);
        Assert.Equal("#3B82F6", nullEntity.Color); // null maps to default blue

        var unknownEntity = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == unknownCalId);
        Assert.Equal("#3B82F6", unknownEntity.Color); // unknown maps to default blue
    }

    [Fact]
    public async Task Rediscovery_ReplacesSoftDeletedCalendarWithoutRestoringDeletedHistory()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        var operationId = Guid.NewGuid();
        var oldCalendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Old Deleted Calendar",
            Color = "#69AFE5",
            Kind = "calendar",
            Source = "outlook",
            IsVisible = true,
            DeletedAt = FixedNow.AddDays(-1),
            DeletedByOperationId = operationId,
            DeletedByOperationKind = "calendar.delete"
        };
        db.Set<CalendarEntity>().Add(oldCalendar);
        await db.SaveChangesAsync();

        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId,
            PimCalendarId = oldCalendar.Id,
            GraphCalendarId = "course-cal",
            Name = "Old Binding",
            IsSelected = true,
            RemoteState = "active"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();

        var evt = new EventEntity
        {
            CalendarId = oldCalendar.Id,
            Uid = "old-event@pim",
            Title = "Old Event",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook"
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();

        var oldCalId = oldCalendar.Id;

        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{CourseCalendar}}]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        var bindingResult = result.Single(r => r.GraphCalendarId == "course-cal");
        Assert.NotEqual(oldCalId, bindingResult.PimCalendarId);

        var newCalendar = await db.Set<CalendarEntity>()
            .FirstAsync(c => c.Id == bindingResult.PimCalendarId);
        Assert.Null(newCalendar.DeletedAt);
        Assert.Equal("outlook", newCalendar.Source);
        Assert.True(newCalendar.IsVisible);
        Assert.Equal("课程日历", newCalendar.Name);

        var oldReloaded = await db.Set<CalendarEntity>()
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == oldCalId);
        Assert.NotNull(oldReloaded.DeletedAt);
        Assert.Equal(operationId, oldReloaded.DeletedByOperationId);
        Assert.Equal("calendar.delete", oldReloaded.DeletedByOperationKind);

        var bindingReloaded = await db.Set<OutlookCalendarBindingEntity>()
            .FirstAsync(b => b.Id == binding.Id);
        Assert.Equal(bindingResult.PimCalendarId, bindingReloaded.PimCalendarId);

        var oldEvents = await db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.CalendarId == oldCalId)
            .ToListAsync();
        Assert.NotEmpty(oldEvents);
    }

    [Fact]
    public async Task Rediscovery_PreservesUserSelectionAndReactivatesMissing()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Seed an existing binding that was previously deselected
        var cal = new CalendarEntity
        {
            UserId = UserId, Name = "Previously Deselected", Source = "outlook", IsVisible = true
        };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();

        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId,
            PimCalendarId = cal.Id,
            GraphCalendarId = "course-cal",
            Name = "Previously Deselected",
            IsSelected = false,
            RemoteState = "remote-missing"
        });
        await db.SaveChangesAsync();

        // Discover - graph returns this calendar
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{CourseCalendar}}]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        var binding = result.Single(r => r.GraphCalendarId == "course-cal");
        Assert.False(binding.IsSelected); // preserved
        Assert.Equal("active", binding.RemoteState); // reactivated

        var calendar = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == binding.PimCalendarId);
        Assert.False(calendar.IsVisible); // preserved
    }

    [Fact]
    public async Task Discovery_MarksOldBindingsRemoteMissingAfterSuccess()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Seed a binding that won't appear in discovery
        var cal = new CalendarEntity { UserId = UserId, Name = "Old", Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();
        var oldBinding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal.Id,
            GraphCalendarId = "old-cal", Name = "Old", IsSelected = true
        };
        db.Set<OutlookCalendarBindingEntity>().Add(oldBinding);
        await db.SaveChangesAsync();

        // Discovery returns no calendars (empty groups + empty root)
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        var service = CreateService(db, graph);
        var result = await service.DiscoverAsync(UserId, CancellationToken.None);

        // Old binding still appears in result but with remote-missing state
        var old = Assert.Single(result);
        Assert.Equal("old-cal", old.GraphCalendarId);
        Assert.Equal("remote-missing", old.RemoteState);

        var binding = await db.Set<OutlookCalendarBindingEntity>()
            .IgnoreQueryFilters()
            .FirstAsync(b => b.Id == oldBinding.Id);
        Assert.Equal("remote-missing", binding.RemoteState);

        // Calendar and events should still exist
        var calendar = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == cal.Id);
        Assert.NotNull(calendar);
    }

    [Fact]
    public async Task FailedDiscovery_DoesNotMarkExistingBindingMissing()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedBindingAndCalendarAsync(db, UserId, "existing-calendar");
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Enqueue groups page - success
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        // Enqueue root calendars - fails with 503
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        // GraphCalendarClient will retry: need 2 more 503s to exhaust attempts
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var service = CreateService(db, graph);
        await Assert.ThrowsAsync<GraphRequestException>(() =>
            service.DiscoverAsync(UserId, CancellationToken.None));

        var binding = await db.Set<OutlookCalendarBindingEntity>()
            .FirstAsync(b => b.GraphCalendarId == "existing-calendar");
        Assert.Equal("active", binding.RemoteState);
    }

    [Fact]
    public async Task FailedDiscovery_DoesNotPersistPartialResults()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // One group that succeeds
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"g1","name":"G1"}]}""");
        // Group calendars that succeeds
        handler.Enqueue(HttpStatusCode.OK, $$"""{"value":[{{DefaultCalendar}}]}""");
        // Root calendars that fails
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var service = CreateService(db, graph);
        await Assert.ThrowsAsync<GraphRequestException>(() =>
            service.DiscoverAsync(UserId, CancellationToken.None));

        // No calendars should have been persisted
        Assert.Equal(0, await db.Set<CalendarEntity>().CountAsync());
        Assert.Equal(0, await db.Set<OutlookCalendarBindingEntity>().CountAsync());
    }

    [Fact]
    public async Task SetSelection_HidesCalendarWithoutDeletingEvents()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Seed two bindings
        var cal1 = new CalendarEntity { UserId = UserId, Name = "Selected", Source = "outlook", IsVisible = true };
        var cal2 = new CalendarEntity { UserId = UserId, Name = "Paused", Source = "outlook", IsVisible = true };
        db.Set<CalendarEntity>().AddRange(cal1, cal2);
        await db.SaveChangesAsync();

        var binding1 = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal1.Id,
            GraphCalendarId = "cal-1", Name = "Selected", IsSelected = true
        };
        var binding2 = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal2.Id,
            GraphCalendarId = "cal-2", Name = "Paused", IsSelected = true
        };
        db.Set<OutlookCalendarBindingEntity>().AddRange(binding1, binding2);
        await db.SaveChangesAsync();

        // Seed event on paused calendar
        await SeedEventAsync(db, cal2.Id, binding2.Id, "event-1");

        var service = CreateService(db, graph);

        // Set selection: only binding1 is selected
        await service.SetSelectionAsync(UserId, new[] { binding1.Id }, CancellationToken.None);

        // Verify binding2 is deselected
        var b2 = await db.Set<OutlookCalendarBindingEntity>().FirstAsync(b => b.Id == binding2.Id);
        Assert.False(b2.IsSelected);

        // Verify cal2 is hidden
        var c2 = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == cal2.Id);
        Assert.False(c2.IsVisible);

        // Verify cal1 is still visible
        var c1 = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == cal1.Id);
        Assert.True(c1.IsVisible);

        // Verify event not deleted
        var evt = await db.Set<EventEntity>().FirstAsync(e => e.OutlookEventId == "event-1");
        Assert.Null(evt.DeletedAt);
    }

    [Fact]
    public async Task SetSelection_RejectsCrossUserBindingId()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        // Seed other user's binding
        var otherCal = new CalendarEntity { UserId = OtherUserId, Name = "Other", Source = "outlook" };
        db.Set<CalendarEntity>().Add(otherCal);
        await db.SaveChangesAsync();
        var otherBinding = new OutlookCalendarBindingEntity
        {
            ConnectionId = Guid.NewGuid(), PimCalendarId = otherCal.Id,
            GraphCalendarId = "other-cal", Name = "Other", IsSelected = true
        };
        db.Set<OutlookCalendarBindingEntity>().Add(otherBinding);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SetSelectionAsync(UserId, new[] { otherBinding.Id }, CancellationToken.None));
        Assert.Equal(02005, ex.ErrorCode);

        // Verify no partial updates
        Assert.True(otherBinding.IsSelected);
    }

    [Fact]
    public async Task SetSelection_EmptySelectionHidesAll()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);

        var cal = new CalendarEntity { UserId = UserId, Name = "Only", Source = "outlook", IsVisible = true };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();
        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal.Id,
            GraphCalendarId = "only-cal", Name = "Only", IsSelected = true
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        await service.SetSelectionAsync(UserId, Array.Empty<Guid>(), CancellationToken.None);

        var b = await db.Set<OutlookCalendarBindingEntity>().FirstAsync(b => b.Id == binding.Id);
        Assert.False(b.IsSelected);

        var c = await db.Set<CalendarEntity>().FirstAsync(c => c.Id == cal.Id);
        Assert.False(c.IsVisible);
    }

    [Fact]
    public async Task ListCalendarsAsync_UserIsolation()
    {
        var db = CreateDb();
        var otherConnectionId = Guid.NewGuid();
        await SeedConnectionAsync(db, UserId);
        await SeedConnectionAsync(db, OtherUserId, otherConnectionId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        // Seed a binding for other user
        var otherCal = new CalendarEntity { UserId = OtherUserId, Name = "Other", Source = "outlook" };
        db.Set<CalendarEntity>().Add(otherCal);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = otherConnectionId, PimCalendarId = otherCal.Id,
            GraphCalendarId = "other-cal", Name = "Other"
        });
        await db.SaveChangesAsync();

        // Other user has bindings
        var resultNotEmpty = await service.ListCalendarsAsync(OtherUserId, CancellationToken.None);
        Assert.NotEmpty(resultNotEmpty);

        // User has connection with no bindings -> empty list
        var empty = await service.ListCalendarsAsync(UserId, CancellationToken.None);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task ListCalendarsAsync_DtoMapping()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);

        // Seed a binding with all fields populated
        var cal = new CalendarEntity
        {
            UserId = UserId, Name = "Full DTO", Source = "outlook", Color = "#69AFE5", IsVisible = true
        };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();

        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal.Id,
            GraphCalendarId = "full-cal", Name = "Full DTO", Color = "lightBlue",
            GraphGroupId = "g1", GraphGroupName = "Group1",
            OwnerName = "Owner", OwnerAddress = "o@t",
            IsDefaultCalendar = true, CanEdit = false, IsSelected = true,
            RemoteState = "active",
            LastSyncedAt = FixedNow, LastErrorMessage = "no error"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();

        var service = CreateService(db, graph);
        var result = await service.ListCalendarsAsync(UserId, CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(binding.Id, dto.Id);
        Assert.Equal(binding.PimCalendarId, dto.PimCalendarId);
        Assert.Equal("full-cal", dto.GraphCalendarId);
        Assert.Equal("g1", dto.GroupId);
        Assert.Equal("Group1", dto.GroupName);
        Assert.Equal("Full DTO", dto.Name);
        Assert.Equal("lightBlue", dto.Color);
        Assert.Equal("Owner", dto.OwnerName);
        Assert.Equal("o@t", dto.OwnerAddress);
        Assert.True(dto.IsDefault);
        Assert.False(dto.CanEdit);
        Assert.True(dto.IsSelected);
        Assert.Equal("active", dto.RemoteState);
        Assert.Equal(FixedNow, dto.LastSyncedAt);
        Assert.Equal("no error", dto.LastError);
    }

    [Fact]
    public async Task CalendarService_GetCalendarsAsync_LeftJoinProjectsOutlookFields()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PimDbContext(options);

        var userId = Guid.NewGuid();
        // Manual calendar (no binding)
        var manualCal = new CalendarEntity
        {
            UserId = userId, Name = "Manual", Source = "manual", Color = "#3B82F6", IsVisible = true
        };
        db.Set<CalendarEntity>().Add(manualCal);
        // Seed an event for event count
        db.Set<EventEntity>().Add(new EventEntity
        {
            CalendarId = manualCal.Id,
            Uid = "manual@pim",
            Title = "Event",
            DtStart = FixedNow, DtEnd = FixedNow.AddHours(1)
        });

        // Outlook calendar with binding
        var outlookCal = new CalendarEntity
        {
            UserId = userId, Name = "Outlook", Source = "outlook", Color = "#69AFE5", IsVisible = true
        };
        db.Set<CalendarEntity>().Add(outlookCal);
        await db.SaveChangesAsync();

        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = outlookCal.Id,
            GraphCalendarId = "outlook-cal", Name = "Outlook",
            CanEdit = false
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();

        var fixedUser = new FixedCurrentUserService(userId);
        var recurrenceService = new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var calendarService = new CalendarService(db, fixedUser, recurrenceService);

        var calendars = await calendarService.GetCalendarsAsync(null, CancellationToken.None);

        Assert.Equal(2, calendars.Count);

        var manual = calendars.Single(c => c.Source == "manual");
        Assert.Null(manual.OutlookCalendarBindingId);
        Assert.True(manual.CanEdit);
        Assert.Equal(1, manual.EventCount);

        var outlook = calendars.Single(c => c.Source == "outlook");
        Assert.Equal(binding.Id, outlook.OutlookCalendarBindingId);
        Assert.False(outlook.CanEdit);
        Assert.Equal(0, outlook.EventCount);
    }

    private sealed class FixedCurrentUserService(Guid userId) : Infrastructure.Auth.ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    [Fact]
    public async Task CalendarService_GetCalendarsAsync_KindFilterTranslatesForNpgsql()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var sentinel = new InvalidOperationException("SENTINEL_REACHED_CONNECTION_OPENING");
        var interceptor = new SentinelConnectionInterceptor(sentinel);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_test;Username=test;Password=test")
            .AddInterceptors(interceptor)
            .Options;
        var db = new PimDbContext(options);
        var userId = Guid.NewGuid();
        var service = new CalendarService(
            db,
            new FixedCurrentUserService(userId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetCalendarsAsync("calendar", CancellationToken.None));

        Assert.Equal("SENTINEL_REACHED_CONNECTION_OPENING", ex.Message);
    }

    [Fact]
    public async Task CalendarService_GetCalendarsAsync_KindFilterReturnsOnlyMatchingKind()
    {
        var (service, db, userId) = CreateService();
        var taskCal = new CalendarEntity
        {
            UserId = userId, Name = "Tasks", Kind = "task", Color = "#F9D859"
        };
        var eventCal = new CalendarEntity
        {
            UserId = userId, Name = "Events", Kind = "calendar", Color = "#3B82F6"
        };
        db.Set<CalendarEntity>().AddRange(taskCal, eventCal);
        await db.SaveChangesAsync();

        var calendars = await service.GetCalendarsAsync("calendar", CancellationToken.None);

        var cal = Assert.Single(calendars);
        Assert.Equal("Events", cal.Name);
        Assert.Equal("calendar", cal.Kind);
    }

    private static (CalendarService Service, PimDbContext Db, Guid UserId) CreateService()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PimDbContext(options);
        var service = new CalendarService(
            db,
            new FixedCurrentUserService(userId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

        return (service, db, userId);
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
}
