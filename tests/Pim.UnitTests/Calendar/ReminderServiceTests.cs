using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class ReminderServiceTests
{
    private static readonly Guid UserId = Guid.Parse("78787878-7878-7878-7878-787878787878");

    [Fact]
    public async Task ReminderStoresTriggerRiskChannelsDndHistoryAndRelatedObject()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var relatedId = Guid.NewGuid();

        var reminder = await service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "confirmation",
            RelatedObjectId: relatedId,
            Title: "Review Outlook change",
            Body: "Location changed in Outlook.",
            TriggerReason: "L3 confirmation waiting",
            RiskLevel: "L3ExternalSourceOrWriteback",
            Channels: ["Web", "WindowsToast", "AndroidNotification"],
            DoNotDisturbStart: "22:00",
            DoNotDisturbEnd: "08:00",
            ScheduledAt: new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero)), CancellationToken.None);

        Assert.Equal("Review Outlook change", reminder.Title);
        Assert.Equal(relatedId, reminder.RelatedObjectId);
        Assert.Contains("WindowsToast", reminder.Channels);
        Assert.Equal("Open", reminder.Status);
        Assert.Equal("22:00", reminder.DoNotDisturbStart);
        Assert.Equal("08:00", reminder.DoNotDisturbEnd);
    }

    [Fact]
    public async Task LowRiskActionExecutesAndHighRiskActionReturnsOpenDetail()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var lowRisk = await service.CreateAsync(Request("Low", "L1LowRiskAction"), CancellationToken.None);
        var highRisk = await service.CreateAsync(Request("High", "L3ExternalSourceOrWriteback"), CancellationToken.None);

        var lowAction = await service.HandleActionAsync(lowRisk.Id, "dismiss", CancellationToken.None);
        var highAction = await service.HandleActionAsync(highRisk.Id, "confirm", CancellationToken.None);

        Assert.Equal("Executed", lowAction.Kind);
        Assert.Equal("Dismissed", lowAction.Status);
        Assert.Equal("OpenDetailRequired", highAction.Kind);
        Assert.Contains("/confirmations/", highAction.DetailUrl);
        var highRiskDeliveries = await db.Set<ReminderDeliveryEntity>()
            .Where(d => d.ReminderId == highRisk.Id)
            .ToListAsync(CancellationToken.None);
        Assert.Contains(highRiskDeliveries, d => d.Status == "OpenDetailRequired");
    }

    [Fact]
    public async Task CreateAsync_OmittedOptionalFields_SucceedsWithDefaults()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var reminder = await service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "task",
            RelatedObjectId: Guid.NewGuid(),
            Title: "MCP reminder without channels",
            Body: null!,
            TriggerReason: null!,
            RiskLevel: null!,
            Channels: null!,
            DoNotDisturbStart: null,
            DoNotDisturbEnd: null,
            ScheduledAt: DateTimeOffset.Parse("2026-09-07T12:00:00Z")), CancellationToken.None);

        Assert.Equal("MCP reminder without channels", reminder.Title);
        Assert.Empty(reminder.Body);
        Assert.Empty(reminder.TriggerReason);
        Assert.Equal("L1LowRiskAction", reminder.RiskLevel);
        Assert.Empty(reminder.Channels);
        Assert.Null(reminder.DoNotDisturbStart);
        Assert.Null(reminder.DoNotDisturbEnd);
        Assert.Equal("Open", reminder.Status);
    }

    [Fact]
    public async Task CreateAsync_EmptyChannels_NormalizesToEmptyArray()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var reminder = await service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "task",
            RelatedObjectId: Guid.NewGuid(),
            Title: "Reminder with empty channels",
            Body: "Body",
            TriggerReason: "test",
            RiskLevel: null!,
            Channels: [],
            DoNotDisturbStart: null,
            DoNotDisturbEnd: null,
            ScheduledAt: DateTimeOffset.Parse("2026-09-07T12:00:00Z")), CancellationToken.None);

        Assert.Empty(reminder.Channels);
    }

    [Fact]
    public async Task CreateAsync_JsonBodyWithOmittedOptionalFields_BindsNullAndSucceeds()
    {
        // Contract-level regression (issue #196): MCP 转发层在 Python _clean_params 语义下
        // 不会发送 channels/body/triggerReason/riskLevel；ASP.NET Json 绑定对缺失字段得到
        // null 而非报错。此测试模拟该绑定形态（Web 命名策略），确保服务层 null-safe。
        var json = """
            {
              "relatedObjectType": "task",
              "relatedObjectId": "4e2e35a7-4488-4c12-b455-a7b41b728e85",
              "title": "MCP reminder without channels",
              "scheduledAt": "2026-09-07T12:00:00Z"
            }
            """;

        var request = JsonSerializer.Deserialize<CreateReminderRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("deserialization failed");
        Assert.Null(request.Body);
        Assert.Null(request.TriggerReason);
        Assert.Null(request.RiskLevel);
        Assert.Null(request.Channels);

        await using var db = CreateDb();
        var service = CreateService(db);
        var reminder = await service.CreateAsync(request, CancellationToken.None);

        Assert.Equal("MCP reminder without channels", reminder.Title);
        Assert.Empty(reminder.Body);
        Assert.Empty(reminder.TriggerReason);
        Assert.Equal("L1LowRiskAction", reminder.RiskLevel);
        Assert.Empty(reminder.Channels);
        Assert.Equal("Open", reminder.Status);
    }

    [Fact]
    public async Task CreateAsync_BlankAndDuplicateChannels_NormalizesAndDedupes()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var reminder = await service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "task",
            RelatedObjectId: Guid.NewGuid(),
            Title: "Dedupe channels",
            Body: "Body",
            TriggerReason: "test",
            RiskLevel: "LOW",
            Channels: ["web", "WEB", "  ", "desktop"],
            DoNotDisturbStart: null,
            DoNotDisturbEnd: null,
            ScheduledAt: DateTimeOffset.Parse("2026-09-07T12:00:00Z")), CancellationToken.None);

        Assert.Equal(2, reminder.Channels.Count);
        Assert.Contains(reminder.Channels, c => c.Equals("web", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reminder.Channels, c => c.Equals("desktop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reminder.Channels, c => string.IsNullOrWhiteSpace(c));
    }

    [Fact]
    public async Task CreateAsync_GuidEmptyRelatedObject_Rejects()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "task",
            RelatedObjectId: Guid.Empty,
            Title: "Invalid reminder",
            Body: "Body",
            TriggerReason: "test",
            RiskLevel: "L1LowRiskAction",
            Channels: ["Web"],
            DoNotDisturbStart: null,
            DoNotDisturbEnd: null,
            ScheduledAt: DateTimeOffset.Parse("2026-09-07T12:00:00Z")), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_MissingScheduledAt_Rejects()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "task",
            RelatedObjectId: Guid.NewGuid(),
            Title: "Reminder without scheduledAt",
            Body: "Body",
            TriggerReason: "test",
            RiskLevel: "L1LowRiskAction",
            Channels: ["Web"],
            DoNotDisturbStart: null,
            DoNotDisturbEnd: null,
            ScheduledAt: null), CancellationToken.None));
    }

    [Fact]
    public async Task HardAction_CaseInsensitive_Matches()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var highRisk = await service.CreateAsync(Request("High", "L3ExternalSourceOrWriteback"), CancellationToken.None);

        // "Confirm" 大写应被视为非 allow 动作 → OpenDetailRequired
        var result = await service.HandleActionAsync(highRisk.Id, "Confirm", CancellationToken.None);

        Assert.Equal("OpenDetailRequired", result.Kind);
        Assert.Contains("/confirmations/", result.DetailUrl);
    }

    [Fact]
    public async Task LowAction_CaseInsensitive_Executes()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var lowRisk = await service.CreateAsync(Request("Low", "L1LowRiskAction"), CancellationToken.None);

        // "Dismiss" 大写应视为 dismiss → Dismissed
        var result = await service.HandleActionAsync(lowRisk.Id, "Dismiss", CancellationToken.None);

        Assert.Equal("Executed", result.Kind);
        Assert.Equal("Dismissed", result.Status);
    }

    private static CreateReminderRequest Request(string title, string risk)
        => new(
            "confirmation",
            Guid.NewGuid(),
            title,
            "Body",
            "Test trigger",
            risk,
            ["Web"],
            null,
            null,
            DateTimeOffset.UtcNow);

    private static ReminderService CreateService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId));

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReminderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"reminder-service-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
