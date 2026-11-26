using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// P05 历史位置纠正的 HTTP 边界：在真实路由、鉴权、异常中间件和服务注册上验证
/// 401/400/404/409、跨用户隔离，以及分段合并的幂等/冲突语义。
/// </summary>
public sealed class MobileLocationCorrectionHttpTests : IClassFixture<SyntheticMobileApiHost>
{
    private const string DeviceId = "qa-location-correction-http";
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.UtcNow.AddHours(-1);

    private readonly SyntheticMobileApiHost _host;

    public MobileLocationCorrectionHttpTests(SyntheticMobileApiHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task ProtectedLocationRoutes_WithoutTokenReturn401()
    {
        using var client = _host.Factory.CreateClient();

        var list = await client.GetAsync($"{MobileEndpointPaths.LocationPoints}?{RangeQuery()}");
        var patch = await client.PatchAsJsonAsync(
            $"{MobileEndpointPaths.LocationPoints}/{Guid.NewGuid()}",
            new { latitude = 31.2, longitude = 121.4, revision = 0 });
        var merge = await client.PostAsJsonAsync(
            "/api/v1/mobile/location/segments/merge",
            SegmentMergeRequest(
                new SegmentMergeFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                DeviceId,
                "move"));

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, merge.StatusCode);
    }

    [Fact]
    public async Task GetPoints_ReturnsOnlyCurrentUsersPoints()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000001");
        var otherId = Guid.Parse("62040000-0000-4000-8000-000000000002");
        var ownerPoint = await SeedPointAsync(ownerId, "owner-device", RecordedAt);
        var otherPoint = await SeedPointAsync(otherId, "other-device", RecordedAt);

        using var ownerClient = _host.CreateClient(ownerId);
        using var otherClient = _host.CreateClient(otherId);

        var ownerPage = await ReadPageAsync(
            await ownerClient.GetAsync($"{MobileEndpointPaths.LocationPoints}?{RangeQuery()}"));
        var otherPage = await ReadPageAsync(
            await otherClient.GetAsync($"{MobileEndpointPaths.LocationPoints}?{RangeQuery()}"));

        Assert.Equal(ownerPoint.Id, Assert.Single(ownerPage).GetProperty("id").GetGuid());
        Assert.Equal(otherPoint.Id, Assert.Single(otherPage).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Correction_InvalidCoordinatesReturn400WithoutMutating()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000003");
        var point = await SeedPointAsync(ownerId, DeviceId, RecordedAt);
        using var client = _host.CreateClient(ownerId);

        var response = await client.PatchAsJsonAsync(
            $"{MobileEndpointPaths.LocationPoints}/{point.Id}",
            new { latitude = 91, longitude = 121.4, revision = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(6201, body.RootElement.GetProperty("code").GetInt32());
        var stored = await ReadPointAsync(point.Id);
        Assert.Equal(0, stored.Revision);
        Assert.Equal(31.230416m, stored.Latitude);
    }

    [Fact]
    public async Task Correction_OtherUsersPointReturns404WithoutMutating()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000004");
        var otherId = Guid.Parse("62040000-0000-4000-8000-000000000005");
        var point = await SeedPointAsync(otherId, "other-owned-device", RecordedAt);
        using var client = _host.CreateClient(ownerId);

        var response = await client.PatchAsJsonAsync(
            $"{MobileEndpointPaths.LocationPoints}/{point.Id}",
            new { latitude = 31.25, longitude = 121.5, revision = 0 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(40401, body.RootElement.GetProperty("code").GetInt32());
        var stored = await ReadPointAsync(point.Id);
        Assert.Equal(0, stored.Revision);
        Assert.Equal(31.230416m, stored.Latitude);
    }

    [Fact]
    public async Task Correction_StaleRevisionReturns409ThenLatestRevisionSucceeds()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000006");
        var point = await SeedPointAsync(ownerId, DeviceId, RecordedAt);
        using var client = _host.CreateClient(ownerId);
        var path = $"{MobileEndpointPaths.LocationPoints}/{point.Id}";

        var first = await client.PatchAsJsonAsync(
            path,
            new { latitude = 31.24, longitude = 121.48, revision = 0 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await client.PatchAsJsonAsync(
            path,
            new { latitude = 31.25, longitude = 121.49, revision = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()))
            Assert.Equal(6204, body.RootElement.GetProperty("code").GetInt32());

        var latest = await ReadPointAsync(point.Id);
        Assert.Equal(1, latest.Revision);
        Assert.Equal(31.24m, latest.Latitude);

        var retried = await client.PatchAsJsonAsync(
            path,
            new { latitude = 31.25, longitude = 121.49, revision = latest.Revision });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var updated = await ReadPointAsync(point.Id);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(31.25m, updated.Latitude);
    }

    [Fact]
    public async Task SegmentMerge_SameKindRetrySucceedsAndDifferentKindReturns409()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000007");
        var fixture = await SeedSegmentMergeAsync(ownerId, DeviceId, "move");
        using var client = _host.CreateClient(ownerId);
        var request = SegmentMergeRequest(fixture, DeviceId, "move");

        var first = await client.PostAsJsonAsync("/api/v1/mobile/location/segments/merge", request);
        var retried = await client.PostAsJsonAsync("/api/v1/mobile/location/segments/merge", request);
        var differentKind = await client.PostAsJsonAsync(
            "/api/v1/mobile/location/segments/merge",
            SegmentMergeRequest(fixture, DeviceId, "stay"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, differentKind.StatusCode);
        using var body = JsonDocument.Parse(await differentKind.Content.ReadAsStringAsync());
        Assert.Equal(6205, body.RootElement.GetProperty("code").GetInt32());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var stored = Assert.Single(await db.Set<MobileLocationSegmentMergeEntity>()
            .Where(merge => merge.UserId == ownerId)
            .ToListAsync());
        Assert.Equal("move", stored.Kind);
        Assert.Equal(fixture.BoundaryId, stored.BeforePointId);
        Assert.Equal(fixture.BoundaryId, stored.AfterPointId);
    }

    [Fact]
    public async Task SegmentMerge_UnknownBoundaryReturns404AndInvalidKindReturns400()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000008");
        using var client = _host.CreateClient(ownerId);

        var missing = await client.PostAsJsonAsync(
            "/api/v1/mobile/location/segments/merge",
            SegmentMergeRequest(
                new SegmentMergeFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                DeviceId,
                "move"));
        var invalidKind = await client.PostAsJsonAsync(
            "/api/v1/mobile/location/segments/merge",
            SegmentMergeRequest(
                new SegmentMergeFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                DeviceId,
                "unknown"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()))
            Assert.Equal(40401, body.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, invalidKind.StatusCode);
        using (var body = JsonDocument.Parse(await invalidKind.Content.ReadAsStringAsync()))
            Assert.Equal(6210, body.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SegmentMerge_OtherUsersBoundaryReturns404()
    {
        var ownerId = Guid.Parse("62040000-0000-4000-8000-000000000009");
        var otherId = Guid.Parse("62040000-0000-4000-8000-000000000010");
        var fixture = await SeedSegmentMergeAsync(ownerId, "owner-segment-device", "move");
        using var client = _host.CreateClient(otherId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/mobile/location/segments/merge",
            SegmentMergeRequest(fixture, "owner-segment-device", "move"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(40401, body.RootElement.GetProperty("code").GetInt32());

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var merge = Assert.Single(await db.Set<MobileLocationSegmentMergeEntity>()
            .Where(row => row.UserId == ownerId)
            .ToListAsync());
        Assert.Equal("move", merge.Kind);
    }

    private async Task<MobileLocationPointEntity> SeedPointAsync(
        Guid userId,
        string deviceId,
        DateTimeOffset recordedAt)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        if (!await db.Set<MobileDeviceEntity>().AnyAsync(device => device.UserId == userId && device.DeviceId == deviceId))
        {
            db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
            {
                UserId = userId,
                DeviceId = deviceId,
                DeviceHash = $"hash-{userId:N}-{deviceId}",
                DisplayName = "QA device",
                RegisteredAtUtc = recordedAt.AddDays(-1),
                LastSeenAtUtc = recordedAt,
            });
        }

        var point = new MobileLocationPointEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            RecordedAtUtc = recordedAt,
            Latitude = 31.230416m,
            Longitude = 121.473701m,
            HorizontalAccuracyMeters = 12.5m,
            Provider = "gps",
            Source = "auto",
        };
        db.Set<MobileLocationPointEntity>().Add(point);
        await db.SaveChangesAsync();
        return point;
    }

    private async Task<SegmentMergeFixture> SeedSegmentMergeAsync(Guid userId, string deviceId, string kind)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var first = Point(userId, deviceId, RecordedAt.AddMinutes(-5), 31.230000m, 121.473000m);
        var boundary = Point(userId, deviceId, RecordedAt, 31.230416m, 121.473701m);
        var last = Point(userId, deviceId, RecordedAt.AddMinutes(5), 31.230900m, 121.474000m);
        db.Set<MobileLocationPointEntity>().AddRange(first, boundary, last);
        db.Set<MobileLocationSegmentMergeEntity>().Add(new MobileLocationSegmentMergeEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            BeforePointId = boundary.Id,
            AfterPointId = boundary.Id,
            Kind = kind,
            ActorUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return new SegmentMergeFixture(boundary.Id, first.Id, last.Id);
    }

    private async Task<MobileLocationPointEntity> ReadPointAsync(Guid pointId)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return await db.Set<MobileLocationPointEntity>().AsNoTracking().SingleAsync(point => point.Id == pointId);
    }

    private static MobileLocationPointEntity Point(
        Guid userId,
        string deviceId,
        DateTimeOffset recordedAt,
        decimal latitude,
        decimal longitude)
        => new()
        {
            UserId = userId,
            DeviceId = deviceId,
            RecordedAtUtc = recordedAt,
            Latitude = latitude,
            Longitude = longitude,
            HorizontalAccuracyMeters = 12.5m,
            Provider = "gps",
            Source = "auto",
        };

    private static object SegmentMergeRequest(SegmentMergeFixture fixture, string deviceId, string kind)
    {
        return new
        {
            segmentIds = new[]
            {
                $"segment_{fixture.FirstId:N}_{fixture.BoundaryId:N}",
                $"segment_{fixture.BoundaryId:N}_{fixture.LastId:N}",
            },
            kind,
            rangeStartUtc = RecordedAt.AddHours(-1),
            rangeEndUtc = RecordedAt.AddHours(1),
            timezone = "Asia/Shanghai",
            deviceId,
        };
    }

    private static string RangeQuery()
        => $"rangeStartUtc={Uri.EscapeDataString(RecordedAt.AddHours(-1).ToString("O"))}"
            + $"&rangeEndUtc={Uri.EscapeDataString(RecordedAt.AddHours(1).ToString("O"))}"
            + "&timezone=Asia%2FShanghai&includeExcluded=true&pageSize=50";

    private static async Task<JsonElement[]> ReadPageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").GetProperty("items")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
    }

    private sealed record SegmentMergeFixture(Guid BoundaryId, Guid FirstId, Guid LastId);
}
