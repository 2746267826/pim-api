using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class DiagnosticHttpTests : IClassFixture<SyntheticDiagnosticApiHost>
{
    private readonly SyntheticDiagnosticApiHost _host;

    public DiagnosticHttpTests(SyntheticDiagnosticApiHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task DiagnosticEndpoints_RequireAuthentication()
    {
        using var client = _host.Factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/diagnostics/overview")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/diagnostics/export?days=7")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(
                "/api/v1/diagnostics/events",
                Request("anonymous-device", DiagnosticPlatforms.Web, "anonymous-event", "no auth")))
            .StatusCode);
    }

    [Fact]
    public async Task FakeAndroidWindowsAndWebClients_ReportRedactedEventsAndExportAllowlistedZip()
    {
        var ownerId = Guid.Parse("d0511111-1111-4111-8111-111111111111");
        using var client = _host.CreateClient(ownerId);

        foreach (var platform in new[]
                 {
                     DiagnosticPlatforms.Android,
                     DiagnosticPlatforms.Windows,
                     DiagnosticPlatforms.Web
                 })
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/diagnostics/events",
                Request(
                    $"{platform}-q05-device",
                    platform,
                    $"{platform}-event",
                    $"token=owner-secret {platform} GraphEventId=graph-{platform}-secret ChangeKey=change-{platform}-secret"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            Assert.Equal(1, body.RootElement.GetProperty("data").GetProperty("acceptedCount").GetInt32());
        }

        using var overviewResponse = await client.GetAsync("/api/v1/diagnostics/overview?limit=50");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        using var overview = await ReadJsonAsync(overviewResponse);
        Assert.Equal(3, overview.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());

        using var exportResponse = await client.GetAsync("/api/v1/diagnostics/export?days=30");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("application/zip", exportResponse.Content.Headers.ContentType?.MediaType);
        var entries = await ReadZipEntriesAsync(exportResponse);

        Assert.Equal(
            ["README.txt", "diagnostic-events.json", "manifest.json", "server-summary.json"],
            entries.Keys.Order(StringComparer.Ordinal));
        var combined = string.Join("\n", entries.Values);
        foreach (var secret in new[]
                 {
                     "owner-secret",
                     "graph-android-secret",
                     "change-windows-secret",
                     "sk-abcdefghijklmnopqrstuvwxyz",
                     "pim-private-log-line",
                     _host.LogFileName
                 })
        {
            Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);
        }

        var payloadEntries = entries["diagnostic-events.json"] + entries["server-summary.json"];
        foreach (var sensitiveName in new[]
                 {
                     "graphEventId",
                     "changeKey",
                     "access_token",
                     "authorization",
                     "lastError",
                     "details"
                 })
        {
            Assert.DoesNotContain(sensitiveName, payloadEntries, StringComparison.OrdinalIgnoreCase);
        }

        using var serverSummary = JsonDocument.Parse(entries["server-summary.json"]);
        Assert.Equal(
            [
                "schemaVersion",
                "apiVersion",
                "webVersion",
                "webSha",
                "webBuiltAt",
                "environment",
                "runtime",
                "operatingSystem",
                "generatedAtUtc",
                "processUptimeSeconds",
                "logs",
                "jobs",
                "components",
                "redactionPolicy"
            ],
            serverSummary.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(serverSummary.RootElement
            .GetProperty("logs")
            .GetProperty("fileCount")
            .GetInt32() >= 1);
    }

    [Fact]
    public async Task SameDeviceAcrossAccounts_IsIsolatedInOverviewAndExport()
    {
        var ownerA = Guid.Parse("d0522222-2222-4222-8222-222222222221");
        var ownerB = Guid.Parse("d0522222-2222-4222-8222-222222222222");
        using var clientA = _host.CreateClient(ownerA);
        using var clientB = _host.CreateClient(ownerB);

        var responseA = await clientA.PostAsJsonAsync(
            "/api/v1/diagnostics/events",
            Request("shared-q05-device", DiagnosticPlatforms.Android, "owner-a-event", "owner-a-only"));
        var responseB = await clientB.PostAsJsonAsync(
            "/api/v1/diagnostics/events",
            Request("shared-q05-device", DiagnosticPlatforms.Android, "owner-b-event", "owner-b-only"));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);

        using var overviewA = await ReadJsonAsync(
            await clientA.GetAsync("/api/v1/diagnostics/overview?deviceId=shared-q05-device"));
        using var overviewB = await ReadJsonAsync(
            await clientB.GetAsync("/api/v1/diagnostics/overview?deviceId=shared-q05-device"));
        Assert.Equal("owner-a-only", overviewA.RootElement
            .GetProperty("data").GetProperty("items")[0].GetProperty("message").GetString());
        Assert.Equal("owner-b-only", overviewB.RootElement
            .GetProperty("data").GetProperty("items")[0].GetProperty("message").GetString());

        var exportB = await ReadZipEntriesAsync(
            await clientB.GetAsync("/api/v1/diagnostics/export?deviceId=shared-q05-device&days=30"));
        var exportText = string.Join("\n", exportB.Values);
        Assert.Contains("owner-b-only", exportText, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-a-only", exportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SensitiveDeviceId_IsRedactedInOverviewAndExport()
    {
        var ownerId = Guid.Parse("d0555555-5555-4555-8555-555555555555");
        using var client = _host.CreateClient(ownerId);
        const string sensitiveDeviceId = "Bearer abcdefghijklmnop /tmp/pim/device.bin";

        var response = await client.PostAsJsonAsync(
            "/api/v1/diagnostics/events",
            Request(
                sensitiveDeviceId,
                DiagnosticPlatforms.Web,
                "sensitive-device-event",
                "device id redaction"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var overview = await ReadJsonAsync(
            await client.GetAsync("/api/v1/diagnostics/overview"));
        var overviewDeviceId = overview.RootElement
            .GetProperty("data")
            .GetProperty("items")[0]
            .GetProperty("deviceId")
            .GetString();
        Assert.NotNull(overviewDeviceId);
        Assert.DoesNotContain("abcdefghijklmnop", overviewDeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", overviewDeviceId, StringComparison.Ordinal);
        Assert.Contains("[已脱敏]", overviewDeviceId, StringComparison.Ordinal);

        var exportEntries = await ReadZipEntriesAsync(
            await client.GetAsync("/api/v1/diagnostics/export?days=30"));
        using var exportedEvents = JsonDocument.Parse(exportEntries["diagnostic-events.json"]);
        var exportedDeviceId = exportedEvents.RootElement[0]
            .GetProperty("deviceId")
            .GetString();
        Assert.NotNull(exportedDeviceId);
        Assert.DoesNotContain("abcdefghijklmnop", exportedDeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", exportedDeviceId, StringComparison.Ordinal);
        Assert.Contains("[已脱敏]", exportedDeviceId, StringComparison.Ordinal);
        var exportText = string.Join("\n", exportEntries.Values);
        Assert.DoesNotContain("abcdefghijklmnop", exportText, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", exportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyQuota_Returns429RetryAfterAndDoesNotWriteTheBatch()
    {
        var ownerId = Guid.Parse("d0533333-3333-4333-8333-333333333333");
        using var client = _host.CreateClient(ownerId);
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var start = DateTimeOffset.UtcNow.Date;
            db.Set<DiagnosticEventEntity>().AddRange(
                Enumerable.Range(0, DiagnosticLimits.MaxEventsPerUserPerUtcDay)
                    .Select(index => new DiagnosticEventEntity
                    {
                        UserId = ownerId,
                        DeviceId = "quota-http-device",
                        Platform = DiagnosticPlatforms.Web,
                        ClientEventId = $"seed-{index}",
                        ClientBatchId = "seed",
                        Kind = DiagnosticEventKinds.Error,
                        Severity = DiagnosticSeverities.Error,
                        AppVersion = "1.0",
                        OsVersion = "Web",
                        Message = "seed",
                        OccurredAtUtc = start,
                        ReceivedAtUtc = start,
                        CreatedAt = start
                    }));
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            "/api/v1/diagnostics/events",
            Request("quota-http-device", DiagnosticPlatforms.Android, "over-quota", "blocked"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.True(response.Headers.RetryAfter!.Delta > TimeSpan.Zero);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(42901, body.RootElement.GetProperty("code").GetInt32());
        using var verify = _host.Factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await verifyDb.Set<DiagnosticEventEntity>()
            .AnyAsync(row => row.UserId == ownerId && row.ClientEventId == "over-quota"));
    }

    [Fact]
    public async Task OversizedPayload_Returns413WithoutPersisting()
    {
        var ownerId = Guid.Parse("d0544444-4444-4444-8444-444444444444");
        using var client = _host.CreateClient(ownerId);
        var oversized = new string('x', DiagnosticLimits.MaxRequestBytes + 1024);
        using var content = new StringContent(
            $$"""{"deviceId":"oversize-device","platform":"web","clientBatchId":"oversize","events":[{"clientEventId":"oversize-event","kind":"error","severity":"error","occurredAtUtc":"2026-11-25T12:00:00Z","appVersion":"1.0","osVersion":"Web","message":"{{oversized}}"}]}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/v1/diagnostics/events", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await db.Set<DiagnosticEventEntity>()
            .AnyAsync(row => row.UserId == ownerId));
    }

    private static object Request(
        string deviceId,
        string platform,
        string clientEventId,
        string message)
        => new
        {
            deviceId,
            platform,
            clientBatchId = $"batch-{clientEventId}",
            events = new[]
            {
                new
                {
                    clientEventId,
                    kind = "error",
                    severity = "error",
                    occurredAtUtc = DateTimeOffset.UtcNow,
                    appVersion = "1.0.0",
                    osVersion = platform,
                    message = $"{message} Authorization: Bearer abcdefghijklmnop sk-abcdefghijklmnopqrstuvwxyz /tmp/pim/pim-private-log-line",
                    stackTrace = @"at C:\Users\alice\AppData\Local\Temp\pim-private-log-line.txt:1",
                    attributes = new Dictionary<string, string>
                    {
                        ["route"] = "/diagnostics",
                        ["access_token"] = "must-not-leak",
                        ["GraphEventId"] = "graph-attribute-secret",
                        ["detail"] = "file:///tmp/pim/pim-private-log-line.bin"
                    }
                }
            }
        };

    private static async Task<Dictionary<string, string>> ReadZipEntriesAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

public sealed class SyntheticDiagnosticApiHost : IDisposable
{
    public SyntheticDiagnosticApiHost()
    {
        LogDirectory = Path.Combine(
            Path.GetTempPath(),
            $"pim-diagnostics-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(LogDirectory);
        LogFileName = "pim-api-20261125.jsonl";
        File.WriteAllText(
            Path.Combine(LogDirectory, LogFileName),
            """{"@t":"2026-11-25T00:00:00Z","message":"Authorization: Bearer pim-private-log-line","GraphEventId":"graph-log-secret"}""");

        var databaseName = $"qa-diagnostics-{Guid.NewGuid():N}";
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("Logging:LogDir", LogDirectory);
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-dp-{Guid.NewGuid():N}"));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    item => item.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
            });
        });

        _ = Factory.Services.GetRequiredService<JwtService>();
    }

    public WebApplicationFactory<Program> Factory { get; }
    public string LogDirectory { get; }
    public string LogFileName { get; }

    public HttpClient CreateClient(Guid userId)
    {
        var client = Factory.CreateClient();
        var jwt = Factory.Services.GetRequiredService<JwtService>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            jwt.GenerateAccessToken(userId, $"qa-diagnostics-{userId:N}"[..28], "user"));
        return client;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try
        {
            Directory.Delete(LogDirectory, recursive: true);
        }
        catch
        {
        }
    }
}
