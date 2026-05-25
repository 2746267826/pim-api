using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Operations;

public class AuditAndConfirmationServiceTests
{
    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    [Fact]
    public async Task AuditLogService_RecordsAudit()
    {
        await using var db = CreateDb();
        var service = new AuditLogService(db);

        var audit = await service.RecordAsync(new CreateAuditLogRequest(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AuditActorType.User,
            "calendar.event.delete",
            "calendar_event",
            "event-1",
            "web",
            AuditResult.Success,
            "127.0.0.1",
            "UnitTest",
            "corr-1",
            new Dictionary<string, string> { ["reason"] = "test" },
            null,
            null));

        Assert.NotEqual(Guid.Empty, audit.Id);
        Assert.Equal("calendar.event.delete", audit.Action);
        Assert.Equal(1, await db.AuditLogs.CountAsync());

        var entity = await db.AuditLogs.SingleAsync();
        using var metadata = JsonDocument.Parse(entity.MetadataJson);
        Assert.Equal("test", metadata.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task OperationConfirmationService_HandlesLifecycle()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{\"count\":1}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-2"));

        var confirmed = await service.ConfirmAsync(created.Id, created.RequestedByUserId);
        var executed = await service.MarkExecutedAsync(created.Id, "{\"ok\":true}");

        Assert.Equal(OperationConfirmationStatus.Pending, created.Status);
        Assert.Equal(OperationConfirmationStatus.Confirmed, confirmed.Status);
        Assert.Equal(OperationConfirmationStatus.Executed, executed.Status);
        Assert.NotNull(executed.ExecutedAt);
    }

    [Fact]
    public async Task OperationConfirmationService_ExpiresOldPendingRecords()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move files",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "corr-3"));

        var expired = await service.ExpireOldAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, expired);
        Assert.Equal(OperationConfirmationStatus.Expired.ToString(), (await db.OperationConfirmations.SingleAsync()).Status);
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsPendingConfirmation()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-4"));

        var rejected = await service.RejectAsync(created.Id, created.RequestedByUserId);

        Assert.Equal(OperationConfirmationStatus.Rejected, rejected.Status);
        Assert.Equal(OperationConfirmationStatus.Rejected.ToString(), (await db.OperationConfirmations.SingleAsync()).Status);
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsWrongUserConfirmAndReject()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);
        var ownerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var otherUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var confirmTarget = await service.CreateAsync(new CreateOperationConfirmationRequest(
            ownerId,
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-5"));
        var rejectTarget = await service.CreateAsync(new CreateOperationConfirmationRequest(
            ownerId,
            "file.move",
            "Move files",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-6"));

        var confirmError = await Assert.ThrowsAsync<DomainException>(
            () => service.ConfirmAsync(confirmTarget.Id, otherUserId));
        var rejectError = await Assert.ThrowsAsync<DomainException>(
            () => service.RejectAsync(rejectTarget.Id, otherUserId));

        Assert.Equal(3005, confirmError.ErrorCode);
        Assert.Equal(3005, rejectError.ErrorCode);
        Assert.Equal(2, await db.OperationConfirmations.CountAsync(c => c.Status == OperationConfirmationStatus.Pending.ToString()));
    }

    [Fact]
    public async Task OperationConfirmationService_ListsPendingForUserWithoutOtherUsersConfirmations()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);
        var ownerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var otherUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var owner = await service.CreateAsync(new CreateOperationConfirmationRequest(
            ownerId,
            "outlook.write",
            "Write owner event",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(20),
            "corr-user-list-owner"));
        var system = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move system files",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "corr-user-list-system"));
        await service.CreateAsync(new CreateOperationConfirmationRequest(
            otherUserId,
            "outlook.delete",
            "Delete other event",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(5),
            "corr-user-list-other"));

        var pending = await service.ListPendingForUserAsync(ownerId);

        Assert.Equal(new[] { system.Id, owner.Id }, pending.Select(c => c.Id));
    }

    [Fact]
    public async Task OperationConfirmationService_ListsOnlySystemPendingRecordsWithoutUser()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);
        var ownerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        await service.CreateAsync(new CreateOperationConfirmationRequest(
            ownerId,
            "outlook.write",
            "Write owner event",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(5),
            "corr-system-list-owner"));
        var system = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move system files",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "corr-system-list-system"));

        var pending = await service.ListPendingForUserAsync(null);

        Assert.Single(pending);
        Assert.Equal(system.Id, pending[0].Id);
    }

    [Fact]
    public async Task OperationConfirmationService_AllowsAuthenticatedUserToRejectSystemConfirmation()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move files",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-7b"));

        var rejected = await service.RejectAsync(created.Id, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));

        Assert.Equal(OperationConfirmationStatus.Rejected, rejected.Status);
    }

    [Fact]
    public async Task OperationConfirmationService_AllowsGlobalConfirmationWithoutUser()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move files",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-7"));

        var confirmed = await service.ConfirmAsync(created.Id, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));

        Assert.Equal(OperationConfirmationStatus.Confirmed, confirmed.Status);
    }

    [Fact]
    public async Task OperationConfirmationService_DoesNotListExpiredPendingRecordsAndPersistsExpiredStatus()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var expired = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move expired",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "corr-8"));
        var active = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.copy",
            "Copy active",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "corr-9"));

        var pending = await service.ListPendingAsync();

        var expiredEntity = await db.OperationConfirmations.SingleAsync(c => c.Id == expired.Id);
        Assert.Single(pending);
        Assert.Equal(active.Id, pending[0].Id);
        Assert.Equal(OperationConfirmationStatus.Expired.ToString(), expiredEntity.Status);
    }

    [Fact]
    public async Task OperationConfirmationService_ListsPendingRecordsOrderedByExpiration()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var later = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.move",
            "Move later",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-10"));
        var earlier = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "file.copy",
            "Copy earlier",
            OperationRiskLevel.High,
            "job",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "corr-11"));

        var pending = await service.ListPendingAsync();

        Assert.Equal(new[] { earlier.Id, later.Id }, pending.Select(c => c.Id));
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsRepeatedAndNonPendingTransitions()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-12"));

        await service.ConfirmAsync(created.Id, null);

        var confirmAgainError = await Assert.ThrowsAsync<DomainException>(
            () => service.ConfirmAsync(created.Id, null));
        var rejectAfterConfirmError = await Assert.ThrowsAsync<DomainException>(
            () => service.RejectAsync(created.Id, null));

        Assert.Equal(3003, confirmAgainError.ErrorCode);
        Assert.Equal(3003, rejectAfterConfirmError.ErrorCode);
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsExecutingPendingRecords()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-13"));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.MarkExecutedAsync(created.Id, "{}"));

        Assert.Equal(3002, error.ErrorCode);
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsConfirmingExpiredRecordsAndPersistsExpiredStatus()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "corr-14"));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.ConfirmAsync(created.Id, null));

        Assert.Equal(3004, error.ErrorCode);
        Assert.Equal(OperationConfirmationStatus.Expired.ToString(), (await db.OperationConfirmations.SingleAsync()).Status);
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsInvalidPayloadJson()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.CreateAsync(new CreateOperationConfirmationRequest(
                null,
                "outlook.write",
                "Write event to Outlook",
                OperationRiskLevel.High,
                "web",
                "{invalid",
                "{}",
                DateTimeOffset.UtcNow.AddMinutes(30),
                "corr-15")));

        Assert.Equal(3006, error.ErrorCode);
        Assert.Equal(0, await db.OperationConfirmations.CountAsync());
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsInvalidPreviewJson()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.CreateAsync(new CreateOperationConfirmationRequest(
                null,
                "outlook.write",
                "Write event to Outlook",
                OperationRiskLevel.High,
                "web",
                "{}",
                "{invalid",
                DateTimeOffset.UtcNow.AddMinutes(30),
                "corr-16")));

        Assert.Equal(3007, error.ErrorCode);
        Assert.Equal(0, await db.OperationConfirmations.CountAsync());
    }

    [Fact]
    public async Task OperationConfirmationService_RejectsInvalidResultJson()
    {
        await using var db = CreateDb();
        var service = new OperationConfirmationService(db);

        var created = await service.CreateAsync(new CreateOperationConfirmationRequest(
            null,
            "outlook.write",
            "Write event to Outlook",
            OperationRiskLevel.High,
            "web",
            "{}",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "corr-17"));

        await service.ConfirmAsync(created.Id, null);
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.MarkExecutedAsync(created.Id, "{invalid"));

        Assert.Equal(3008, error.ErrorCode);
        Assert.Null((await db.OperationConfirmations.SingleAsync()).ExecutedAt);
    }
}
