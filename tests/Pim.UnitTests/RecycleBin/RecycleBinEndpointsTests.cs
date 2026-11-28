using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.DTOs;
using Pim.Core.Common;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Api;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.RecycleBin;

public sealed class RecycleBinEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = AuthRegistrationTestHost.Create(
        AuthRegistrationTestHost.NewDbName("q03-http"));

    [Fact]
    public async Task UnifiedRoutes_RequireAuthenticationAndRejectUnsupportedDomain()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/recycle-bin")).StatusCode);

        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "q03-http-user");
        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);

        var listed = await client.GetAsync("/api/v1/recycle-bin?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = (await listed.Content.ReadFromJsonAsync<ApiResponse<RecycleBinPageDto>>())!.Data!;
        Assert.Empty(page.Items);
        Assert.Equal(4, page.Groups.Count);

        var invalid = await client.GetAsync("/api/v1/recycle-bin?domain=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(40001, (await invalid.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);

        var empty = await client.PostAsJsonAsync(
            "/api/v1/recycle-bin/restore",
            new RecycleBinBatchRequest([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(40001, (await empty.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);
    }

    [Fact]
    public async Task BatchRestore_SameOperationIdReplaysReceiptAndDifferentPayloadIs409()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
            _factory, "q03-http-operation");
        Guid eventId;
        DateTimeOffset deletedAt;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var calendar = new CalendarEntity
            {
                UserId = session.UserId,
                Name = "幂等回执日历",
                Kind = "calendar",
                Color = "#2563EB"
            };
            var deletedAtValue = DateTimeOffset.UtcNow;
            var evt = new EventEntity
            {
                Calendar = calendar,
                CalendarId = calendar.Id,
                Title = "幂等回执日程",
                Uid = Guid.NewGuid().ToString("N"),
                DtStart = deletedAtValue.AddHours(1),
                DtEnd = deletedAtValue.AddHours(2),
                DeletedAt = deletedAtValue,
                DeletedByOperationId = Guid.NewGuid(),
                DeletedByOperationKind = "single"
            };
            db.AddRange(calendar, evt);
            await db.SaveChangesAsync();
            eventId = evt.Id;
            deletedAt = deletedAtValue;
        }

        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);
        var operationId = Guid.NewGuid();
        var request = new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Calendar, eventId, deletedAt)
        ], operationId);

        var firstResponse = await client.PostAsJsonAsync("/api/v1/recycle-bin/restore", request);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = (await firstResponse.Content
            .ReadFromJsonAsync<ApiResponse<RecycleBinBatchReceiptDto>>())!.Data!;
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(first.Items).Status);

        var replayResponse = await client.PostAsJsonAsync("/api/v1/recycle-bin/restore", request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = (await replayResponse.Content
            .ReadFromJsonAsync<ApiResponse<RecycleBinBatchReceiptDto>>())!.Data!;
        Assert.Equal(first.ReceiptId, replay.ReceiptId);
        Assert.Equal(operationId, replay.OperationId);
        Assert.Equal(first.Items, replay.Items);

        var conflictResponse = await client.PostAsJsonAsync(
            "/api/v1/recycle-bin/restore",
            new RecycleBinBatchRequest(
            [
                new RecycleBinItemRef(
                    RecycleBinDomains.Calendar,
                    eventId,
                    deletedAt.AddSeconds(-1))
            ], operationId));
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        var (code, _) = await AuthRegistrationTestHost.ReadErrorAsync(conflictResponse);
        Assert.Equal(40902, code);
    }

    public void Dispose() => _factory.Dispose();
}
