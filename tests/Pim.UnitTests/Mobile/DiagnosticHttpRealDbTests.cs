using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// Q05 的真实 PostgreSQL + 真实 HTTP 宿主回归：路由、JWT、全局用户过滤器、EF
/// 关系查询和 ZIP 导出必须在同一链路里成立，不能用 InMemory 或源码断言替代。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class DiagnosticHttpRealDbTests
{
    [SkippableFact]
    public async Task RealHost_ScopesEventsByAccountAndExportsOnlyAllowlistedServerSummary()
    {
        PimDbContext.RegisterModuleAssembly(typeof(DiagnosticEventEntity).Assembly);
        await using var temp = await TempMigrationDatabase.CreateAsync("test_diagnostics_q05");
        await temp.MigrateAsync();

        var logDirectory = Path.Combine(Path.GetTempPath(), $"pim-q05-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);
        var secretLogName = "pim-secret-log.jsonl";
        await File.WriteAllTextAsync(
            Path.Combine(logDirectory, secretLogName),
            """{"@t":"2026-11-25T00:00:00Z","message":"Authorization: Bearer pim-q05-log-secret"}""");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", temp.ConnectionString);
            builder.UseSetting("Logging:LogDir", logDirectory);
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-dp-{Guid.NewGuid():N}"));
        });

        var ownerA = Guid.Parse("d0611111-1111-4111-8111-111111111111");
        var ownerB = Guid.Parse("d0622222-2222-4222-8222-222222222222");
        var jwt = factory.Services.GetRequiredService<JwtService>();
        using var clientA = CreateClient(factory, jwt, ownerA);
        using var clientB = CreateClient(factory, jwt, ownerB);

        Assert.Equal(
            HttpStatusCode.OK,
            (await clientA.PostAsJsonAsync(
                "/api/v1/diagnostics/events",
                Request("q05-shared-device", DiagnosticPlatforms.Android, "a-android", "owner-a-android"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await clientA.PostAsJsonAsync(
                "/api/v1/diagnostics/events",
                Request("q05-shared-device", DiagnosticPlatforms.Web, "a-web", "owner-a-web"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await clientB.PostAsJsonAsync(
                "/api/v1/diagnostics/events",
                Request("q05-shared-device", DiagnosticPlatforms.Windows, "b-windows", "owner-b-windows"))).StatusCode);

        using var overviewA = await ReadJsonAsync(
            await clientA.GetAsync("/api/v1/diagnostics/overview?deviceId=q05-shared-device"));
        using var overviewB = await ReadJsonAsync(
            await clientB.GetAsync("/api/v1/diagnostics/overview?deviceId=q05-shared-device"));
        Assert.Equal(2, overviewA.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, overviewB.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());

        var entriesA = await ReadZipEntriesAsync(
            await clientA.GetAsync("/api/v1/diagnostics/export?deviceId=q05-shared-device&days=30"));
        var entriesB = await ReadZipEntriesAsync(
            await clientB.GetAsync("/api/v1/diagnostics/export?deviceId=q05-shared-device&days=30"));
        Assert.All(entriesA.Keys, name => Assert.Contains(
            name,
            new[] { "README.txt", "diagnostic-events.json", "manifest.json", "server-summary.json" }));
        Assert.Contains("owner-a-android", entriesA["diagnostic-events.json"], StringComparison.Ordinal);
        Assert.Contains("owner-a-web", entriesA["diagnostic-events.json"], StringComparison.Ordinal);
        Assert.DoesNotContain("owner-b-windows", entriesA["diagnostic-events.json"], StringComparison.Ordinal);
        Assert.Contains("owner-b-windows", entriesB["diagnostic-events.json"], StringComparison.Ordinal);
        Assert.DoesNotContain("owner-a-android", entriesB["diagnostic-events.json"], StringComparison.Ordinal);

        using var summary = JsonDocument.Parse(entriesA["server-summary.json"]);
        Assert.True(summary.RootElement.GetProperty("logs").GetProperty("fileCount").GetInt32() >= 1);
        Assert.DoesNotContain(secretLogName, entriesA["server-summary.json"], StringComparison.Ordinal);
        Assert.DoesNotContain("pim-q05-log-secret", entriesA["server-summary.json"], StringComparison.Ordinal);

        Directory.Delete(logDirectory, recursive: true);
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<Program> factory,
        JwtService jwt,
        Guid ownerId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            jwt.GenerateAccessToken(ownerId, $"q05-{ownerId:N}"[..28], "user"));
        return client;
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
                    kind = DiagnosticEventKinds.Error,
                    severity = DiagnosticSeverities.Error,
                    occurredAtUtc = DateTimeOffset.UtcNow,
                    appVersion = "26.11.1158",
                    osVersion = platform,
                    message,
                    attributes = new Dictionary<string, string>
                    {
                        ["route"] = "/diagnostics",
                        ["GraphEventId"] = "q05-graph-secret",
                        ["detail"] = "/tmp/q05-private.bin"
                    }
                }
            }
        };

    private static async Task<Dictionary<string, string>> ReadZipEntriesAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
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
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
