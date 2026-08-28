using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Stats.DTOs;
using Pim.Module.Stats.Entities;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Stats;

public class StatsCoverageTests
{
    [Fact]
    public async Task IngestBatch_PersistsEntriesWithCorrectMapping()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("device-1", new List<AppUsageEntry>
        {
            new("com.example.app1", nowMs - 60000, nowMs, 60000, nowMs)
        });

        var count = await svc.IngestBatchAsync(batch, default);

        Assert.Equal(1, count);
        var stored = await db.Set<AppUsageEntity>().SingleAsync();
        Assert.Equal("device-1", stored.DeviceId);
        Assert.Equal("com.example.app1", stored.PackageName);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(nowMs - 60000), stored.StartTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), stored.EndTime);
        Assert.Equal(60000, stored.DurationMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), stored.LastTimeUsed);
    }

    [Fact]
    public async Task IngestBatch_EmptyBatchReturnsZeroAndStoresNothing()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var batch = new UploadBatch("device-empty", new List<AppUsageEntry>());

        var count = await svc.IngestBatchAsync(batch, default);

        Assert.Equal(0, count);
        Assert.Empty(await db.Set<AppUsageEntity>().ToListAsync());
    }

    [Fact]
    public async Task IngestBatch_PurgesRecordsOlderThan30Days()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var old = new AppUsageEntity
        {
            DeviceId = "old-device",
            PackageName = "com.old",
            StartTime = DateTimeOffset.UtcNow.AddDays(-31),
            EndTime = DateTimeOffset.UtcNow.AddDays(-31),
            DurationMs = 100,
            LastTimeUsed = DateTimeOffset.UtcNow.AddDays(-31),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-31)
        };
        db.Set<AppUsageEntity>().Add(old);
        await db.SaveChangesAsync();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("device-new", new List<AppUsageEntry>
        {
            new("com.new", nowMs - 1000, nowMs, 1000, nowMs)
        });
        await svc.IngestBatchAsync(batch, default);

        var all = await db.Set<AppUsageEntity>().ToListAsync();
        Assert.Single(all);
        Assert.Equal("com.new", all[0].PackageName);
    }

    [Fact]
    public async Task IngestBatch_DoesNotPurgeRecentRecords()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var recent = new AppUsageEntity
        {
            DeviceId = "recent-device",
            PackageName = "com.recent",
            StartTime = DateTimeOffset.UtcNow.AddDays(-10),
            EndTime = DateTimeOffset.UtcNow.AddDays(-10),
            DurationMs = 100,
            LastTimeUsed = DateTimeOffset.UtcNow.AddDays(-10),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10)
        };
        db.Set<AppUsageEntity>().Add(recent);
        await db.SaveChangesAsync();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("device-new", new List<AppUsageEntry>
        {
            new("com.new", nowMs - 1000, nowMs, 1000, nowMs)
        });
        await svc.IngestBatchAsync(batch, default);

        var all = await db.Set<AppUsageEntity>().ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.PackageName == "com.recent");
        Assert.Contains(all, e => e.PackageName == "com.new");
    }

    [Fact]
    public async Task IngestBatch_MultipleBatchesAccumulate()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var b1 = new UploadBatch("dev", new List<AppUsageEntry> { new("com.a", nowMs - 2000, nowMs - 1000, 1000, nowMs - 1000) });
        var b2 = new UploadBatch("dev", new List<AppUsageEntry> { new("com.b", nowMs - 1000, nowMs, 1000, nowMs) });

        await svc.IngestBatchAsync(b1, default);
        await svc.IngestBatchAsync(b2, default);

        var stored = await db.Set<AppUsageEntity>().OrderBy(e => e.PackageName).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal("com.a", stored[0].PackageName);
        Assert.Equal("com.b", stored[1].PackageName);
    }

    [Fact]
    public async Task IngestBatch_StoresDeviceIdForEachEntry()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("my-device-123", new List<AppUsageEntry>
        {
            new("com.x", nowMs - 1000, nowMs, 1000, nowMs),
            new("com.y", nowMs - 2000, nowMs - 1000, 1000, nowMs)
        });

        await svc.IngestBatchAsync(batch, default);

        var all = await db.Set<AppUsageEntity>().ToListAsync();
        Assert.All(all, e => Assert.Equal("my-device-123", e.DeviceId));
    }

    [Fact]
    public async Task IngestBatch_PreservesDurationMs()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("d", new List<AppUsageEntry>
        {
            new("com.d", nowMs - 5000, nowMs, 12345, nowMs)
        });

        await svc.IngestBatchAsync(batch, default);

        var stored = await db.Set<AppUsageEntity>().SingleAsync();
        Assert.Equal(12345, stored.DurationMs);
    }

    [Fact]
    public async Task IngestBatch_SingleEntry_EndToEnd()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("single", new List<AppUsageEntry>
        {
            new("com.single", nowMs - 100, nowMs, 100, nowMs)
        });

        var c = await svc.IngestBatchAsync(batch, default);
        Assert.Equal(1, c);
        Assert.Equal(1, await db.Set<AppUsageEntity>().CountAsync());
    }

    [Fact]
    public void ClampHealthScore_ClampsViaReflection()
    {
        var method = typeof(Pim.Module.Stats.Services.StatsService).GetMethod("ClampHealthScore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(0, (int)method!.Invoke(null, new object[] { -10.0 })!);
        Assert.Equal(0, (int)method.Invoke(null, new object[] { -0.4 })!);
        Assert.Equal(100, (int)method.Invoke(null, new object[] { 150.0 })!);
        Assert.Equal(50, (int)method.Invoke(null, new object[] { 50.4 })!);
        Assert.Equal(50, (int)method.Invoke(null, new object[] { 50.5 })!);
        Assert.Equal(51, (int)method.Invoke(null, new object[] { 50.6 })!);
        Assert.Equal(100, (int)method.Invoke(null, new object[] { 99.6 })!);
    }

    [Fact]
    public async Task StatsModule_RegistersAndMapsEndpoints()
    {
        using var db = ServiceTestBase.CreateDb();
        var svc = ServiceTestBase.CreateStatsService(db);
        Assert.NotNull(svc);
        Assert.Equal("stats", new Pim.Module.Stats.StatsModule().Name);
        Assert.Equal("1.0.0", new Pim.Module.Stats.StatsModule().Version);
        await new Pim.Module.Stats.StatsModule().InitializeAsync(new ServiceCollection().BuildServiceProvider());

        // MapEndpoints registers /api/v1/stats/upload with authorization
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        new Pim.Module.Stats.StatsModule().MapEndpoints(app);
        await app.StartAsync();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var upload = endpoints.FirstOrDefault(e => e.RoutePattern.RawText == "/api/v1/stats/upload");
        Assert.NotNull(upload);
        Assert.NotNull(upload!.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>());

        var entry = new AppUsageEntry("pkg", 1000, 2000, 1000, 2000);
        Assert.Equal("pkg", entry.PackageName);
        var batch = new UploadBatch("dev", new List<AppUsageEntry> { entry });
        Assert.Single(batch.Entries);
    }

    [Fact]
    public async Task StatsModule_UploadEndpoint_BranchesEmptyAndNonEmpty()
    {
        // exercise the endpoint handler branches (empty batch vs non-empty) via TestServer
        var db = ServiceTestBase.CreateDb();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication("Test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddSingleton<PimDbContext>(db);
        builder.Services.AddScoped<Pim.Module.Stats.Services.StatsService>(_ => new Pim.Module.Stats.Services.StatsService(db));
        var app = builder.Build();
        new Pim.Module.Stats.StatsModule().MapEndpoints(app);
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        // empty batch branch
        var emptyResp = await client.PostAsJsonAsync("/api/v1/stats/upload", new UploadBatch("dev", new List<AppUsageEntry>()));
        Assert.True(emptyResp.IsSuccessStatusCode);
        var emptyBody = await emptyResp.Content.ReadAsStringAsync();
        Assert.Contains("\"data\":0", emptyBody);

        // non-empty branch
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new UploadBatch("dev", new List<AppUsageEntry> { new("com.a", nowMs - 1000, nowMs, 1000, nowMs) });
        var resp = await client.PostAsJsonAsync("/api/v1/stats/upload", batch);
        Assert.True(resp.IsSuccessStatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"data\":1", body);
        await app.StopAsync();
    }

    private sealed class TestAuthHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
    {
        public TestAuthHandler(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder) : base(options, logger, encoder) { }
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, ServiceTestBase.DefaultUserId.ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "user") };
            var identity = new System.Security.Claims.ClaimsIdentity(claims, "Test");
            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "Test");
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
        }
    }
}
