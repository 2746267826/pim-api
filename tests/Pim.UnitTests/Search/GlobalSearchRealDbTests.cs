using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Module.Calendar.Entities;
using Pim.UnitTests.Api;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Search;

/// <summary>
/// Q08 的真实 PostgreSQL 回归：使用临时库跑完整迁移链，再从真实 HTTP 路由
/// 搜索任务、日程和原生笔记，并验证两个账号之间的结果与详情端点隔离。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class GlobalSearchRealDbTests
{
    [SkippableFact]
    public async Task SearchAndDetailEndpointsUseRealPostgresAndKeepOwnersIsolated()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        await using var database = await TempMigrationDatabase.CreateAsync("test_q08_search");
        await database.MigrateAsync();

        var storageRoot = Path.Combine(
            Path.GetTempPath(), "pim-q08-real-search", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);
        try
        {
            using var factory = CreateHost(database.ConnectionString, storageRoot);
            var owner = Guid.NewGuid();
            var other = Guid.NewGuid();
            var seeded = await SeedCalendarObjectsAsync(factory, owner, other);
            var ownerNote = await CreateNoteAsync(
                factory, owner, "研究方法笔记", "课堂材料正文");
            var otherNote = await CreateNoteAsync(
                factory, other, "研究方法他人笔记", "他人正文");

            using var ownerClient = Authed(factory, owner);
            using var otherClient = Authed(factory, other);

            using var response = await ownerClient.GetAsync(
                $"/api/v1/search?q={Uri.EscapeDataString("研究方法")}&page=1&pageSize=20");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(3, data.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, data.GetProperty("page").GetInt32());
            Assert.Equal(20, data.GetProperty("pageSize").GetInt32());

            var items = data.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(new[] { "event", "note", "task" },
                items.Select(item => item.GetProperty("domain").GetString()!)
                    .OrderBy(domain => domain, StringComparer.Ordinal).ToArray());
            Assert.Contains(items, item =>
                item.GetProperty("id").GetString() == ownerNote.NoteId.ToString("D"));
            Assert.DoesNotContain(items, item =>
                item.GetProperty("id").GetString() == otherNote.NoteId.ToString("D"));
            Assert.DoesNotContain(items, item =>
                item.GetProperty("id").GetString() == seeded.OtherTaskId.ToString("D"));
            Assert.DoesNotContain(items, item =>
                item.GetProperty("id").GetString() == seeded.OtherEventId.ToString("D"));

            var groups = data.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal(new[] { "note", "task", "event" },
                groups.Select(group => group.GetProperty("domain").GetString()!).ToArray());
            Assert.All(groups, group => Assert.Equal(1, group.GetProperty("totalCount").GetInt32()));

            Assert.False(body.Contains("inputJson", StringComparison.OrdinalIgnoreCase));
            Assert.False(body.Contains("manifestJson", StringComparison.OrdinalIgnoreCase));
            Assert.False(body.Contains("storagePath", StringComparison.OrdinalIgnoreCase));

            using var otherResponse = await otherClient.GetAsync(
                $"/api/v1/search?q={Uri.EscapeDataString("研究方法")}&page=1&pageSize=20");
            Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
            var otherData = (await AuthRegistrationTestHost.ReadJsonAsync(otherResponse))
                .GetProperty("data");
            var otherItems = otherData.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(3, otherData.GetProperty("totalCount").GetInt32());
            Assert.Contains(otherItems, item =>
                item.GetProperty("id").GetString() == otherNote.NoteId.ToString("D"));
            Assert.DoesNotContain(otherItems, item =>
                item.GetProperty("id").GetString() == ownerNote.NoteId.ToString("D"));
            Assert.DoesNotContain(otherItems, item =>
                item.GetProperty("id").GetString() == seeded.OwnerTaskId.ToString("D"));
            Assert.DoesNotContain(otherItems, item =>
                item.GetProperty("id").GetString() == seeded.OwnerEventId.ToString("D"));

            using var pageTwo = await ownerClient.GetAsync(
                $"/api/v1/search?q={Uri.EscapeDataString("研究方法")}&page=2&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, pageTwo.StatusCode);
            var pageTwoData = (await AuthRegistrationTestHost.ReadJsonAsync(pageTwo))
                .GetProperty("data");
            Assert.Equal(2, pageTwoData.GetProperty("page").GetInt32());
            Assert.Equal(1, pageTwoData.GetProperty("items").GetArrayLength());
            Assert.Equal(3, pageTwoData.GetProperty("totalCount").GetInt32());

            using var tooLong = await ownerClient.GetAsync(
                $"/api/v1/search?q={new string('x', 201)}");
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            Assert.Equal(40001, (await AuthRegistrationTestHost.ReadErrorAsync(tooLong)).Code);

            using var ownerTask = await ownerClient.GetAsync(
                $"/api/v1/calendar/tasks/{seeded.OwnerTaskId:D}");
            Assert.Equal(HttpStatusCode.OK, ownerTask.StatusCode);
            using var ownerEvent = await ownerClient.GetAsync(
                $"/api/v1/calendar/events/{seeded.OwnerEventId:D}");
            Assert.Equal(HttpStatusCode.OK, ownerEvent.StatusCode);

            using var otherTask = await otherClient.GetAsync(
                $"/api/v1/calendar/tasks/{seeded.OwnerTaskId:D}");
            Assert.Equal(HttpStatusCode.NotFound, otherTask.StatusCode);
            Assert.Equal(40401, (await AuthRegistrationTestHost.ReadErrorAsync(otherTask)).Code);
            using var otherEvent = await otherClient.GetAsync(
                $"/api/v1/calendar/events/{seeded.OwnerEventId:D}");
            Assert.Equal(HttpStatusCode.NotFound, otherEvent.StatusCode);
            Assert.Equal(40401, (await AuthRegistrationTestHost.ReadErrorAsync(otherEvent)).Code);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> CreateHost(
        string connectionString, string storageRoot)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("NativeNotes:StorageRoot", Path.Combine(storageRoot, "materials"));
            builder.UseSetting("ManagedStorage:Root", Path.Combine(storageRoot, "managed-storage"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(storageRoot, "keys"));
        });

    private static HttpClient Authed(WebApplicationFactory<Program> factory, Guid userId)
    {
        var jwt = factory.Services.GetRequiredService<JwtService>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.GenerateAccessToken(userId, $"q08-{userId:N}", "user"));
        return client;
    }

    private static async Task<SeededObjects> SeedCalendarObjectsAsync(
        WebApplicationFactory<Program> factory, Guid owner, Guid other)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        db.Users.AddRange(
            NewUser(owner, "owner"),
            NewUser(other, "other"));

        var ownerCalendar = new CalendarEntity { UserId = owner, Name = "Q08 搜索日历" };
        var otherCalendar = new CalendarEntity { UserId = other, Name = "Q08 他人日历" };
        var ownerTask = NewTask(owner, "研究方法任务");
        var otherTask = NewTask(other, "研究方法他人任务");
        var ownerEvent = NewEvent(ownerCalendar, "研究方法日程");
        var otherEvent = NewEvent(otherCalendar, "研究方法他人日程");
        db.AddRange(ownerCalendar, otherCalendar, ownerTask, otherTask, ownerEvent, otherEvent);
        await db.SaveChangesAsync();

        return new SeededObjects(ownerTask.Id, ownerEvent.Id, otherTask.Id, otherEvent.Id);
    }

    private static UserEntity NewUser(Guid id, string label)
        => new()
        {
            Id = id,
            Username = $"q08-{label}-{id:N}",
            Email = $"q08-{label}-{id:N}@example.invalid",
            PasswordHash = "not-used-in-search-test",
            DisplayName = label,
            Role = "user",
            IsActive = true
        };

    private static TaskEntity NewTask(Guid owner, string title)
        => new()
        {
            UserId = owner,
            Uid = Guid.NewGuid().ToString("D"),
            Title = title,
            Due = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero)
        };

    private static EventEntity NewEvent(CalendarEntity calendar, string title)
        => new()
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = Guid.NewGuid().ToString("D"),
            Title = title,
            DtStart = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 12, 1, 11, 0, 0, TimeSpan.Zero)
        };

    private static async Task<(Guid NoteId, Guid VersionId)> CreateNoteAsync(
        WebApplicationFactory<Program> factory, Guid owner, string title, string body)
    {
        var noteId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = noteId.ToString("D"),
            title,
            revision = 1,
            createdAtMillis = 1_700_000_000_000L,
            blocks = new[]
            {
                new { id = Guid.NewGuid().ToString("D"), kind = "markdown", text = body }
            }
        });
        var inputJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = versionId.ToString("D"),
            createdAtMillis = 1_700_000_000_000L,
            note = JsonSerializer.Deserialize<JsonElement>(noteJson),
            recordings = Array.Empty<object>()
        });
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var markdownBytes = Encoding.UTF8.GetBytes(body);
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = Hash(inputJson),
            files = new[]
            {
                new { path = "note.json", kind = "Document", sha256 = Hash(noteJson), sizeBytes = noteBytes.Length },
                new { path = "document.md", kind = "Markdown", sha256 = Hash(body), sizeBytes = markdownBytes.Length }
            }
        });

        var store = factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await store.BeginAsync(owner, new BeginNoteMaterialUploadRequest(
            versionId, noteId, null, inputJson, manifestJson, true));
        await store.UploadPartAsync(owner, versionId, 0, 0, Hash(noteBytes),
            new MemoryStream(noteBytes), default);
        await store.UploadPartAsync(owner, versionId, 1, 0, Hash(markdownBytes),
            new MemoryStream(markdownBytes), default);
        await store.CompleteAsync(owner, versionId);
        return (noteId, versionId);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record SeededObjects(
        Guid OwnerTaskId,
        Guid OwnerEventId,
        Guid OtherTaskId,
        Guid OtherEventId);
}
