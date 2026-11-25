using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Search;

public sealed class CalendarSearchDetailEndpointTests
{
    [Fact]
    public async Task SearchResultDetailsAreOwnerScopedAndNeverRevealAnotherAccount()
    {
        using var factory = AuthRegistrationTestHost.Create(
                AuthRegistrationTestHost.NewDbName("search-detail"))
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("Database:Migrations:FailFast", "false");
            });
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(
            factory, "search-detail-owner-" + Guid.NewGuid().ToString("N"));
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(
            factory, "search-detail-other-" + Guid.NewGuid().ToString("N"));
        using var ownerClient = AuthRegistrationTestHost.Authed(factory, owner.AccessToken);
        using var otherClient = AuthRegistrationTestHost.Authed(factory, other.AccessToken);

        var calendar = new CalendarEntity { UserId = owner.UserId, Name = "搜索详情日历" };
        var task = new TaskEntity
        {
            UserId = owner.UserId,
            Title = "搜索详情任务",
            Due = DateTimeOffset.Parse("2026-12-01T09:00:00Z")
        };
        var calendarEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Title = "搜索详情日程",
            DtStart = DateTimeOffset.Parse("2026-12-01T10:00:00Z"),
            DtEnd = DateTimeOffset.Parse("2026-12-01T11:00:00Z")
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.AddRange(calendar, task, calendarEvent);
            await db.SaveChangesAsync();
        }

        using (var taskResponse = await ownerClient.GetAsync($"/api/v1/calendar/tasks/{task.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, taskResponse.StatusCode);
            var root = await AuthRegistrationTestHost.ReadJsonAsync(taskResponse);
            Assert.Equal(task.Id, root.GetProperty("data").GetProperty("id").GetGuid());
            Assert.Equal("搜索详情任务", root.GetProperty("data").GetProperty("title").GetString());
        }
        using (var eventResponse = await ownerClient.GetAsync(
                   $"/api/v1/calendar/events/{calendarEvent.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
            var root = await AuthRegistrationTestHost.ReadJsonAsync(eventResponse);
            Assert.Equal(calendarEvent.Id, root.GetProperty("data").GetProperty("id").GetGuid());
            Assert.Equal("搜索详情日程", root.GetProperty("data").GetProperty("title").GetString());
        }

        using (var taskResponse = await otherClient.GetAsync($"/api/v1/calendar/tasks/{task.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, taskResponse.StatusCode);
            Assert.Equal(40401, (await AuthRegistrationTestHost.ReadErrorAsync(taskResponse)).Code);
        }
        using (var eventResponse = await otherClient.GetAsync(
                   $"/api/v1/calendar/events/{calendarEvent.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, eventResponse.StatusCode);
            Assert.Equal(40401, (await AuthRegistrationTestHost.ReadErrorAsync(eventResponse)).Code);
        }
    }
}
