using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// HTTP boundary regression for the mobile half-open window and same-id replay.
/// The host uses the normal routing, authentication, middleware, and mobile service
/// paths, with a process-local synthetic database.
/// </summary>
public sealed class MobileSyncHttpRecoveryTests : IClassFixture<SyntheticMobileApiHost>
{
    private static readonly DateTimeOffset WindowStart =
        DateTimeOffset.Parse("2026-10-30T00:00:00Z");
    private static readonly DateTimeOffset WindowEnd = WindowStart.AddHours(1);
    private readonly SyntheticMobileApiHost _host;

    public MobileSyncHttpRecoveryTests(SyntheticMobileApiHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task EventAtExclusiveEnd_Returns400AndDoesNotCreatePendingBatch()
    {
        const string batchId = "qa-525-event-at-exclusive-end";
        const string deviceId = "qa-synthetic-mobile-exclusive-end-20261030";
        var ownerId = Guid.Parse("52533333-3333-4333-8333-333333333331");
        using var client = _host.CreateClient(ownerId);
        var response = await client.PostAsJsonAsync(
            MobileEndpointPaths.UsageEvents,
            Request(deviceId, batchId, WindowEnd, eventAt: WindowEnd));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(40001, body.RootElement.GetProperty("code").GetInt32());
        Assert.Contains(
            "使用事件时间",
            body.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await db.Set<MobileSyncBatchEntity>()
            .AnyAsync(row => row.BatchId == batchId));
        Assert.False(await db.Set<MobileUsageEventEntity>()
            .AnyAsync(row => row.UserId == ownerId && row.DeviceId == deviceId));
    }

    [Fact]
    public async Task CorrectedEnd_Returns200AndPersistsCountsAndRows()
    {
        const string batchId = "qa-525-corrected-end";
        const string deviceId = "qa-synthetic-mobile-corrected-end-20261030";
        var ownerId = Guid.Parse("52533333-3333-4333-8333-333333333332");
        var exclusiveWindowEnd = WindowEnd.AddMilliseconds(1);
        using var client = _host.CreateClient(ownerId);
        var response = await client.PostAsJsonAsync(
            MobileEndpointPaths.UsageEvents,
            Request(deviceId, batchId, exclusiveWindowEnd, eventAt: WindowEnd));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("acceptedCount").GetInt32());
        Assert.Equal(0, data.GetProperty("failedCount").GetInt32());
        Assert.Equal(0, data.GetProperty("rejectedCount").GetInt32());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var batch = await db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.BatchId == batchId);
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
        Assert.Equal(exclusiveWindowEnd, batch.WindowEndUtc);
        Assert.Equal(2, batch.AcceptedCount);
        Assert.Equal(0, batch.FailedCount);
        Assert.Equal(1, await db.Set<MobileAppCatalogEntity>()
            .CountAsync(row => row.UserId == ownerId && row.DeviceId == deviceId));
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>()
            .CountAsync(row => row.UserId == ownerId
                && row.DeviceId == deviceId
                && row.EventTimestampUtc == WindowEnd));
        var usageEvent = await db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .SingleAsync(row => row.UserId == ownerId && row.DeviceId == deviceId);
        Assert.Equal(WindowEnd, usageEvent.EventTimestampUtc);
    }

    [Fact]
    public async Task SameBatchAndPayloadReplay_DoesNotWriteDuplicates()
    {
        const string batchId = "qa-525-same-id-replay";
        const string deviceId = "qa-synthetic-mobile-same-id-20261030";
        var ownerId = Guid.Parse("52533333-3333-4333-8333-333333333333");
        using var client = _host.CreateClient(ownerId);
        var request = Request(
            deviceId,
            batchId,
            WindowEnd.AddMilliseconds(1),
            eventAt: WindowEnd);

        var first = await client.PostAsJsonAsync(MobileEndpointPaths.UsageEvents, request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var firstBody = await ReadJsonAsync(first);
        var firstData = firstBody.RootElement.GetProperty("data").Clone();
        var firstItemResults = firstData.GetProperty("itemResults").GetRawText();

        var second = await client.PostAsJsonAsync(MobileEndpointPaths.UsageEvents, request);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondBody = await ReadJsonAsync(second);
        var secondData = secondBody.RootElement.GetProperty("data");
        Assert.Equal(firstData.GetProperty("acceptedCount").GetInt32(),
            secondData.GetProperty("acceptedCount").GetInt32());
        Assert.Equal(firstItemResults, secondData.GetProperty("itemResults").GetRawText());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Equal(1, await db.Set<MobileSyncBatchEntity>()
            .CountAsync(row => row.BatchId == batchId));
        Assert.Equal(1, await db.Set<MobileUsageEventEntity>()
            .CountAsync(row => row.UserId == ownerId
                && row.DeviceId == deviceId
                && row.EventTimestampUtc == WindowEnd));
        Assert.Equal(1, await db.Set<MobileAppCatalogEntity>()
            .CountAsync(row => row.UserId == ownerId && row.DeviceId == deviceId));
    }

    private static object Request(
        string deviceId,
        string batchId,
        DateTimeOffset windowEnd,
        DateTimeOffset eventAt)
        => new
        {
            deviceId,
            clientBatchId = batchId,
            sourceWindowStartUtc = WindowStart,
            sourceWindowEndUtc = windowEnd,
            apps = new[]
            {
                new
                {
                    packageName = "com.example.synthetic.usage",
                    displayName = "Synthetic Usage",
                    versionName = "1.0",
                    versionCode = 1,
                    isSystemApp = false,
                    categoryName = "tools",
                    installerPackageName = (string?)null,
                    firstInstallTimeUtc = (DateTimeOffset?)null,
                    lastUpdateTimeUtc = (DateTimeOffset?)null,
                    rawJson = "{}",
                    clientItemKey = $"app-{batchId}"
                }
            },
            events = new[]
            {
                new
                {
                    packageName = "com.example.synthetic.usage",
                    eventType = "USER_INTERACTION",
                    eventTimestampUtc = eventAt,
                    className = "SyntheticActivity",
                    collectedAtUtc = WindowEnd,
                    rawJson = "{}",
                    clientItemKey = $"event-{batchId}"
                }
            },
            fallbackSummaries = Array.Empty<object>()
        };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

public sealed class SyntheticMobileApiHost : IDisposable
{
    public SyntheticMobileApiHost()
    {
        var databaseName = $"qa-mobile-half-open-{Guid.NewGuid():N}";
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-dp-{Guid.NewGuid():N}"));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    item => item.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        });

        _ = Factory.Services.GetRequiredService<JwtService>();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient CreateClient(Guid userId)
    {
        var client = Factory.CreateClient();
        var jwt = Factory.Services.GetRequiredService<JwtService>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            jwt.GenerateAccessToken(userId, $"qa-synthetic-owner-{userId:N}"[..24], "user"));
        return client;
    }

    public void Dispose() => Factory.Dispose();
}
