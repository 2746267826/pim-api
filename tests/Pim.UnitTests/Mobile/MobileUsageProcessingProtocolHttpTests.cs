using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Mobile;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// Issue #539 processing/backoff protocol at the real HTTP boundary: the responses go
/// through routing, authentication, and the actual endpoint status-code/header logic
/// (<c>Results.Json(202)</c> + Retry-After), not just the service layer.
/// </summary>
public sealed class MobileUsageProcessingProtocolHttpTests : IClassFixture<SyntheticMobileApiHost>
{
    private static readonly DateTimeOffset WindowStart =
        DateTimeOffset.Parse("2026-10-30T00:00:00Z");
    private static readonly DateTimeOffset WindowEnd = WindowStart.AddHours(1);
    private static readonly DateTimeOffset EventAt = WindowStart.AddMinutes(30);

    private readonly SyntheticMobileApiHost _host;

    public MobileUsageProcessingProtocolHttpTests(SyntheticMobileApiHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task ActiveLease_Returns202InFlightEnvelopeWithRetryAfterHeader()
    {
        var ownerId = Guid.Parse("53911111-1111-4111-8111-111111111111");
        var request = Request(ownerId, "qa-539-http-in-flight");
        await SeedPendingBatchAsync(
            ownerId,
            request,
            MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                [],
                MobileUsageIngestService.ComputePayloadSha256(request),
                DateTimeOffset.UtcNow.AddMinutes(10),
                attemptCount: 1,
                leaseId: Guid.NewGuid().ToString("N")));

        using var client = _host.CreateClient(ownerId);
        var response = await client.PostAsJsonAsync(MobileEndpointPaths.UsageEvents, request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("in-flight", data.GetProperty("processingState").GetString());
        Assert.True(data.GetProperty("retryAfterSeconds").GetInt32() >= 1);
        Assert.True(data.TryGetProperty("leaseUntilUtc", out _));
        Assert.False(
            data.TryGetProperty("retryAtUtc", out _),
            "in-flight must not expose retryAtUtc");
        Assert.Equal(2, data.GetProperty("failedCount").GetInt32());
        Assert.Empty(data.GetProperty("itemResults").EnumerateArray());
        Assert.Equal(
            TimeSpan.FromSeconds(data.GetProperty("retryAfterSeconds").GetInt32()),
            response.Headers.RetryAfter!.Delta!.Value);

        await AssertPendingRowUnchangedAsync(ownerId, request);
    }

    [Fact]
    public async Task RetryCooldown_Returns202RetryScheduledEnvelopeWithRetryAfterHeader()
    {
        var ownerId = Guid.Parse("53922222-2222-4222-8222-222222222222");
        var request = Request(ownerId, "qa-539-http-retry-scheduled");
        await SeedPendingBatchAsync(
            ownerId,
            request,
            MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                [],
                MobileUsageIngestService.ComputePayloadSha256(request),
                attemptCount: 1,
                leaseId: Guid.NewGuid().ToString("N"),
                retryAfterUtc: DateTimeOffset.UtcNow.AddMinutes(5)));

        using var client = _host.CreateClient(ownerId);
        var response = await client.PostAsJsonAsync(MobileEndpointPaths.UsageEvents, request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        using var body = await ReadJsonAsync(response);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("retry-scheduled", data.GetProperty("processingState").GetString());
        Assert.True(data.GetProperty("retryAfterSeconds").GetInt32() >= 1);
        Assert.True(data.TryGetProperty("retryAtUtc", out _));
        Assert.False(
            data.TryGetProperty("leaseUntilUtc", out _),
            "retry-scheduled must not expose leaseUntilUtc");
        Assert.Equal(2, data.GetProperty("failedCount").GetInt32());
        Assert.Empty(data.GetProperty("itemResults").EnumerateArray());
        Assert.Equal(
            TimeSpan.FromSeconds(data.GetProperty("retryAfterSeconds").GetInt32()),
            response.Headers.RetryAfter!.Delta!.Value);

        await AssertPendingRowUnchangedAsync(ownerId, request);
    }

    [Fact]
    public async Task TerminalReplay_Returns200WithoutAnyProcessingFields()
    {
        var ownerId = Guid.Parse("53933333-3333-4333-8333-333333333333");
        var request = Request(ownerId, "qa-539-http-terminal-replay");
        await SeedTerminalBatchAsync(ownerId, request);

        using var client = _host.CreateClient(ownerId);
        var response = await client.PostAsJsonAsync(MobileEndpointPaths.UsageEvents, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        using var body = await ReadJsonAsync(response);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("acceptedCount").GetInt32());
        Assert.False(data.TryGetProperty("processingState", out _));
        Assert.False(data.TryGetProperty("retryAfterSeconds", out _));
        Assert.False(data.TryGetProperty("leaseUntilUtc", out _));
        Assert.False(data.TryGetProperty("retryAtUtc", out _));
    }

    private async Task SeedPendingBatchAsync(
        Guid ownerId,
        MobileUsageEventsUploadRequest request,
        string errorJson)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = ownerId,
            DeviceId = request.DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = errorJson,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private async Task SeedTerminalBatchAsync(
        Guid ownerId,
        MobileUsageEventsUploadRequest request)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = ownerId,
            DeviceId = request.DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            AcceptedCount = 1,
            FailedCount = 0,
            Status = MobileSyncBatchStatus.Completed,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                [
                    new MobileIngestItemResult(
                        "event-1",
                        "event",
                        "accepted",
                        "0",
                        "ok")
                ],
                []),
            CreatedAt = WindowEnd.AddMinutes(1),
            CompletedAtUtc = WindowEnd.AddMinutes(2)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private async Task AssertPendingRowUnchangedAsync(
        Guid ownerId,
        MobileUsageEventsUploadRequest request)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var row = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.UserId == ownerId && row.BatchId == request.BatchId);
        Assert.Equal(MobileSyncBatchStatus.Pending, row.Status);
        Assert.Equal(0, row.AcceptedCount);
        Assert.Equal(0, row.FailedCount);
        Assert.Null(row.CompletedAtUtc);
    }

    private static MobileUsageEventsUploadRequest Request(Guid ownerId, string batchId)
        => new(
            $"qa-synthetic-539-{ownerId:N}"[..40],
            batchId,
            WindowStart,
            WindowEnd,
            [
                new MobileAppMetadataDto(
                    "com.example.synthetic.539",
                    "Synthetic 539",
                    "1.0",
                    1,
                    false,
                    "tools",
                    null,
                    null,
                    null,
                    "{}",
                    $"app-{batchId}")
            ],
            [
                new MobileUsageEventDto(
                    "com.example.synthetic.539",
                    "USER_INTERACTION",
                    EventAt,
                    "Synthetic539Activity",
                    WindowEnd,
                    "{}",
                    $"event-{batchId}")
            ],
            []);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
