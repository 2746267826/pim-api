using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

[Trait("Category", "Integration")]
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

    // ===== Task 4: Sync Tests =====

    private const string SyncEvent1 = """
        {
            "@odata.etag": "etag-1",
            "id": "event-1",
            "subject": "Test 1",
            "body": {"contentType": "text", "content": "desc-1"},
            "start": {"dateTime": "2026-05-01T09:00:00.0000000", "timeZone": "UTC"},
            "end": {"dateTime": "2026-05-01T10:00:00.0000000", "timeZone": "UTC"},
            "location": {"displayName": "Room A"},
            "isAllDay": false,
            "type": "singleInstance",
            "iCalUId": "event-1@outlook",
            "changeKey": "ck-1",
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC"
        }
        """;

    private const string SyncEvent2 = """
        {
            "@odata.etag": "etag-2",
            "id": "event-2",
            "subject": "Test 2",
            "body": {"contentType": "text", "content": "desc-2"},
            "start": {"dateTime": "2026-06-01T09:00:00.0000000", "timeZone": "UTC"},
            "end": {"dateTime": "2026-06-01T10:00:00.0000000", "timeZone": "UTC"},
            "location": {"displayName": "Room B"},
            "isAllDay": false,
            "type": "singleInstance",
            "iCalUId": "event-2@outlook",
            "changeKey": "ck-2",
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC"
        }
        """;

    private const string SyncEvent3 = """
        {
            "@odata.etag": "etag-3",
            "id": "event-3",
            "subject": "Test 3",
            "body": {"contentType": "text", "content": "desc-3"},
            "start": {"dateTime": "2026-07-01T09:00:00.0000000", "timeZone": "UTC"},
            "end": {"dateTime": "2026-07-01T10:00:00.0000000", "timeZone": "UTC"},
            "location": {"displayName": "Room C"},
            "isAllDay": false,
            "type": "singleInstance",
            "iCalUId": "event-3@outlook",
            "changeKey": "ck-3",
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC"
        }
        """;

    private const string SyncEventSensitive = """
        {
            "@odata.etag": "etag-sensitive",
            "id": "event-sensitive",
            "subject": "Sensitive Test",
            "body": {"contentType": "text", "content": "SECRET_BODY_MARKER_12345"},
            "start": {"dateTime": "2026-05-15T09:00:00.0000000", "timeZone": "UTC"},
            "end": {"dateTime": "2026-05-15T10:00:00.0000000", "timeZone": "UTC"},
            "location": {"displayName": "Conf Room"},
            "isAllDay": false,
            "type": "singleInstance",
            "iCalUId": "event-sensitive@outlook",
            "changeKey": "ck-sensitive",
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC"
        }
        """;

    private static string CalendarViewResponse(params string[] events)
    {
        var joined = string.Join(",", events);
        return $$"""{"value":[{{joined}}]}""";
    }

    private static string CalendarViewPageResponse(string nextLink, params string[] events)
    {
        var joined = string.Join(",", events);
        return $$"""{"value":[{{joined}}],"@odata.nextLink":"{{nextLink}}"}""";
    }

    private static string SingleEventResponse(string graphEventJson)
        => graphEventJson;

    private static string EventNotFoundResponse => """{"error":{"code":"ErrorItemNotFound","message":"Item not found"}}""";

    private sealed record CalendarViewRequest(DateTimeOffset Start, DateTimeOffset End);

    private static List<CalendarViewRequest> ExtractCalendarViewRequests(ScriptedHttpMessageHandler handler)
    {
        var result = new List<CalendarViewRequest>();
        foreach (var req in handler.Requests)
        {
            var url = req.RequestUri?.ToString();
            if (url is null || !url.Contains("/calendarView")) continue;
            var query = req.RequestUri?.Query;
            if (query is null) continue;
            var startMatch = Regex.Match(query, @"[?&]startDateTime=([^&]+)");
            var endMatch = Regex.Match(query, @"[?&]endDateTime=([^&]+)");
            if (startMatch.Success && endMatch.Success)
            {
                result.Add(new CalendarViewRequest(
                    DateTimeOffset.Parse(Uri.UnescapeDataString(startMatch.Groups[1].Value), null, System.Globalization.DateTimeStyles.AssumeUniversal),
                    DateTimeOffset.Parse(Uri.UnescapeDataString(endMatch.Groups[1].Value), null, System.Globalization.DateTimeStyles.AssumeUniversal)));
            }
        }
        return result;
    }

    private static async Task<OutlookSyncBatchEntity> LatestBatchAsync(PimDbContext db)
    {
        return await db.Set<OutlookSyncBatchEntity>()
            .OrderByDescending(b => b.StartedAt)
            .FirstAsync();
    }

    private static async Task<EventEntity?> LoadEventByOutlookIdAsync(PimDbContext db, string outlookEventId)
    {
        return await db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == outlookEventId);
    }

    private static async Task<OutlookCalendarBindingEntity> BindingByGraphIdAsync(PimDbContext db, string graphCalendarId)
    {
        return await db.Set<OutlookCalendarBindingEntity>()
            .FirstAsync(b => b.GraphCalendarId == graphCalendarId);
    }

    private static async Task SeedTwoSelectedBindingsAsync(PimDbContext db, Guid userId, Guid connectionId)
    {
        var cal1 = new CalendarEntity { UserId = userId, Name = "Cal 1", Source = "outlook", IsVisible = true };
        var cal2 = new CalendarEntity { UserId = userId, Name = "Cal 2", Source = "outlook", IsVisible = true };
        db.Set<CalendarEntity>().AddRange(cal1, cal2);
        await db.SaveChangesAsync();

        db.Set<OutlookCalendarBindingEntity>().AddRange(
            new OutlookCalendarBindingEntity
            {
                ConnectionId = connectionId, PimCalendarId = cal1.Id,
                GraphCalendarId = "cal-1", Name = "Cal 1",
                IsSelected = true, RemoteState = "active"
            },
            new OutlookCalendarBindingEntity
            {
                ConnectionId = connectionId, PimCalendarId = cal2.Id,
                GraphCalendarId = "cal-2", Name = "Cal 2",
                IsSelected = true, RemoteState = "active"
            });
        await db.SaveChangesAsync();
    }

    private sealed class EventSaveCounterInterceptor : SaveChangesInterceptor
    {
        public List<int> EventSaves { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var db = eventData.Context;
            if (db is not null)
            {
                var count = db.ChangeTracker.Entries<EventEntity>()
                    .Count(e => e.State == EntityState.Added || e.State == EntityState.Modified);
                if (count > 0)
                    EventSaves.Add(count);
            }
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private static PimDbContext CreateDbWithInterceptor(string? name = null, ISaveChangesInterceptor? interceptor = null)
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var builder = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString());
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new PimDbContext(builder.Options);
    }

    [Fact]
    public async Task SyncAsync_NoConnection_ThrowsDomainException()
    {
        var db = CreateDb();
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None));
        Assert.Equal(02005, ex.ErrorCode);
    }

    [Fact]
    public async Task SyncAsync_DisconnectedConnection_ThrowsDomainException()
    {
        var db = CreateDb();
        var connection = new OutlookConnectionEntity
        {
            Id = ConnectionId,
            UserId = UserId,
            Status = "not-connected"
        };
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None));
        Assert.Equal(02005, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SyncAsync_UnsupportedMode_ThrowsDomainException()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("unsupported-mode"), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
    }

    [Fact]
    public async Task SyncAsync_ComputesWindowOnceForWholeBatch()
    {
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero) };
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);
        var handler = new ScriptedHttpMessageHandler();
        // Two bindings, each one page with one event
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var cvRequests = ExtractCalendarViewRequests(handler);
        Assert.Equal(2, cvRequests.Count);
        var expectedStart = new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.Zero);
        var expectedEnd = new DateTimeOffset(2027, 7, 12, 0, 0, 0, TimeSpan.Zero);
        Assert.All(cvRequests, r =>
        {
            Assert.Equal(expectedStart, r.Start);
            Assert.Equal(expectedEnd, r.End);
        });
        var batch = await LatestBatchAsync(db);
        Assert.Equal(expectedStart, batch.RequestedWindowStart);
        Assert.Equal(expectedEnd, batch.RequestedWindowEnd);
    }

    [Fact]
    public async Task SyncAsync_OnlyProcessesSelectedActiveBindings()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        // 3 bindings: selected+active, selected+inactive, unselected+active
        var cal1 = new CalendarEntity { UserId = UserId, Name = "C1", Source = "outlook" };
        var cal2 = new CalendarEntity { UserId = UserId, Name = "C2", Source = "outlook" };
        var cal3 = new CalendarEntity { UserId = UserId, Name = "C3", Source = "outlook" };
        db.Set<CalendarEntity>().AddRange(cal1, cal2, cal3);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().AddRange(
            new OutlookCalendarBindingEntity { ConnectionId = ConnectionId, PimCalendarId = cal1.Id, GraphCalendarId = "active-selected", Name = "A", IsSelected = true, RemoteState = "active" },
            new OutlookCalendarBindingEntity { ConnectionId = ConnectionId, PimCalendarId = cal2.Id, GraphCalendarId = "inactive-selected", Name = "B", IsSelected = true, RemoteState = "inactive" },
            new OutlookCalendarBindingEntity { ConnectionId = ConnectionId, PimCalendarId = cal3.Id, GraphCalendarId = "active-unselected", Name = "C", IsSelected = false, RemoteState = "active" });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1)); // only one binding gets calendarView
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        var cvRequests = ExtractCalendarViewRequests(handler);
        Assert.Single(cvRequests);
        Assert.Contains("active-selected", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task SyncAsync_SavesEachCompletedPage()
    {
        var counter = new EventSaveCounterInterceptor();
        var db = CreateDbWithInterceptor("save-page-test", counter);
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        // Binding 1: 2 pages, page1 has nextLink, page2 finishes
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse("https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p1", SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        // Binding 2: 1 page
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent3));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        // Each page with events should trigger a SaveChangesAsync with event changes
        // (binding 1 page 1: event-1, binding 1 page 2: event-2, binding 2 page 1: event-3)
        Assert.Equal(3, counter.EventSaves.Count);
        Assert.All(counter.EventSaves, c => Assert.Equal(1, c));
    }

    [Fact]
    public async Task SyncAsync_IsIdempotentUpsert()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "event-1");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1, SyncEvent2));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var events = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId)
            .ToListAsync();
        var event1 = events.Single(e => e.OutlookEventId == "event-1");
        var event2 = events.Single(e => e.OutlookEventId == "event-2");
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events.Count(e => e.OutlookEventId == "event-1")); // not duplicated
        Assert.Equal(1, events.Count(e => e.OutlookEventId == "event-2"));
    }

    private static async Task<(Guid CalId, Guid BindingId)> SeedSingleBindingAsync(PimDbContext db, Guid userId, Guid connectionId, string graphCalendarId)
    {
        var cal = new CalendarEntity { UserId = userId, Name = graphCalendarId, Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();
        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = connectionId, PimCalendarId = cal.Id,
            GraphCalendarId = graphCalendarId, Name = graphCalendarId,
            IsSelected = true, RemoteState = "active"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();
        return (cal.Id, binding.Id);
    }

    private static async Task<(Guid CalId, Guid BindingId)> SeedBindingWithFixedIdsAsync(PimDbContext db, Guid userId, Guid connectionId, string graphCalendarId, Guid calId, Guid bindingId)
    {
        var cal = new CalendarEntity { Id = calId, UserId = userId, Name = graphCalendarId, Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal);
        var binding = new OutlookCalendarBindingEntity
        {
            Id = bindingId, ConnectionId = connectionId, PimCalendarId = calId,
            GraphCalendarId = graphCalendarId, Name = graphCalendarId,
            IsSelected = true, RemoteState = "active"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();
        return (cal.Id, binding.Id);
    }

    [Fact]
    public async Task SyncAsync_ImmutableIdMove_SourceProcessedFirst()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        // Use fixed IDs to ensure cal-1 (lower GUID) is processed first by OrderBy(Id)
        var sourceBindingId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var sourceCalId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var targetBindingId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var targetCalId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        await SeedBindingWithFixedIdsAsync(db, UserId, ConnectionId, "cal-source", sourceCalId, sourceBindingId);
        await SeedBindingWithFixedIdsAsync(db, UserId, ConnectionId, "cal-target", targetCalId, targetBindingId);
        // Event starts in source
        await SeedEventAsync(db, sourceCalId, sourceBindingId, "event-move");

        var handler = new ScriptedHttpMessageHandler();
        // Source calendarView: empty (event moved away)
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse());
        // Source GetEventAsync missing verification -> 404
        handler.Enqueue(HttpStatusCode.NotFound, EventNotFoundResponse);
        // Target calendarView: has the event
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1.Replace("\"event-1\"", "\"event-move\"")));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var events = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookEventId == "event-move")
            .ToListAsync();
        Assert.Single(events);
        var evt = events[0];
        // Moved to target
        Assert.Equal(targetCalId, evt.CalendarId);
        Assert.Equal(targetBindingId, evt.OutlookCalendarBindingId);
        Assert.Null(evt.DeletedAt);
    }

    [Fact]
    public async Task SyncAsync_ImmutableIdMove_TargetProcessedFirst()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId1, bindingId1) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        var (calId2, bindingId2) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-2");
        // Event starts in cal-1
        await SeedEventAsync(db, calId1, bindingId1, "event-move");

        var handler = new ScriptedHttpMessageHandler();
        // cal-2 processed FIRST (alphabetically by GraphCalendarId)... actually order is by Id.
        // Let me control order by fixed IDs
        // Actually, let me redo with controlled IDs
        var db2 = CreateDb();
        await SeedConnectionAsync(db2, UserId);
        // binding A (processed first because lower Id): target
        var targetBindingId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var targetCalId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        await SeedBindingWithFixedIdsAsync(db2, UserId, ConnectionId, "binding-a", targetCalId, targetBindingId);
        // binding B (processed second): source
        var sourceBindingId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var sourceCalId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        await SeedBindingWithFixedIdsAsync(db2, UserId, ConnectionId, "binding-b", sourceCalId, sourceBindingId);
        // Event starts in B (source)
        await SeedEventAsync(db2, sourceCalId, sourceBindingId, "event-move");

        var handler2 = new ScriptedHttpMessageHandler();
        // binding-a (target) calendarView: has the event first
        handler2.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1.Replace("\"event-1\"", "\"event-move\"")));
        // binding-b (source) calendarView: empty
        handler2.Enqueue(HttpStatusCode.OK, CalendarViewResponse());
        // GetEventAsync for binding-b missing verification -> 404
        handler2.Enqueue(HttpStatusCode.NotFound, EventNotFoundResponse);
        var graph2 = CreateGraphClient(handler2);
        var time2 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service2 = CreateService(db2, graph2, time2);

        await service2.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var eventsAfter = await db2.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookEventId == "event-move")
            .ToListAsync();
        Assert.Single(eventsAfter);
        var moved = eventsAfter[0];
        // Moved to binding-a (target)
        Assert.Equal(targetCalId, moved.CalendarId);
        Assert.Equal(targetBindingId, moved.OutlookCalendarBindingId);
        Assert.Null(moved.DeletedAt);
    }

    [Fact]
    public async Task SyncAsync_RestoresSoftDeletedImmutableEvent()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        // Event was soft-deleted
        var evt = new EventEntity
        {
            CalendarId = calId,
            Uid = "event-1@outlook",
            Title = "Deleted Event",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = "event-1",
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = ConnectionId,
            DeletedAt = FixedNow.AddDays(-1),
            DeletedByOperationId = Guid.NewGuid(),
            DeletedByOperationKind = "outlook-sync"
        };
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var restored = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstAsync(e => e.OutlookEventId == "event-1");
        Assert.Null(restored.DeletedAt);
        Assert.Null(restored.DeletedByOperationId);
        Assert.Null(restored.DeletedByOperationKind);
    }

    [Fact]
    public async Task MissingEvent_IsDeletedOnlyAfterCompletePagingAndGet404()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");
        // Seed a second event that should NOT be deleted (still in window)
        var evt2 = new EventEntity
        {
            CalendarId = calId, Uid = "keep@outlook", Title = "Keep",
            DtStart = FixedNow.AddDays(10), DtEnd = FixedNow.AddDays(10).AddHours(1),
            Source = "outlook", OutlookEventId = "keep-event",
            OutlookCalendarBindingId = bindingId, OutlookConnectionId = ConnectionId
        };
        db.Set<EventEntity>().Add(evt2);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        // Two complete pages
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse("https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=s1", SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(
            SyncEvent2.Replace("\"event-2\"", "\"keep-event\"")));
        // GetEventAsync for missing-event -> 404
        handler.Enqueue(HttpStatusCode.NotFound, EventNotFoundResponse);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var missing = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(missing!.DeletedAt);
        // keep-event should NOT be deleted (it's in the calendarView pages)
        var keep = await LoadEventByOutlookIdAsync(db, "keep-event");
        Assert.Null(keep!.DeletedAt);
    }

    [Fact]
    public async Task SyncAsync_MissingEventStillExistsIsUpdated()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");

        var handler = new ScriptedHttpMessageHandler();
        // CalendarView: does NOT include missing-event
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        // GetEventAsync -> event still exists, update it (response must have id="missing-event")
        handler.Enqueue(HttpStatusCode.OK, SingleEventResponse(SyncEvent1.Replace("\"event-1\"", "\"missing-event\"")));
        var graph = CreateGraphClient(handler);
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        var time = new StubTimeProvider { UtcNowValue = now };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var evt = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);
        // Should have been updated with fresh data
        Assert.Equal("Test 1", evt.Title);
    }

    [Fact]
    public async Task FailedPage_NeverInfersDeletion()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");

        var handler = new ScriptedHttpMessageHandler();
        // Page 1: success
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse("https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2", SyncEvent1));
        // Page 2: fail with 503 - need 3 attempts total
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var evt = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.Null(evt!.DeletedAt); // NOT deleted because paging didn't complete

        var batch = await LatestBatchAsync(db);
        Assert.Equal("partial", batch.Status);
    }

    [Fact]
    public async Task SyncAsync_SingleCalendarFailureContinuesWithNextCalendar()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        // Binding 1: fails with 503 (3 attempts)
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        // Binding 2: succeeds
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);
        // Event from binding 2 should still be saved
        var evt = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);
    }

    [Fact]
    public async Task SyncAsync_ReauthenticationUpdatesConnectionAndStopsRemainingCalendars()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        // Re-authentication scenario: 401 triggers force refresh -> still 401 -> exception
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("failed", response.Status);

        var connection = await db.Set<OutlookConnectionEntity>().FirstAsync(c => c.Id == ConnectionId);
        Assert.Equal("reauth-required", connection.Status);
        Assert.Equal("interaction-required", connection.TokenHealth);
        Assert.NotNull(connection.LastError);
    }

    [Fact]
    public async Task SyncAsync_CallerCancellationPersistsCanceledBatch()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        // Block first request, cancel while blocked
        handler.Enqueue(request =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), cts.Token);

        Assert.Equal("canceled", response.Status);
        var batch = await LatestBatchAsync(db);
        Assert.Equal("canceled", batch.Status);
        Assert.NotNull(batch.FinishedAt);
    }

    [Fact]
    public async Task SyncAsync_BusyManualCallReturnsRunningBatchWithoutSecondGraphRequest()
    {
        var dbName = "concurrent-manual-" + Guid.NewGuid();
        var sharedOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        using (var seedCtx = new PimDbContext(sharedOptions))
        {
            await SeedConnectionAsync(seedCtx, UserId);
            await SeedTwoSelectedBindingsAsync(seedCtx, UserId, ConnectionId);
        }

        var batchCreated = new TaskCompletionSource();
        var canProceed = new TaskCompletionSource();
        var handler1 = new ScriptedHttpMessageHandler();
        handler1.Enqueue(request =>
        {
            batchCreated.TrySetResult();
            canProceed.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CalendarViewResponse(SyncEvent1), System.Text.Encoding.UTF8, "application/json") };
        });
        handler1.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));

        OutlookSyncBatchResponse? response1 = null;
        var thread1 = new Thread(() =>
        {
            using var ctx1 = new PimDbContext(sharedOptions);
            var graph1 = CreateGraphClient(handler1);
            var time1 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
            var service1 = CreateService(ctx1, graph1, time1);
            response1 = service1.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None)
                .GetAwaiter().GetResult();
        });
        thread1.Start();

        await batchCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            using var ctx2 = new PimDbContext(sharedOptions);
            var handler2 = new ScriptedHttpMessageHandler();
            var graph2 = CreateGraphClient(handler2);
            var time2 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
            var service2 = CreateService(ctx2, graph2, time2);

            var response2 = await service2.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

            Assert.Equal("running", response2.Status);
            Assert.Empty(handler2.Requests);
        }
        finally
        {
            canProceed.TrySetResult();
            thread1.Join(TimeSpan.FromSeconds(10));
        }

        Assert.False(thread1.IsAlive);
        Assert.NotNull(response1);
        Assert.Equal("completed", response1.Status);
    }

    [Fact]
    public async Task SyncAsync_BusyAutomaticCallStartsNoDuplicateBatch()
    {
        var dbName = "concurrent-auto-" + Guid.NewGuid();
        var sharedOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        using (var seedCtx = new PimDbContext(sharedOptions))
        {
            await SeedConnectionAsync(seedCtx, UserId);
            await SeedTwoSelectedBindingsAsync(seedCtx, UserId, ConnectionId);
        }

        var batchCreated = new TaskCompletionSource();
        var canProceed = new TaskCompletionSource();
        var handler1 = new ScriptedHttpMessageHandler();
        handler1.Enqueue(request =>
        {
            batchCreated.TrySetResult();
            canProceed.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CalendarViewResponse(SyncEvent1), System.Text.Encoding.UTF8, "application/json") };
        });
        handler1.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));

        OutlookSyncBatchResponse? response1 = null;
        var thread1 = new Thread(() =>
        {
            using var ctx1 = new PimDbContext(sharedOptions);
            var graph1 = CreateGraphClient(handler1);
            var time1 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
            var service1 = CreateService(ctx1, graph1, time1);
            response1 = service1.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None)
                .GetAwaiter().GetResult();
        });
        thread1.Start();

        await batchCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var ctx2 = new PimDbContext(sharedOptions);
        var handler2 = new ScriptedHttpMessageHandler();
        var graph2 = CreateGraphClient(handler2);
        var time2 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service2 = CreateService(ctx2, graph2, time2);

        var response2 = await service2.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("running", response2.Status);
        Assert.Empty(handler2.Requests);

        using var countCtx = new PimDbContext(sharedOptions);
        var batches = await countCtx.Set<OutlookSyncBatchEntity>().ToListAsync();
        var nonInterrupted = batches.Where(b => b.Status != "interrupted").ToList();
        Assert.Single(nonInterrupted);

        canProceed.TrySetResult();
        thread1.Join(TimeSpan.FromSeconds(10));
        Assert.NotNull(response1);
    }

    [Fact]
    public async Task SyncAsync_MarksOldRunningBatchInterrupted()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        // Old running batch (non-writeback)
        var oldBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = ConnectionId,
            Status = "running",
            Mode = "normal",
            StartedAt = FixedNow.AddHours(-2)
        };
        db.Set<OutlookSyncBatchEntity>().Add(oldBatch);
        await db.SaveChangesAsync();
        var oldBatchId = oldBatch.Id;

        // Old writeback running batch (should NOT be interrupted)
        var writebackBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = ConnectionId,
            Status = "running",
            Mode = "writeback",
            StartedAt = FixedNow.AddHours(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(writebackBatch);
        await db.SaveChangesAsync();
        var writebackBatchId = writebackBatch.Id;

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var interrupted = await db.Set<OutlookSyncBatchEntity>().FirstAsync(b => b.Id == oldBatchId);
        Assert.Equal("interrupted", interrupted.Status);
        Assert.NotNull(interrupted.FinishedAt);

        var writebackReloaded = await db.Set<OutlookSyncBatchEntity>().FirstAsync(b => b.Id == writebackBatchId);
        Assert.Equal("running", writebackReloaded.Status); // Not interrupted
        Assert.Null(writebackReloaded.FinishedAt);
    }

    [Fact]
    public async Task SyncAsync_HistoryContainsOnlySafeIdTitleSummariesAndConfirmationZero()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        // Pre-seed an event that will be missing (will trigger GET)
        await SeedEventAsync(db, calId, bindingId, "event-sensitive");

        var handler = new ScriptedHttpMessageHandler();
        // CalendarView: includes event-1, NOT missing-event-sensitive
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        // GetEventAsync for event-sensitive -> still exists
        handler.Enqueue(HttpStatusCode.OK, SingleEventResponse(SyncEventSensitive));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var batch = await LatestBatchAsync(db);

        // ConfirmationCount must be 0
        Assert.Equal(0, batch.ConfirmationCount);

        // PerCalendarJson should not contain sensitive body content
        Assert.DoesNotContain("SECRET_BODY_MARKER_12345", batch.PerCalendarJson);

        // StepsJson should not contain sensitive body content
        Assert.DoesNotContain("SECRET_BODY_MARKER_12345", batch.StepsJson);

        // ErrorsJson should not contain sensitive body content
        Assert.DoesNotContain("SECRET_BODY_MARKER_12345", batch.ErrorsJson);

        // ErrorSummary should not contain sensitive body content
        if (batch.ErrorSummary is not null)
            Assert.DoesNotContain("SECRET_BODY_MARKER_12345", batch.ErrorSummary);

        // PerCalendarJson should contain event ID and title
        Assert.Contains("event-sensitive", batch.PerCalendarJson);
        Assert.Contains("Sensitive Test", batch.PerCalendarJson);
    }

    private sealed class CaptureLogger : ILogger<OutlookCalendarSyncService>
    {
        public List<string> Messages { get; } = new();
        public List<Exception?> CapturedExceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            Messages.Add(msg);
            CapturedExceptions.Add(exception);
        }
    }

    private sealed class FailOnNthEventSaveInterceptor : SaveChangesInterceptor
    {
        private int _nthEventSave;
        private int _eventSaveCount;

        public void Arm(int nthEventSave)
        {
            if (nthEventSave < 1)
                throw new ArgumentOutOfRangeException(nameof(nthEventSave));

            _nthEventSave = nthEventSave;
            _eventSaveCount = 0;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var db = eventData.Context;
            if (db is not null)
            {
                var count = db.ChangeTracker.Entries<EventEntity>()
                    .Count(e => e.State == EntityState.Added || e.State == EntityState.Modified);
                if (_nthEventSave > 0 && count > 0)
                {
                    _eventSaveCount++;
                    if (_eventSaveCount == _nthEventSave)
                    {
                        _nthEventSave = 0;
                        throw new InvalidOperationException("Simulated event save failure");
                    }
                }
            }
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task SyncAsync_LoggerDoesNotContainSensitiveData()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "event-sensitive");

        var logger = new CaptureLogger();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        // GetEventAsync -> still exists with sensitive body
        handler.Enqueue(HttpStatusCode.OK, SingleEventResponse(SyncEventSensitive));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = new OutlookCalendarSyncService(db, graph, time, logger);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain("SECRET_BODY_MARKER_12345", msg);
            Assert.DoesNotContain("test-access-token", msg);
        }
    }

    // ===== Phase 1: A. 时间、空 binding 与锁 =====

    private sealed class CountingTimeProvider : TimeProvider
    {
        private readonly TimeProvider _inner;
        public int CallCount { get; private set; }
        public CountingTimeProvider(TimeProvider inner) { _inner = inner; }
        public override DateTimeOffset GetUtcNow() { CallCount++; return _inner.GetUtcNow(); }
    }

    [Fact]
    public async Task SyncAsync_ReadsTimeProviderOnceAndReusesTimestamp()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var stub = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var counting = new CountingTimeProvider(stub);
        var service = CreateService(db, graph, counting);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal(1, counting.CallCount);
        var batch = await LatestBatchAsync(db);
        var expectedNow = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNow, batch.StartedAt);
        Assert.Equal(expectedNow, batch.FinishedAt);
        var steps = string.IsNullOrEmpty(batch.StepsJson) || batch.StepsJson == "[]"
            ? Array.Empty<OutlookSyncStep>()
            : JsonSerializer.Deserialize<OutlookSyncStep[]>(batch.StepsJson)!;
        Assert.All(steps, s => Assert.Equal(expectedNow, s.At));
    }

    [Fact]
    public async Task SyncAsync_NoSelectedBindings_CompletesWithoutGraph()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var cal = new CalendarEntity { UserId = UserId, Name = "Cal", Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal.Id,
            GraphCalendarId = "cal-1", Name = "Cal",
            IsSelected = false, RemoteState = "active"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(handler.Requests);
        var expectedStart = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        var expectedEnd = new DateTimeOffset(2027, 7, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedStart, response.RequestedWindowStart);
        Assert.Equal(expectedEnd, response.RequestedWindowEnd);
        Assert.Equal(0, response.ConfirmationCount);

        var batch = await LatestBatchAsync(db);
        Assert.Equal(expectedStart, batch.RequestedWindowStart);
        Assert.Equal(expectedEnd, batch.RequestedWindowEnd);
        Assert.Equal("[]", batch.RequestedCalendarIdsJson);
        Assert.Equal(0, batch.ConfirmationCount);
    }

    [Fact]
    public async Task SyncAsync_NoSelectedBindingsStillHonorsBusyLock()
    {
        var dbName = "no-binding-busy-" + Guid.NewGuid();
        var sharedOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        using (var seedCtx = new PimDbContext(sharedOptions))
        {
            await SeedConnectionAsync(seedCtx, UserId);
            var cal = new CalendarEntity { UserId = UserId, Name = "C1", Source = "outlook" };
            seedCtx.Set<CalendarEntity>().Add(cal);
            await seedCtx.SaveChangesAsync();
            seedCtx.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
            {
                ConnectionId = ConnectionId, PimCalendarId = cal.Id,
                GraphCalendarId = "cal-1", Name = "C1",
                IsSelected = true, RemoteState = "active"
            });
            await seedCtx.SaveChangesAsync();
        }

        var batchCreated = new TaskCompletionSource();
        var canProceed = new TaskCompletionSource();
        var handler1 = new ScriptedHttpMessageHandler();
        handler1.Enqueue(request =>
        {
            batchCreated.TrySetResult();
            canProceed.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CalendarViewResponse(SyncEvent1), System.Text.Encoding.UTF8, "application/json")
            };
        });

        OutlookSyncBatchResponse? response1 = null;
        var thread1 = new Thread(() =>
        {
            using var ctx1 = new PimDbContext(sharedOptions);
            var graph1 = CreateGraphClient(handler1);
            var time1 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
            var service1 = CreateService(ctx1, graph1, time1);
            response1 = service1.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None)
                .GetAwaiter().GetResult();
        });
        thread1.Start();
        await batchCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Now deselect all bindings in DB
        using (var ctxMod = new PimDbContext(sharedOptions))
        {
            var bindings = await ctxMod.Set<OutlookCalendarBindingEntity>().ToListAsync();
            foreach (var b in bindings) b.IsSelected = false;
            await ctxMod.SaveChangesAsync();
        }

        using var ctx2 = new PimDbContext(sharedOptions);
        var handler2 = new ScriptedHttpMessageHandler();
        var graph2 = CreateGraphClient(handler2);
        var time2 = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service2 = CreateService(ctx2, graph2, time2);

        var response2 = await service2.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("running", response2.Status);
        Assert.Empty(handler2.Requests);

        canProceed.TrySetResult();
        thread1.Join(TimeSpan.FromSeconds(10));
        Assert.NotNull(response1);
    }

    [Fact]
    public async Task SyncAsync_BusyLockWithoutVisibleRunningBatchFailsFast()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        var connectionLocksField = typeof(OutlookCalendarSyncService)
            .GetField("ConnectionLocks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var locksDict = (ConcurrentDictionary<Guid, SemaphoreSlim>)connectionLocksField.GetValue(null)!;
        var semaphore = locksDict.GetOrAdd(ConnectionId, _ => new SemaphoreSlim(1, 1));

        bool acquired = false;
        try
        {
            acquired = await semaphore.WaitAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(acquired, "Should have acquired the lock in test setup");

            var handler = new ScriptedHttpMessageHandler();
            var graph = CreateGraphClient(handler);
            var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
            var service = CreateService(db, graph, time);

            var syncTask = service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);
            var completedTask = await Task.WhenAny(syncTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Equal(completedTask, syncTask);

            var ex = await Assert.ThrowsAsync<DomainException>(() => syncTask);
            Assert.Contains("running", ex.Message);

            var batches = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
            Assert.DoesNotContain(batches, b => b.Status == "running");
        }
        finally
        {
            if (acquired) semaphore.Release();
            locksDict.TryRemove(ConnectionId, out _);
        }
    }

    // ===== Phase 1: B. 逐日历状态和进度 =====

    [Fact]
    public async Task SyncAsync_PerCalendarJsonUsesLocalCounts()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2, SyncEvent3));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var batch = await LatestBatchAsync(db);
        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var cal1 = items[0];
        Assert.Equal(1, cal1.GetProperty("readCount").GetInt32());
        Assert.Equal(1, cal1.GetProperty("createdCount").GetInt32());
        Assert.Equal(0, cal1.GetProperty("updatedCount").GetInt32());
        Assert.True(cal1.TryGetProperty("deletedCount", out _));

        var cal2 = items[1];
        Assert.Equal(2, cal2.GetProperty("readCount").GetInt32());
        Assert.Equal(2, cal2.GetProperty("createdCount").GetInt32());
        Assert.Equal(0, cal2.GetProperty("updatedCount").GetInt32());
        Assert.True(cal2.TryGetProperty("deletedCount", out _));
    }

    [Fact]
    public async Task SyncAsync_PerCalendarJsonMarksFailedAndCompletedBindings()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);

        using var doc = JsonDocument.Parse(response.PerCalendarJson!);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var cal1 = items[0];
        Assert.Equal("failed", cal1.GetProperty("status").GetString());
        var failCount1 = cal1.TryGetProperty("failureCount", out var fc1) ? fc1.GetInt32() : 0;
        Assert.Equal(1, failCount1);

        var cal2 = items[1];
        Assert.Equal("completed", cal2.GetProperty("status").GetString());
        var failCount2 = cal2.TryGetProperty("failureCount", out var fc2) ? fc2.GetInt32() : 0;
        Assert.Equal(0, failCount2);
    }

    [Fact]
    public async Task SyncAsync_PageOneChangesRemainInPartialCalendarHistory()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse(
            "https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2", SyncEvent1));
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);

        using var doc = JsonDocument.Parse(response.PerCalendarJson!);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        var cal = items[0];
        Assert.Equal("partial", cal.GetProperty("status").GetString());
        Assert.Equal(1, cal.GetProperty("readCount").GetInt32());

        var changes = cal.GetProperty("changes").EnumerateArray().ToList();
        Assert.Single(changes);
        Assert.Equal("event-1", changes[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task SyncAsync_EmptySuccessfulPageThenFailureIsPartial()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse(
            "https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2"));
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("partial", batch.Status);
    }

    // ===== Phase 1: C. connection 和 missing verification =====

    [Fact]
    public async Task SyncAsync_AllCalendarsFailBeforeAnyPageSetsFailed()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var oldLastSyncedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var conn = await db.Set<OutlookConnectionEntity>().FirstAsync(c => c.Id == ConnectionId);
        conn.LastSyncedAt = oldLastSyncedAt;
        conn.LastError = "old error";
        await db.SaveChangesAsync();

        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("failed", response.Status);

        var reloadedConn = await db.Set<OutlookConnectionEntity>().FirstAsync(c => c.Id == ConnectionId);
        Assert.Equal(oldLastSyncedAt, reloadedConn.LastSyncedAt);
        Assert.NotNull(reloadedConn.LastError);
        Assert.NotEqual("old error", reloadedConn.LastError);
        Assert.Contains("失败", reloadedConn.LastError);
    }

    [Fact]
    public async Task SyncAsync_MissingEventPermissionErrorIsPreserved()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"ErrorAccessDenied"}}""");
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var evt = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("partial", batch.Status);
        Assert.Equal(1, batch.FailureCount);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        var cal = items[0];
        Assert.Equal("partial", cal.GetProperty("status").GetString());
        var failures = cal.GetProperty("failures").EnumerateArray().ToList();
        Assert.Single(failures);
        var failure = failures[0];
        Assert.Equal("missing-event", failure.GetProperty("eventId").GetString());
        Assert.Equal("Test Event", failure.GetProperty("title").GetString());
        Assert.Equal("403", failure.GetProperty("code").GetString());
        Assert.Contains(response.Steps,
            step => step.Name == bindingId.ToString() && step.Status == "partial");

        var errorsJson = batch.ErrorsJson;
        Assert.Contains("missing-event", errorsJson);
        Assert.DoesNotContain("raw body content", errorsJson);
    }

    [Fact]
    public async Task SyncAsync_MissingEventNetworkErrorIsPreservedAndRecorded()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        handler.EnqueueException(new HttpRequestException("Network failure"));
        handler.EnqueueException(new HttpRequestException("Network failure"));
        handler.EnqueueException(new HttpRequestException("Network failure"));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var evt = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("partial", batch.Status);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        var cal = items[0];
        Assert.Equal("partial", cal.GetProperty("status").GetString());
        var failures = cal.GetProperty("failures").EnumerateArray().ToList();
        Assert.Single(failures);
        var failure = failures[0];
        Assert.Equal("missing-event", failure.GetProperty("eventId").GetString());
        Assert.Equal("Test Event", failure.GetProperty("title").GetString());
        Assert.Equal("network", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncAsync_MissingEventTimeoutIsPreservedAndRecorded()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        handler.EnqueueTimeout();
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        var evt = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("partial", batch.Status);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        var cal = items[0];
        Assert.Equal("partial", cal.GetProperty("status").GetString());
        var failures = cal.GetProperty("failures").EnumerateArray().ToList();
        Assert.Single(failures);
        var failure = failures[0];
        Assert.Equal("missing-event", failure.GetProperty("eventId").GetString());
        Assert.Equal("Test Event", failure.GetProperty("title").GetString());
        Assert.Equal("timeout", failure.GetProperty("code").GetString());

        var errorsJson = batch.ErrorsJson;
        Assert.Contains("missing-event", errorsJson);
        Assert.DoesNotContain("OperationCanceledException", errorsJson);
        Assert.Contains("timeout", errorsJson);
    }

    [Fact]
    public async Task SyncAsync_UnknownExceptionDoesNotLeakExceptionToLog()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await db.Set<CalendarEntity>().AddAsync(new CalendarEntity
        {
            UserId = UserId, Name = "test", Source = "outlook"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => throw new InvalidOperationException("SECRET_EXCEPTION_MARKER_invalid_op"));
        var logger = new CaptureLogger();
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = new OutlookCalendarSyncService(db, graph, time, logger);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("failed", response.Status);

        var unknownBatch = await LatestBatchAsync(db);
        using (var unknownDoc = JsonDocument.Parse(response.PerCalendarJson!))
        {
            var unknownCal = Assert.Single(unknownDoc.RootElement.EnumerateArray().ToList());
            var unknownFailure = Assert.Single(unknownCal.GetProperty("failures").EnumerateArray().ToList());
            Assert.Equal("unknown", unknownFailure.GetProperty("code").GetString());
            Assert.Equal("未知错误", unknownFailure.GetProperty("message").GetString());
            Assert.DoesNotContain("SECRET_EXCEPTION_MARKER", response.PerCalendarJson!);
            Assert.DoesNotContain("SECRET_EXCEPTION_MARKER", unknownBatch.ErrorsJson);
        }

        var bindingAfterUnknown = await db.Set<OutlookCalendarBindingEntity>().SingleAsync(b => b.Id == bindingId);
        Assert.Equal("unknown", bindingAfterUnknown.LastErrorCode);
        Assert.Equal("未知错误", bindingAfterUnknown.LastErrorMessage);

        foreach (var msg in logger.Messages)
            Assert.DoesNotContain("SECRET_EXCEPTION_MARKER", msg);
        foreach (var ex in logger.CapturedExceptions)
        {
            if (ex is not null)
                Assert.DoesNotContain("SECRET_EXCEPTION_MARKER", ex.ToString());
        }
    }

    [Fact]
    public async Task SyncAsync_InvalidNextLink_MapsToSafeFailureCodeAndMessage()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse(
            "https://evil.example.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=SECRET_NEXT_LINK_TOKEN",
            SyncEvent1));
        var logger = new CaptureLogger();
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = new OutlookCalendarSyncService(db, graph, time, logger);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);
        var batch = await LatestBatchAsync(db);
        Assert.DoesNotContain("SECRET_NEXT_LINK_TOKEN", response.PerCalendarJson!);
        Assert.DoesNotContain("SECRET_NEXT_LINK_TOKEN", batch.ErrorsJson);
        Assert.DoesNotContain("Invalid nextLink rejected", response.PerCalendarJson!);
        Assert.DoesNotContain("evil.example.com", response.PerCalendarJson!);
        Assert.DoesNotContain("Invalid nextLink rejected", batch.ErrorsJson);
        Assert.DoesNotContain("evil.example.com", batch.ErrorsJson);

        using var doc = JsonDocument.Parse(response.PerCalendarJson!);
        var cal = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal("partial", cal.GetProperty("status").GetString());
        Assert.Equal(1, cal.GetProperty("readCount").GetInt32());
        var failure = Assert.Single(cal.GetProperty("failures").EnumerateArray().ToList());
        Assert.Equal("invalid-next-link", failure.GetProperty("code").GetString());
        Assert.Equal("分页链接校验失败", failure.GetProperty("message").GetString());

        using var errorsDoc = JsonDocument.Parse(batch.ErrorsJson);
        var error = Assert.Single(errorsDoc.RootElement.EnumerateArray().ToList());
        Assert.Equal("invalid-next-link", error.GetProperty("code").GetString());
        Assert.Equal("分页链接校验失败", error.GetProperty("message").GetString());

        var binding = await db.Set<OutlookCalendarBindingEntity>().SingleAsync(b => b.Id == bindingId);
        Assert.Equal("invalid-next-link", binding.LastErrorCode);
        Assert.Equal("分页链接校验失败", binding.LastErrorMessage);

        Assert.Contains(logger.Messages, m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("invalid-next-link", StringComparison.Ordinal));
        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain("SECRET_NEXT_LINK_TOKEN", msg);
            Assert.DoesNotContain("evil.example.com", msg);
        }
    }

    [Fact]
    public async Task SyncAsync_FailedSecondPageSaveDoesNotCommitHalfPage()
    {
        var interceptor = new FailOnNthEventSaveInterceptor();
        var db = CreateDbWithInterceptor("rollback-test-" + Guid.NewGuid(), interceptor);
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        var evt1 = new EventEntity
        {
            CalendarId = calId, Uid = "event-1@pim", Title = "Event 1",
            DtStart = FixedNow, DtEnd = FixedNow.AddHours(1),
            Source = "outlook", OutlookEventId = "event-1",
            OutlookCalendarBindingId = bindingId, OutlookConnectionId = ConnectionId,
            LastSeenSyncGeneration = Guid.NewGuid()
        };
        var evt2 = new EventEntity
        {
            CalendarId = calId, Uid = "event-2@pim", Title = "Original Event 2",
            DtStart = FixedNow, DtEnd = FixedNow.AddHours(1),
            Source = "outlook", OutlookEventId = "event-2",
            OutlookCalendarBindingId = bindingId, OutlookConnectionId = ConnectionId,
            LastSeenSyncGeneration = Guid.NewGuid()
        };
        db.Set<EventEntity>().AddRange(evt1, evt2);
        await db.SaveChangesAsync();
        interceptor.Arm(2);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse(
            "https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2", SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2, SyncEvent3));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);

        var page1 = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "event-1");
        Assert.NotNull(page1);
        Assert.Equal("Test 1", page1.Title);

        var page2Modified = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "event-2");
        Assert.NotNull(page2Modified);
        Assert.Equal("Original Event 2", page2Modified.Title);

        var page2Added = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "event-3");
        Assert.Null(page2Added);

        using var doc = JsonDocument.Parse(response.PerCalendarJson!);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        var cal = items[0];
        Assert.Equal("partial", cal.GetProperty("status").GetString());
        Assert.Equal(1, cal.GetProperty("readCount").GetInt32());
        Assert.Equal(0, cal.GetProperty("createdCount").GetInt32());
        Assert.Equal(1, cal.GetProperty("updatedCount").GetInt32());
    }

    [Fact]
    public async Task SyncAsync_FailedMissingVerificationSaveDoesNotCommitDeletion()
    {
        var interceptor = new FailOnNthEventSaveInterceptor();
        var db = CreateDbWithInterceptor("missing-rollback-" + Guid.NewGuid(), interceptor);
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "missing-event");
        interceptor.Arm(2);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        handler.Enqueue(HttpStatusCode.NotFound, EventNotFoundResponse);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);
        var missing = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(missing);
        Assert.Null(missing.DeletedAt);

        using var doc = JsonDocument.Parse(response.PerCalendarJson!);
        var calendar = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal("partial", calendar.GetProperty("status").GetString());
        Assert.Equal(0, calendar.GetProperty("deletedCount").GetInt32());
        Assert.DoesNotContain(calendar.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("action").GetString() == "deleted");
    }

    [Fact]
    public async Task SyncAsync_MissingEventGetDeletedAndDeletedCountIsAccurate()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "event-1");
        await SeedEventAsync(db, calId, bindingId, "event-2");

        var handler = new ScriptedHttpMessageHandler();
        // calendarView: only event-2 appears -> event-1 is "missing"
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse(
            "https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2", SyncEvent2));
        // second page: no more events (no nextLink -> pagination ends)
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        // GetEventAsync for event-1 -> 404 not found (deleted remotely)
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"code":"ErrorItemNotFound"}}""");
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("completed", batch.Status);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        var cal = items[0];
        Assert.Equal("completed", cal.GetProperty("status").GetString());
        Assert.Equal(1, cal.GetProperty("deletedCount").GetInt32());

        var changes = cal.GetProperty("changes").EnumerateArray().ToList();
        var deletedChanges = changes.Where(c => c.GetProperty("action").GetString() == "deleted").ToList();
        Assert.Single(deletedChanges);
        Assert.Equal("event-1", deletedChanges[0].GetProperty("id").GetString());
    }

    // ===== Task 5A: Deep Sync Modes =====

    [Fact]
    public async Task FullResources_OnlyUpsertsAndNeverDeletesMissingEvents()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedEventAsync(db, calId, bindingId, "not-returned");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest(
            "full-resources",
            RangeStart: FixedNow,
            RangeEnd: FixedNow.AddDays(30)), CancellationToken.None);

        Assert.Equal("full-resources", response.Mode);
        Assert.Null(response.RequestedWindowStart);
        Assert.Null(response.RequestedWindowEnd);
        Assert.Equal(0, response.ConfirmationCount);

        var cvRequests = ExtractCalendarViewRequests(handler);
        Assert.Empty(cvRequests);

        Assert.Single(handler.Requests);
        Assert.Contains("/events", handler.Requests[0].RequestUri!.ToString());

        var evt1 = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(evt1);
        Assert.Equal("Test 1", evt1!.Title);

        var notReturned = await LoadEventByOutlookIdAsync(db, "not-returned");
        Assert.NotNull(notReturned);
        Assert.Null(notReturned!.DeletedAt);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("full-resources", batch.Mode);
        Assert.Null(batch.RequestedWindowStart);
        Assert.Null(batch.RequestedWindowEnd);
        Assert.Equal(0, batch.ConfirmationCount);
    }

    [Fact]
    public async Task FullResources_PaginatesAndCommitsEachPage()
    {
        var counter = new EventSaveCounterInterceptor();
        var db = CreateDbWithInterceptor("full-resources-pagination-" + Guid.NewGuid(), counter);
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewPageResponse(
            "https://graph.microsoft.com/v1.0/me/calendars/cal-1/events?$skiptoken=p1", SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent3));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources"), CancellationToken.None);

        Assert.Equal(3, counter.EventSaves.Count);
        Assert.All(counter.EventSaves, c => Assert.Equal(1, c));

        var batch = await LatestBatchAsync(db);
        Assert.Equal(3, batch.ReadCount);
        Assert.Equal(3, batch.CreatedCount);
        Assert.Equal(0, batch.UpdatedCount);
    }

    [Fact]
    public async Task FullResources_BackfillsEmptyExternalMetadataWithVersion2AndTypedFields()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Calendar", "Fixtures", "graph-event-pr2.json");
        Assert.True(File.Exists(fixturePath), $"Fixture not copied to test output: {fixturePath}");
        var fixtureJson = File.ReadAllText(fixturePath);

        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        var oldEvent = new EventEntity
        {
            CalendarId = calId,
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = ConnectionId,
            OutlookEventId = "pr2-fixture-event",
            Uid = "pr2-fixture@outlook.test",
            Title = "Old Title",
            DtStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
            Source = "outlook",
            ExternalMetadataJson = "{}"
        };
        db.Set<EventEntity>().Add(oldEvent);
        await db.SaveChangesAsync();
        var oldEventId = oldEvent.Id;

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(fixtureJson));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources"), CancellationToken.None);

        var reloaded = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstAsync(e => e.Id == oldEventId);
        Assert.Equal(oldEventId, reloaded.Id);
        Assert.Equal("high", reloaded.Importance);
        Assert.Equal("private", reloaded.Sensitivity);
        Assert.Equal("tentative", reloaded.ShowAs);
        Assert.Equal("html", reloaded.DescriptionFormat);
        Assert.True(reloaded.IsReminderOn);
        Assert.Equal(15, reloaded.ReminderMinutesBeforeStart);
        Assert.Equal("teams", reloaded.OnlineMeetingProvider);
        Assert.True(reloaded.IsOnlineMeeting);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/xxx", reloaded.OnlineMeetingUrl);
        Assert.Equal("https://outlook.office.com/calendar/deeplink/xxx", reloaded.ExternalLink);
        Assert.DoesNotContain("<h1>", reloaded.Description);
        Assert.NotEqual("{}", reloaded.ExternalMetadataJson);
        using var metaDoc = JsonDocument.Parse(reloaded.ExternalMetadataJson);
        Assert.Equal(2, metaDoc.RootElement.GetProperty("mappingVersion").GetInt32());

        using var orgDoc = JsonDocument.Parse(reloaded.OrganizerJson!);
        Assert.Equal("张三", orgDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("zhangsan@contoso.com", orgDoc.RootElement.GetProperty("email").GetString());

        using var attDoc = JsonDocument.Parse(reloaded.AttendeesJson);
        var atts = attDoc.RootElement.EnumerateArray().ToList();
        Assert.Single(atts);
        Assert.Equal("required", atts[0].GetProperty("type").GetString());
        Assert.Equal("李四", atts[0].GetProperty("name").GetString());
        Assert.Equal("lisi@contoso.com", atts[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task RangeInstances_UsesAtMost180DayChunksAndDeduplicatesIds()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        var handler = new ScriptedHttpMessageHandler();
        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        // Chunk 1: [2026-01-01, 2026-06-30) — 180 days
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        // Chunk 2: [2026-06-30, 2026-07-05) — 5 days
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(
            SyncEvent1.Replace("\"event-1\"", "\"event-2\""),
            SyncEvent1.Replace("\"Test 1\"", "\"Duplicate Override\"")));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("range-instances",
            RangeStart: rangeStart, RangeEnd: rangeEnd), CancellationToken.None);

        Assert.Equal("range-instances", response.Mode);
        Assert.Equal(rangeStart, response.RequestedWindowStart);
        Assert.Equal(rangeEnd, response.RequestedWindowEnd);

        var cvRequests = ExtractCalendarViewRequests(handler);
        Assert.Equal(2, cvRequests.Count);
        foreach (var r in cvRequests)
        {
            var days = (r.End - r.Start).TotalDays;
            Assert.True(days <= 180, $"Chunk {r.Start} to {r.End} is {days} days (exceeds 180)");
        }
        Assert.Equal(cvRequests[0].End, cvRequests[1].Start);
        Assert.Equal(rangeStart, cvRequests[0].Start);
        Assert.Equal(rangeEnd, cvRequests[1].End);

        var events = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.OutlookEventId == "event-1");
        Assert.Contains(events, e => e.OutlookEventId == "event-2");
        Assert.Equal("Test 1", events.Single(e => e.OutlookEventId == "event-1").Title);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("range-instances", batch.Mode);
        Assert.Equal(rangeStart, batch.RequestedWindowStart);
        Assert.Equal(rangeEnd, batch.RequestedWindowEnd);
        Assert.Equal(2, batch.ReadCount);
        Assert.Equal(2, batch.CreatedCount);
        Assert.Equal(0, batch.UpdatedCount);

        using var history = JsonDocument.Parse(batch.PerCalendarJson);
        var calendar = Assert.Single(history.RootElement.EnumerateArray().ToList());
        Assert.Equal(2, calendar.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task RangeInstances_RequiresValidRange()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("range-instances"), CancellationToken.None));
        Assert.Equal(02009, ex1.ErrorCode);

        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("range-instances",
                RangeStart: start, RangeEnd: end), CancellationToken.None));
        Assert.Equal(02009, ex2.ErrorCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RangeInstances_AllDuplicatePage_SkipsRemappingAndFinishesCompleted()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");

        var handler = new ScriptedHttpMessageHandler();
        // 180-day chunks from Jan 1 cover: [Jan 1, Jun 30), [Jun 30, Jul 5) — 2 chunks
        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        // Chunk 1: has event-1 and event-2
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1, SyncEvent2));
        // Chunk 2: has only event-1 duplicate with different title (all duplicates)
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1.Replace("\"Test 1\"", "\"Remap Check\"")));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("range-instances",
            RangeStart: rangeStart, RangeEnd: rangeEnd), CancellationToken.None);

        Assert.Equal("completed", response.Status);

        var batch = await LatestBatchAsync(db);
        Assert.Equal("completed", batch.Status);
        Assert.Equal(2, batch.ReadCount);
        Assert.Equal(2, batch.CreatedCount);
        Assert.Equal(0, batch.UpdatedCount);

        var events = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == bindingId)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        var event1 = events.Single(e => e.OutlookEventId == "event-1");
        Assert.Equal("Test 1", event1.Title);
        Assert.Equal(0, batch.FailureCount);

        using var history = JsonDocument.Parse(batch.PerCalendarJson);
        var calendar = Assert.Single(history.RootElement.EnumerateArray().ToList());
        Assert.Equal("completed", calendar.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeepMode_ExplicitBindingIdsProcessesOnlyRequestedSelectedActiveBindings()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedTwoSelectedBindingsAsync(db, UserId, ConnectionId);
        var binding1 = await BindingByGraphIdAsync(db, "cal-1");

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources",
            CalendarBindingIds: new[] { binding1.Id }), CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Contains("cal-1", handler.Requests[0].RequestUri!.ToString());

        var events = await db.Set<EventEntity>().IgnoreQueryFilters().ToListAsync();
        Assert.Single(events);
        Assert.Equal("event-1", events[0].OutlookEventId);
    }

    [Fact]
    public async Task DeepMode_RejectsUnknownOrUnselectedBindingIds()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId1, bindingId1) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        // Unselected binding
        var cal2 = new CalendarEntity { UserId = UserId, Name = "Unselected", Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal2);
        await db.SaveChangesAsync();
        var unselectedBinding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal2.Id,
            GraphCalendarId = "cal-2", Name = "Unselected",
            IsSelected = false, RemoteState = "active"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(unselectedBinding);
        await db.SaveChangesAsync();
        // Remote-missing binding
        var cal3 = new CalendarEntity { UserId = UserId, Name = "Missing", Source = "outlook" };
        db.Set<CalendarEntity>().Add(cal3);
        await db.SaveChangesAsync();
        var missingBinding = new OutlookCalendarBindingEntity
        {
            ConnectionId = ConnectionId, PimCalendarId = cal3.Id,
            GraphCalendarId = "cal-3", Name = "Missing",
            IsSelected = true, RemoteState = "remote-missing"
        };
        db.Set<OutlookCalendarBindingEntity>().Add(missingBinding);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        // Unknown binding ID
        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("full-resources",
                CalendarBindingIds: new[] { Guid.NewGuid() }), CancellationToken.None));
        Assert.Equal(02009, ex1.ErrorCode);

        // Unselected binding ID
        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("full-resources",
                CalendarBindingIds: new[] { unselectedBinding.Id }), CancellationToken.None));
        Assert.Equal(02009, ex2.ErrorCode);

        // Remote-missing binding ID
        var ex3 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("full-resources",
                CalendarBindingIds: new[] { missingBinding.Id }), CancellationToken.None));
        Assert.Equal(02009, ex3.ErrorCode);

        Assert.Empty(handler.Requests);
    }

    // ===== Task 5B: Retry =====

    private sealed record RetryTestHelper(Guid BatchId, Guid Binding1Id, Guid Binding2Id, Guid Binding3Id);

    private static async Task<RetryTestHelper> SeedRetryScenarioAsync(PimDbContext db)
    {
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var b3 = Guid.NewGuid();
        var cal1 = new CalendarEntity { UserId = UserId, Name = "Cal1", Source = "outlook", IsVisible = true };
        var cal2 = new CalendarEntity { UserId = UserId, Name = "Cal2", Source = "outlook", IsVisible = true };
        var cal3 = new CalendarEntity { UserId = UserId, Name = "Cal3", Source = "outlook", IsVisible = true };
        db.Set<CalendarEntity>().AddRange(cal1, cal2, cal3);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().AddRange(
            new OutlookCalendarBindingEntity { Id = b1, ConnectionId = ConnectionId, PimCalendarId = cal1.Id, GraphCalendarId = "cal-1", Name = "Cal1", IsSelected = true, RemoteState = "active" },
            new OutlookCalendarBindingEntity { Id = b2, ConnectionId = ConnectionId, PimCalendarId = cal2.Id, GraphCalendarId = "cal-2", Name = "Cal2", IsSelected = true, RemoteState = "active" },
            new OutlookCalendarBindingEntity { Id = b3, ConnectionId = ConnectionId, PimCalendarId = cal3.Id, GraphCalendarId = "cal-3", Name = "Cal3", IsSelected = true, RemoteState = "active" });
        await db.SaveChangesAsync();
        return new RetryTestHelper(Guid.Empty, b1, b2, b3);
    }

    [Fact]
    public async Task Retry_CreatesNewBatchLinkedToOriginalAndRunsOnlyFailedBindings()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);
        var binding1Id = helper.Binding1Id;
        var binding2Id = helper.Binding2Id;

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            StartedAt = FixedNow.AddDays(-1),
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = binding1Id.ToString(), calendarName = "Cal1", status = "failed", readCount = 0, createdCount = 0, updatedCount = 0, deletedCount = 0, failureCount = 1, changes = Array.Empty<object>(), failures = Array.Empty<object>() },
                new { bindingId = binding2Id.ToString(), calendarName = "Cal2", status = "completed", readCount = 5, createdCount = 3, updatedCount = 1, deletedCount = 1, failureCount = 0, changes = Array.Empty<object>(), failures = Array.Empty<object>() }
            })
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();
        var originalId = originalBatch.Id;

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalId), CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Contains("cal-1", handler.Requests[0].RequestUri!.ToString());

        var newBatch = await LatestBatchAsync(db);
        Assert.NotEqual(originalId, newBatch.Id);

        using var idsDoc = JsonDocument.Parse(newBatch.RequestedCalendarIdsJson);
        var ids = idsDoc.RootElement.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
        Assert.Single(ids);
        Assert.Equal(binding1Id, ids[0]);

        using var calDoc = JsonDocument.Parse(newBatch.PerCalendarJson);
        var entries = calDoc.RootElement.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal(binding1Id.ToString(), entries[0].GetProperty("bindingId").GetString());
        Assert.True(entries[0].TryGetProperty("retryOfBatchId", out var rob));
        Assert.Equal(originalId.ToString(), rob.GetString());

        var originalReloaded = await db.Set<OutlookSyncBatchEntity>().FirstAsync(b => b.Id == originalId);
        Assert.Equal("partial", originalReloaded.Status);
    }

    [Fact]
    public async Task Retry_WithoutExplicitIdsRunsFailedAndPartialBindings()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            StartedAt = FixedNow.AddDays(-1),
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString(), calendarName = "Failed", status = "failed", readCount = 0, createdCount = 0, updatedCount = 0, deletedCount = 0, failureCount = 1, changes = Array.Empty<object>(), failures = Array.Empty<object>() },
                new { bindingId = helper.Binding2Id.ToString(), calendarName = "Partial", status = "partial", readCount = 2, createdCount = 1, updatedCount = 0, deletedCount = 0, failureCount = 0, changes = Array.Empty<object>(), failures = Array.Empty<object>() },
                new { bindingId = helper.Binding3Id.ToString(), calendarName = "Completed", status = "completed", readCount = 5, createdCount = 3, updatedCount = 1, deletedCount = 1, failureCount = 0, changes = Array.Empty<object>(), failures = Array.Empty<object>() }
            })
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();
        var originalId = originalBatch.Id;

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalId), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var calIds = handler.Requests.Select(r => r.RequestUri!.ToString()).ToList();
        Assert.Contains(calIds, u => u.Contains("cal-1"));
        Assert.Contains(calIds, u => u.Contains("cal-2"));

        using var idsDoc = JsonDocument.Parse((await LatestBatchAsync(db)).RequestedCalendarIdsJson);
        var ids = idsDoc.RootElement.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).Order().ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(helper.Binding1Id, ids);
        Assert.Contains(helper.Binding2Id, ids);
    }

    [Fact]
    public async Task Retry_ExplicitIdsMustBeSubsetOfRetryableBindings()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString(), status = "failed" },
                new { bindingId = helper.Binding2Id.ToString(), status = "completed" }
            })
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        // Completed binding ID should be rejected
        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal",
                CalendarBindingIds: new[] { helper.Binding2Id }, RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex1.ErrorCode);

        // Unknown binding ID
        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal",
                CalendarBindingIds: new[] { Guid.NewGuid() }, RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex2.ErrorCode);

        Assert.Empty(handler.Requests);
        var batchesAfter = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        Assert.Single(batchesAfter); // only original
    }

    [Fact]
    public async Task Retry_RejectsCrossUserOrCrossConnectionBatch()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        // Batch for different user and connection
        var otherBatch = new OutlookSyncBatchEntity
        {
            UserId = OtherUserId, ConnectionId = Guid.NewGuid(), Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString(), status = "failed" }
            })
        };
        db.Set<OutlookSyncBatchEntity>().Add(otherBatch);
        await db.SaveChangesAsync();
        var otherBatchId = otherBatch.Id;

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: otherBatchId), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);

        Assert.Empty(handler.Requests);
        var batchesAfter = await db.Set<OutlookSyncBatchEntity>().ToListAsync();
        Assert.Single(batchesAfter); // only the seeded one
    }

    [Fact]
    public async Task Retry_RequiresMatchingModeAndValidHistory()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        async Task<Guid> SeedBatchAsync(string mode, string status, string perCalendarJson)
        {
            var b = new OutlookSyncBatchEntity { UserId = UserId, ConnectionId = ConnectionId, Mode = mode, Status = status, PerCalendarJson = perCalendarJson };
            db.Set<OutlookSyncBatchEntity>().Add(b);
            await db.SaveChangesAsync();
            return b.Id;
        }

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        // Mode mismatch
        var modeMismatchId = await SeedBatchAsync("full-resources", "partial",
            JsonSerializer.Serialize(new object[] { new { bindingId = helper.Binding1Id.ToString(), status = "failed" } }));
        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: modeMismatchId), CancellationToken.None));
        Assert.Equal(02009, ex1.ErrorCode);

        // PerCalendarJson not an array (null)
        var nullJsonId = await SeedBatchAsync("normal", "partial", "null");
        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: nullJsonId), CancellationToken.None));
        Assert.Equal(02009, ex2.ErrorCode);

        // PerCalendarJson empty array → no retryable entries
        var emptyArrayId = await SeedBatchAsync("normal", "partial", "[]");
        var ex3 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: emptyArrayId), CancellationToken.None));
        Assert.Equal(02009, ex3.ErrorCode);

        // PerCalendarJson with no failed/partial entries
        var noRetryableId = await SeedBatchAsync("normal", "completed",
            JsonSerializer.Serialize(new object[] { new { bindingId = helper.Binding1Id.ToString(), status = "completed" } }));
        var ex4 = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: noRetryableId), CancellationToken.None));
        Assert.Equal(02009, ex4.ErrorCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_MalformedPerCalendarJson_ThrowsDomainException()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = "this is not valid json {{{",
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsNonObjectEntryInHistory()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = """["not-an-object"]""",
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsEntryMissingBindingId()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { status = "failed" }
            }),
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsEntryWithInvalidBindingIdGuid()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = "not-a-guid", status = "failed" }
            }),
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsEntryMissingStatus()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString() }
            }),
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsUnknownStatus()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString(), status = "garbage-status" }
            }),
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsDuplicateBindingEntry()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString(), status = "failed" },
                new { bindingId = helper.Binding1Id.ToString(), status = "failed" }
            }),
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retry_RejectsUppercaseStatus()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var helper = await SeedRetryScenarioAsync(db);

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId, ConnectionId = ConnectionId, Mode = "normal", Status = "partial",
            PerCalendarJson = JsonSerializer.Serialize(new object[]
            {
                new { bindingId = helper.Binding1Id.ToString(), status = "Failed" },
                new { bindingId = helper.Binding2Id.ToString(), status = "failed" }
            }),
            StartedAt = FixedNow.AddDays(-1)
        };
        db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SyncAsync(UserId, new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id), CancellationToken.None));
        Assert.Equal(02009, ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    // ===== Task 5B: Cancel =====

    [Fact]
    public async Task CancelRequested_StopsBeforeNextPageAndPreservesCommittedPage()
    {
        var dbName = "cancel-paging-" + Guid.NewGuid();
        var sharedOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        using (var seedCtx = new PimDbContext(sharedOptions))
        {
            await SeedConnectionAsync(seedCtx, UserId);
            await SeedSingleBindingAsync(seedCtx, UserId, ConnectionId, "cal-1");
        }

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(request =>
        {
            using var db = new PimDbContext(sharedOptions);
            var batch = db.Set<OutlookSyncBatchEntity>().FirstOrDefault(b => b.Status == "running");
            if (batch is not null)
            {
                batch.CancelRequested = true;
                db.SaveChanges();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    CalendarViewPageResponse("https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2", SyncEvent1),
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };

        using var ctx = new PimDbContext(sharedOptions);
        var service = CreateService(ctx, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("canceled", response.Status);
        Assert.True(response.CancelRequested);

        Assert.Single(handler.Requests);

        var evt = await LoadEventByOutlookIdAsync(ctx, "event-1");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);

        var batch = await LatestBatchAsync(ctx);
        Assert.Equal("canceled", batch.Status);
        Assert.True(batch.CancelRequested);
        Assert.NotNull(batch.FinishedAt);
        Assert.True(batch.ReadCount >= 1);
        Assert.Equal(0, batch.FailureCount);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var entries = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("canceled", entries[0].GetProperty("status").GetString());

        var step = Assert.Single(response.Steps);
        Assert.Equal("canceled", step.Status);
    }

    [Fact]
    public async Task CancelRequested_WithTwoBindings_StopsAfterFirstBinding()
    {
        var dbName = "cancel-two-bindings-" + Guid.NewGuid();
        var sharedOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        using (var seedCtx = new PimDbContext(sharedOptions))
        {
            await SeedConnectionAsync(seedCtx, UserId);
            await SeedTwoSelectedBindingsAsync(seedCtx, UserId, ConnectionId);
        }

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(request =>
        {
            using var db = new PimDbContext(sharedOptions);
            var batch = db.Set<OutlookSyncBatchEntity>().FirstOrDefault(b => b.Status == "running");
            if (batch is not null)
            {
                batch.CancelRequested = true;
                db.SaveChanges();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    CalendarViewPageResponse("https://graph.microsoft.com/v1.0/me/calendars/cal-1/calendarView?$skiptoken=p2", SyncEvent1),
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        // Second binding response queued but should never be requested
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));

        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };

        using var ctx = new PimDbContext(sharedOptions);
        var service = CreateService(ctx, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("canceled", response.Status);
        Assert.True(response.CancelRequested);

        Assert.Single(handler.Requests);

        var evt = await LoadEventByOutlookIdAsync(ctx, "event-1");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);

        var batch = await LatestBatchAsync(ctx);
        Assert.Equal("canceled", batch.Status);
        Assert.True(batch.CancelRequested);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var entries = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("canceled", entries[0].GetProperty("status").GetString());

        var requestedUri = handler.Requests[0].RequestUri!.ToString();
        var processedGraphId = requestedUri.Contains("cal-1") ? "cal-1" : "cal-2";
        var processedBinding = await BindingByGraphIdAsync(ctx, processedGraphId);
        Assert.Equal(processedBinding.Id.ToString(), entries[0].GetProperty("bindingId").GetString());
    }

    [Fact]
    public async Task CancelRequested_StopsBeforeNextRangeChunk()
    {
        var dbName = "cancel-chunk-" + Guid.NewGuid();
        var sharedOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        using (var seedCtx = new PimDbContext(sharedOptions))
        {
            await SeedConnectionAsync(seedCtx, UserId);
            await SeedSingleBindingAsync(seedCtx, UserId, ConnectionId, "cal-1");
        }

        var handler = new ScriptedHttpMessageHandler();
        // Chunk 1 response: single page with no nextLink
        handler.Enqueue(request =>
        {
            using var db = new PimDbContext(sharedOptions);
            var batch = db.Set<OutlookSyncBatchEntity>().FirstOrDefault(b => b.Status == "running");
            if (batch is not null)
            {
                batch.CancelRequested = true;
                db.SaveChanges();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CalendarViewResponse(SyncEvent1),
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        // Chunk 2 response: queued but will never be requested
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));

        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };

        using var ctx = new PimDbContext(sharedOptions);
        var service = CreateService(ctx, graph, time);

        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("range-instances",
            RangeStart: rangeStart, RangeEnd: rangeEnd), CancellationToken.None);

        Assert.Equal("canceled", response.Status);
        Assert.True(response.CancelRequested);
        Assert.Single(handler.Requests);

        var evt = await LoadEventByOutlookIdAsync(ctx, "event-1");
        Assert.NotNull(evt);
        Assert.Null(evt.DeletedAt);

        var batch = await LatestBatchAsync(ctx);
        Assert.Equal("canceled", batch.Status);
        Assert.True(batch.CancelRequested);

        using var doc = JsonDocument.Parse(batch.PerCalendarJson);
        var entries = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("canceled", entries[0].GetProperty("status").GetString());

        var step = Assert.Single(response.Steps);
        Assert.Equal("canceled", step.Status);
    }

    // ===== Task 7: Legacy Event Rebinding =====

    [Fact]
    public async Task Rebind_PrefersExactGraphId()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "rebind-cal");

        var legacyId = Guid.NewGuid();
        var legacy = new EventEntity
        {
            Id = legacyId,
            CalendarId = calId,
            Uid = "legacy-old@pim",
            Title = "Legacy Exact",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook-ics",
            OutlookEventId = "rebind-exact-graph",
            OutlookConnectionId = null,
            OutlookCalendarBindingId = null,
            DeletedAt = FixedNow.AddDays(-1),
            DeletedByOperationId = Guid.NewGuid(),
            DeletedByOperationKind = "outlook-sync",
            OutlookSyncState = "legacy-unbound"
        };
        db.Set<EventEntity>().Add(legacy);

        var otherCal = new CalendarEntity { Id = Guid.NewGuid(), UserId = OtherUserId, Name = "OtherCal", Source = "outlook" };
        db.Set<CalendarEntity>().Add(otherCal);
        await db.SaveChangesAsync();

        var otherLegacyId = Guid.NewGuid();
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = otherLegacyId,
            CalendarId = otherCal.Id,
            Uid = "legacy-other@pim",
            Title = "Other Legacy",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook-ics",
            OutlookEventId = "rebind-exact-graph",
            OutlookConnectionId = null,
            OutlookCalendarBindingId = null
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(
            SyncEvent1.Replace("\"event-1\"", "\"rebind-exact-graph\"")
                .Replace("\"event-1@outlook\"", "\"exact-new@test\"")
                .Replace("\"Test 1\"", "\"Exact Rebound\"")));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources"), CancellationToken.None);

        var rebound = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == legacyId);
        Assert.Equal("outlook", rebound.Source);
        Assert.Equal("rebind-exact-graph", rebound.OutlookEventId);
        Assert.Equal(bindingId, rebound.OutlookCalendarBindingId);
        Assert.Equal(ConnectionId, rebound.OutlookConnectionId);
        Assert.Null(rebound.OutlookSyncState);
        Assert.Equal("Exact Rebound", rebound.Title);
        Assert.Null(rebound.DeletedAt);
        Assert.Null(rebound.DeletedByOperationId);
        Assert.Null(rebound.DeletedByOperationKind);

        var outlookEventsWithGraphId = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookEventId == "rebind-exact-graph" && e.Source == "outlook").ToListAsync();
        Assert.Single(outlookEventsWithGraphId);

        var otherEvent = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == otherLegacyId);
        Assert.Equal("outlook-ics", otherEvent.Source);
        Assert.Null(otherEvent.OutlookCalendarBindingId);
        Assert.Equal("Other Legacy", otherEvent.Title);

        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task Rebind_UsesOnlyUniqueIcalUid()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "rebind-ical-cal");

        var legacyId = Guid.NewGuid();
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = legacyId,
            CalendarId = calId,
            Uid = "rebind-unique-ical",
            Title = "ICal Legacy",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook-ics",
            OutlookEventId = null,
            SourceUid = "rebind-unique-ical",
            OutlookConnectionId = null,
            OutlookCalendarBindingId = null,
            OutlookSyncState = "legacy-unbound"
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(
            SyncEvent1.Replace("\"event-1\"", "\"rebind-ical-graph\"")
                .Replace("\"event-1@outlook\"", "\"rebind-unique-ical\"")
                .Replace("\"Test 1\"", "\"Rebound by ICAL\"")));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources"), CancellationToken.None);

        var rebound = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == legacyId);
        Assert.Equal("outlook", rebound.Source);
        Assert.Equal("rebind-ical-graph", rebound.OutlookEventId);
        Assert.Equal(bindingId, rebound.OutlookCalendarBindingId);
        Assert.Equal(ConnectionId, rebound.OutlookConnectionId);
        Assert.Null(rebound.OutlookSyncState);
        Assert.Equal("Rebound by ICAL", rebound.Title);

        var eventsWithGraphId = await db.Set<EventEntity>().IgnoreQueryFilters()
            .Where(e => e.OutlookEventId == "rebind-ical-graph").ToListAsync();
        Assert.Single(eventsWithGraphId);

        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task Rebind_DoesNotUseDuplicateIcalUid()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "rebind-dup-cal");

        var legacyAId = Guid.NewGuid();
        var legacyBId = Guid.NewGuid();
        db.Set<EventEntity>().AddRange(
            new EventEntity
            {
                Id = legacyAId,
                CalendarId = calId,
                Uid = "rebind-dup-ical",
                Title = "Dup A",
                DtStart = FixedNow,
                DtEnd = FixedNow.AddHours(1),
                Source = "outlook-ics",
                SourceUid = "rebind-dup-ical",
                OutlookEventId = null,
                OutlookConnectionId = null,
                OutlookCalendarBindingId = null
            },
            new EventEntity
            {
                Id = legacyBId,
                CalendarId = calId,
                Uid = "rebind-dup-ical",
                Title = "Dup B",
                DtStart = FixedNow.AddDays(1),
                DtEnd = FixedNow.AddDays(1).AddHours(1),
                Source = "outlook-ics",
                SourceUid = "rebind-dup-ical",
                OutlookEventId = null,
                OutlookConnectionId = null,
                OutlookCalendarBindingId = null
            });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(
            SyncEvent1.Replace("\"event-1\"", "\"rebind-dup-graph\"")
                .Replace("\"event-1@outlook\"", "\"rebind-dup-ical\"")
                .Replace("\"Test 1\"", "\"Should Not Rebind\"")));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources"), CancellationToken.None);

        // Neither legacy should be rebound
        var eventA = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == legacyAId);
        Assert.Equal("outlook-ics", eventA.Source);
        Assert.Equal("legacy-unbound", eventA.OutlookSyncState);
        Assert.Null(eventA.OutlookCalendarBindingId);

        var eventB = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == legacyBId);
        Assert.Equal("outlook-ics", eventB.Source);
        Assert.Equal("legacy-unbound", eventB.OutlookSyncState);
        Assert.Null(eventB.OutlookCalendarBindingId);

        // A new normal event should exist representing the Graph event
        var normalEvent = await db.Set<EventEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OutlookEventId == "rebind-dup-graph" && e.Source == "outlook");
        Assert.NotNull(normalEvent);
        Assert.Equal(bindingId, normalEvent!.OutlookCalendarBindingId);
        Assert.Equal(ConnectionId, normalEvent.OutlookConnectionId);

        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task Rebind_MarksUnmatchedEventLegacyUnbound()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, _) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "rebind-empty-cal");

        var legacyId = Guid.NewGuid();
        db.Set<EventEntity>().Add(new EventEntity
        {
            Id = legacyId,
            CalendarId = calId,
            Uid = "rebind-unmatched",
            Title = "Unmatched Legacy",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook-ics",
            SourceUid = "rebind-unmatched-ical",
            OutlookEventId = null,
            OutlookConnectionId = null,
            OutlookCalendarBindingId = null
        });
        await db.SaveChangesAsync();

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse());
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("full-resources"), CancellationToken.None);

        var unmatched = await db.Set<EventEntity>().IgnoreQueryFilters().FirstAsync(e => e.Id == legacyId);
        Assert.NotNull(unmatched);
        Assert.Equal("outlook-ics", unmatched.Source);
        Assert.Equal("legacy-unbound", unmatched.OutlookSyncState);
        Assert.Equal("Unmatched Legacy", unmatched.Title);
        Assert.Null(unmatched.DeletedAt);

        Assert.Equal("completed", response.Status);
    }

    // ===== Task 5: Attachment reference hydration =====

    private const string SyncEventWithAttachments = """
        {
            "@odata.etag": "etag-att",
            "id": "event-1",
            "subject": "Test With Attachments",
            "body": {"contentType": "text", "content": "desc-att"},
            "start": {"dateTime": "2026-05-01T09:00:00.0000000", "timeZone": "UTC"},
            "end": {"dateTime": "2026-05-01T10:00:00.0000000", "timeZone": "UTC"},
            "location": {"displayName": "Room A"},
            "isAllDay": false,
            "type": "singleInstance",
            "iCalUId": "event-1@outlook",
            "changeKey": "ck-1",
            "hasAttachments": true,
            "originalStartTimeZone": "UTC",
            "originalEndTimeZone": "UTC"
        }
        """;

    private const string FileAttachmentMetadata = """
        {
            "@odata.type": "#microsoft.graph.fileAttachment",
            "id": "att-1",
            "name": "Report.pdf",
            "contentType": "application/pdf",
            "size": 2048,
            "isInline": false
        }
        """;

    private const string ExistingOutlookRefJson = """
        [{"kind":"outlook","id":"att-1","name":"Old.pdf","contentType":"application/pdf","size":10,"canDownload":true}]
        """;

    private const string InlineAndReferenceAttachmentMetadata = """
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
            "id": "att-inline",
            "name": "Inline.png",
            "contentType": "image/png",
            "size": 512,
            "isInline": true
        }
        """;

    private static string AttachmentsMetadataPage(string items)
        => $$"""{"value":[{{items}}]}""";

    private static List<string> ExtractAttachmentMetadataRequests(ScriptedHttpMessageHandler handler)
        => handler.Requests
            .Select(r => r.RequestUri?.ToString() ?? string.Empty)
            .Where(u => u.Contains("/attachments?"))
            .Where(u => u.Contains("$select="))
            .ToList();

    private static async Task<EventEntity> SeedOutlookEventAsync(
        PimDbContext db, Guid calendarId, Guid bindingId,
        string outlookEventId = "event-1", string? changeKey = null, string refsJson = "[]")
    {
        var entity = new EventEntity
        {
            CalendarId = calendarId,
            Uid = outlookEventId + "@pim",
            Title = "Test Event",
            DtStart = FixedNow,
            DtEnd = FixedNow.AddHours(1),
            Source = "outlook",
            OutlookEventId = outlookEventId,
            OutlookCalendarBindingId = bindingId,
            OutlookConnectionId = ConnectionId,
            OutlookChangeKey = changeKey,
            AttachmentReferencesJson = refsJson
        };
        db.Set<EventEntity>().Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private static async Task<OutlookCalendarBindingEntity> SeedOutlookBindingWithCalendarAsync(
        PimDbContext db, string graphCalendarId = "cal-1")
    {
        var calendar = new CalendarEntity { UserId = UserId, Name = "Cal " + graphCalendarId, Source = "outlook" };
        db.Set<CalendarEntity>().Add(calendar);
        await db.SaveChangesAsync();
        await SeedBindingWithCalendarAsync(db, calendar, graphCalendarId);
        return await BindingByGraphIdAsync(db, graphCalendarId);
    }

    [Fact]
    public async Task SyncAsync_NewEventWithAttachments_FetchesMetadataAndHydratesOutlookReferences()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedOutlookBindingWithCalendarAsync(db);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        var req = Assert.Single(ExtractAttachmentMetadataRequests(handler));
        Assert.Contains("/calendars/cal-1/events/event-1/attachments?", req);
        Assert.Contains("$select=id,name,contentType,size,isInline,@odata.type", req);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        var references = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        var reference = Assert.Single(references);
        Assert.Equal("outlook", reference.Kind);
        Assert.Equal("att-1", reference.Id);
        Assert.Equal("Report.pdf", reference.Name);
        Assert.Equal("application/pdf", reference.ContentType);
        Assert.Equal(2048, reference.Size);
        Assert.True(reference.CanDownload);
    }

    [Fact]
    public async Task SyncAsync_ExistingEventWithUnchangedChangeKey_SkipsAttachmentMetadataFetch()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: ExistingOutlookRefJson);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("ck-1", stored.OutlookChangeKey);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("Old.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_ExistingEventWithChangedChangeKey_RefetchesAttachmentMetadata()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-old", refsJson: ExistingOutlookRefJson);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        var req = Assert.Single(ExtractAttachmentMetadataRequests(handler));
        Assert.Contains("/calendars/cal-1/events/event-1/attachments?", req);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("ck-1", stored.OutlookChangeKey);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("Report.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_ExistingEventWithEmptyStoredReferences_RefetchesAttachmentMetadata()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: "[]");
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Single(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("Report.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_EventWithoutAttachments_StoresEmptyReferences_AndMakesNoAttachmentRequest()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedOutlookBindingWithCalendarAsync(db);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("[]", stored.AttachmentReferencesJson);
    }

    [Fact]
    public async Task SyncAsync_AttachmentMetadataFailure_PreservesExistingReferences_AndRecordsBindingFailure()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-old", refsJson: ExistingOutlookRefJson);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);
        Assert.Equal(3, ExtractAttachmentMetadataRequests(handler).Count);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("Old.pdf", reference.Name);

        var reloadedBinding = await BindingByGraphIdAsync(db, "cal-1");
        Assert.NotNull(reloadedBinding.LastErrorMessage);
    }

    [Fact]
    public async Task SyncAsync_ExistingEventWithMixedReferences_AndExplicitNoAttachments_PreservesOnlyPimFile_AndMakesNoAttachmentRequest()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        var mixedRefsJson = """
            [
                {"kind":"pimFile","id":"pim-file-1","name":"Local.pdf","contentType":"application/pdf","size":1024,"canDownload":true},
                {"kind":"outlook","id":"att-1","name":"Old.pdf","contentType":"application/pdf","size":10,"canDownload":true}
            ]
            """;
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: mixedRefsJson);
        var handler = new ScriptedHttpMessageHandler();
        var noAttachmentsEvent = SyncEventWithAttachments.Replace(
            "\"hasAttachments\": true", "\"hasAttachments\": false");
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(noAttachmentsEvent));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        var references = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        var reference = Assert.Single(references);
        Assert.Equal("pimFile", reference.Kind);
        Assert.Equal("pim-file-1", reference.Id);
    }

    [Fact]
    public async Task SyncAsync_ExistingEventWithOutlookReferences_AndOmittedHasAttachments_PreservesReferencesAndMakesNoMetadataRequest()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: ExistingOutlookRefJson);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent1));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("outlook", reference.Kind);
        Assert.Equal("att-1", reference.Id);
        Assert.Equal("Old.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_ExistingEventWithOnlyPimFileReferences_AndHasAttachments_FetchesGraphRefsAndRetainsPimFile()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        var pimFileOnlyRefsJson = """
            [{"kind":"pimFile","id":"pim-file-1","name":"Local.pdf","contentType":"application/pdf","size":1024,"canDownload":true}]
            """;
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: pimFileOnlyRefsJson);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Single(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        var references = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        Assert.Equal(2, references.Count);
        var pimFile = Assert.Single(references, r => r.Kind == "pimFile");
        Assert.Equal("pim-file-1", pimFile.Id);
        var outlook = Assert.Single(references, r => r.Kind == "outlook");
        Assert.Equal("att-1", outlook.Id);
        Assert.Equal("Report.pdf", outlook.Name);
    }

    [Fact]
    public async Task SyncAsync_EventWithOnlyInlineOrReferenceAttachments_HydratesEmptyOnce_ThenSkipsOnUnchangedSync_AndRefetchesOnChangeKeyChange()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: "[]");
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(InlineAndReferenceAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var first = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", first.Status);
        Assert.Single(ExtractAttachmentMetadataRequests(handler));
        var afterFirst = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(afterFirst);
        Assert.Equal("attachments-hydrated-empty", afterFirst.OutlookSyncState);
        Assert.Equal("[]", afterFirst.AttachmentReferencesJson);

        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));

        var second = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", second.Status);
        Assert.Single(ExtractAttachmentMetadataRequests(handler));
        var afterSecond = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(afterSecond);
        Assert.Equal("attachments-hydrated-empty", afterSecond.OutlookSyncState);

        var changedChangeKeyEvent = SyncEventWithAttachments.Replace("\"changeKey\": \"ck-1\"", "\"changeKey\": \"ck-2\"");
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(changedChangeKeyEvent));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(InlineAndReferenceAttachmentMetadata));

        var third = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", third.Status);
        Assert.Equal(2, ExtractAttachmentMetadataRequests(handler).Count);
        var afterThird = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(afterThird);
        Assert.Equal("ck-2", afterThird.OutlookChangeKey);
        Assert.Equal("attachments-hydrated-empty", afterThird.OutlookSyncState);
        Assert.Equal("[]", afterThird.AttachmentReferencesJson);
    }

    [Fact]
    public async Task SyncAsync_NonEmptyHydration_ClearsHydratedEmptyMarker()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        var entity = await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-old", refsJson: "[]");
        entity.OutlookSyncState = "attachments-hydrated-empty";
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        var req = Assert.Single(ExtractAttachmentMetadataRequests(handler));
        Assert.Contains("/calendars/cal-1/events/event-1/attachments?", req);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Null(stored.OutlookSyncState);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("Report.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_ExplicitNoAttachments_ClearsHydratedEmptyMarker_AndMakesNoAttachmentRequest()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        var entity = await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-1", refsJson: "[]");
        entity.OutlookSyncState = "attachments-hydrated-empty";
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        var noAttachmentsEvent = SyncEventWithAttachments.Replace(
            "\"hasAttachments\": true", "\"hasAttachments\": false");
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(noAttachmentsEvent));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Null(stored.OutlookSyncState);
        Assert.Equal("[]", stored.AttachmentReferencesJson);
    }

    [Fact]
    public async Task SyncAsync_AttachmentHydrationFailure_ThenSameChangeKeyRetry_RefetchesAndRefreshesMetadata()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-old", refsJson: ExistingOutlookRefJson);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var first = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", first.Status);

        var afterFailure = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(afterFailure);
        Assert.Equal("ck-1", afterFailure.OutlookChangeKey);
        Assert.Equal("attachments-pending", afterFailure.OutlookSyncState);
        Assert.Equal("Old.pdf", Assert.Single(EventFieldCodec.DeserializeAttachments(afterFailure.AttachmentReferencesJson)).Name);

        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));

        var second = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", second.Status);

        var refreshed = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(refreshed);
        Assert.Equal("ck-1", refreshed.OutlookChangeKey);
        Assert.Null(refreshed.OutlookSyncState);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(refreshed.AttachmentReferencesJson));
        Assert.Equal("Report.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_AttachmentHydrationFailure_DoesNotOverwriteLegacyUnboundState()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        var entity = await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-old", refsJson: ExistingOutlookRefJson);
        entity.OutlookSyncState = "legacy-unbound";
        await db.SaveChangesAsync();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("legacy-unbound", stored.OutlookSyncState);
        Assert.Equal("Old.pdf", Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson)).Name);
    }

    [Fact]
    public async Task SyncAsync_AttachmentReauthAfterRemoteChange_DoesNotPersistNewChangeKeyWithOldReferences()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var binding = await SeedOutlookBindingWithCalendarAsync(db);
        await SeedOutlookEventAsync(db, binding.PimCalendarId, binding.Id,
            changeKey: "ck-old", refsJson: ExistingOutlookRefJson);
        var handler = new ScriptedHttpMessageHandler();
        // CalendarView: same event, remote changeKey/title changed, hasAttachments=true
        var changedEvent = SyncEventWithAttachments
            .Replace("\"changeKey\": \"ck-1\"", "\"changeKey\": \"ck-new\"")
            .Replace("\"subject\": \"Test With Attachments\"", "\"subject\": \"Renamed Subject\"");
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(changedEvent));
        // Attachment metadata request: first 401 -> force refresh replay, second 401 -> reauth
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("failed", response.Status);
        var connection = await db.Set<OutlookConnectionEntity>().FirstAsync(c => c.Id == ConnectionId);
        Assert.Equal("reauth-required", connection.Status);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("ck-old", stored.OutlookChangeKey);
        Assert.Equal("Test Event", stored.Title);
        Assert.Equal(ExistingOutlookRefJson, stored.AttachmentReferencesJson);
        Assert.Equal("Old.pdf", Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson)).Name);
    }

    [Fact]
    public async Task SyncAsync_MissingVerificationRestore_WithChangedChangeKey_RefetchesAttachmentMetadata()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedOutlookEventAsync(db, calId, bindingId,
            outlookEventId: "missing-event", changeKey: "ck-old", refsJson: ExistingOutlookRefJson);

        var handler = new ScriptedHttpMessageHandler();
        // CalendarView: missing-event not present
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        // GetEventAsync -> event still exists with a changed changeKey and hasAttachments=true
        var restoredEvent = SyncEventWithAttachments.Replace("\"event-1\"", "\"missing-event\"");
        handler.Enqueue(HttpStatusCode.OK, SingleEventResponse(restoredEvent));
        handler.Enqueue(HttpStatusCode.OK, AttachmentsMetadataPage(FileAttachmentMetadata));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        var req = Assert.Single(ExtractAttachmentMetadataRequests(handler));
        Assert.Contains("/calendars/cal-1/events/missing-event/attachments?", req);

        var stored = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(stored);
        Assert.Equal("ck-1", stored.OutlookChangeKey);
        Assert.Null(stored.OutlookSyncState);
        var reference = Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson));
        Assert.Equal("outlook", reference.Kind);
        Assert.Equal("Report.pdf", reference.Name);
    }

    [Fact]
    public async Task SyncAsync_MissingVerificationRestore_ExplicitNoAttachments_ClearsOutlookRefsKeepsPimFile()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        var mixedRefsJson = """
            [
                {"kind":"pimFile","id":"pim-file-1","name":"Local.pdf","contentType":"application/pdf","size":1024,"canDownload":true},
                {"kind":"outlook","id":"att-1","name":"Old.pdf","contentType":"application/pdf","size":10,"canDownload":true}
            ]
            """;
        await SeedOutlookEventAsync(db, calId, bindingId,
            outlookEventId: "missing-event", changeKey: "ck-1", refsJson: mixedRefsJson);

        var handler = new ScriptedHttpMessageHandler();
        // CalendarView: missing-event not present
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEvent2));
        // GetEventAsync -> event still exists but explicitly has no attachments
        var noAttachmentsEvent = SyncEventWithAttachments.Replace(
            "\"event-1\"", "\"missing-event\"").Replace("\"hasAttachments\": true", "\"hasAttachments\": false");
        handler.Enqueue(HttpStatusCode.OK, SingleEventResponse(noAttachmentsEvent));
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(stored);
        var references = EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson);
        var reference = Assert.Single(references);
        Assert.Equal("pimFile", reference.Kind);
        Assert.Equal("pim-file-1", reference.Id);
    }

    // ===== Task 5 final hardening: regression locks =====

    [Fact]
    public async Task SyncAsync_NewEventMetadata503_StoresEmptyReferencesAndPendingState_PartialBatch()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedOutlookBindingWithCalendarAsync(db);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(SyncEventWithAttachments));
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);
        Assert.Equal(3, ExtractAttachmentMetadataRequests(handler).Count);

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("[]", stored.AttachmentReferencesJson);
        Assert.Equal("attachments-pending", stored.OutlookSyncState);
    }

    [Fact]
    public async Task SyncAsync_NewEventExplicitNoAttachments_SavesEmptyReferencesWithoutMetadataRequest()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        await SeedOutlookBindingWithCalendarAsync(db);
        var handler = new ScriptedHttpMessageHandler();
        var noAttachmentsEvent = SyncEventWithAttachments.Replace(
            "\"hasAttachments\": true", "\"hasAttachments\": false");
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse(noAttachmentsEvent));
        var graph = CreateGraphClient(handler);
        var service = CreateService(db, graph);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("completed", response.Status);
        Assert.Empty(ExtractAttachmentMetadataRequests(handler));

        var stored = await LoadEventByOutlookIdAsync(db, "event-1");
        Assert.NotNull(stored);
        Assert.Equal("[]", stored.AttachmentReferencesJson);
    }

    [Fact]
    public async Task SyncAsync_MissingVerificationRestore_ReauthOnMetadata_RollsBackEventAndSetsConnectionReauth()
    {
        var db = CreateDb();
        await SeedConnectionAsync(db, UserId);
        var (calId, bindingId) = await SeedSingleBindingAsync(db, UserId, ConnectionId, "cal-1");
        await SeedOutlookEventAsync(db, calId, bindingId,
            outlookEventId: "missing-event", changeKey: "ck-old", refsJson: ExistingOutlookRefJson);

        var handler = new ScriptedHttpMessageHandler();
        // CalendarView: missing-event not present
        handler.Enqueue(HttpStatusCode.OK, CalendarViewResponse());
        // GetEventAsync -> event still exists with changed key/title and hasAttachments=true
        var restoredEvent = SyncEventWithAttachments
            .Replace("\"event-1\"", "\"missing-event\"")
            .Replace("\"subject\": \"Test With Attachments\"", "\"subject\": \"Renamed Subject\"")
            .Replace("\"changeKey\": \"ck-1\"", "\"changeKey\": \"ck-new\"");
        handler.Enqueue(HttpStatusCode.OK, SingleEventResponse(restoredEvent));
        // Attachment metadata request: first 401 -> force refresh replay, second 401 -> reauth
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);
        var graph = CreateGraphClient(handler);
        var time = new StubTimeProvider { UtcNowValue = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero) };
        var service = CreateService(db, graph, time);

        var response = await service.SyncAsync(UserId, new OutlookSyncRequest("normal"), CancellationToken.None);

        Assert.Equal("partial", response.Status);
        var connection = await db.Set<OutlookConnectionEntity>().FirstAsync(c => c.Id == ConnectionId);
        Assert.Equal("reauth-required", connection.Status);
        Assert.Equal("interaction-required", connection.TokenHealth);
        Assert.NotNull(connection.LastError);

        var stored = await LoadEventByOutlookIdAsync(db, "missing-event");
        Assert.NotNull(stored);
        Assert.Equal("ck-old", stored.OutlookChangeKey);
        Assert.Equal("Test Event", stored.Title);
        Assert.Equal("Old.pdf", Assert.Single(EventFieldCodec.DeserializeAttachments(stored.AttachmentReferencesJson)).Name);
    }
}
