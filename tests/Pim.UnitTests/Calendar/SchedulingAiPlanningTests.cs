using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class SchedulingAiPlanningTests : ServiceTestBase
{
    private static readonly Guid TestUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void InvertAvailableWindows_WithSingleWindow_ProducesBusySlotsBeforeAndAfter()
    {
        var horizonStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var horizonEnd = new DateTimeOffset(2026, 6, 1, 23, 59, 59, TimeSpan.Zero);

        var windows = new List<TimeSlot>
        {
            new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero))
        };

        var inverted = SchedulingEngine.InvertAvailableWindows(windows, horizonStart, horizonEnd);

        Assert.Equal(2, inverted.Count);
        // Before 09:00 is busy
        Assert.Equal(horizonStart, inverted[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), inverted[0].End);
        // After 18:00 is busy
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero), inverted[1].Start);
        Assert.Equal(horizonEnd, inverted[1].End);
    }

    [Fact]
    public void InvertAvailableWindows_EmptyWindows_CoversEntireHorizon()
    {
        var horizonStart = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var horizonEnd = new DateTimeOffset(2026, 6, 1, 17, 0, 0, TimeSpan.Zero);

        var inverted = SchedulingEngine.InvertAvailableWindows(new List<TimeSlot>(), horizonStart, horizonEnd);

        Assert.Single(inverted);
        Assert.Equal(horizonStart, inverted[0].Start);
        Assert.Equal(horizonEnd, inverted[0].End);
    }

    [Fact]
    public void InvertAvailableWindows_WindowCoversEntireHorizon_ReturnsEmpty()
    {
        var horizonStart = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var horizonEnd = new DateTimeOffset(2026, 6, 1, 17, 0, 0, TimeSpan.Zero);

        var windows = new List<TimeSlot>
        {
            new(horizonStart, horizonEnd)
        };

        var inverted = SchedulingEngine.InvertAvailableWindows(windows, horizonStart, horizonEnd);

        Assert.Empty(inverted);
    }

    [Fact]
    public void ParseAiPlanJson_ParsesValidJsonWithMarkdownWrapping()
    {
        var now = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var horizonEnd = now.AddDays(7);

        var rawResponse = @"Here is your optimized schedule:
```json
[
  {
    ""title"": ""Refactor authentication module"",
    ""startsAt"": ""2026-06-01T09:00:00Z"",
    ""endsAt"": ""2026-06-01T11:00:00Z"",
    ""reason"": ""High priority task scheduled during prime morning focus window""
  },
  {
    ""title"": ""Review PRs"",
    ""startsAt"": ""2026-06-01T14:00:00Z"",
    ""endsAt"": ""2026-06-01T15:00:00Z"",
    ""reason"": ""Scheduled after lunch""
  }
]
```
Let me know if you need changes.";

        var parsed = PlanningModelService.ParseAiPlanJson(rawResponse, now, horizonEnd);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Refactor authentication module", parsed[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), parsed[0].StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero), parsed[0].EndsAt);
        Assert.Equal("High priority task scheduled during prime morning focus window", parsed[0].Reason);
    }

    [Fact]
    public void ParseAiPlanJson_FiltersOutPastAndInvertedIntervals()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var horizonEnd = now.AddDays(7);

        var raw = @"[
  {
    ""title"": ""Past task"",
    ""startsAt"": ""2026-05-30T09:00:00Z"",
    ""endsAt"": ""2026-05-30T10:00:00Z"",
    ""reason"": ""Past""
  },
  {
    ""title"": ""End before start"",
    ""startsAt"": ""2026-06-02T11:00:00Z"",
    ""endsAt"": ""2026-06-02T10:00:00Z"",
    ""reason"": ""Invalid""
  },
  {
    ""title"": ""Valid task"",
    ""startsAt"": ""2026-06-02T14:00:00Z"",
    ""endsAt"": ""2026-06-02T15:30:00Z"",
    ""reason"": ""Valid slot""
  }
]";

        var parsed = PlanningModelService.ParseAiPlanJson(raw, now, horizonEnd);

        Assert.Single(parsed);
        Assert.Equal("Valid task", parsed[0].Title);
    }

    [Fact]
    public async Task GenerateAiPlanAsync_FallbackToGreedyEngine_WhenNoAiGatewayConfigured()
    {
        await using var db = CreateDb();
        var currentUser = CurrentUser(TestUserId);
        var service = new PlanningModelService(
            db,
            currentUser,
            confirmationService: null,
            recurrence: null,
            aiGateway: null);

        // Add a pending task
        var task = new TaskEntity
        {
            UserId = TestUserId,
            Uid = "plan-task-1@pim",
            Title = "Finish documentation",
            Priority = 1,
            Status = "NEEDS-ACTION",
            EstimatedDuration = TimeSpan.FromHours(1.5)
        };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();

        var response = await service.GenerateAiPlanAsync(
            new GenerateAiPlanRequest(HorizonDays: 3, TaskIds: null));

        Assert.NotNull(response);
        Assert.Equal("rule-engine", response.Source);
        Assert.NotEmpty(response.Placeholders);

        var placeholder = response.Placeholders[0];
        Assert.Equal("Finish documentation", placeholder.Title);
        Assert.Equal("Suggested", placeholder.Status);
        Assert.Equal("rule-engine", placeholder.Source);

        // Check it persisted to DB
        var persisted = await service.ListAiPlaceholdersAsync("Suggested");
        Assert.Single(persisted);
        Assert.Equal(placeholder.Id, persisted[0].Id);
    }

    [Fact]
    public async Task DismissAiPlaceholderAsync_MarksStatusAsDismissed()
    {
        await using var db = CreateDb();
        var currentUser = CurrentUser(TestUserId);
        var service = new PlanningModelService(
            db,
            currentUser,
            confirmationService: null,
            recurrence: null,
            aiGateway: null);

        var placeholder = new AiPlanningPlaceholderEntity
        {
            UserId = TestUserId,
            Title = "Task to dismiss",
            StartsAt = DateTimeOffset.UtcNow.AddHours(2),
            EndsAt = DateTimeOffset.UtcNow.AddHours(3),
            Reason = "Test reason",
            Source = "rule-engine",
            Status = "Suggested"
        };
        db.Set<AiPlanningPlaceholderEntity>().Add(placeholder);
        await db.SaveChangesAsync();

        var dismissed = await service.DismissAiPlaceholderAsync(placeholder.Id);

        Assert.NotNull(dismissed);
        Assert.Equal("Dismissed", dismissed.Status);

        var active = await service.ListAiPlaceholdersAsync("Suggested");
        Assert.Empty(active);
    }

    [Fact]
    public async Task ConfirmAiPlaceholderAsync_CreatesConfirmationAndMarksPending()
    {
        await using var db = CreateDb();
        var currentUser = CurrentUser(TestUserId);
        var service = new PlanningModelService(
            db,
            currentUser,
            confirmationService: null,
            recurrence: null,
            aiGateway: null);

        var placeholder = new AiPlanningPlaceholderEntity
        {
            UserId = TestUserId,
            Title = "Task to confirm",
            StartsAt = DateTimeOffset.UtcNow.AddHours(1),
            EndsAt = DateTimeOffset.UtcNow.AddHours(2),
            Reason = "Test reason",
            Source = "rule-engine",
            Status = "Suggested"
        };
        db.Set<AiPlanningPlaceholderEntity>().Add(placeholder);
        await db.SaveChangesAsync();

        var confirmation = await service.ConfirmAiPlaceholderAsync(placeholder.Id);

        Assert.NotNull(confirmation);
        Assert.Equal("calendar.ai_placeholder.confirm", confirmation.OperationType);

        var updated = await db.Set<AiPlanningPlaceholderEntity>().SingleAsync(p => p.Id == placeholder.Id);
        Assert.Equal("PendingConfirmation", updated.Status);
        Assert.Equal(confirmation.Id, updated.ConfirmationId);
    }
}
